using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Service for processing comic files using ComicArchive
/// Converted from Python's process_file.py functionality
/// </summary>
public class ComicProcessorService : IComicProcessorService
{
    private readonly AppSettings _settings;
    private readonly ILogger<ComicProcessorService> _logger;
    private readonly IFileStoreService _fileStore;
    private readonly ConcurrentDictionary<Guid, ProcessingJob> _jobs = new();

    public ComicProcessorService(
        IOptions<AppSettings> settings,
        ILogger<ComicProcessorService> logger,
        IFileStoreService fileStore)
    {
        _settings = settings.Value;
        _logger = logger;
        _fileStore = fileStore;
    }

    public Task<bool> ProcessFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        return ProcessFileAsync(filePath, cancellationToken, true, true, true);
    }

    public async Task<bool> ProcessFileAsync(
        string filePath, 
        CancellationToken cancellationToken,
        bool fixTitle,
        bool fixSeries, 
        bool fixFilename)
    {
        try
        {
            _logger.LogInformation("Processing file: {FilePath}", filePath);

            if (!File.Exists(filePath))
            {
                _logger.LogWarning("File not found: {FilePath}", filePath);
                return false;
            }

            var comicFolder = Path.GetDirectoryName(filePath);
            var filenameTemplate = _settings.FilenameFormat ?? "{series} - Chapter {issue}";
            var issuePadding = _settings.IssueNumberPadding;

            // Check if file is already normalized
            if (ComicFileProcessor.IsFileAlreadyNormalized(
                filePath, filenameTemplate, fixTitle, fixSeries, fixFilename, comicFolder, issuePadding))
            {
                _logger.LogInformation("File {FileName} is already normalized. Skipping processing.", 
                    Path.GetFileName(filePath));
                await _fileStore.MarkFileProcessedAsync(filePath, true, cancellationToken);
                return true;
            }

            _logger.LogInformation("File needs normalization, proceeding with processing");

            var beforeFilename = Path.GetFileName(filePath);
            string? beforeTitle = null, beforeSeries = null, beforeIssue = null;
            
            using var ca = new ComicArchive(filePath);
            var tags = ca.ReadTags("cr");
            
            if (tags == null)
            {
                tags = new ComicInfo();
            }

            // Capture before state
            beforeTitle = tags.Title;
            beforeSeries = tags.Series;
            beforeIssue = tags.Number;

            var tagsChanged = false;

            // Title and issue logic
            if (fixTitle)
            {
                _logger.LogInformation("Processing title and issue");
                
                string? issueNumber = null;
                if (!string.IsNullOrEmpty(tags.Number))
                {
                    issueNumber = tags.Number;
                    _logger.LogInformation("Issue number: {IssueNumber}", issueNumber);
                }

                if (string.IsNullOrEmpty(issueNumber))
                {
                    issueNumber = ComicFileProcessor.ParseChapterNumber(Path.GetFileNameWithoutExtension(filePath));
                }

                if (!string.IsNullOrEmpty(issueNumber))
                {
                    _logger.LogInformation("Parsed chapter number: {IssueNumber}", issueNumber);
                    var currentTitle = tags.Title;
                    _logger.LogInformation("Current title: {Title}", currentTitle);

                    var expectedTitle = $"Chapter {issueNumber}";
                    if (currentTitle != expectedTitle)
                    {
                        _logger.LogInformation("Updating title to: {Title}", expectedTitle);
                        tags.Title = expectedTitle;
                        if (string.IsNullOrEmpty(tags.Number))
                        {
                            tags.Number = issueNumber;
                        }
                        tagsChanged = true;
                    }
                    else
                    {
                        _logger.LogInformation("Already tagged title as Chapter {IssueNumber}, skipping {FileName}...", 
                            issueNumber, Path.GetFileName(filePath));
                    }
                }
                else
                {
                    _logger.LogInformation("Could not parse chapter number from filename for {FileName}. Skipping...", 
                        Path.GetFileName(filePath));
                }
            }

            // Series logic
            if (fixSeries && !string.IsNullOrEmpty(comicFolder))
            {
                _logger.LogInformation("Processing series metadata");
                
                var seriesName = ComicFileProcessor.NormalizeSeriesName(Path.GetFileName(comicFolder));
                _logger.LogInformation("Series name: {SeriesName}", seriesName);

                var seriesNameCompare = ComicFileProcessor.GetSeriesNameForComparison(seriesName);

                if (!string.IsNullOrEmpty(tags.Series))
                {
                    var tagsSeriesCompare = ComicFileProcessor.GetSeriesNameForComparison(tags.Series);
                    if (tagsSeriesCompare != seriesNameCompare)
                    {
                        _logger.LogInformation("Fixing series name to: {SeriesName}", seriesName);
                        tags.Series = seriesName;
                        tagsChanged = true;
                    }
                    else
                    {
                        _logger.LogInformation("Series name already correct for {FileName}, skipping...", 
                            Path.GetFileName(filePath));
                    }
                }
                else
                {
                    _logger.LogInformation("Fixing series name to: {SeriesName}", seriesName);
                    tags.Series = seriesName;
                    tagsChanged = true;
                }
            }

            // Write tags back to file if changed
            if (tagsChanged)
            {
                _logger.LogInformation("Tags were changed, writing back to file");
                ca.WriteTags(tags, "cr");
                _logger.LogInformation("Successfully wrote tags");
            }
            else
            {
                _logger.LogInformation("No tag changes needed");
            }

            var finalFilePath = filePath;

            // Filename logic
            if (fixFilename && !string.IsNullOrEmpty(tags.Number))
            {
                _logger.LogInformation("Processing filename");
                
                var originalExt = Path.GetExtension(filePath).ToLowerInvariant();
                var newFileName = ComicFileProcessor.FormatFilename(
                    filenameTemplate, tags, tags.Number, originalExt, issuePadding);
                
                _logger.LogInformation("Formatted new filename: {NewFileName}", newFileName);

                var newFilePath = Path.Combine(Path.GetDirectoryName(filePath)!, newFileName);
                
                if (Path.GetFullPath(filePath) != Path.GetFullPath(newFilePath))
                {
                    _logger.LogInformation("File needs to be renamed");

                    if (File.Exists(newFilePath))
                    {
                        _logger.LogWarning("Target filename already exists - duplicate detected: {Target}", newFilePath);
                        
                        // Mark as duplicate
                        await _fileStore.MarkFileDuplicateAsync(filePath, true, cancellationToken);

                        var duplicateDir = _settings.DuplicateDirectory;
                        if (!string.IsNullOrEmpty(duplicateDir))
                        {
                            var originalParent = Path.GetFileName(Path.GetDirectoryName(filePath)!);
                            var targetDir = Path.Combine(duplicateDir, originalParent);
                            
                            _logger.LogInformation("Moving duplicate to duplicate directory: {TargetDir}", targetDir);
                            
                            Directory.CreateDirectory(targetDir);
                            var destPath = Path.Combine(targetDir, Path.GetFileName(filePath));
                            
                            _logger.LogInformation("Duplicate detected. Moving {Source} to {Dest}", filePath, destPath);
                            try
                            {
                                File.Move(filePath, destPath);
                                finalFilePath = destPath;
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Error moving duplicate file {Source} to {Dest}", filePath, destPath);
                            }
                        }
                        else
                        {
                            _logger.LogInformation("A file with the name {NewFileName} already exists. Skipping rename for {FileName}. DUPLICATE_DIR not set.",
                                newFileName, Path.GetFileName(filePath));
                        }
                    }
                    else
                    {
                        _logger.LogInformation("Renaming file to: {NewFileName}", newFileName);
                        File.Move(filePath, newFilePath);
                        finalFilePath = newFilePath;
                        _logger.LogInformation("Successfully renamed file");
                    }
                }
                else
                {
                    _logger.LogInformation("Filename already correct for {FileName}, skipping rename.", 
                        Path.GetFileName(filePath));
                }
            }

            await _fileStore.MarkFileProcessedAsync(finalFilePath, true, cancellationToken);
            _logger.LogInformation("File processed successfully: {FilePath}", finalFilePath);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing file: {FilePath}", filePath);
            return false;
        }
    }

    public async Task<Guid> ProcessFilesAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken = default)
    {
        var jobId = Guid.NewGuid();
        var fileList = filePaths.ToList();

        var job = new ProcessingJob
        {
            JobId = jobId,
            Status = JobStatus.Queued,
            Files = fileList,
            TotalFiles = fileList.Count,
            StartTime = DateTime.UtcNow
        };

        _jobs[jobId] = job;

        // Process files asynchronously
        _ = Task.Run(async () =>
        {
            try
            {
                job.Status = JobStatus.Running;

                foreach (var file in fileList)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        job.Status = JobStatus.Cancelled;
                        return;
                    }

                    job.CurrentFile = file;
                    var success = await ProcessFileAsync(file, cancellationToken);

                    if (success)
                    {
                        job.ProcessedFiles++;
                    }
                    else
                    {
                        job.FailedFiles++;
                        job.Errors[file] = "Processing failed";
                    }
                }

                job.Status = JobStatus.Completed;
                job.EndTime = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing batch job: {JobId}", jobId);
                job.Status = JobStatus.Failed;
                job.EndTime = DateTime.UtcNow;
            }
        }, cancellationToken);

        return jobId;
    }

    public ProcessingJob? GetJob(Guid jobId)
    {
        return _jobs.TryGetValue(jobId, out var job) ? job : null;
    }

    public Task<ComicMetadata?> GetMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return Task.FromResult<ComicMetadata?>(null);
            }

            using var ca = new ComicArchive(filePath);
            var tags = ca.ReadTags("cr");
            
            if (tags == null)
            {
                return Task.FromResult<ComicMetadata?>(null);
            }

            return Task.FromResult<ComicMetadata?>(tags.ToMetadata());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading metadata from: {FilePath}", filePath);
            return Task.FromResult<ComicMetadata?>(null);
        }
    }

    public Task<bool> UpdateMetadataAsync(string filePath, ComicMetadata metadata, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                _logger.LogWarning("File not found: {FilePath}", filePath);
                return Task.FromResult(false);
            }

            _logger.LogInformation("Updating metadata for: {FilePath}", filePath);

            using var ca = new ComicArchive(filePath);
            var tags = ca.ReadTags("cr") ?? new ComicInfo();
            
            // Update tags from metadata
            tags.Title = metadata.Title;
            tags.Series = metadata.Series;
            tags.Number = metadata.Issue;
            tags.Volume = metadata.Volume;
            tags.Publisher = metadata.Publisher;
            tags.Year = metadata.Year;
            tags.Summary = metadata.Summary;
            
            if (metadata.Authors.Any())
            {
                tags.Writer = metadata.Authors.First();
            }

            ca.WriteTags(tags, "cr");
            
            _logger.LogInformation("Successfully updated metadata for: {FilePath}", filePath);
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating metadata for: {FilePath}", filePath);
            return Task.FromResult(false);
        }
    }
}
