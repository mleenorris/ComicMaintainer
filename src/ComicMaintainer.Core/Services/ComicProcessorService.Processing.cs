using ComicMaintainer.Core.ErrorReporting;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Configuration;
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
    public Task<bool> ProcessFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        return ProcessFileAsync(filePath, false, cancellationToken);
    }

    public async Task<bool> ProcessFileAsync(string filePath, bool forceReprocess, CancellationToken cancellationToken = default)
    {
        await _processingSemaphore.WaitAsync(cancellationToken);
        try
        {
            var success = await ProcessFileCoreAsync(filePath, forceReprocess, cancellationToken);
            _metrics?.RecordFileProcessed("Process", success);
            return success;
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
            // File system I/O below uses the validated physical path so a linked
            // path component cannot redirect it outside the library; the caller's
            // path stays the key for the file store, history and series naming.
            if (!TryResolveAllowedPath(filePath, out var resolvedPath))
            {
                _logger.LogWarning("ProcessFileAsync: File is outside the allowed directories: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                return false;
            }

            if (new FileInfo(filePath).LinkTarget is not null)
            {
                _logger.LogWarning("ProcessFileAsync: File symlinks are not processed: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                await _fileStore.MarkFileNormalizedAsync(filePath, false, cancellationToken);
                await _fileStore.MarkFileRenamedAsync(filePath, false, cancellationToken);
                await LogHistoryAsync(filePath, "Process", false, "File symlinks are not processed", cancellationToken);
                return false;
            }

            _logger.LogInformation("ProcessFileAsync: Starting processing for file: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));

            // Skip files that the database already considers fully processed, preventing
            // repeated work caused by watcher events fired during or after processing.
            if (!forceReprocess && await _fileStore.IsFileProcessedAsync(filePath, cancellationToken))
            {
                _logger.LogInformation("ProcessFileAsync: File already fully processed (database), skipping: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                return true;
            }

            _logger.LogDebug("ProcessFileAsync: Checking if file exists: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));

            if (!File.Exists(resolvedPath))
            {
                _logger.LogWarning("ProcessFileAsync: File not found: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                await LogHistoryAsync(filePath, "Process", false, "File not found", cancellationToken);
                return false;
            }

            _logger.LogDebug("ProcessFileAsync: Verifying file is a comic archive: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            
            // Verify it's a comic archive
            if (!IsComicArchive(resolvedPath))
            {
                _logger.LogWarning("ProcessFileAsync: File is not a comic archive: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                await LogHistoryAsync(filePath, "Process", false, "File is not a comic archive", cancellationToken);
                return false;
            }

            _logger.LogDebug("ProcessFileAsync: Extracting metadata from archive: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            
            // Extract metadata from the archive
            var metadata = await GetMetadataAtResolvedPath(resolvedPath, filePath);
            
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

                    normalizeSuccess = await UpdateMetadataCoreAsync(resolvedPath, normalizedMetadata, cancellationToken);
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
                await EnsureChapterMetadataPreservedAsync(resolvedPath, metadata, cancellationToken);

                _logger.LogDebug("ProcessFileAsync: Attempting to rename file: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                var newResolvedPath = GenerateFileName(metadata, resolvedPath);
                if (newResolvedPath != null)
                {
                    _logger.LogInformation(
                        "Renaming file from {OldPath} to {NewPath}",
                        LoggingHelper.SanitizePathForLog(filePath),
                        LoggingHelper.SanitizePathForLog(newResolvedPath));
                }

                if (newResolvedPath == null)
                {
                    await _fileStore.MarkFileRenamedAsync(filePath, false, cancellationToken);
                    await LogHistoryAsync(filePath, "Rename", false, FilenameGenerationFailedMessage, cancellationToken);
                }
                else if (newResolvedPath == resolvedPath)
                {
                    // File already has correct name, no rename needed
                    _logger.LogDebug("File already has correct name: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                    await _fileStore.MarkFileRenamedAsync(filePath, true, cancellationToken);
                    var filename = Path.GetFileName(filePath);
                    await LogHistoryWithChangesAsync(filePath, "Rename", true, "File already correctly named", 
                        filename, filename, metadata, metadata, cancellationToken);
                    renameSuccess = true;
                }
                else if (TargetFileExists(newResolvedPath, resolvedPath))
                {
                    // Target file already exists - treat as duplicate
                    _logger.LogInformation("ProcessFileAsync: Duplicate detected - target file already exists: {NewPath}", LoggingHelper.SanitizePathForLog(newResolvedPath));
                    await _fileStore.MarkFileRenamedAsync(filePath, false, cancellationToken);
                    await _fileStore.MarkFileDuplicateAsync(filePath, true, cancellationToken);
                    await LogHistoryAsync(filePath, "Duplicate Detection", true, "Target file already exists", cancellationToken);
                    renameSuccess = false;
                }
                else if (!TryResolveAllowedPath(resolvedPath, out var currentPath) || currentPath != resolvedPath)
                {
                    // A folder on the validated path was replaced by a link while the file
                    // was processed: abort rather than move whatever the link now leads to.
                    _logger.LogWarning("ProcessFileAsync: File path changed during processing, not renaming: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                    await _fileStore.MarkFileRenamedAsync(filePath, false, cancellationToken);
                    await LogHistoryAsync(filePath, "Rename", false, "File path changed during processing", cancellationToken);
                }
                else
                {
                    var newFilePath = newResolvedPath;
                    try
                    {
                        using var renameDirectoryHandle = OpenRenameDirectory(resolvedPath);
                        if (!TryResolveAllowedPath(resolvedPath, out currentPath) || currentPath != resolvedPath)
                        {
                            _logger.LogWarning("ProcessFileAsync: File path changed during processing, not renaming: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                            await _fileStore.MarkFileRenamedAsync(filePath, false, cancellationToken);
                            await LogHistoryAsync(filePath, "Rename", false, "File path changed during processing", cancellationToken);
                        }
                        else
                        {
                            _logger.LogDebug("ProcessFileAsync: Rename path validated: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                            var movedPath = MoveFileInRenameDirectory(renameDirectoryHandle, resolvedPath, newResolvedPath);
                            newFilePath = GetAllowedLogicalPath(movedPath);
                            var oldFilePath = filePath;
                            var beforeFilename = Path.GetFileName(oldFilePath);
                            var afterFilename = Path.GetFileName(newFilePath);

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
                    }
                    catch (IOException ex) when (TargetFileExists(newResolvedPath, resolvedPath))
                    {
                        // Target file already exists - treat as duplicate
                        _logger.LogInformation(ex, "ProcessFileAsync: Duplicate detected - target file already exists during move: {NewPath}", LoggingHelper.SanitizePathForLog(newFilePath));
                        await _fileStore.MarkFileRenamedAsync(filePath, false, cancellationToken);
                        await _fileStore.MarkFileDuplicateAsync(filePath, true, cancellationToken);
                        await LogHistoryAsync(filePath, "Duplicate Detection", true, "Target file already exists", cancellationToken);
                        renameSuccess = false;
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
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

            // Series marked for automatic ereader delivery get the freshly
            // processed issue queued for email. Queueing is cheap (a database
            // row plus a channel write); the send itself happens on the email
            // queue's background worker.
            if (isFullyProcessed)
            {
                await TryQueueAutomaticEmailAsync(filePath, metadata?.Series, cancellationToken);
            }

            return true;
        }
        // A cancelled job didn't fail this file: leave it and its stored state as they are.
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A single unprocessable archive is field noise, not a defect: the
            // file is recorded in history and the operator sees the log line,
            // but it must not file a GitHub issue per bad comic in a library.
            using (_logger.BeginScope(ErrorReportLogProperties.Exclude("per-file processing failure")))
            {
                _logger.LogError(ex, "ProcessFileAsync: Error processing file: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            }

            await LogHistoryAsync(filePath, "Process", false, ex.Message, cancellationToken);
            return false;
        }
    }

    /// <summary>
    /// Queues automatic email delivery for a newly processed issue. Failures are
    /// logged and swallowed: a delivery problem must never fail file processing.
    /// </summary>
    private async Task TryQueueAutomaticEmailAsync(string filePath, string? seriesName, CancellationToken cancellationToken)
    {
        if (_comicEmail is null)
        {
            return;
        }

        try
        {
            var queued = await _comicEmail.QueueAutoSendAsync(filePath, seriesName, cancellationToken);
            if (queued > 0)
            {
                _logger.LogInformation(
                    "Queued {Count} automatic ereader delivery/deliveries for {FilePath}",
                    queued,
                    LoggingHelper.SanitizePathForLog(filePath));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(
                ex,
                "Failed to queue automatic ereader delivery for {FilePath}",
                LoggingHelper.SanitizePathForLog(filePath));
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
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "Failed to stamp series-metadata version on {FilePath}",
                LoggingHelper.SanitizePathForLog(filePath));
        }
    }
}
