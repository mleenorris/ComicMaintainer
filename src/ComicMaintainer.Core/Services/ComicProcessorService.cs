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
    private readonly ConcurrentDictionary<Guid, ProcessingJob> _jobs = new();

    public ComicProcessorService(
        IOptions<AppSettings> settings,
        ILogger<ComicProcessorService> logger,
        IFileStoreService fileStore,
        IEventBroadcaster? eventBroadcaster = null)
    {
        _settings = settings.Value;
        _logger = logger;
        _fileStore = fileStore;
        _eventBroadcaster = eventBroadcaster;
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

            // Verify it's a comic archive
            if (!IsComicArchive(filePath))
            {
                _logger.LogWarning("File is not a comic archive: {FilePath}", filePath);
                return false;
            }

            // Extract metadata from the archive
            var metadata = await GetMetadataAsync(filePath, cancellationToken);
            
            // Check for duplicates based on metadata
            if (await IsDuplicateAsync(filePath, metadata, cancellationToken))
            {
                _logger.LogInformation("Duplicate detected: {FilePath}", filePath);
                await MoveToDuplicatesAsync(filePath, cancellationToken);
                return true;
            }

            // Rename file based on template if metadata is available
            if (metadata != null && !string.IsNullOrEmpty(metadata.Series))
            {
                var newFilePath = GenerateFileName(metadata, filePath);
                if (newFilePath != filePath && !File.Exists(newFilePath))
                {
                    _logger.LogInformation("Renaming file from {OldPath} to {NewPath}", filePath, newFilePath);
                    File.Move(filePath, newFilePath);
                    filePath = newFilePath;
                }
            }

            // Mark as processed
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

    public Task<Guid> ProcessFilesAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken = default)
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

        // Broadcast initial job status
        _ = BroadcastJobStatusAsync(job);

        // Process files asynchronously
        _ = Task.Run(async () =>
        {
            try
            {
                job.Status = JobStatus.Running;
                await BroadcastJobStatusAsync(job);

                foreach (var file in fileList)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        job.Status = JobStatus.Cancelled;
                        await BroadcastJobStatusAsync(job);
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
                await BroadcastJobStatusAsync(job);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing batch job: {JobId}", jobId);
                job.Status = JobStatus.Failed;
                job.EndTime = DateTime.UtcNow;
                await BroadcastJobStatusAsync(job);
            }
        }, cancellationToken);

        return Task.FromResult(jobId);
    }

    private async Task BroadcastJobStatusAsync(ProcessingJob job)
    {
        if (_eventBroadcaster != null)
        {
            await _eventBroadcaster.BroadcastJobUpdateAsync(
                job.JobId,
                job.Status.ToString().ToLower(),
                job.ProcessedFiles,
                job.TotalFiles,
                job.ProcessedFiles - job.FailedFiles,
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

    public Task<bool> UpdateMetadataAsync(string filePath, ComicMetadata metadata, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(filePath) || !IsComicArchive(filePath))
                return Task.FromResult(false);

            _logger.LogInformation("Updating metadata for: {FilePath}", filePath);

            // Create ComicInfo.xml content
            var comicInfoXml = GenerateComicInfoXml(metadata);
            
            // Create a temporary file
            var tempFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.cbz");
            
            try
            {
                // Create new archive with updated metadata
                using (var sourceArchive = ArchiveFactory.Open(filePath))
                using (var writer = ZipArchive.Create())
                {
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
                
                // Replace original file with updated one using atomic operation
                // File.Replace is safer as it creates a backup and ensures atomicity
                var backupPath = $"{filePath}.backup";
                try
                {
                    File.Replace(tempFile, filePath, backupPath);
                    // Clean up backup if replace succeeded
                    if (File.Exists(backupPath))
                        File.Delete(backupPath);
                }
                catch (Exception)
                {
                    // If Replace fails, restore from backup
                    if (File.Exists(backupPath))
                    {
                        File.Copy(backupPath, filePath, true);
                        File.Delete(backupPath);
                    }
                    throw;
                }
                
                _logger.LogInformation("Successfully updated metadata for: {FilePath}", filePath);
                return Task.FromResult(true);
            }
            finally
            {
                // Clean up temp file if it still exists
                if (File.Exists(tempFile))
                    File.Delete(tempFile);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating metadata for {FilePath}", filePath);
            return Task.FromResult(false);
        }
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

    private async Task<bool> IsDuplicateAsync(string filePath, ComicMetadata? metadata, CancellationToken cancellationToken)
    {
        try
        {
            if (metadata == null || string.IsNullOrEmpty(metadata.Series))
                return false;

            var allFiles = await _fileStore.GetFilteredFilesAsync(null, cancellationToken);
            var fileInfo = new FileInfo(filePath);
            
            // Check for files with same series/issue but different path
            foreach (var file in allFiles)
            {
                if (file.FilePath == filePath)
                    continue;

                if (file.Metadata?.Series == metadata.Series && 
                    file.Metadata?.Issue == metadata.Issue &&
                    Math.Abs(file.FileSize - fileInfo.Length) < 1024 * 10) // Within 10KB
                {
                    _logger.LogInformation("Found duplicate: {File1} matches {File2}", filePath, file.FilePath);
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking for duplicates");
            return false;
        }
    }

    private async Task MoveToDuplicatesAsync(string filePath, CancellationToken cancellationToken)
    {
        try
        {
            var fileName = Path.GetFileName(filePath);
            var duplicatePath = Path.Combine(_settings.DuplicateDirectory, fileName);
            
            // Ensure duplicate directory exists
            Directory.CreateDirectory(_settings.DuplicateDirectory);
            
            // Handle filename conflicts
            var counter = 1;
            while (File.Exists(duplicatePath))
            {
                var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
                var ext = Path.GetExtension(fileName);
                duplicatePath = Path.Combine(_settings.DuplicateDirectory, $"{nameWithoutExt}_{counter}{ext}");
                counter++;
            }
            
            File.Move(filePath, duplicatePath);
            await _fileStore.MarkFileProcessedAsync(duplicatePath, true, cancellationToken);
            
            _logger.LogInformation("Moved duplicate file to: {DuplicatePath}", duplicatePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error moving duplicate file");
        }
    }
}
