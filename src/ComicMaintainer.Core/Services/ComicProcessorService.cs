using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Service for processing comic files
/// Note: This is a placeholder implementation. Full comic processing would require
/// integration with a C# comic library (e.g., SharpCompress for archive handling)
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

    public async Task<bool> ProcessFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Processing file: {FilePath}", filePath);

            if (!File.Exists(filePath))
            {
                _logger.LogWarning("File not found: {FilePath}", filePath);
                return false;
            }

            // TODO: Implement actual comic processing logic
            // This would involve:
            // 1. Reading comic archive (CBZ/CBR)
            // 2. Extracting/updating metadata
            // 3. Renaming file based on template
            // 4. Checking for duplicates
            // 5. Moving duplicates to duplicate directory

            // For now, just mark as processed
            await _fileStore.MarkFileProcessedAsync(filePath, true, cancellationToken);
            _logger.LogInformation("File processed successfully: {FilePath}", filePath);

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
        // TODO: Implement metadata extraction from comic archive
        // This would use a library like SharpCompress to read the archive
        // and extract ComicInfo.xml or other metadata formats

        return Task.FromResult<ComicMetadata?>(new ComicMetadata
        {
            Series = "Unknown Series",
            Issue = ParseIssueNumber(Path.GetFileNameWithoutExtension(filePath))
        });
    }

    public Task<bool> UpdateMetadataAsync(string filePath, ComicMetadata metadata, CancellationToken cancellationToken = default)
    {
        // TODO: Implement metadata updating in comic archive
        // This would involve:
        // 1. Opening the archive
        // 2. Creating/updating ComicInfo.xml
        // 3. Saving the archive

        _logger.LogInformation("Updating metadata for: {FilePath}", filePath);
        return Task.FromResult(true);
    }

    private static string? ParseIssueNumber(string filename)
    {
        // Simple pattern matching for issue numbers
        var match = Regex.Match(filename, @"(?i)(?:ch|chapter|issue|#)?\s*(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }
}
