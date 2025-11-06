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
public class ComicProcessorService : IComicProcessorService
{
    private readonly AppSettings _settings;
    private readonly ILogger<ComicProcessorService> _logger;
    private readonly IFileStoreService _fileStore;
    private readonly IEventBroadcaster? _eventBroadcaster;
    private readonly IProcessingHistoryService _historyService;
    private readonly ConcurrentDictionary<Guid, ProcessingJob> _jobs = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _jobCancellationTokens = new();

    public ComicProcessorService(
        IOptions<AppSettings> settings,
        ILogger<ComicProcessorService> logger,
        IFileStoreService fileStore,
        IProcessingHistoryService historyService,
        IEventBroadcaster? eventBroadcaster = null)
    {
        _settings = settings.Value;
        _logger = logger;
        _fileStore = fileStore;
        _historyService = historyService;
        _eventBroadcaster = eventBroadcaster;
    }

    public async Task<bool> ProcessFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("ProcessFileAsync: Starting processing for file: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
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
                    metadata.Series, metadata.Issue, metadata.Title);
            }
            else
            {
                _logger.LogDebug("ProcessFileAsync: No metadata extracted from file: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            }
            
            _logger.LogDebug("ProcessFileAsync: Continuing with processing: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));

            // Track rename and normalize separately
            bool renameSuccess = false;
            bool normalizeSuccess = false;

            _logger.LogDebug("ProcessFileAsync: Starting rename phase - WatcherEnableRename: {RenameEnabled}, Has Metadata: {HasMetadata}, Has Series: {HasSeries}",
                _settings.WatcherEnableRename, metadata != null, metadata?.Series != null);

            // Rename file based on template if metadata is available and rename is enabled
            if (_settings.WatcherEnableRename && metadata != null && !string.IsNullOrEmpty(metadata.Series))
            {
                _logger.LogDebug("ProcessFileAsync: Attempting to rename file: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                var newFilePath = GenerateFileName(metadata, filePath);
                if (newFilePath == filePath)
                {
                    // File already has correct name, no rename needed
                    _logger.LogDebug("File already has correct name: {FilePath}", filePath);
                    await _fileStore.MarkFileRenamedAsync(filePath, true, cancellationToken);
                    var filename = Path.GetFileName(filePath);
                    await LogHistoryWithChangesAsync(filePath, "Rename", true, "File already correctly named", 
                        filename, filename, metadata, metadata, cancellationToken);
                    renameSuccess = true;
                }
                else if (File.Exists(newFilePath))
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
                        _logger.LogInformation("Renaming file from {OldPath} to {NewPath}", filePath, newFilePath);
                        var oldFilePath = filePath;
                        var beforeFilename = Path.GetFileName(oldFilePath);
                        var afterFilename = Path.GetFileName(newFilePath);
                        
                        File.Move(filePath, newFilePath);
                        
                        // Update file store with new path
                        await _fileStore.RemoveFileAsync(oldFilePath, cancellationToken);
                        await _fileStore.AddFileAsync(newFilePath, cancellationToken);
                        await _fileStore.MarkFileRenamedAsync(newFilePath, true, cancellationToken);
                        
                        await LogHistoryWithChangesAsync(newFilePath, "Rename", true, null, 
                            beforeFilename, afterFilename, metadata, metadata, cancellationToken);
                        
                        filePath = newFilePath;
                        renameSuccess = true;
                    }
                    catch (IOException ex) when (File.Exists(newFilePath))
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
                        _logger.LogError(ex, "Failed to rename file: {FilePath}", filePath);
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
                    _logger.LogDebug("Rename disabled in settings: {FilePath}", filePath);
                    await _fileStore.MarkFileRenamedAsync(filePath, true, cancellationToken); // Mark as "renamed" (skipped)
                    await LogHistoryAsync(filePath, "Rename", true, "Rename disabled", cancellationToken);
                    renameSuccess = true;
                }
                else
                {
                    _logger.LogDebug("No metadata or series information for rename: {FilePath}", filePath);
                    await _fileStore.MarkFileRenamedAsync(filePath, false, cancellationToken);
                    await LogHistoryAsync(filePath, "Rename", false, "No metadata or series information", cancellationToken);
                }
            }

            _logger.LogDebug("ProcessFileAsync: Starting normalize phase - WatcherEnableNormalize: {NormalizeEnabled}, Has Metadata: {HasMetadata}",
                _settings.WatcherEnableNormalize, metadata != null);

            // Normalize metadata (update ComicInfo.xml) if enabled
            if (_settings.WatcherEnableNormalize && metadata != null)
            {
                _logger.LogDebug("ProcessFileAsync: Attempting to normalize file: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                // Check if metadata is already normalized (has ComicInfo.xml with valid data)
                if (IsMetadataNormalized(metadata))
                {
                    _logger.LogDebug("File already has normalized metadata: {FilePath}", filePath);
                    await _fileStore.MarkFileNormalizedAsync(filePath, true, cancellationToken);
                    var filename = Path.GetFileName(filePath);
                    await LogHistoryWithChangesAsync(filePath, "Normalize", true, "File already normalized",
                        filename, filename, metadata, metadata, cancellationToken);
                    normalizeSuccess = true;
                }
                else
                {
                    var beforeMetadata = metadata;
                    normalizeSuccess = await UpdateMetadataAsync(filePath, metadata, cancellationToken);
                    await _fileStore.MarkFileNormalizedAsync(filePath, normalizeSuccess, cancellationToken);
                    if (normalizeSuccess)
                    {
                        var filename = Path.GetFileName(filePath);
                        await LogHistoryWithChangesAsync(filePath, "Normalize", true, null,
                            filename, filename, beforeMetadata, metadata, cancellationToken);
                    }
                    else
                    {
                        _logger.LogWarning("Failed to normalize metadata for: {FilePath}", filePath);
                        await LogHistoryAsync(filePath, "Normalize", false, "Failed to update metadata", cancellationToken);
                    }
                }
            }
            else if (!_settings.WatcherEnableNormalize)
            {
                // Normalize disabled in settings
                _logger.LogDebug("Normalize disabled in settings: {FilePath}", filePath);
                await _fileStore.MarkFileNormalizedAsync(filePath, true, cancellationToken); // Mark as "normalized" (skipped)
                await LogHistoryAsync(filePath, "Normalize", true, "Normalize disabled", cancellationToken);
                normalizeSuccess = true;
            }
            else
            {
                await _fileStore.MarkFileNormalizedAsync(filePath, false, cancellationToken);
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
        var jobId = Guid.NewGuid();
        var fileList = filePaths.ToList();

        _logger.LogDebug("ProcessFilesAsync: Creating new processing job {JobId} for {FileCount} files", jobId, fileList.Count);

        var job = new ProcessingJob
        {
            JobId = jobId,
            Status = JobStatus.Queued,
            Files = fileList,
            TotalFiles = fileList.Count,
            StartTime = DateTime.UtcNow
        };

        _jobs[jobId] = job;
        
        // Create a CancellationTokenSource for this job that can be cancelled independently
        var jobCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _jobCancellationTokens[jobId] = jobCts;

        _logger.LogDebug("ProcessFilesAsync: Job {JobId} created and queued with {FileCount} files", jobId, fileList.Count);

        // Broadcast initial job status
        _ = BroadcastJobStatusAsync(job);

        // Process files asynchronously using LongRunning for potentially long batch operations
        _ = Task.Factory.StartNew(async () =>
        {
            try
            {
                _logger.LogDebug("ProcessFilesAsync: Job {JobId} starting execution", jobId);
                job.Status = JobStatus.Running;
                await BroadcastJobStatusAsync(job);

                var fileIndex = 0;
                foreach (var file in fileList)
                {
                    fileIndex++;
                    _logger.LogDebug("ProcessFilesAsync: Job {JobId} processing file {FileIndex}/{TotalFiles}: {FilePath}", 
                        jobId, fileIndex, fileList.Count, LoggingHelper.SanitizePathForLog(file));
                    
                    if (jobCts.Token.IsCancellationRequested)
                    {
                        _logger.LogInformation("ProcessFilesAsync: Job {JobId} cancellation requested at file {FileIndex}/{TotalFiles}", 
                            jobId, fileIndex, fileList.Count);
                        job.Status = JobStatus.Cancelled;
                        await BroadcastJobStatusAsync(job);
                        _jobCancellationTokens.TryRemove(jobId, out _);
                        return;
                    }

                    job.CurrentFile = file;
                    var success = await ProcessFileAsync(file, jobCts.Token);

                    if (success)
                    {
                        job.ProcessedFiles++;
                        _logger.LogDebug("ProcessFilesAsync: Job {JobId} successfully processed file {FileIndex}/{TotalFiles}: {FilePath}", 
                            jobId, fileIndex, fileList.Count, LoggingHelper.SanitizePathForLog(file));
                    }
                    else
                    {
                        job.FailedFiles++;
                        job.Errors[file] = "Processing failed";
                        _logger.LogDebug("ProcessFilesAsync: Job {JobId} failed to process file {FileIndex}/{TotalFiles}: {FilePath}", 
                            jobId, fileIndex, fileList.Count, LoggingHelper.SanitizePathForLog(file));
                    }

                    // Broadcast progress after each file
                    await BroadcastJobStatusAsync(job);
                    
                    // Broadcast individual file processed event
                    if (_eventBroadcaster != null)
                    {
                        await _eventBroadcaster.BroadcastFileProcessedAsync(
                            Path.GetFileName(file), 
                            success, 
                            success ? null : "Processing failed");
                    }
                }

                job.Status = JobStatus.Completed;
                job.EndTime = DateTime.UtcNow;
                _logger.LogInformation("ProcessFilesAsync: Job {JobId} completed - Processed: {ProcessedFiles}, Failed: {FailedFiles}, Total: {TotalFiles}",
                    jobId, job.ProcessedFiles, job.FailedFiles, job.TotalFiles);
                await BroadcastJobStatusAsync(job);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("ProcessFilesAsync: Processing job cancelled: {JobId}", jobId);
                job.Status = JobStatus.Cancelled;
                job.EndTime = DateTime.UtcNow;
                await BroadcastJobStatusAsync(job);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ProcessFilesAsync: Error processing batch job: {JobId}", jobId);
                job.Status = JobStatus.Failed;
                job.EndTime = DateTime.UtcNow;
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

    public Task<Guid> RenameFilesAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken = default)
    {
        var jobId = Guid.NewGuid();
        var fileList = filePaths.ToList();

        _logger.LogDebug("RenameFilesAsync: Creating new rename job {JobId} for {FileCount} files", jobId, fileList.Count);

        var job = new ProcessingJob
        {
            JobId = jobId,
            Status = JobStatus.Queued,
            Files = fileList,
            TotalFiles = fileList.Count,
            StartTime = DateTime.UtcNow
        };

        _jobs[jobId] = job;
        
        // Create a CancellationTokenSource for this job that can be cancelled independently
        var jobCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _jobCancellationTokens[jobId] = jobCts;

        _logger.LogDebug("RenameFilesAsync: Job {JobId} created and queued with {FileCount} files", jobId, fileList.Count);

        // Broadcast initial job status
        _ = BroadcastJobStatusAsync(job);

        // Rename files asynchronously using LongRunning for potentially long batch operations
        _ = Task.Factory.StartNew(async () =>
        {
            try
            {
                _logger.LogDebug("RenameFilesAsync: Job {JobId} starting execution", jobId);
                job.Status = JobStatus.Running;
                await BroadcastJobStatusAsync(job);

                var fileIndex = 0;
                foreach (var file in fileList)
                {
                    fileIndex++;
                    _logger.LogDebug("RenameFilesAsync: Job {JobId} renaming file {FileIndex}/{TotalFiles}: {FilePath}", 
                        jobId, fileIndex, fileList.Count, LoggingHelper.SanitizePathForLog(file));
                    
                    if (jobCts.Token.IsCancellationRequested)
                    {
                        _logger.LogInformation("RenameFilesAsync: Job {JobId} cancellation requested at file {FileIndex}/{TotalFiles}", 
                            jobId, fileIndex, fileList.Count);
                        job.Status = JobStatus.Cancelled;
                        await BroadcastJobStatusAsync(job);
                        _jobCancellationTokens.TryRemove(jobId, out _);
                        return;
                    }

                    job.CurrentFile = file;
                    var success = await RenameFileAsync(file, jobCts.Token);

                    if (success)
                    {
                        job.ProcessedFiles++;
                        _logger.LogDebug("RenameFilesAsync: Job {JobId} successfully renamed file {FileIndex}/{TotalFiles}: {FilePath}", 
                            jobId, fileIndex, fileList.Count, LoggingHelper.SanitizePathForLog(file));
                    }
                    else
                    {
                        job.FailedFiles++;
                        job.Errors[file] = "Rename failed";
                        _logger.LogDebug("RenameFilesAsync: Job {JobId} failed to rename file {FileIndex}/{TotalFiles}: {FilePath}", 
                            jobId, fileIndex, fileList.Count, LoggingHelper.SanitizePathForLog(file));
                    }

                    // Broadcast progress after each file
                    await BroadcastJobStatusAsync(job);
                    
                    // Broadcast individual file processed event
                    if (_eventBroadcaster != null)
                    {
                        await _eventBroadcaster.BroadcastFileProcessedAsync(
                            Path.GetFileName(file), 
                            success, 
                            success ? null : "Rename failed");
                    }
                }

                job.Status = JobStatus.Completed;
                job.EndTime = DateTime.UtcNow;
                _logger.LogInformation("RenameFilesAsync: Job {JobId} completed - Processed: {ProcessedFiles}, Failed: {FailedFiles}, Total: {TotalFiles}",
                    jobId, job.ProcessedFiles, job.FailedFiles, job.TotalFiles);
                await BroadcastJobStatusAsync(job);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("RenameFilesAsync: Rename job cancelled: {JobId}", jobId);
                job.Status = JobStatus.Cancelled;
                job.EndTime = DateTime.UtcNow;
                await BroadcastJobStatusAsync(job);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "RenameFilesAsync: Error processing rename job: {JobId}", jobId);
                job.Status = JobStatus.Failed;
                job.EndTime = DateTime.UtcNow;
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

    public Task<Guid> NormalizeFilesAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken = default)
    {
        var jobId = Guid.NewGuid();
        var fileList = filePaths.ToList();

        _logger.LogDebug("NormalizeFilesAsync: Creating new normalize job {JobId} for {FileCount} files", jobId, fileList.Count);

        var job = new ProcessingJob
        {
            JobId = jobId,
            Status = JobStatus.Queued,
            Files = fileList,
            TotalFiles = fileList.Count,
            StartTime = DateTime.UtcNow
        };

        _jobs[jobId] = job;
        
        // Create a CancellationTokenSource for this job that can be cancelled independently
        var jobCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _jobCancellationTokens[jobId] = jobCts;

        _logger.LogDebug("NormalizeFilesAsync: Job {JobId} created and queued with {FileCount} files", jobId, fileList.Count);

        // Broadcast initial job status
        _ = BroadcastJobStatusAsync(job);

        // Normalize files asynchronously using LongRunning for potentially long batch operations
        _ = Task.Factory.StartNew(async () =>
        {
            try
            {
                _logger.LogDebug("NormalizeFilesAsync: Job {JobId} starting execution", jobId);
                job.Status = JobStatus.Running;
                await BroadcastJobStatusAsync(job);

                var fileIndex = 0;
                foreach (var file in fileList)
                {
                    fileIndex++;
                    _logger.LogDebug("NormalizeFilesAsync: Job {JobId} normalizing file {FileIndex}/{TotalFiles}: {FilePath}", 
                        jobId, fileIndex, fileList.Count, LoggingHelper.SanitizePathForLog(file));
                    
                    if (jobCts.Token.IsCancellationRequested)
                    {
                        _logger.LogInformation("NormalizeFilesAsync: Job {JobId} cancellation requested at file {FileIndex}/{TotalFiles}", 
                            jobId, fileIndex, fileList.Count);
                        job.Status = JobStatus.Cancelled;
                        await BroadcastJobStatusAsync(job);
                        _jobCancellationTokens.TryRemove(jobId, out _);
                        return;
                    }

                    job.CurrentFile = file;
                    var success = await NormalizeFileAsync(file, jobCts.Token);

                    if (success)
                    {
                        job.ProcessedFiles++;
                        _logger.LogDebug("NormalizeFilesAsync: Job {JobId} successfully normalized file {FileIndex}/{TotalFiles}: {FilePath}", 
                            jobId, fileIndex, fileList.Count, LoggingHelper.SanitizePathForLog(file));
                    }
                    else
                    {
                        job.FailedFiles++;
                        job.Errors[file] = "Normalize failed";
                        _logger.LogDebug("NormalizeFilesAsync: Job {JobId} failed to normalize file {FileIndex}/{TotalFiles}: {FilePath}", 
                            jobId, fileIndex, fileList.Count, LoggingHelper.SanitizePathForLog(file));
                    }

                    // Broadcast progress after each file
                    await BroadcastJobStatusAsync(job);
                    
                    // Broadcast individual file processed event
                    if (_eventBroadcaster != null)
                    {
                        await _eventBroadcaster.BroadcastFileProcessedAsync(
                            Path.GetFileName(file), 
                            success, 
                            success ? null : "Normalize failed");
                    }
                }

                job.Status = JobStatus.Completed;
                job.EndTime = DateTime.UtcNow;
                _logger.LogInformation("NormalizeFilesAsync: Job {JobId} completed - Processed: {ProcessedFiles}, Failed: {FailedFiles}, Total: {TotalFiles}",
                    jobId, job.ProcessedFiles, job.FailedFiles, job.TotalFiles);
                await BroadcastJobStatusAsync(job);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("NormalizeFilesAsync: Normalize job cancelled: {JobId}", jobId);
                job.Status = JobStatus.Cancelled;
                job.EndTime = DateTime.UtcNow;
                await BroadcastJobStatusAsync(job);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "NormalizeFilesAsync: Error processing normalize job: {JobId}", jobId);
                job.Status = JobStatus.Failed;
                job.EndTime = DateTime.UtcNow;
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
        try
        {
            _logger.LogInformation("RenameFileAsync: Starting rename for file: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
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
                    metadata.Series, metadata.Issue);
            }
            else
            {
                _logger.LogDebug("RenameFileAsync: No metadata extracted from file: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            }
            
            // Rename file based on template if metadata is available
            if (metadata != null && !string.IsNullOrEmpty(metadata.Series))
            {
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
                    _logger.LogInformation("Renaming file from {OldPath} to {NewPath}", filePath, newFilePath);
                    
                    // Capture before/after filenames for history
                    var beforeFilename = Path.GetFileName(filePath);
                    var afterFilename = Path.GetFileName(newFilePath);
                    
                    // File.Move will throw IOException if target exists, which we catch and handle
                    // This avoids race condition from check-then-act pattern
                    File.Move(filePath, newFilePath);
                    
                    // Update file store with new path
                    await _fileStore.RemoveFileAsync(filePath, cancellationToken);
                    await _fileStore.AddFileAsync(newFilePath, cancellationToken);
                    await _fileStore.MarkFileRenamedAsync(newFilePath, true, cancellationToken);
                    
                    _logger.LogInformation("File renamed successfully: {NewPath}", newFilePath);
                    await LogHistoryWithChangesAsync(newFilePath, "Rename", true, null, 
                        beforeFilename, afterFilename, metadata, metadata, cancellationToken);
                    return true;
                }
                catch (IOException ex) when (File.Exists(newFilePath))
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
        try
        {
            _logger.LogInformation("NormalizeFileAsync: Starting normalize for file: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
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
                metadata.Series, metadata.Issue, metadata.Title);

            // Check if metadata is already normalized (has ComicInfo.xml with valid data)
            // If we successfully read metadata, it means ComicInfo.xml exists and is parseable
            var isNormalized = IsMetadataNormalized(metadata);
            _logger.LogDebug("NormalizeFileAsync: Metadata normalization check - IsNormalized: {IsNormalized}", isNormalized);
            
            if (isNormalized)
            {
                _logger.LogInformation("NormalizeFileAsync: File already has normalized metadata: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                await _fileStore.MarkFileNormalizedAsync(filePath, true, cancellationToken);
                var filename = Path.GetFileName(filePath);
                await LogHistoryWithChangesAsync(filePath, "Normalize", true, "File already normalized",
                    filename, filename, metadata, metadata, cancellationToken);
                return true;
            }
            
            _logger.LogDebug("NormalizeFileAsync: File needs normalization, updating metadata: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));

            // Capture before metadata state (current metadata)
            var beforeMetadata = metadata;
            
            // Update metadata (normalize it by re-writing ComicInfo.xml)
            var success = await UpdateMetadataAsync(filePath, metadata, cancellationToken);
            await _fileStore.MarkFileNormalizedAsync(filePath, success, cancellationToken);
            
            if (success)
            {
                _logger.LogInformation("NormalizeFileAsync: File normalized successfully: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                var filename = Path.GetFileName(filePath);
                // For normalize operations, we log the same metadata as before/after since we're ensuring
                // the existing metadata is written properly to ComicInfo.xml
                await LogHistoryWithChangesAsync(filePath, "Normalize", true, null, 
                    filename, filename, metadata, metadata, cancellationToken);
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
            await _eventBroadcaster.BroadcastJobUpdateAsync(
                job.JobId,
                job.Status.ToString().ToLower(),
                job.ProcessedFiles + job.FailedFiles,
                job.TotalFiles,
                job.ProcessedFiles,
                job.FailedFiles);
        }
    }

    public ProcessingJob? GetJob(Guid jobId)
    {
        return _jobs.TryGetValue(jobId, out var job) ? job : null;
    }

    public ProcessingJob? GetActiveJob()
    {
        return _jobs.Values
            .FirstOrDefault(j => j.Status == JobStatus.Running || j.Status == JobStatus.Queued);
    }

    public IEnumerable<ProcessingJob> GetAllJobs()
    {
        return _jobs.Values.OrderByDescending(j => j.StartTime);
    }

    public bool DeleteJob(Guid jobId)
    {
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
                using var stream = comicInfoEntry.OpenEntryStream();
                using var reader = new StreamReader(stream);
                var xmlContent = reader.ReadToEnd();
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
            _logger.LogError(ex, "Error reading metadata from {FilePath}", filePath);
            return Task.FromResult<ComicMetadata?>(null);
        }
    }

    public async Task<bool> UpdateMetadataAsync(string filePath, ComicMetadata metadata, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(filePath) || !IsComicArchive(filePath))
                return false;

            _logger.LogInformation("Updating metadata for: {FilePath}", filePath);

            // Create ComicInfo.xml content
            var comicInfoXml = GenerateComicInfoXml(metadata);
            
            // Create a temporary file in the same directory as the target file
            // to ensure they are on the same file system (prevents cross-device link errors)
            var targetDirectory = Path.GetDirectoryName(filePath) ?? Path.GetTempPath();
            var tempFile = Path.Combine(targetDirectory, $".tmp_{Guid.NewGuid()}.cbz");
            
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
                                _logger.LogWarning("Skipping large entry {Entry} ({Size} bytes) to prevent memory issues", entry.Key, entry.Size);
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
                
                _logger.LogInformation("Successfully updated metadata for: {FilePath}", filePath);
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
                        _logger.LogWarning(ex, "Failed to delete temp file: {TempFile}", tempFile);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating metadata for {FilePath}", filePath);
            return false;
        }
    }

    /// <summary>
    /// Maximum number of retry attempts for file replacement operations
    /// </summary>
    private const int MaxFileReplaceRetries = 5;
    
    /// <summary>
    /// Delay in milliseconds to allow file handles to be fully released after archive disposal
    /// </summary>
    private const int FileHandleReleaseDelayMs = 100;
    
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
                _logger.LogError(ex, "File replace failed for {File}, attempting restore from backup", targetFile);
                await TryRestoreBackupAsync(backupPath, targetFile, cancellationToken);
                throw;
            }
        }
        
        // If we exhausted all retries, throw the last exception
        if (lastException != null)
        {
            _logger.LogError(lastException, "Failed to replace file after {MaxRetries} attempts: {File}", MaxFileReplaceRetries, targetFile);
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
        return match.Success ? match.Groups[1].Value : null;
    }

    private static bool IsComicArchive(string filePath)
    {
        return ComicFileExtensions.IsComicArchive(filePath);
    }

    private static string ExtractSeriesFromFilename(string filePath)
    {
        var filename = Path.GetFileNameWithoutExtension(filePath);
        // Remove issue numbers and common patterns
        var series = Regex.Replace(filename, @"(?i)(?:ch|chapter|issue|#)?\s*\d+(?:\.\d+)?.*$", "").Trim();
        return string.IsNullOrEmpty(series) ? "Unknown Series" : series;
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
            
            // Apply filename template
            var filename = _settings.FilenameFormat
                .Replace("{series}", metadata.Series ?? "Unknown")
                .Replace("{title}", metadata.Title ?? "")
                .Replace("{issue}", metadata.Issue?.PadLeft(_settings.IssueNumberPadding, '0') ?? "")
                .Replace("{volume}", metadata.Volume ?? "");
            
            // Clean filename
            foreach (var c in Path.GetInvalidFileNameChars())
            {
                filename = filename.Replace(c, '_');
            }
            
            return Path.Combine(directory, filename + extension);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating filename");
            return originalPath;
        }
    }

    /// <summary>
    /// Helper method to check if metadata is already normalized.
    /// Verifies that the file has ComicInfo.xml with minimum required fields to identify the comic.
    /// A file is considered normalized if it has either a Series name OR both Title and Issue number.
    /// This avoids unnecessary re-writing of metadata when the file already has valid comic information.
    /// </summary>
    /// <param name="metadata">The metadata to check</param>
    /// <returns>True if the metadata meets minimum normalization requirements, false otherwise</returns>
    private bool IsMetadataNormalized(ComicMetadata metadata)
    {
        // Metadata is considered normalized if it has at least Series OR (Title AND Issue)
        // This ensures we have enough information to identify the comic
        var hasSeries = !string.IsNullOrEmpty(metadata.Series);
        var hasTitleAndIssue = !string.IsNullOrEmpty(metadata.Title) && !string.IsNullOrEmpty(metadata.Issue);
        
        return hasSeries || hasTitleAndIssue;
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
