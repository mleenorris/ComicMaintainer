using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SharpCompress.Archives;
using SharpCompress.Archives.Zip;
using SharpCompress.Common;
using SharpCompress.Writers;
using SharpCompress.Writers.Zip;

namespace ComicMaintainer.Core.Services;

public partial class ComicProcessorService
{
    public Task<Guid> ProcessFilesAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken = default)
    {
        return ProcessFilesAsync(filePaths, false, cancellationToken);
    }

    public Task<Guid> ProcessFilesAsync(IEnumerable<string> filePaths, bool forceReprocess, CancellationToken cancellationToken = default)
    {
        return QueueBatchJobAsync(
            filePaths,
            "ProcessFilesAsync",
            "processing",
            (filePath, token) => ProcessFileCoreAsync(filePath, forceReprocess, token),
            "Processing failed",
            cancellationToken);
    }

    public Task<Guid> RenameFilesAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken = default)
    {
        return RenameFilesAsync(filePaths, false, cancellationToken);
    }

    public Task<Guid> RenameFilesAsync(IEnumerable<string> filePaths, bool forceReprocess, CancellationToken cancellationToken = default)
    {
        return QueueBatchJobAsync(
            filePaths,
            "RenameFilesAsync",
            "renaming",
            (filePath, token) => RenameFileCoreAsync(filePath, forceReprocess, token),
            "Rename failed",
            cancellationToken);
    }

    public Task<Guid> NormalizeFilesAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken = default)
    {
        return NormalizeFilesAsync(filePaths, false, cancellationToken);
    }

    public Task<Guid> NormalizeFilesAsync(IEnumerable<string> filePaths, bool forceReprocess, CancellationToken cancellationToken = default)
    {
        return QueueBatchJobAsync(
            filePaths,
            "NormalizeFilesAsync",
            "normalizing",
            (filePath, token) => NormalizeFileCoreAsync(filePath, forceReprocess, token),
            "Normalize failed",
            cancellationToken);
    }

    public Task<Guid> UpdateMetadataAsync(IEnumerable<string> filePaths, ComicMetadata metadata, CancellationToken cancellationToken = default)
    {
        return QueueBatchJobAsync(
            filePaths,
            "UpdateMetadataAsync",
            "updating metadata for",
            (file, token) => UpdateMetadataCoreAsync(file, metadata, token),
            "Update metadata failed",
            cancellationToken);
    }

    public Task<Guid> RemoveMetadataFromFilesAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken = default)
    {
        return QueueBatchJobAsync(
            filePaths,
            "RemoveMetadataFromFilesAsync",
            "removing metadata from",
            async (filePath, token) =>
            {
                var removed = await RemoveMetadataCoreAsync(filePath, token);
                if (!removed)
                {
                    return false;
                }

                // Mark the file as unprocessed so the rename/normalize pipeline
                // will re-evaluate it on the next run, mirroring the per-file
                // DELETE /api/files/{path}/metadata behavior.
                try
                {
                    await _fileStore.ClearProcessedStatusAsync(new[] { filePath }, token);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "RemoveMetadataFromFilesAsync: failed to clear processed status for {FilePath}",
                        LoggingHelper.SanitizePathForLog(filePath));
                }

                return true;
            },
            "Remove metadata failed",
            cancellationToken);
    }

    public Task<Guid> DeleteFilesAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken = default)
    {
        return QueueBatchJobAsync(
            filePaths,
            "DeleteFilesAsync",
            "deleting",
            async (filePath, token) =>
            {
                try
                {
                    if (!TryResolveAllowedEntry(filePath, out var entryPath))
                    {
                        _logger.LogWarning(
                            "DeleteFilesAsync: file is outside the allowed directories: {FilePath}",
                            LoggingHelper.SanitizePathForLog(filePath));
                        return false;
                    }

                    // The library path of the entry that is deleted, which differs from the
                    // requested path when that goes through a link or contains "..".
                    var entryDirectory = Path.GetDirectoryName(entryPath)!;
                    var canonicalPath = Path.Join(GetAllowedLogicalPath(entryDirectory), Path.GetFileName(entryPath));
                    var storePaths = new[] { filePath, canonicalPath }.Distinct(StringComparer.Ordinal).ToArray();

                    // Open the checked directory now, so the entry is deleted from it even if
                    // the directory is replaced (e.g. by a link out of the library) meanwhile.
                    using var directoryHandle = Directory.Exists(entryDirectory) ? OpenRenameDirectory(entryPath) : null;
                    if (FileEntryExists(GetEntryPathInDirectory(directoryHandle, entryPath)))
                    {
                        // Remove from file store first (unlikely to fail), then
                        // delete the physical file. This order prevents orphaned
                        // file-store entries if the disk delete fails.
                        foreach (var storePath in storePaths)
                            await _fileStore.RemoveFileAsync(storePath, token);
                        DeleteFileInDirectory(directoryHandle, entryPath);
                        await LogHistoryAsync(canonicalPath, "Delete", true, null, token);
                        return true;
                    }

                    // File already missing on disk — still scrub the store entry
                    // so the library list reflects reality.
                    try
                    {
                        foreach (var storePath in storePaths)
                            await _fileStore.RemoveFileAsync(storePath, token);
                    }
                    catch (Exception removeEx)
                    {
                        _logger.LogDebug(removeEx,
                            "DeleteFilesAsync: file-store cleanup failed for missing file {FilePath}",
                            LoggingHelper.SanitizePathForLog(filePath));
                    }
                    await LogHistoryAsync(canonicalPath, "Delete", false, "File not found on disk", token);
                    return false;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "DeleteFilesAsync: error deleting file {FilePath}",
                        LoggingHelper.SanitizePathForLog(filePath));
                    await LogHistoryAsync(filePath, "Delete", false, ex.Message, token);
                    return false;
                }
            },
            "Delete failed",
            cancellationToken);
    }

    public Task<Guid> NormalizeAndRenameFilesAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken = default)
    {
        // For each file, normalize first (so ComicInfo.xml has the series name derived
        // from the current folder) and then rename so the filename uses that fresh
        // series name. Both steps force reprocessing because files moved by a folder
        // combine may already be marked normalized/renamed in the database under their
        // previous (stale) series.
        return QueueBatchJobAsync(
            filePaths,
            "NormalizeAndRenameFilesAsync",
            "normalizing and renaming",
            async (filePath, token) =>
            {
                var normalizeOk = await NormalizeFileCoreAsync(filePath, true, token);
                if (!normalizeOk)
                {
                    return false;
                }
                return await RenameFileCoreAsync(filePath, true, token);
            },
            "Normalize and rename failed",
            cancellationToken);
    }

    public Task<Guid> RunCustomBatchJobAsync(
        string operationName,
        IEnumerable<string> trackedItems,
        Func<string, CancellationToken, Task<bool>> itemOperation,
        string failureMessage,
        Func<CancellationToken, Task>? postLoopAsync = null,
        CancellationToken cancellationToken = default)
    {
        return QueueBatchJobAsync(
            trackedItems,
            operationName,
            operationName.ToLowerInvariant(),
            itemOperation,
            failureMessage,
            cancellationToken,
            postLoopAsync);
    }

    private Task<Guid> QueueBatchJobAsync(
        IEnumerable<string> filePaths,
        string operationName,
        string actionDescription,
        Func<string, CancellationToken, Task<bool>> fileOperation,
        string failureMessage,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? postLoopAsync = null)
    {
        var jobId = Guid.NewGuid();
        var rawList = filePaths?.ToList() ?? new List<string>();
        var skippedInvalid = 0;
        var fileList = new List<string>(rawList.Count);
        foreach (var path in rawList)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                skippedInvalid++;
                continue;
            }
            fileList.Add(path);
        }

        if (skippedInvalid > 0)
        {
            _logger.LogWarning(
                "{OperationName}: Skipping {SkippedCount} null or empty file path(s) in job {JobId}",
                operationName, skippedInvalid, jobId);
        }

        _logger.LogDebug("{OperationName}: Creating new {ActionDescription} job {JobId} for {FileCount} files", operationName, actionDescription, jobId, fileList.Count);

        var job = new ProcessingJob
        {
            JobId = jobId,
            Status = JobStatus.Queued,
            OperationName = operationName,
            Files = fileList,
            TotalFiles = fileList.Count,
            StartTime = DateTime.UtcNow
        };

        _jobs[jobId] = job;
        var jobSyncLock = _jobSyncLocks.GetOrAdd(jobId, static _ => new object());
        
        // Create a CancellationTokenSource for this job that can be cancelled independently
        var jobCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _jobCancellationTokens[jobId] = jobCts;

        _logger.LogDebug("{OperationName}: Job {JobId} created and queued with {FileCount} files using up to {MaxWorkers} workers", operationName, jobId, fileList.Count, _maxWorkers);

        // Broadcast initial job status
        _ = BroadcastJobStatusAsync(job);

        // Process files asynchronously using LongRunning for potentially long batch operations.
        // StartNew gets CancellationToken.None so the delegate always runs: if jobCts is already
        // cancelled (e.g. the request was aborted), the delegate itself must end the job as
        // Cancelled and release jobCts, rather than StartNew skipping it and leaving it Queued.
        _ = Task.Factory.StartNew(async () =>
        {
            try
            {
                jobCts.Token.ThrowIfCancellationRequested();

                _logger.LogDebug("{OperationName}: Job {JobId} starting execution", operationName, jobId);
                lock (jobSyncLock)
                {
                    job.Status = JobStatus.Running;
                }
                await BroadcastJobStatusAsync(job);

                var parallelOptions = new ParallelOptions
                {
                    CancellationToken = jobCts.Token,
                    MaxDegreeOfParallelism = _maxWorkers
                };

                await Parallel.ForEachAsync(
                    fileList.Select((file, index) => new { file, index }),
                    parallelOptions,
                    async (item, token) =>
                    {
                        _logger.LogDebug("{OperationName}: Job {JobId} {ActionDescription} file {FileIndex}/{TotalFiles}: {FilePath}",
                            operationName,
                            jobId,
                            actionDescription,
                            item.index + 1,
                            fileList.Count,
                            LoggingHelper.SanitizePathForLog(item.file));

                        var success = await fileOperation(item.file, token);
                        _metrics?.RecordFileProcessed(operationName, success);

                        lock (jobSyncLock)
                        {
                            job.CurrentFile = item.file;
                            if (success)
                            {
                                job.ProcessedFiles++;
                            }
                            else
                            {
                                job.FailedFiles++;
                                job.Errors[item.file] = failureMessage;
                            }
                        }

                        _logger.LogDebug("{OperationName}: Job {JobId} completed file {FileIndex}/{TotalFiles} with success={Success}: {FilePath}",
                            operationName,
                            jobId,
                            item.index + 1,
                            fileList.Count,
                            success,
                            LoggingHelper.SanitizePathForLog(item.file));

                        // Broadcast progress after each file
                        await BroadcastJobStatusAsync(job);

                        // Broadcast individual file processed event
                        if (_eventBroadcaster != null)
                        {
                            await _eventBroadcaster.BroadcastFileProcessedAsync(
                                Path.GetFileName(item.file),
                                success,
                                success ? null : failureMessage);
                        }
                    });

                if (postLoopAsync is not null)
                {
                    _logger.LogDebug("{OperationName}: Job {JobId} running post-loop hook", operationName, jobId);
                    await postLoopAsync(jobCts.Token);
                }

                lock (jobSyncLock)
                {
                    job.Status = JobStatus.Completed;
                    job.EndTime = DateTime.UtcNow;
                }
                _logger.LogInformation("{OperationName}: Job {JobId} completed - Processed: {ProcessedFiles}, Failed: {FailedFiles}, Total: {TotalFiles}",
                    operationName, jobId, job.ProcessedFiles, job.FailedFiles, job.TotalFiles);
                await BroadcastJobStatusAsync(job);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("{OperationName}: Job cancelled: {JobId}", operationName, jobId);
                lock (jobSyncLock)
                {
                    job.Status = JobStatus.Cancelled;
                    job.EndTime = DateTime.UtcNow;
                }
                await BroadcastJobStatusAsync(job);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{OperationName}: Error processing batch job: {JobId}", operationName, jobId);
                lock (jobSyncLock)
                {
                    job.Status = JobStatus.Failed;
                    job.EndTime = DateTime.UtcNow;
                }
                await BroadcastJobStatusAsync(job);
            }
            finally
            {
                _metrics?.RecordProcessingJob(
                    operationName,
                    job.Status.ToString(),
                    (job.EndTime ?? DateTime.UtcNow) - job.StartTime);
                _jobCancellationTokens.TryRemove(jobId, out _);
                jobCts.Dispose();
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

        return Task.FromResult(jobId);
    }
}
