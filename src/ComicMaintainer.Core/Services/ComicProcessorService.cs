using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SharpCompress.Archives;
using SharpCompress.Archives.Zip;
using SharpCompress.Common;
using SharpCompress.Writers;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Service for processing comic files with SharpCompress integration
/// </summary>
public class ComicProcessorService : IComicProcessorService, IDisposable
{
    private const string UnknownSeries = "Unknown Series";
    private readonly IOptionsMonitor<AppSettings> _settingsMonitor;
    private readonly ILogger<ComicProcessorService> _logger;
    private readonly IFileStoreService _fileStore;
    private readonly IEventBroadcaster? _eventBroadcaster;
    private readonly IExternalSeriesMetadataService? _externalSeriesMetadata;
    private readonly ISeriesMetadataCacheService? _seriesMetadataCache;
    private readonly IProcessingHistoryService _historyService;
    private readonly ISeriesNameResolver? _seriesNameResolver;
    private readonly ConcurrentDictionary<Guid, ProcessingJob> _jobs = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _jobCancellationTokens = new();
    private readonly ConcurrentDictionary<Guid, object> _jobSyncLocks = new();
    private readonly SemaphoreSlim _processingSemaphore;
    private readonly int _maxWorkers;
    private bool _disposed;

    /// <summary>
    /// Returns the current AppSettings snapshot. Settings are re-read on each access so that
    /// changes made via SettingsService take effect on the next operation without restarting
    /// the process.
    /// Note: <see cref="AppSettings.MaxWorkers"/> is captured into a fixed-size semaphore at
    /// construction and therefore still requires a restart to change.
    /// </summary>
    private AppSettings _settings => _settingsMonitor.CurrentValue;

    public ComicProcessorService(
        IOptionsMonitor<AppSettings> settings,
        ILogger<ComicProcessorService> logger,
        IFileStoreService fileStore,
        IProcessingHistoryService historyService,
        IEventBroadcaster? eventBroadcaster = null,
        IExternalSeriesMetadataService? externalSeriesMetadata = null,
        ISeriesMetadataCacheService? seriesMetadataCache = null,
        ISeriesNameResolver? seriesNameResolver = null)
    {
        _settingsMonitor = settings;
        _logger = logger;
        _fileStore = fileStore;
        _historyService = historyService;
        _eventBroadcaster = eventBroadcaster;
        _externalSeriesMetadata = externalSeriesMetadata;
        _seriesMetadataCache = seriesMetadataCache;
        _seriesNameResolver = seriesNameResolver;
        _maxWorkers = Math.Max(1, _settingsMonitor.CurrentValue.MaxWorkers);
        _processingSemaphore = new SemaphoreSlim(_maxWorkers, _maxWorkers);
    }

    public Task<bool> ProcessFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        return ProcessFileAsync(filePath, false, cancellationToken);
    }

    public async Task<bool> ProcessFileAsync(string filePath, bool forceReprocess, CancellationToken cancellationToken = default)
    {
        await _processingSemaphore.WaitAsync(cancellationToken);
        try
        {
            return await ProcessFileCoreAsync(filePath, forceReprocess, cancellationToken);
        }
        finally
        {
            _processingSemaphore.Release();
        }
    }

    private async Task<bool> ProcessFileCoreAsync(string filePath, bool forceReprocess, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("ProcessFileAsync: Starting processing for file: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));

            // Skip files that the database already considers fully processed, preventing
            // repeated work caused by watcher events fired during or after processing.
            if (!forceReprocess && await _fileStore.IsFileProcessedAsync(filePath, cancellationToken))
            {
                _logger.LogInformation("ProcessFileAsync: File already fully processed (database), skipping: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                return true;
            }

            _logger.LogDebug("ProcessFileAsync: Checking if file exists: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));

            if (!File.Exists(filePath))
            {
                _logger.LogWarning("ProcessFileAsync: File not found: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                await LogHistoryAsync(filePath, "Process", false, "File not found", cancellationToken);
                return false;
            }

            _logger.LogDebug("ProcessFileAsync: Verifying file is a comic archive: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            
            // Verify it's a comic archive
            if (!IsComicArchive(filePath))
            {
                _logger.LogWarning("ProcessFileAsync: File is not a comic archive: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                await LogHistoryAsync(filePath, "Process", false, "File is not a comic archive", cancellationToken);
                return false;
            }

            _logger.LogDebug("ProcessFileAsync: Extracting metadata from archive: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            
            // Extract metadata from the archive
            var metadata = await GetMetadataAsync(filePath, cancellationToken);
            
            if (metadata != null)
            {
                _logger.LogDebug("ProcessFileAsync: Metadata extracted - Series: {Series}, Issue: {Issue}, Title: {Title}", 
                    LoggingHelper.SanitizeForLog(metadata.Series),
                    LoggingHelper.SanitizeForLog(metadata.Issue),
                    LoggingHelper.SanitizeForLog(metadata.Title));
            }
            else
            {
                _logger.LogDebug("ProcessFileAsync: No metadata extracted from file: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            }
            
            _logger.LogDebug("ProcessFileAsync: Continuing with processing: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));

            // Track rename and normalize separately
            bool renameSuccess = false;
            bool normalizeSuccess = false;

            _logger.LogDebug("ProcessFileAsync: Starting normalize phase - WatcherEnableNormalize: {NormalizeEnabled}, Has Metadata: {HasMetadata}",
                _settings.WatcherEnableNormalize, metadata != null);

            // Normalize metadata (update ComicInfo.xml) FIRST so the rename phase below uses
            // the resolved/normalized series name (and any chapter/title fields populated by
            // normalization) when generating the target filename.
            if (_settings.WatcherEnableNormalize && metadata != null)
            {
                _logger.LogDebug("ProcessFileAsync: Attempting to normalize file: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                // Check if metadata is already normalized (has ComicInfo.xml with valid data and series name matches folder)
                if (await IsMetadataNormalizedAsync(metadata, filePath, cancellationToken))
                {
                    _logger.LogDebug("File already has normalized metadata: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                    await _fileStore.MarkFileNormalizedAsync(filePath, true, cancellationToken);
                    var filename = Path.GetFileName(filePath);
                    await LogHistoryWithChangesAsync(filePath, "Normalize", true, "File already normalized",
                        filename, filename, metadata, metadata, cancellationToken);
                    normalizeSuccess = true;
                }
                else
                {
                    var beforeMetadata = metadata.Clone();
                    var normalizedMetadata = await NormalizeMetadataAsync(metadata, filePath, cancellationToken);

                    normalizeSuccess = await UpdateMetadataCoreAsync(filePath, normalizedMetadata, cancellationToken);
                    await _fileStore.MarkFileNormalizedAsync(filePath, normalizeSuccess, cancellationToken);
                    if (normalizeSuccess)
                    {
                        var filename = Path.GetFileName(filePath);
                        await LogHistoryWithChangesAsync(filePath, "Normalize", true, null,
                            filename, filename, beforeMetadata, normalizedMetadata, cancellationToken);

                        // Use the normalized metadata for the rename phase so the new filename
                        // reflects the resolved series name and any populated issue/title.
                        metadata = normalizedMetadata;
                    }
                    else
                    {
                        _logger.LogWarning("Failed to normalize metadata for: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                        await LogHistoryAsync(filePath, "Normalize", false, "Failed to update metadata", cancellationToken);
                    }
                }
            }
            else if (!_settings.WatcherEnableNormalize)
            {
                // Normalize disabled in settings
                _logger.LogDebug("Normalize disabled in settings: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                await _fileStore.MarkFileNormalizedAsync(filePath, true, cancellationToken); // Mark as "normalized" (skipped)
                await LogHistoryAsync(filePath, "Normalize", true, "Normalize disabled", cancellationToken);
                normalizeSuccess = true;
            }
            else
            {
                await _fileStore.MarkFileNormalizedAsync(filePath, false, cancellationToken);
            }

            _logger.LogDebug("ProcessFileAsync: Starting rename phase - WatcherEnableRename: {RenameEnabled}, Has Metadata: {HasMetadata}, Has Series: {HasSeries}",
                _settings.WatcherEnableRename, metadata != null, metadata?.Series != null);

            // Rename file based on template if metadata is available and rename is enabled
            if (_settings.WatcherEnableRename && metadata != null && !string.IsNullOrEmpty(metadata.Series))
            {
                // If only the series tag is set on the file, recover an explicit
                // "Ch/Chapter <n>" chapter number from the original filename into
                // the metadata's Issue field (and set Title="Chapter <n>") before
                // generating the new filename. This guarantees that when rename
                // preserves the chapter number in the filename, it also preserves
                // it in the file's metadata.
                await EnsureChapterMetadataPreservedAsync(filePath, metadata, cancellationToken);

                _logger.LogDebug("ProcessFileAsync: Attempting to rename file: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                var newFilePath = GenerateFileName(metadata, filePath);
                if (newFilePath == filePath)
                {
                    // File already has correct name, no rename needed
                    _logger.LogDebug("File already has correct name: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                    await _fileStore.MarkFileRenamedAsync(filePath, true, cancellationToken);
                    var filename = Path.GetFileName(filePath);
                    await LogHistoryWithChangesAsync(filePath, "Rename", true, "File already correctly named", 
                        filename, filename, metadata, metadata, cancellationToken);
                    renameSuccess = true;
                }
                else if (TargetFileExists(newFilePath, filePath))
                {
                    // Target file already exists - treat as duplicate
                    _logger.LogInformation("ProcessFileAsync: Duplicate detected - target file already exists: {NewPath}", LoggingHelper.SanitizePathForLog(newFilePath));
                    await _fileStore.MarkFileRenamedAsync(filePath, false, cancellationToken);
                    await _fileStore.MarkFileDuplicateAsync(filePath, true, cancellationToken);
                    await LogHistoryAsync(filePath, "Duplicate Detection", true, "Target file already exists", cancellationToken);
                    renameSuccess = false;
                }
                else
                {
                    try
                    {
                        _logger.LogInformation(
                            "Renaming file from {OldPath} to {NewPath}",
                            LoggingHelper.SanitizePathForLog(filePath),
                            LoggingHelper.SanitizePathForLog(newFilePath));
                        var oldFilePath = filePath;
                        var beforeFilename = Path.GetFileName(oldFilePath);
                        var afterFilename = Path.GetFileName(newFilePath);
                        
                        File.Move(filePath, newFilePath);
                        
                        // Atomically move the database record to the new path, preserving all
                        // processing state so the FileSystemWatcher rename event sees the entry
                        // as already tracked and does not re-queue it for processing.
                        await _fileStore.UpdateFilePathAsync(oldFilePath, newFilePath, cancellationToken);
                        await _fileStore.MarkFileRenamedAsync(newFilePath, true, cancellationToken);

                        // The normalize phase above marked the (old-path) row normalized. Re-assert
                        // that on the new path so the move can't drop it if the watcher removed the
                        // old row before UpdateFilePathAsync migrated it (Delete+Create race),
                        // which would otherwise leave the file showing as unprocessed.
                        if (normalizeSuccess)
                        {
                            await _fileStore.MarkFileNormalizedAsync(newFilePath, true, cancellationToken);
                        }
                        
                        await LogHistoryWithChangesAsync(newFilePath, "Rename", true, null, 
                            beforeFilename, afterFilename, metadata, metadata, cancellationToken);
                        
                        filePath = newFilePath;
                        renameSuccess = true;
                    }
                    catch (IOException ex) when (TargetFileExists(newFilePath, filePath))
                    {
                        // Target file already exists - treat as duplicate
                        _logger.LogInformation(ex, "ProcessFileAsync: Duplicate detected - target file already exists during move: {NewPath}", LoggingHelper.SanitizePathForLog(newFilePath));
                        await _fileStore.MarkFileRenamedAsync(filePath, false, cancellationToken);
                        await _fileStore.MarkFileDuplicateAsync(filePath, true, cancellationToken);
                        await LogHistoryAsync(filePath, "Duplicate Detection", true, "Target file already exists", cancellationToken);
                        renameSuccess = false;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to rename file: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                        await _fileStore.MarkFileRenamedAsync(filePath, false, cancellationToken);
                        await LogHistoryAsync(filePath, "Rename", false, ex.Message, cancellationToken);
                    }
                }
            }
            else
            {
                // No metadata or series info available, or rename disabled
                if (!_settings.WatcherEnableRename)
                {
                    _logger.LogDebug("Rename disabled in settings: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                    await _fileStore.MarkFileRenamedAsync(filePath, true, cancellationToken); // Mark as "renamed" (skipped)
                    await LogHistoryAsync(filePath, "Rename", true, "Rename disabled", cancellationToken);
                    renameSuccess = true;
                }
                else
                {
                    _logger.LogDebug("No metadata or series information for rename: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                    await _fileStore.MarkFileRenamedAsync(filePath, false, cancellationToken);
                    await LogHistoryAsync(filePath, "Rename", false, "No metadata or series information", cancellationToken);
                }
            }

            // After a successful normalize (or "already normalized" detection),
            // stamp the file with the current series-metadata-cache version so
            // the library-scan job's stale-retag pass knows this file reflects
            // the latest cache record. Skipped when normalize was disabled in
            // settings (we didn't actually apply any cache state).
            if (normalizeSuccess && metadata != null && _settings.WatcherEnableNormalize)
            {
                await StampSeriesMetadataVersionAsync(filePath, metadata.Series, cancellationToken);
            }

            // IsProcessed is automatically computed in MarkFileRenamedAsync and MarkFileNormalizedAsync
            // It will only be true when both rename and normalize are successful
            var isFullyProcessed = renameSuccess && normalizeSuccess;
            _logger.LogInformation("ProcessFileAsync: File processing completed: {FilePath} (Renamed: {Renamed}, Normalized: {Normalized}, Fully Processed: {FullyProcessed})", 
                LoggingHelper.SanitizePathForLog(filePath), renameSuccess, normalizeSuccess, isFullyProcessed);

            await LogHistoryAsync(filePath, "Process", isFullyProcessed, 
                isFullyProcessed ? null : "File not fully processed (rename or normalize incomplete)", 
                cancellationToken);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ProcessFileAsync: Error processing file: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            await LogHistoryAsync(filePath, "Process", false, ex.Message, cancellationToken);
            return false;
        }
    }

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
                    if (File.Exists(filePath))
                    {
                        // Remove from file store first (unlikely to fail), then
                        // delete the physical file. This order prevents orphaned
                        // file-store entries if the disk delete fails.
                        await _fileStore.RemoveFileAsync(filePath, token);
                        File.Delete(filePath);
                        await LogHistoryAsync(filePath, "Delete", true, null, token);
                        return true;
                    }

                    // File already missing on disk — still scrub the store entry
                    // so the library list reflects reality.
                    try
                    {
                        await _fileStore.RemoveFileAsync(filePath, token);
                    }
                    catch (Exception removeEx)
                    {
                        _logger.LogDebug(removeEx,
                            "DeleteFilesAsync: file-store cleanup failed for missing file {FilePath}",
                            LoggingHelper.SanitizePathForLog(filePath));
                    }
                    await LogHistoryAsync(filePath, "Delete", false, "File not found on disk", token);
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

        // Process files asynchronously using LongRunning for potentially long batch operations
        _ = Task.Factory.StartNew(async () =>
        {
            try
            {
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
                _jobCancellationTokens.TryRemove(jobId, out _);
                jobCts.Dispose();
            }
        }, jobCts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

        return Task.FromResult(jobId);
    }

    private async Task<bool> RenameFileAsync(string filePath, CancellationToken cancellationToken)
    {
        await _processingSemaphore.WaitAsync(cancellationToken);
        try
        {
            return await RenameFileCoreAsync(filePath, false, cancellationToken);
        }
        finally
        {
            _processingSemaphore.Release();
        }
    }

    private async Task<bool> RenameFileCoreAsync(string filePath, bool forceReprocess, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("RenameFileAsync: Starting rename for file: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));

            // Skip files that are already marked as renamed in the database.
            if (!forceReprocess && await _fileStore.IsFileRenamedAsync(filePath, cancellationToken))
            {
                _logger.LogInformation("RenameFileAsync: File already renamed (database), skipping: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                return true;
            }

            _logger.LogDebug("RenameFileAsync: Checking if file exists and is a comic archive: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));

            if (!File.Exists(filePath) || !IsComicArchive(filePath))
            {
                _logger.LogWarning("RenameFileAsync: File not found or not a comic archive: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                await LogHistoryAsync(filePath, "Rename", false, "File not found or not a comic archive", cancellationToken);
                return false;
            }

            _logger.LogDebug("RenameFileAsync: Extracting metadata from archive: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            
            // Extract metadata from the archive
            var metadata = await GetMetadataAsync(filePath, cancellationToken);
            
            if (metadata != null)
            {
                _logger.LogDebug("RenameFileAsync: Metadata extracted - Series: {Series}, Issue: {Issue}", 
                    LoggingHelper.SanitizeForLog(metadata.Series),
                    LoggingHelper.SanitizeForLog(metadata.Issue));
            }
            else
            {
                _logger.LogDebug("RenameFileAsync: No metadata extracted from file: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            }
            
            // Rename file based on template if metadata is available
            if (metadata != null && !string.IsNullOrEmpty(metadata.Series))
            {
                // If the metadata has only the series tag set (no Issue/Number),
                // but the original filename contains an explicit "Ch/Chapter <n>"
                // token, recover that chapter number into the metadata so the
                // rename preserves it not only in the filename but also in
                // ComicInfo.xml.
                await EnsureChapterMetadataPreservedAsync(filePath, metadata, cancellationToken);

                // Resolve the normalized series name before generating the new
                // filename so a standalone rename produces the exact same target
                // name as the full process pipeline (ProcessFileCoreAsync), where
                // the normalize phase resolves the series first and the rename
                // phase then renders the filename from that resolved name. Without
                // this, renaming via the rename endpoint / file-naming audit would
                // use the raw <Series> from ComicInfo.xml and could yield a
                // different filename than the watcher/process path for the same
                // file. The resolved name is applied in-memory for filename
                // generation only; rewriting ComicInfo.xml remains the normalize
                // operation's responsibility.
                var resolvedSeries = await ResolveNormalizedSeriesAsync(metadata, filePath, cancellationToken);
                if (!string.IsNullOrEmpty(resolvedSeries) && metadata.Series != resolvedSeries)
                {
                    _logger.LogDebug(
                        "RenameFileAsync: Using resolved series name for filename: {SeriesName}",
                        LoggingHelper.SanitizeForLog(resolvedSeries));
                    metadata.Series = resolvedSeries;
                }

                _logger.LogDebug("RenameFileAsync: Generating new filename for file: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                var newFilePath = GenerateFileName(metadata, filePath);
                _logger.LogDebug("RenameFileAsync: Generated filename: {NewFilePath} (Original: {OriginalFilePath})", 
                    LoggingHelper.SanitizePathForLog(newFilePath), LoggingHelper.SanitizePathForLog(filePath));
                
                if (newFilePath == filePath)
                {
                    _logger.LogInformation("RenameFileAsync: File already has correct name: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                    await _fileStore.MarkFileRenamedAsync(filePath, true, cancellationToken);
                    var filename = Path.GetFileName(filePath);
                    await LogHistoryWithChangesAsync(filePath, "Rename", true, "File already correctly named",
                        filename, filename, metadata, metadata, cancellationToken);
                    return true;
                }
                
                try
                {
                    _logger.LogInformation(
                        "Renaming file from {OldPath} to {NewPath}",
                        LoggingHelper.SanitizePathForLog(filePath),
                        LoggingHelper.SanitizePathForLog(newFilePath));
                    
                    // Capture before/after filenames for history
                    var beforeFilename = Path.GetFileName(filePath);
                    var afterFilename = Path.GetFileName(newFilePath);

                    // Capture the old path's normalized state *before* the move so it can be
                    // re-asserted on the new path afterwards. UpdateFilePathAsync normally
                    // carries this state forward, but the FileSystemWatcher can observe the
                    // move as a Delete(old)+Create(new) pair and remove the authoritative
                    // old-path row before the processor migrates it. When that happens the
                    // merge in UpdateFilePathAsync has nothing to merge from and the new row
                    // keeps the watcher stub's default IsNormalized=false, so a
                    // normalize-then-rename sequence would leave the file "unprocessed" until
                    // it is normalized again. Re-stamping the captured state closes that gap.
                    var wasNormalized = await _fileStore.IsFileNormalizedAsync(filePath, cancellationToken);

                    // File.Move will throw IOException if target exists, which we catch and handle
                    // This avoids race condition from check-then-act pattern
                    File.Move(filePath, newFilePath);
                    
                    // Atomically remap the file-store row from the old path to the new path,
                    // preserving all processing state (IsRenamed/IsNormalized/SeriesMetadataVersion/
                    // IsDuplicate/IsRead/...) and avoiding the race with FileWatcherService's
                    // OnFileRenamed handler, which also calls UpdateFilePathAsync. Using
                    // RemoveFileAsync+AddFileAsync here previously left the old path tracked
                    // when the watcher's update interleaved with the processor's removal —
                    // the file then only disappeared from the file list on the next full
                    // library scan (reconcile-deletions pass). This matches the pattern
                    // already used by ProcessFileCoreAsync.
                    await _fileStore.UpdateFilePathAsync(filePath, newFilePath, cancellationToken);
                    await _fileStore.MarkFileRenamedAsync(newFilePath, true, cancellationToken);

                    // Re-assert the captured normalized state on the new path so it survives
                    // even if the watcher removed the old row before UpdateFilePathAsync ran.
                    if (wasNormalized)
                    {
                        await _fileStore.MarkFileNormalizedAsync(newFilePath, true, cancellationToken);
                    }
                    
                    _logger.LogInformation("File renamed successfully: {NewPath}", LoggingHelper.SanitizePathForLog(newFilePath));
                    await LogHistoryWithChangesAsync(newFilePath, "Rename", true, null, 
                        beforeFilename, afterFilename, metadata, metadata, cancellationToken);
                    return true;
                }
                catch (IOException ex) when (TargetFileExists(newFilePath, filePath))
                {
                    // Target file already exists - treat as duplicate
                    _logger.LogInformation(ex, "RenameFileAsync: Duplicate detected - target file already exists: {NewPath}", LoggingHelper.SanitizePathForLog(newFilePath));
                    await _fileStore.MarkFileRenamedAsync(filePath, false, cancellationToken);
                    await _fileStore.MarkFileDuplicateAsync(filePath, true, cancellationToken);
                    await LogHistoryAsync(filePath, "Duplicate Detection", true, "Target file already exists", cancellationToken);
                    return false;
                }
            }
            else
            {
                _logger.LogWarning("RenameFileAsync: No metadata or series information found for: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                await _fileStore.MarkFileRenamedAsync(filePath, false, cancellationToken);
                await LogHistoryAsync(filePath, "Rename", false, "No metadata or series information found", cancellationToken);
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RenameFileAsync: Error renaming file: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            await _fileStore.MarkFileRenamedAsync(filePath, false, cancellationToken);
            await LogHistoryAsync(filePath, "Rename", false, ex.Message, cancellationToken);
            return false;
        }
    }

    private async Task<bool> NormalizeFileAsync(string filePath, CancellationToken cancellationToken)
    {
        await _processingSemaphore.WaitAsync(cancellationToken);
        try
        {
            return await NormalizeFileCoreAsync(filePath, false, cancellationToken);
        }
        finally
        {
            _processingSemaphore.Release();
        }
    }

    /// <summary>
    /// After a successful normalize, look up the cache record for the
    /// resolved series and stamp the file with the record's current
    /// <c>MetadataVersion</c>. This is the read-side of the
    /// metadata-version invalidation scheme: a future library-scan run
    /// compares this stamp to the latest record version to decide whether
    /// the file needs another normalize. Best-effort; cache lookup failure
    /// must not fail the normalize itself.
    /// </summary>
    private async Task StampSeriesMetadataVersionAsync(string filePath, string? resolvedSeries, CancellationToken cancellationToken)
    {
        if (_seriesMetadataCache is null || string.IsNullOrWhiteSpace(resolvedSeries)) return;
        try
        {
            var key = _seriesMetadataCache.NormalizeKey(resolvedSeries);
            if (string.IsNullOrWhiteSpace(key)) return;
            var record = await _seriesMetadataCache.GetAsync(key, cancellationToken);
            if (record is null) return;
            await _fileStore.SetFileSeriesMetadataVersionAsync(filePath, record.MetadataVersion, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to stamp series-metadata version on {FilePath}",
                LoggingHelper.SanitizePathForLog(filePath));
        }
    }

    private async Task<bool> NormalizeFileCoreAsync(string filePath, bool forceReprocess, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("NormalizeFileAsync: Starting normalize for file: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));

            // Skip files that are already marked as normalized in the database.
            if (!forceReprocess && await _fileStore.IsFileNormalizedAsync(filePath, cancellationToken))
            {
                _logger.LogInformation("NormalizeFileAsync: File already normalized (database), skipping: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                return true;
            }

            _logger.LogDebug("NormalizeFileAsync: Checking if file exists and is a comic archive: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));

            if (!File.Exists(filePath) || !IsComicArchive(filePath))
            {
                _logger.LogWarning("NormalizeFileAsync: File not found or not a comic archive: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                await _fileStore.MarkFileNormalizedAsync(filePath, false, cancellationToken);
                await LogHistoryAsync(filePath, "Normalize", false, "File not found or not a comic archive", cancellationToken);
                return false;
            }

            _logger.LogDebug("NormalizeFileAsync: Extracting metadata from archive: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            
            // Extract metadata from the archive
            var metadata = await GetMetadataAsync(filePath, cancellationToken);
            
            if (metadata == null)
            {
                _logger.LogWarning("NormalizeFileAsync: No metadata found for: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                await _fileStore.MarkFileNormalizedAsync(filePath, false, cancellationToken);
                await LogHistoryAsync(filePath, "Normalize", false, "No metadata found", cancellationToken);
                return false;
            }

            _logger.LogDebug("NormalizeFileAsync: Metadata extracted - Series: {Series}, Issue: {Issue}, Title: {Title}", 
                LoggingHelper.SanitizeForLog(metadata.Series),
                LoggingHelper.SanitizeForLog(metadata.Issue),
                LoggingHelper.SanitizeForLog(metadata.Title));

            // Check if metadata is already normalized (has ComicInfo.xml with valid data and series name matches folder)
            // If we successfully read metadata, it means ComicInfo.xml exists and is parseable
            var isNormalized = await IsMetadataNormalizedAsync(metadata, filePath, cancellationToken);
            _logger.LogDebug("NormalizeFileAsync: Metadata normalization check - IsNormalized: {IsNormalized}", isNormalized);
            
            if (isNormalized)
            {
                _logger.LogInformation("NormalizeFileAsync: File already has normalized metadata: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                await _fileStore.MarkFileNormalizedAsync(filePath, true, cancellationToken);
                await StampSeriesMetadataVersionAsync(filePath, metadata.Series, cancellationToken);
                var filename = Path.GetFileName(filePath);
                await LogHistoryWithChangesAsync(filePath, "Normalize", true, "File already normalized",
                    filename, filename, metadata, metadata, cancellationToken);
                return true;
            }
            
            _logger.LogDebug("NormalizeFileAsync: File needs normalization, updating metadata: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));

            // Capture before metadata state (current metadata)
            var beforeMetadata = metadata.Clone();
            var normalizedMetadata = await NormalizeMetadataAsync(metadata, filePath, cancellationToken);

            // Update metadata (normalize it by re-writing ComicInfo.xml)
            var success = await UpdateMetadataCoreAsync(filePath, normalizedMetadata, cancellationToken);
            await _fileStore.MarkFileNormalizedAsync(filePath, success, cancellationToken);
            
            if (success)
            {
                await StampSeriesMetadataVersionAsync(filePath, normalizedMetadata.Series, cancellationToken);
                _logger.LogInformation("NormalizeFileAsync: File normalized successfully: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                var filename = Path.GetFileName(filePath);
                // For normalize operations, we log the same metadata as before/after since we're ensuring
                // the existing metadata is written properly to ComicInfo.xml
                await LogHistoryWithChangesAsync(filePath, "Normalize", true, null, 
                    filename, filename, beforeMetadata, normalizedMetadata, cancellationToken);
            }
            else
            {
                _logger.LogWarning("NormalizeFileAsync: Failed to normalize file: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                await LogHistoryAsync(filePath, "Normalize", false, "Failed to update metadata", cancellationToken);
            }
            
            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NormalizeFileAsync: Error normalizing file: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            await _fileStore.MarkFileNormalizedAsync(filePath, false, cancellationToken);
            await LogHistoryAsync(filePath, "Normalize", false, ex.Message, cancellationToken);
            return false;
        }
    }

    private async Task BroadcastJobStatusAsync(ProcessingJob job)
    {
        if (_eventBroadcaster != null)
        {
            var snapshot = CloneJob(job);
            await _eventBroadcaster.BroadcastJobUpdateAsync(
                snapshot.JobId,
                snapshot.Status.ToString().ToLower(),
                snapshot.ProcessedFiles + snapshot.FailedFiles,
                snapshot.TotalFiles,
                snapshot.ProcessedFiles,
                snapshot.FailedFiles);
        }
    }

    public ProcessingJob? GetJob(Guid jobId)
    {
        return _jobs.TryGetValue(jobId, out var job) ? CloneJob(job) : null;
    }

    public ProcessingJob? GetActiveJob()
    {
        return _jobs.Values
            .Select(CloneJob)
            .FirstOrDefault(j => j.Status == JobStatus.Running || j.Status == JobStatus.Queued);
    }

    public IEnumerable<ProcessingJob> GetAllJobs()
    {
        return _jobs.Values
            .Select(CloneJob)
            .OrderByDescending(j => j.StartTime);
    }

    public bool DeleteJob(Guid jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var job))
        {
            return false;
        }

        var snapshot = CloneJob(job);
        if (snapshot.Status == JobStatus.Running || snapshot.Status == JobStatus.Queued)
        {
            _logger.LogWarning("Cannot delete active job: {JobId}", jobId);
            return false;
        }

        return _jobs.TryRemove(jobId, out _);
    }

    public bool CancelJob(Guid jobId)
    {
        if (_jobCancellationTokens.TryGetValue(jobId, out var cts))
        {
            _logger.LogInformation("Cancelling job: {JobId}", jobId);
            cts.Cancel();
            return true;
        }
        
        _logger.LogWarning("Cannot cancel job {JobId}: no active cancellation token found", jobId);
        return false;
    }

    private ProcessingJob CloneJob(ProcessingJob job)
    {
        var syncLock = GetJobSyncLock(job.JobId);
        lock (syncLock)
        {
            return new ProcessingJob
            {
                JobId = job.JobId,
                Status = job.Status,
                Files = new List<string>(job.Files),
                TotalFiles = job.TotalFiles,
                ProcessedFiles = job.ProcessedFiles,
                FailedFiles = job.FailedFiles,
                StartTime = job.StartTime,
                EndTime = job.EndTime,
                CurrentFile = job.CurrentFile,
                Errors = new Dictionary<string, string>(job.Errors)
            };
        }
    }

    private object GetJobSyncLock(Guid jobId)
    {
        return _jobSyncLocks.GetOrAdd(jobId, static _ => new object());
    }

    public Task<ComicMetadata?> GetMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(filePath) || !IsComicArchive(filePath))
                return Task.FromResult<ComicMetadata?>(null);

            using var archive = ArchiveFactory.Open(filePath);
            
            // Look for ComicInfo.xml
            var comicInfoEntry = archive.Entries.FirstOrDefault(e => 
                e.Key?.Equals("ComicInfo.xml", StringComparison.OrdinalIgnoreCase) == true);

            if (comicInfoEntry != null)
            {
                var xmlContent = ReadComicInfoXml(comicInfoEntry);
                return Task.FromResult(ParseComicInfoXml(xmlContent));
            }

            // Fallback: parse from filename
            return Task.FromResult<ComicMetadata?>(new ComicMetadata
            {
                Series = ExtractSeriesFromFilename(filePath),
                Issue = ParseIssueNumber(Path.GetFileNameWithoutExtension(filePath))
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading metadata from {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            return Task.FromResult<ComicMetadata?>(null);
        }
    }

    public Task<SeriesMetadata?> GetSeriesMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(filePath) || !IsComicArchive(filePath))
                return Task.FromResult<SeriesMetadata?>(null);

            using var archive = ArchiveFactory.Open(filePath);
            var comicInfoEntry = archive.Entries.FirstOrDefault(e =>
                e.Key?.Equals("ComicInfo.xml", StringComparison.OrdinalIgnoreCase) == true);

            if (comicInfoEntry != null)
            {
                var xmlContent = ReadComicInfoXml(comicInfoEntry);
                return Task.FromResult(ParseSeriesMetadataXml(xmlContent));
            }

            return Task.FromResult<SeriesMetadata?>(new SeriesMetadata
            {
                Series = ExtractSeriesFromFilename(filePath),
                Issue = ParseIssueNumber(Path.GetFileNameWithoutExtension(filePath)),
                SeriesGroup = Path.GetFileName(Path.GetDirectoryName(filePath))
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading series metadata from {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            return Task.FromResult<SeriesMetadata?>(null);
        }
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            foreach (var cancellationTokenSource in _jobCancellationTokens.Values)
            {
                cancellationTokenSource.Cancel();
            }

            _processingSemaphore.Dispose();
        }

        _disposed = true;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    public async Task<bool> UpdateMetadataAsync(string filePath, ComicMetadata metadata, CancellationToken cancellationToken = default)
    {
        await _processingSemaphore.WaitAsync(cancellationToken);
        try
        {
            return await UpdateMetadataCoreAsync(filePath, metadata, cancellationToken);
        }
        finally
        {
            _processingSemaphore.Release();
        }
    }

    private async Task<bool> UpdateMetadataCoreAsync(string filePath, ComicMetadata metadata, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                _logger.LogWarning(
                    "Cannot update metadata: file does not exist: {FilePath}",
                    LoggingHelper.SanitizePathForLog(filePath));
                return false;
            }

            if (!IsComicArchive(filePath))
            {
                _logger.LogWarning(
                    "Cannot update metadata: file is not a supported comic archive: {FilePath}",
                    LoggingHelper.SanitizePathForLog(filePath));
                return false;
            }

            _logger.LogInformation("Updating metadata for: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));

            // Create ComicInfo.xml content
            var comicInfoXml = GenerateComicInfoXml(metadata);
            
            // Create temporary file in the configured temp directory
            // Ensure the temp directory exists
            if (!Directory.Exists(_settings.TempFileDirectory))
            {
                Directory.CreateDirectory(_settings.TempFileDirectory);
                _logger.LogInformation("Created temp directory: {TempDir}", LoggingHelper.SanitizePathForLog(_settings.TempFileDirectory));
            }
            
            var tempFile = Path.Combine(_settings.TempFileDirectory, $".tmp_{Guid.NewGuid()}.cbz");
            
            try
            {
                // Create new archive with updated metadata in a separate scope
                // to ensure all file handles are released before file replacement
                {
                    using var sourceArchive = ArchiveFactory.Open(filePath);
                    using var writer = ZipArchive.Create();
                    
                    // Add all existing entries except ComicInfo.xml
                    foreach (var entry in sourceArchive.Entries.Where(e => !e.IsDirectory))
                    {
                        if (entry.Key?.Equals("ComicInfo.xml", StringComparison.OrdinalIgnoreCase) != true && entry.Key != null)
                        {
                            using var stream = entry.OpenEntryStream();
                            // Use a buffer to avoid loading entire files into memory
                            const int maxBufferSize = 10 * 1024 * 1024; // 10MB limit per entry
                            if (entry.Size > maxBufferSize)
                            {
                                _logger.LogWarning(
                                    "Skipping large entry {Entry} ({Size} bytes) to prevent memory issues",
                                    LoggingHelper.SanitizeForLog(entry.Key),
                                    entry.Size);
                                continue;
                            }
                            var memStream = new MemoryStream();
                            stream.CopyTo(memStream);
                            memStream.Position = 0;
                            writer.AddEntry(entry.Key, memStream, true, entry.Size, entry.LastModifiedTime);
                        }
                    }
                    
                    // Add new ComicInfo.xml
                    var xmlBytes = System.Text.Encoding.UTF8.GetBytes(comicInfoXml);
                    var xmlStream = new MemoryStream(xmlBytes);
                    writer.AddEntry("ComicInfo.xml", xmlStream, true);
                    
                    // Save to temp file
                    writer.SaveTo(tempFile, new WriterOptions(CompressionType.Deflate));
                }
                
                // Give a brief moment for any file handles to be fully released
                await Task.Delay(FileHandleReleaseDelayMs, cancellationToken);
                
                // Replace original file with updated one using retry logic
                // to handle transient file locks from file system watchers or antivirus
                await ReplaceFileWithRetryAsync(tempFile, filePath, cancellationToken);
                
                _logger.LogInformation("Successfully updated metadata for: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                return true;
            }
            finally
            {
                // Clean up temp file if it still exists
                if (File.Exists(tempFile))
                {
                    try
                    {
                        File.Delete(tempFile);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete temp file: {TempFile}", LoggingHelper.SanitizePathForLog(tempFile));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating metadata for {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            return false;
        }
    }

    public async Task<bool> RemoveMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        await _processingSemaphore.WaitAsync(cancellationToken);
        try
        {
            return await RemoveMetadataCoreAsync(filePath, cancellationToken);
        }
        finally
        {
            _processingSemaphore.Release();
        }
    }

    private async Task<bool> RemoveMetadataCoreAsync(string filePath, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                _logger.LogWarning(
                    "Cannot remove metadata: file does not exist: {FilePath}",
                    LoggingHelper.SanitizePathForLog(filePath));
                return false;
            }

            if (!IsComicArchive(filePath))
            {
                _logger.LogWarning(
                    "Cannot remove metadata: file is not a supported comic archive: {FilePath}",
                    LoggingHelper.SanitizePathForLog(filePath));
                return false;
            }

            _logger.LogInformation("Removing metadata for: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));

            // Ensure the temp directory exists
            if (!Directory.Exists(_settings.TempFileDirectory))
            {
                Directory.CreateDirectory(_settings.TempFileDirectory);
                _logger.LogInformation("Created temp directory: {TempDir}", LoggingHelper.SanitizePathForLog(_settings.TempFileDirectory));
            }

            var tempFile = Path.Combine(_settings.TempFileDirectory, $".tmp_{Guid.NewGuid()}.cbz");

            try
            {
                // Rewrite the archive without ComicInfo.xml in a separate scope so all
                // file handles are released before File.Replace.
                {
                    using var sourceArchive = ArchiveFactory.Open(filePath);
                    using var writer = ZipArchive.Create();

                    foreach (var entry in sourceArchive.Entries.Where(e => !e.IsDirectory))
                    {
                        if (entry.Key == null)
                        {
                            continue;
                        }

                        // Skip ComicInfo.xml — that is the entry we are removing.
                        if (entry.Key.Equals("ComicInfo.xml", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        using var stream = entry.OpenEntryStream();
                        const int maxBufferSize = 10 * 1024 * 1024; // 10MB limit per entry
                        if (entry.Size > maxBufferSize)
                        {
                            _logger.LogWarning(
                                "Skipping large entry {Entry} ({Size} bytes) to prevent memory issues",
                                LoggingHelper.SanitizeForLog(entry.Key),
                                entry.Size);
                            continue;
                        }
                        var memStream = new MemoryStream();
                        stream.CopyTo(memStream);
                        memStream.Position = 0;
                        writer.AddEntry(entry.Key, memStream, true, entry.Size, entry.LastModifiedTime);
                    }

                    writer.SaveTo(tempFile, new WriterOptions(CompressionType.Deflate));
                }

                await Task.Delay(FileHandleReleaseDelayMs, cancellationToken);

                await ReplaceFileWithRetryAsync(tempFile, filePath, cancellationToken);

                _logger.LogInformation("Successfully removed metadata for: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                return true;
            }
            finally
            {
                if (File.Exists(tempFile))
                {
                    try
                    {
                        File.Delete(tempFile);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete temp file: {TempFile}", LoggingHelper.SanitizePathForLog(tempFile));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing metadata for {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            return false;
        }
    }
    
    /// <summary>
    /// Delay in milliseconds to allow file handles to be fully released after archive disposal
    /// </summary>
    private const int FileHandleReleaseDelayMs = 100;
    
    /// <summary>
    /// Maximum number of retry attempts for file replacement operations
    /// </summary>
    private const int MaxFileReplaceRetries = 5;
    
    /// <summary>
    /// Initial delay in milliseconds for exponential backoff retry logic
    /// </summary>
    private const int RetryInitialDelayMs = 100;
    
    /// <summary>
    /// Delay in milliseconds before attempting to restore from backup
    /// </summary>
    private const int BackupRestoreDelayMs = 200;

    private async Task ReplaceFileWithRetryAsync(string tempFile, string targetFile, CancellationToken cancellationToken)
    {
        var backupPath = $"{targetFile}.backup";
        Exception? lastException = null;
        
        // First, try the atomic File.Replace operation
        try
        {
            File.Replace(tempFile, targetFile, backupPath);
            
            // Clean up backup if replace succeeded
            if (File.Exists(backupPath))
            {
                File.Delete(backupPath);
            }
            
            return; // Success
        }
        catch (IOException ex) when (IsCrossDeviceLinkError(ex))
        {
            // Cross-device link error - use fallback strategy
            // This is deterministic and won't be resolved by retrying
            _logger.LogWarning("Cross-device link detected, using fallback copy strategy for {File}", LoggingHelper.SanitizePathForLog(targetFile));
            
            try
            {
                // Clean up any existing backup file first to ensure we have a clean state
                if (File.Exists(backupPath))
                {
                    File.Delete(backupPath);
                }
                
                // Create backup by copying the target file
                if (File.Exists(targetFile))
                {
                    File.Copy(targetFile, backupPath, overwrite: true);
                }
                
                // Copy temp file to target
                File.Copy(tempFile, targetFile, overwrite: true);
                
                // Delete temp file
                File.Delete(tempFile);
                
                // Clean up backup
                if (File.Exists(backupPath))
                {
                    File.Delete(backupPath);
                }
                
                _logger.LogInformation("Successfully replaced file using fallback strategy: {File}", LoggingHelper.SanitizePathForLog(targetFile));
                return; // Success
            }
            catch (Exception fallbackEx)
            {
                _logger.LogError(fallbackEx, "Fallback strategy failed for {File}, attempting restore from backup", LoggingHelper.SanitizePathForLog(targetFile));
                await TryRestoreBackupAsync(backupPath, targetFile, cancellationToken);
                throw;
            }
        }
        catch (IOException ex)
        {
            lastException = ex;
        }
        catch (Exception ex)
        {
            // For non-IOException, try to restore from backup if it exists
            _logger.LogError(ex, "File replace failed for {File}, attempting restore from backup", LoggingHelper.SanitizePathForLog(targetFile));
            await TryRestoreBackupAsync(backupPath, targetFile, cancellationToken);
            throw;
        }
        
        // If we got here, we have an IOException (but not cross-device) - retry with backoff
        for (int attempt = 0; attempt < MaxFileReplaceRetries; attempt++)
        {
            try
            {
                await DelayWithExponentialBackoffAsync(attempt, lastException, targetFile, cancellationToken);
                
                // Attempt to replace the file
                File.Replace(tempFile, targetFile, backupPath);
                
                // Clean up backup if replace succeeded
                if (File.Exists(backupPath))
                {
                    File.Delete(backupPath);
                }
                
                return; // Success
            }
            catch (IOException ex) when (attempt < MaxFileReplaceRetries - 1)
            {
                lastException = ex;
            }
            catch (Exception ex)
            {
                // For non-IOException or final attempt, try to restore from backup if it exists
                _logger.LogError(ex, "File replace failed for {File}, attempting restore from backup", LoggingHelper.SanitizePathForLog(targetFile));
                await TryRestoreBackupAsync(backupPath, targetFile, cancellationToken);
                throw;
            }
        }
        
        // If we exhausted all retries, throw the last exception
        if (lastException != null)
        {
            _logger.LogError(lastException, "Failed to replace file after {MaxRetries} attempts: {File}", MaxFileReplaceRetries, LoggingHelper.SanitizePathForLog(targetFile));
            throw lastException;
        }
    }

    /// <summary>
    /// Attempts to restore a backup file with proper error handling
    /// </summary>
    private async Task TryRestoreBackupAsync(string backupPath, string targetFile, CancellationToken cancellationToken)
    {
        if (!File.Exists(backupPath))
            return;
            
        try
        {
            // Wait a moment before attempting restore
            await Task.Delay(BackupRestoreDelayMs, cancellationToken);
            
            // Use Move instead of Copy for restore as it's more reliable
            if (File.Exists(targetFile))
            {
                File.Delete(targetFile);
            }
            File.Move(backupPath, targetFile);
            
            _logger.LogWarning("Restored backup file after failed replacement: {File}", LoggingHelper.SanitizePathForLog(targetFile));
        }
        catch (Exception restoreEx)
        {
            _logger.LogError(restoreEx, "Failed to restore backup file: {BackupPath}", LoggingHelper.SanitizePathForLog(backupPath));
        }
    }

    /// <summary>
    /// Delays execution with exponential backoff for retry logic
    /// </summary>
    private async Task DelayWithExponentialBackoffAsync(int attempt, Exception? exception, string targetFile, CancellationToken cancellationToken)
    {
        _logger.LogWarning(exception, "File replace attempt {Attempt} failed for {File}, retrying...", attempt + 1, LoggingHelper.SanitizePathForLog(targetFile));
        
        // Exponential backoff: 100ms, 200ms, 400ms, 800ms
        var delayMs = RetryInitialDelayMs * (int)Math.Pow(2, attempt);
        await Task.Delay(delayMs, cancellationToken);
    }

    /// <summary>
    /// Checks if an IOException is a cross-device link error
    /// </summary>
    private static bool IsCrossDeviceLinkError(IOException ex)
    {
        // EXDEV (errno 18 on Linux/Unix) = Cross-device link
        // On Windows, HResult 0x80070011 = ERROR_NOT_SAME_DEVICE
        const int EXDEV = 18;
        const int ERROR_NOT_SAME_DEVICE = 0x11;
        const int HRESULT_ERROR_CODE_MASK = 0xFFFF;
        
        // Check HResult for Windows (upper 16 bits are facility code, lower 16 bits are error code)
        var hresult = ex.HResult;
        var errorCode = hresult & HRESULT_ERROR_CODE_MASK;
        
        // Check if this is a cross-device error on Windows or Unix
        return errorCode == ERROR_NOT_SAME_DEVICE || 
               errorCode == EXDEV ||
               ex.Message.Contains("cross-device", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ParseIssueNumber(string filename)
    {
        // Simple pattern matching for issue numbers
        var match = Regex.Match(filename, @"(?i)(?:ch|chapter|issue|#)?\s*(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
        // Strip leading zero padding so the per-file metadata issue field holds
        // the bare issue number (e.g. "0012" -> "12"); only the generated
        // filename is zero padded.
        return match.Success ? ComicFileProcessor.StripLeadingZeros(match.Groups[1].Value) : null;
    }

    private static bool IsComicArchive(string filePath)
    {
        return ComicFileExtensions.IsComicArchive(filePath);
    }

    private static string ExtractSeriesFromFilename(string filePath)
    {
        // Extract series name from the parent folder name instead of filename
        var folderName = Path.GetFileName(Path.GetDirectoryName(filePath));
        if (string.IsNullOrEmpty(folderName))
        {
            return UnknownSeries;
        }
        
        // Apply the same normalization as ComicFileProcessor to convert underscores to colons
        var series = ComicFileProcessor.NormalizeSeriesName(folderName, forComparison: false);
        return string.IsNullOrEmpty(series) ? UnknownSeries : series;
    }

    private ComicMetadata? ParseComicInfoXml(string xmlContent)
    {
        try
        {
            var doc = XDocument.Parse(xmlContent);
            var root = doc.Root;
            if (root == null) return null;

            return new ComicMetadata
            {
                Series = root.Element("Series")?.Value,
                Title = root.Element("Title")?.Value,
                Issue = root.Element("Number")?.Value,
                Volume = root.Element("Volume")?.Value,
                Publisher = root.Element("Publisher")?.Value,
                Year = int.TryParse(root.Element("Year")?.Value, out var year) ? year : null,
                Summary = root.Element("Summary")?.Value,
                Authors = root.Elements("Writer").Select(e => e.Value).ToList(),
                Tags = root.Elements("Tag").Select(e => e.Value).ToList()
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing ComicInfo.xml");
            return null;
        }
    }

    private SeriesMetadata? ParseSeriesMetadataXml(string xmlContent)
    {
        try
        {
            var doc = XDocument.Parse(xmlContent);
            var root = doc.Root;
            if (root == null) return null;

            return new SeriesMetadata
            {
                Series = root.Element("Series")?.Value,
                AlternateSeries = root.Element("AlternateSeries")?.Value,
                SeriesGroup = root.Element("SeriesGroup")?.Value,
                Title = root.Element("Title")?.Value,
                Issue = root.Element("Number")?.Value,
                Volume = root.Element("Volume")?.Value,
                Publisher = root.Element("Publisher")?.Value,
                Year = int.TryParse(root.Element("Year")?.Value, out var year) ? year : null
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing series metadata from ComicInfo.xml");
            return null;
        }
    }

    private static string ReadComicInfoXml(IArchiveEntry comicInfoEntry)
    {
        using var stream = comicInfoEntry.OpenEntryStream();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string GenerateComicInfoXml(ComicMetadata metadata)
    {
        var doc = new XDocument(
            new XElement("ComicInfo",
                metadata.Series != null ? new XElement("Series", metadata.Series) : null,
                metadata.Title != null ? new XElement("Title", metadata.Title) : null,
                metadata.Issue != null ? new XElement("Number", metadata.Issue) : null,
                metadata.Volume != null ? new XElement("Volume", metadata.Volume) : null,
                metadata.Publisher != null ? new XElement("Publisher", metadata.Publisher) : null,
                metadata.Year.HasValue ? new XElement("Year", metadata.Year.Value) : null,
                metadata.Summary != null ? new XElement("Summary", metadata.Summary) : null,
                metadata.Authors.Select(a => new XElement("Writer", a)),
                metadata.Tags.Select(t => new XElement("Tag", t))
            )
        );
        return doc.ToString();
    }

    private string GenerateFileName(ComicMetadata metadata, string originalPath)
    {
        try
        {
            var directory = Path.GetDirectoryName(originalPath) ?? _settings.WatchedDirectory;
            var extension = Path.GetExtension(originalPath);
            
            // Convert ComicMetadata to ComicInfo for use with ComicFileProcessor
            var comicInfo = ComicInfo.FromMetadata(metadata);
            
            // Use ComicFileProcessor.FormatFilename to properly handle decimal issue numbers
            var filename = ComicFileProcessor.FormatFilename(
                _settings.FilenameFormat,
                comicInfo,
                metadata.Issue ?? "",
                extension,
                _settings.IssueNumberPadding);
            
            // Remove extension as FormatFilename adds it
            if (filename.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                filename = filename.Substring(0, filename.Length - extension.Length);
            }
            
            // Defensive safety net: ensure the chapter number that exists in
            // the original filename is never lost during rename. This protects
            // against:
            //   * Custom FilenameFormat templates that omit "{issue}" (e.g.
            //     "{series} - {title}").
            //   * Files whose ComicInfo.xml is present but has an empty or
            //     missing <Number>, leaving metadata.Issue null even though
            //     the original filename clearly contains a chapter number.
            // One-shots / volume-only files (no chapter in metadata and no
            // explicit "Ch/Chapter" keyword in the original filename) are
            // intentionally left untouched.
            filename = EnsureChapterNumberPreserved(filename, metadata.Issue, originalPath);

            filename = ComicFileProcessor.SanitizeFileName(filename);
            
            return Path.Combine(directory, filename + extension);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating filename");
            return originalPath;
        }
    }

    /// <summary>
    /// Ensures the chapter number from the original filename (or from
    /// <paramref name="metadataIssue"/>) is present in the new filename
    /// stem. Appends " - Chapter &lt;n&gt;" when it is missing so the
    /// chapter number is never silently dropped during rename.
    /// </summary>
    private string EnsureChapterNumberPreserved(string filenameStem, string? metadataIssue, string originalPath)
    {
        // Preferred chapter source: the authoritative metadata.Issue.
        // Secondary: an explicit "Ch/Chapter <n>" token in the original filename
        // (the strict pattern avoids false-positives on volume-only filenames).
        var originalNameStem = Path.GetFileNameWithoutExtension(originalPath);
        var chapterToPreserve = !string.IsNullOrEmpty(metadataIssue)
            ? metadataIssue
            : ComicFileProcessor.ParseChapterKeyword(originalNameStem);

        if (string.IsNullOrEmpty(chapterToPreserve))
        {
            return filenameStem;
        }

        // Check whether the rendered filename already contains an equivalent
        // chapter number. ParseChapterNumber prefers the chapter keyword and
        // otherwise picks the first non-bracketed number, which mirrors the
        // way the rendered template typically encodes the issue number.
        var renderedChapter = ComicFileProcessor.ParseChapterNumber(filenameStem);
        if (!string.IsNullOrEmpty(renderedChapter) &&
            ComicFileProcessor.ChapterNumbersEquivalent(renderedChapter, chapterToPreserve))
        {
            return filenameStem;
        }

        // Format the chapter number with the configured padding so the
        // appended value is consistent with how the rest of the codebase
        // renders chapter numbers.
        var formattedChapter = FormatChapterForFilename(chapterToPreserve, _settings.IssueNumberPadding);

        _logger.LogWarning(
            "Chapter number '{Chapter}' would be lost from filename during rename of {OriginalPath}; appending it to preserve the chapter.",
            chapterToPreserve,
            LoggingHelper.SanitizePathForLog(originalPath));

        var separator = string.IsNullOrEmpty(filenameStem) ? string.Empty : " - ";
        return $"{filenameStem}{separator}Chapter {formattedChapter}";
    }

    private static string FormatChapterForFilename(string chapter, int padding)
    {
        if (!float.TryParse(chapter, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var asFloat))
        {
            return chapter;
        }

        var integerPart = (int)asFloat;
        var formatString = $"D{Math.Max(0, padding)}";
        var integerFormatted = integerPart.ToString(formatString,
            System.Globalization.CultureInfo.InvariantCulture);

        if (chapter.Contains('.'))
        {
            var parts = chapter.Split('.');
            var decimalPart = parts.Length > 1 ? parts[1].TrimEnd('0') : string.Empty;
            if (!string.IsNullOrEmpty(decimalPart))
            {
                return $"{integerFormatted}.{decimalPart}";
            }
        }

        return integerFormatted;
    }

    private bool TargetFileExists(string targetPath, string? sourcePath = null)
    {
        if (File.Exists(targetPath))
        {
            return true;
        }

        var directory = Path.GetDirectoryName(targetPath);
        var targetFileName = Path.GetFileName(targetPath);
        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(targetFileName) || !Directory.Exists(directory))
        {
            return false;
        }

        var sourceFullPath = sourcePath == null ? null : Path.GetFullPath(sourcePath);

        try
        {
            foreach (var existingPath in Directory.EnumerateFiles(directory))
            {
                var existingFullPath = Path.GetFullPath(existingPath);
                if (sourceFullPath != null && string.Equals(existingFullPath, sourceFullPath, StringComparison.Ordinal))
                {
                    continue;
                }

                if (string.Equals(Path.GetFileName(existingPath), targetFileName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Unable to enumerate directory while checking duplicate target path: {TargetPath}",
                LoggingHelper.SanitizePathForLog(targetPath));
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogDebug(ex, "Access denied while checking duplicate target path: {TargetPath}",
                LoggingHelper.SanitizePathForLog(targetPath));
            return false;
        }

        return false;
    }

    /// <summary>
    /// Helper method to check if metadata is already normalized.
    /// Verifies that the file has ComicInfo.xml with minimum required fields to identify the comic.
    /// A file is considered normalized if it has valid metadata AND the series name matches the folder name.
    /// This avoids unnecessary re-writing of metadata when the file already has valid comic information.
    /// </summary>
    /// <param name="metadata">The metadata to check</param>
    /// <param name="filePath">The file path to check against folder name</param>
    /// <returns>True if the metadata meets minimum normalization requirements, false otherwise</returns>
    private async Task<bool> IsMetadataNormalizedAsync(ComicMetadata metadata, string filePath, CancellationToken cancellationToken)
    {
        // Metadata is considered normalized if it has at least Series OR (Title AND Issue)
        // This ensures we have enough information to identify the comic
        var hasSeries = !string.IsNullOrEmpty(metadata.Series);
        var hasTitleAndIssue = !string.IsNullOrEmpty(metadata.Title) && !string.IsNullOrEmpty(metadata.Issue);
        
        if (!hasSeries && !hasTitleAndIssue)
        {
            return false;
        }
        
        // Additionally check if the series name matches the folder name
        var expectedSeries = await ResolveNormalizedSeriesAsync(metadata, filePath, cancellationToken);
        if (hasSeries && metadata.Series != expectedSeries)
        {
            // Series exists but doesn't match folder name - not normalized
            return false;
        }
        // Compute the expected title using the same logic as NormalizeMetadataAsync:
        // if the ComicInfo issue is missing, fall back to parsing the chapter
        // number from the filename so the expected title becomes "Chapter <n>"
        // instead of "Chapter Unknown" (which would incorrectly mark files with
        // a discoverable chapter number in their filename as already normalized).
        var expectedIssue = metadata.Issue;
        if (string.IsNullOrEmpty(expectedIssue))
        {
            var parsedIssue = ComicFileProcessor.ParseChapterNumber(Path.GetFileNameWithoutExtension(filePath));
            if (!string.IsNullOrEmpty(parsedIssue))
            {
                expectedIssue = parsedIssue;
            }
        }

        // The issue field must not be zero padded. If the stored <Number> is
        // padded (e.g. "0012"), the file is not normalized and should be
        // re-processed so both <Number> and <Title> become unpadded.
        if (!string.IsNullOrEmpty(expectedIssue))
        {
            var strippedIssue = ComicFileProcessor.StripLeadingZeros(expectedIssue);
            if (!string.Equals(strippedIssue, expectedIssue, StringComparison.Ordinal))
            {
                return false;
            }
            expectedIssue = strippedIssue;
        }

        var expectedTitle = CreateNormalizedTitle(expectedIssue);
        _logger.LogInformation(
            "Checking title normalization for file: {FilePath}, Current metadata {title} Expected {expectedTitle}",
            LoggingHelper.SanitizePathForLog(filePath),
            LoggingHelper.SanitizeForLog(metadata.Title),
            LoggingHelper.SanitizeForLog(expectedTitle));
        if (!string.Equals(metadata.Title, expectedTitle, StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    ///<summary>
    /// Helper method to create nomalized title from issue number
    /// </summary>
    private string CreateNormalizedTitle(string? issueNumber)
    {
        if (string.IsNullOrEmpty(issueNumber))
            return "Chapter Unknown";
        
        return $"Chapter {issueNumber}";
    }

    /// <summary>
    /// When a file's metadata is missing the issue/chapter number but the
    /// original filename contains an explicit "Ch"/"Chapter &lt;n&gt;" token,
    /// recover the chapter number into the metadata's <c>Issue</c> field and
    /// set <c>Title</c> to "Chapter &lt;n&gt;", then persist the updated
    /// metadata back to the archive's ComicInfo.xml. This ensures the chapter
    /// number is preserved in metadata (not just in the filename) during
    /// rename operations on files that only have the series tag set.
    /// Returns the (possibly updated) metadata instance and a flag indicating
    /// whether metadata was modified.
    /// </summary>
    private async Task<bool> EnsureChapterMetadataPreservedAsync(
        string filePath,
        ComicMetadata metadata,
        CancellationToken cancellationToken)
    {
        if (metadata == null || !string.IsNullOrEmpty(metadata.Issue))
        {
            return false;
        }

        var originalNameStem = Path.GetFileNameWithoutExtension(filePath);
        var parsedChapter = ComicFileProcessor.ParseChapterKeyword(originalNameStem);
        if (string.IsNullOrEmpty(parsedChapter))
        {
            return false;
        }

        var normalizedTitle = CreateNormalizedTitle(parsedChapter);

        _logger.LogInformation(
            "Recovering chapter number '{Chapter}' from filename into metadata for {FilePath}; setting Issue and Title='{Title}'.",
            parsedChapter,
            LoggingHelper.SanitizePathForLog(filePath),
            LoggingHelper.SanitizeForLog(normalizedTitle));

        metadata.Issue = parsedChapter;
        metadata.Title = normalizedTitle;

        var success = await UpdateMetadataCoreAsync(filePath, metadata, cancellationToken);
        if (!success)
        {
            _logger.LogWarning(
                "Failed to write recovered chapter number '{Chapter}' to metadata for {FilePath}.",
                parsedChapter,
                LoggingHelper.SanitizePathForLog(filePath));
        }

        return success;
    }

    ///<summary>
    /// Helper method to consolidate normalization functions
    /// </summary>
    private async Task<ComicMetadata> NormalizeMetadataAsync(ComicMetadata metadata, string filePath, CancellationToken cancellationToken)
    {
        var normalizedMetadata = metadata.Clone();

        var normalizedSeries = await ResolveNormalizedSeriesAsync(metadata, filePath, cancellationToken);
        if (!string.IsNullOrEmpty(normalizedSeries) && normalizedMetadata.Series != normalizedSeries)
        {
            _logger.LogDebug("Setting normalized series name: {SeriesName}", LoggingHelper.SanitizeForLog(normalizedSeries));
            normalizedMetadata.Series = normalizedSeries;
        }

        // If the ComicInfo metadata is missing the issue/chapter number, fall back
        // to parsing it from the filename so the chapter number isn't lost and the
        // normalized title becomes "Chapter <n>" instead of "Chapter Unknown".
        if (string.IsNullOrEmpty(normalizedMetadata.Issue))
        {
            var parsedIssue = ComicFileProcessor.ParseChapterNumber(Path.GetFileNameWithoutExtension(filePath));
            if (!string.IsNullOrEmpty(parsedIssue))
            {
                _logger.LogDebug("Setting issue number from filename: {Issue}", LoggingHelper.SanitizeForLog(parsedIssue));
                normalizedMetadata.Issue = parsedIssue;
            }
        }

        // The per-file metadata issue field is not zero padded (only the
        // generated filename is). Strip any leading zero padding so the
        // <Number> tag holds the bare issue number, e.g. "0012" -> "12",
        // "0012.5" -> "12.5".
        if (!string.IsNullOrEmpty(normalizedMetadata.Issue))
        {
            var strippedIssue = ComicFileProcessor.StripLeadingZeros(normalizedMetadata.Issue);
            if (!string.Equals(strippedIssue, normalizedMetadata.Issue, StringComparison.Ordinal))
            {
                _logger.LogDebug("Stripping issue padding: {Before} -> {After}",
                    LoggingHelper.SanitizeForLog(normalizedMetadata.Issue),
                    LoggingHelper.SanitizeForLog(strippedIssue));
                normalizedMetadata.Issue = strippedIssue;
            }
        }

        // Set title to standard format
        string normalizedTitle = CreateNormalizedTitle(normalizedMetadata.Issue);
        if(!normalizedTitle.Equals(normalizedMetadata.Title))
        {
            _logger.LogDebug("Setting title to standard format: {Title}", LoggingHelper.SanitizeForLog(normalizedTitle));
            normalizedMetadata.Title = normalizedTitle;
        }
        
        return normalizedMetadata;
    }

    private async Task<string> ResolveNormalizedSeriesAsync(ComicMetadata metadata, string filePath, CancellationToken cancellationToken)
    {
        // Delegate to ISeriesNameResolver when registered so the normalize
        // pipeline, audit handlers, library-scan retag pass, and diagnostic
        // endpoint all share the same documented priority. Falls back to
        // the legacy in-process logic when the resolver hasn't been
        // injected (e.g. in tests that construct the service directly).
        if (_seriesNameResolver is not null)
        {
            var resolution = await _seriesNameResolver.ResolveAsync(filePath, metadata, mutateCache: true, cancellationToken);
            return string.IsNullOrWhiteSpace(resolution.ResolvedSeries) ? UnknownSeries : resolution.ResolvedSeries;
        }

        var fallbackSeries = ExtractSeriesFromFilename(filePath);
        var candidateSeries = new[] { fallbackSeries, metadata.Series }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var globalPreferredLanguage = _settings.DefaultPreferredLanguage;

        // First pass: if any candidate has a user-selected series name, honor
        // it immediately without falling through to external lookups. This
        // guarantees that a user's explicit name choice wins over whatever an
        // external provider returns. SeriesDisplayTitleResolver returns the
        // user-selected name verbatim for that case.
        foreach (var candidate in candidateSeries)
        {
            var userSelectedRecord = await LookupUserSelectedRecordAsync(candidate, cancellationToken);
            if (userSelectedRecord is not null && !string.IsNullOrWhiteSpace(userSelectedRecord.SeriesName))
            {
                var resolved = ResolveDisplayTitle(userSelectedRecord, globalPreferredLanguage);
                await EnsureFolderNameIsAliasAsync(fallbackSeries, userSelectedRecord.CanonicalTitle, cancellationToken);
                return resolved;
            }
        }

        // Second pass: if any candidate has a cached match from an external
        // provider (or a previously-applied manual match), prefer that canonical
        // title over the folder-derived name. This ensures that once a series
        // has been matched, every subsequent normalization writes the matched
        // name into the file's metadata instead of the raw folder name. The
        // resolver picks the localized variant when a language preference is set.
        foreach (var candidate in candidateSeries)
        {
            var matchedRecord = await LookupMatchedRecordAsync(candidate, cancellationToken);
            if (matchedRecord is not null && !string.IsNullOrWhiteSpace(matchedRecord.CanonicalTitle))
            {
                var resolved = ResolveDisplayTitle(matchedRecord, globalPreferredLanguage);
                await EnsureFolderNameIsAliasAsync(fallbackSeries, matchedRecord.CanonicalTitle, cancellationToken);
                return resolved;
            }
        }

        // Defensive lookup: if `<Series>` already holds a localized title that
        // isn't separately indexed under the normalized-key map, fall back to
        // searching the cache record reachable via the folder name and see
        // whether the metadata.Series matches one of its LocalizedTitles. This
        // avoids losing the record once an earlier pass rewrote `<Series>` to
        // a non-canonical language variant.
        if (_seriesMetadataCache is not null
            && !string.IsNullOrWhiteSpace(metadata.Series)
            && !string.IsNullOrWhiteSpace(fallbackSeries)
            && !string.Equals(metadata.Series, fallbackSeries, StringComparison.OrdinalIgnoreCase))
        {
            var folderRecord = await LookupMatchedRecordAsync(fallbackSeries, cancellationToken)
                               ?? await LookupUserSelectedRecordAsync(fallbackSeries, cancellationToken);
            if (folderRecord is not null
                && folderRecord.LocalizedTitles is { Count: > 0 }
                && folderRecord.LocalizedTitles.Any(lt =>
                    !string.IsNullOrWhiteSpace(lt?.Title)
                    && string.Equals(lt!.Title, metadata.Series, StringComparison.OrdinalIgnoreCase)))
            {
                var resolved = ResolveDisplayTitle(folderRecord, globalPreferredLanguage);
                await EnsureFolderNameIsAliasAsync(fallbackSeries, folderRecord.CanonicalTitle, cancellationToken);
                return resolved;
            }
        }

        // Skip Step 4 entirely when the cache holds a "cleared" record for any
        // candidate — the user has explicitly cleared external metadata and
        // it must not be silently re-fetched. Only an explicit refresh /
        // manual match may bring external metadata back.
        var hasClearedRecord = await AnyCandidateClearedAsync(candidateSeries, cancellationToken);

        foreach (var candidate in hasClearedRecord ? (IEnumerable<string>)Array.Empty<string>() : candidateSeries)
        {
            var externalMetadata = await LookupExternalSeriesMetadataAsync(candidate, cancellationToken);
            if (!string.IsNullOrWhiteSpace(externalMetadata?.CanonicalTitle))
            {
                var canonical = externalMetadata!.CanonicalTitle.Trim();

                // Persist the lookup so subsequent normalize calls hit the
                // cache (the matched-record pass above) instead of re-querying
                // the provider on every file. Failure to persist must not
                // prevent the per-file normalize from proceeding.
                SeriesMetadataCacheRecord? persisted = null;
                if (_seriesMetadataCache is not null)
                {
                    try
                    {
                        persisted = await _seriesMetadataCache.PersistExternalLookupAsync(candidate, externalMetadata, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "Failed to persist external lookup for series {SeriesName}",
                            LoggingHelper.SanitizeForLog(candidate));
                    }
                }

                // Resolve the display title from the persisted record when
                // available so per-series language preferences (if any) apply;
                // otherwise fall back to a transient projection of the lookup.
                var resolveSource = persisted ?? new SeriesMetadataCacheRecord
                {
                    CanonicalTitle = canonical,
                    LocalizedTitles = externalMetadata.LocalizedTitles is { Count: > 0 }
                        ? externalMetadata.LocalizedTitles
                            .Where(t => t is not null && !string.IsNullOrWhiteSpace(t.Title))
                            .Select(t => new LocalizedTitle(t.Title, t.Language))
                            .ToList()
                        : new List<LocalizedTitle>(),
                    PreferredLanguage = null
                };

                var resolved = ResolveDisplayTitle(resolveSource, globalPreferredLanguage);
                await EnsureFolderNameIsAliasAsync(fallbackSeries, canonical, cancellationToken);
                return resolved;
            }
        }

        // No external/cache metadata exists for this series. Per project policy,
        // only fall back to the folder-derived name when the file itself has no
        // <Series> value — otherwise preserve whatever the file already contains
        // so we don't overwrite a meaningful series with a folder name solely
        // because we couldn't find external metadata for it.
        if (!string.IsNullOrWhiteSpace(metadata.Series))
        {
            return metadata.Series.Trim();
        }
        return !string.IsNullOrWhiteSpace(fallbackSeries) ? fallbackSeries : UnknownSeries;
    }

    /// <summary>
    /// Run a cache record through <see cref="SeriesDisplayTitleResolver"/> to
    /// apply the per-series / global language preference rules, defending
    /// against an unexpectedly empty result by falling back to the canonical
    /// title (and ultimately <see cref="UnknownSeries"/>).
    /// </summary>
    private static string ResolveDisplayTitle(SeriesMetadataCacheRecord record, string? globalPreferredLanguage)
    {
        var resolved = SeriesDisplayTitleResolver.Resolve(record, globalPreferredLanguage);
        if (!string.IsNullOrWhiteSpace(resolved))
        {
            return resolved.Trim();
        }
        if (!string.IsNullOrWhiteSpace(record.CanonicalTitle))
        {
            return record.CanonicalTitle.Trim();
        }
        return UnknownSeries;
    }

    /// <summary>
    /// Ensures that the file's folder-derived series name is present in the
    /// resolved series' cache record. If the cache contains no record for the
    /// resolved series yet, one is created with the folder name as the sole
    /// user alias. This lets the system "learn" alternative names automatically
    /// the first time we see a file whose folder name differs from its
    /// canonical series title.
    /// </summary>
    private async Task EnsureFolderNameIsAliasAsync(string folderSeriesName, string resolvedSeries, CancellationToken cancellationToken)
    {
        if (_seriesMetadataCache is null
            || string.IsNullOrWhiteSpace(folderSeriesName)
            || string.IsNullOrWhiteSpace(resolvedSeries)
            || string.Equals(folderSeriesName, UnknownSeries, StringComparison.OrdinalIgnoreCase)
            || string.Equals(resolvedSeries, UnknownSeries, StringComparison.OrdinalIgnoreCase)
            || string.Equals(folderSeriesName, resolvedSeries, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            var resolvedKey = _seriesMetadataCache.NormalizeKey(resolvedSeries);
            if (string.IsNullOrWhiteSpace(resolvedKey))
            {
                return;
            }

            var record = await _seriesMetadataCache.GetAsync(resolvedKey, cancellationToken);

            // If the folder name is already known under any title on the
            // record (canonical title, provider alias, or existing user alias),
            // there is nothing to do.
            if (record is not null)
            {
                if (string.Equals(record.CanonicalTitle, folderSeriesName, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
                if (record.Aliases is not null
                    && record.Aliases.Any(a => string.Equals(a, folderSeriesName, StringComparison.OrdinalIgnoreCase)))
                {
                    return;
                }
                if (record.UserAliases is not null
                    && record.UserAliases.Any(a => string.Equals(a, folderSeriesName, StringComparison.OrdinalIgnoreCase)))
                {
                    return;
                }
            }

            // Merge the folder name into the existing user-alias list (or
            // start a fresh list when no record exists yet) and persist it.
            var mergedAliases = new List<string>();
            if (record?.UserAliases is { Count: > 0 } existing)
            {
                mergedAliases.AddRange(existing);
            }
            mergedAliases.Add(folderSeriesName.Trim());

            await _seriesMetadataCache.SetUserAliasesAsync(
                resolvedSeries,
                mergedAliases,
                cancellationToken);

            _logger.LogInformation(
                "Added folder name '{Folder}' as user alias for series '{Series}'",
                LoggingHelper.SanitizeForLog(folderSeriesName),
                LoggingHelper.SanitizeForLog(resolvedSeries));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to add folder name {Folder} as alias for series {Series}",
                LoggingHelper.SanitizeForLog(folderSeriesName),
                LoggingHelper.SanitizeForLog(resolvedSeries));
        }
    }

    private async Task<SeriesMetadataCacheRecord?> LookupUserSelectedRecordAsync(string seriesName, CancellationToken cancellationToken)
    {
        if (_seriesMetadataCache is null || string.IsNullOrWhiteSpace(seriesName))
        {
            return null;
        }

        try
        {
            var key = _seriesMetadataCache.NormalizeKey(seriesName);
            if (string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            var record = await _seriesMetadataCache.GetAsync(key, cancellationToken);
            if (record is not null && !string.IsNullOrWhiteSpace(record.SeriesName))
            {
                return record;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to consult series metadata cache for user-selected name {SeriesName}",
                LoggingHelper.SanitizeForLog(seriesName));
        }

        return null;
    }

    /// <summary>
    /// Returns the cached record for a series when it represents a confirmed
    /// match — either an external provider lookup that succeeded or a manually
    /// applied match. Differs from
    /// <see cref="LookupUserSelectedRecordAsync"/> in that it does not require
    /// the record to be flagged as user-canonical; any successful match is
    /// considered authoritative for normalization so that the matched name
    /// (rather than the raw folder name) ends up in file metadata. The full
    /// record is returned so the caller can apply language preference rules
    /// via <see cref="SeriesDisplayTitleResolver"/>.
    /// </summary>
    private async Task<SeriesMetadataCacheRecord?> LookupMatchedRecordAsync(string seriesName, CancellationToken cancellationToken)
    {
        if (_seriesMetadataCache is null || string.IsNullOrWhiteSpace(seriesName))
        {
            return null;
        }

        try
        {
            var key = _seriesMetadataCache.NormalizeKey(seriesName);
            if (string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            var record = await _seriesMetadataCache.GetAsync(key, cancellationToken);
            if (record is null || string.IsNullOrWhiteSpace(record.CanonicalTitle))
            {
                return null;
            }

            // Recognise any cache record whose status implies a positive match.
            // "success" comes from a provider lookup that returned a hit;
            // "manual_match" is set when the user explicitly picked a candidate
            // from the manual-match UI; "manual" is set when the user takes any
            // direct action on the record (e.g. setting a preferred language,
            // editing user aliases, or saving a canonical-title override) and
            // therefore represents an explicit user decision that should be
            // honoured when rewriting per-file <Series> metadata. Without
            // including "manual" here, setting a per-series preferred language
            // on a series that has never been externally matched would silently
            // fall through to the external-lookup path (or the raw folder name)
            // instead of running the cached record through SeriesDisplayTitleResolver.
            // A user-canonical override is already handled by
            // LookupUserSelectedRecordAsync but we accept it here too so
            // callers can use this method in isolation.
            var status = record.LookupStatus;
            var isMatched = !string.IsNullOrWhiteSpace(record.SeriesName)
                || string.Equals(status, "success", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "manual_match", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "manual", StringComparison.OrdinalIgnoreCase);

            return isMatched ? record : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to consult series metadata cache for matched title {SeriesName}",
                LoggingHelper.SanitizeForLog(seriesName));
        }

        return null;
    }

    private async Task<ExternalSeriesMetadata?> LookupExternalSeriesMetadataAsync(string seriesName, CancellationToken cancellationToken)
    {
        if (_externalSeriesMetadata is null || string.IsNullOrWhiteSpace(seriesName))
        {
            return null;
        }

        try
        {
            return await _externalSeriesMetadata.LookupSeriesAsync(seriesName, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve external metadata for series {SeriesName}", LoggingHelper.SanitizeForLog(seriesName));
            return null;
        }
    }

    /// <summary>
    /// True when any candidate has a cached record with
    /// <c>LookupStatus == "cleared"</c>. Used by the legacy
    /// <see cref="ResolveNormalizedSeriesAsync"/> fallback to honor an explicit
    /// user clear: a cleared series's external metadata must not be silently
    /// re-fetched on subsequent normalize runs.
    /// </summary>
    private async Task<bool> AnyCandidateClearedAsync(IReadOnlyList<string> candidates, CancellationToken cancellationToken)
    {
        if (_seriesMetadataCache is null || candidates.Count == 0) return false;
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            try
            {
                var key = _seriesMetadataCache.NormalizeKey(candidate);
                if (string.IsNullOrWhiteSpace(key)) continue;
                var record = await _seriesMetadataCache.GetAsync(key, cancellationToken);
                if (record is not null
                    && string.Equals(record.LookupStatus, "cleared", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to consult series metadata cache for cleared-status check on {SeriesName}",
                    LoggingHelper.SanitizeForLog(candidate));
            }
        }
        return false;
    }

    /// <summary>
    /// Helper method to log processing history entries
    /// </summary>
    private async Task LogHistoryAsync(string filePath, string action, bool success, string? errorMessage = null, CancellationToken cancellationToken = default)
    {
        await _historyService.AddHistoryEntryAsync(new ProcessingHistoryEntry
        {
            Id = Guid.NewGuid(),
            FilePath = filePath,
            Action = action,
            Timestamp = DateTime.UtcNow,
            Success = success,
            ErrorMessage = errorMessage
        }, cancellationToken);
    }

    /// <summary>
    /// Helper method to log processing history entries with before/after metadata
    /// </summary>
    private async Task LogHistoryWithChangesAsync(
        string filePath, 
        string action, 
        bool success, 
        string? errorMessage,
        string? beforeFilename,
        string? afterFilename,
        ComicMetadata? beforeMetadata,
        ComicMetadata? afterMetadata,
        CancellationToken cancellationToken = default)
    {
        await _historyService.AddHistoryEntryAsync(new ProcessingHistoryEntry
        {
            Id = Guid.NewGuid(),
            FilePath = filePath,
            Action = action,
            Timestamp = DateTime.UtcNow,
            Success = success,
            ErrorMessage = errorMessage,
            BeforeFilename = beforeFilename,
            AfterFilename = afterFilename,
            BeforeTitle = beforeMetadata?.Title,
            AfterTitle = afterMetadata?.Title,
            BeforeSeries = beforeMetadata?.Series,
            AfterSeries = afterMetadata?.Series,
            BeforeIssue = beforeMetadata?.Issue,
            AfterIssue = afterMetadata?.Issue,
            BeforePublisher = beforeMetadata?.Publisher,
            AfterPublisher = afterMetadata?.Publisher,
            BeforeYear = beforeMetadata?.Year,
            AfterYear = afterMetadata?.Year,
            BeforeVolume = beforeMetadata?.Volume,
            AfterVolume = afterMetadata?.Volume
        }, cancellationToken);
    }
}
