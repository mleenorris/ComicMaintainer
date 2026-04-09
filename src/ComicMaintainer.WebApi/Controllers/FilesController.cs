using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Text.RegularExpressions;

namespace ComicMaintainer.WebApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class FilesController : ControllerBase
{
    private static readonly Regex FolderCombineKeySanitizer = new("[^a-z0-9]+", RegexOptions.Compiled);
    private static readonly Regex FileNameSeriesSuffixSanitizer = new(
        @"\s*(?:-|_)?\s*(?:ch|chapter|issue|#)?\s*\d+(?:\.\d+)?[a-z]?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly IFileStoreService _fileStore;
    private readonly IComicProcessorService _processor;
    private readonly IProcessingHistoryService _historyService;
    private readonly ISeriesLibraryService _seriesLibrary;
    private readonly ILogger<FilesController> _logger;
    private readonly AppSettings _settings;
    private readonly IDbContextFactory<ComicMaintainerDbContext>? _dbContextFactory;

    public FilesController(
        IFileStoreService fileStore,
        IComicProcessorService processor,
        IProcessingHistoryService historyService,
        ISeriesLibraryService seriesLibrary,
        ILogger<FilesController> logger,
        IOptions<AppSettings> settings,
        IDbContextFactory<ComicMaintainerDbContext>? dbContextFactory = null)
    {
        _fileStore = fileStore;
        _processor = processor;
        _historyService = historyService;
        _seriesLibrary = seriesLibrary;
        _logger = logger;
        _settings = settings.Value;
        _dbContextFactory = dbContextFactory;
    }

    /// <summary>
    /// Validates that a file path is within the watched directory to prevent path traversal attacks
    /// </summary>
    private bool IsPathSafe(string filePath)
    {
        try
        {
            var fullPath = Path.GetFullPath(filePath);
            var watchedDir = Path.GetFullPath(_settings.WatchedDirectory);
            return fullPath.StartsWith(watchedDir, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    [HttpGet]
    public async Task<ActionResult<object>> GetFiles(
        [FromQuery] string? filter = null,
        [FromQuery] string? search = null,
        [FromQuery] int page = 1,
        [FromQuery] int per_page = 100,
        [FromQuery] string? sort = "name",
        [FromQuery] string? direction = "asc")
    {
        try
        {
            _logger.LogDebug("GetFiles: Request received - Filter: {Filter}, Search: {Search}, Page: {Page}, PerPage: {PerPage}, Sort: {Sort}, Direction: {Direction}", 
                LoggingHelper.SanitizeForLog(filter), LoggingHelper.SanitizeForLog(search), page, per_page, LoggingHelper.SanitizeForLog(sort), LoggingHelper.SanitizeForLog(direction));
            
            var mappedFilter = MapFilter(filter);

            _logger.LogDebug("GetFiles: Mapped filter from '{OriginalFilter}' to '{MappedFilter}'", LoggingHelper.SanitizeForLog(filter), LoggingHelper.SanitizeForLog(mappedFilter));

            var allFiles = await _fileStore.GetFilteredFilesAsync(mappedFilter);
            _logger.LogDebug("GetFiles: Retrieved {FileCount} files after applying filter '{MappedFilter}'", allFiles.Count(), mappedFilter);
            
            // Apply search if provided
            if (!string.IsNullOrEmpty(search))
            {
                allFiles = allFiles.Where(f => 
                    f.FileName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    f.FilePath.Contains(search, StringComparison.OrdinalIgnoreCase));
            }

            // Apply sorting
            allFiles = (sort?.ToLower(), direction?.ToLower()) switch
            {
                ("name", "asc") => allFiles.OrderBy(f => f.FileName),
                ("name", "desc") => allFiles.OrderByDescending(f => f.FileName),
                ("date", "asc") => allFiles.OrderBy(f => f.LastModified),
                ("date", "desc") => allFiles.OrderByDescending(f => f.LastModified),
                ("size", "asc") => allFiles.OrderBy(f => f.FileSize),
                ("size", "desc") => allFiles.OrderByDescending(f => f.FileSize),
                _ => allFiles.OrderBy(f => f.FileName)
            };

            var filesList = allFiles.ToList();
            var totalFiles = filesList.Count;
            
            // Get unmarked count (all unprocessed, non-duplicate files)
            var allUnmarked = await _fileStore.GetFilteredFilesAsync("unprocessed");
            var unmarkedCount = allUnmarked.Count();

            // Handle pagination (-1 means return all)
            if (per_page == -1)
            {
                var allFilesDto = filesList.Select(FileDto.FromComicFile).ToList();
                return Ok(new
                {
                    files = allFilesDto,
                    page = 1,
                    total_pages = 1,
                    total_files = totalFiles,
                    unmarked_count = unmarkedCount
                });
            }

            // Calculate pagination
            var totalPages = (int)Math.Ceiling((double)totalFiles / per_page);
            page = Math.Max(1, Math.Min(page, totalPages == 0 ? 1 : totalPages));
            
            var pagedFiles = filesList
                .Skip((page - 1) * per_page)
                .Take(per_page)
                .Select(FileDto.FromComicFile)
                .ToList();

            return Ok(new
            {
                files = pagedFiles,
                page,
                total_pages = totalPages,
                total_files = totalFiles,
                unmarked_count = unmarkedCount
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting files");
            return StatusCode(500, "Error retrieving files");
        }
    }

    [HttpGet("series")]
    public async Task<ActionResult<object>> GetSeries(
        [FromQuery] string? filter = null,
        [FromQuery] string? search = null,
        [FromQuery] int page = 1,
        [FromQuery] int per_page = 100,
        [FromQuery] string? sort = "name",
        [FromQuery] string? direction = "asc",
        CancellationToken cancellationToken = default)
    {
        try
        {
            var mappedFilter = MapFilter(filter);
            var result = await _seriesLibrary.GetSeriesAsync(mappedFilter, search, page, per_page, sort, direction, cancellationToken);
            var allUnmarked = await _fileStore.GetFilteredFilesAsync("unprocessed", cancellationToken);

            return Ok(new
            {
                series = result.Series,
                page = result.Page,
                total_pages = result.TotalPages,
                total_series = result.TotalSeries,
                unmarked_count = allUnmarked.Count()
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting series library");
            return StatusCode(500, "Error retrieving series library");
        }
    }

    [HttpGet("counts")]
    public async Task<ActionResult<object>> GetFileCounts()
    {
        try
        {
            var (total, processed, unprocessed, duplicates) = await _fileStore.GetFileCountsAsync();
            var combinableFolders = 0;

            try
            {
                combinableFolders = await GetCombinableFolderCountAsync();
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
            {
                _logger.LogWarning(ex, "Error getting combinable folder count");
            }

            return Ok(new { total, processed, unprocessed, duplicates, combinableFolders });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting file counts");
            return StatusCode(500, "Error retrieving file counts");
        }
    }

    private async Task<int> GetCombinableFolderCountAsync(CancellationToken cancellationToken = default)
    {
        var files = ((await _fileStore.GetAllFilesAsync(cancellationToken)) ?? Enumerable.Empty<ComicFile>())
            .Where(file => !string.IsNullOrWhiteSpace(file.FilePath))
            .ToList();

        if (files.Count < 2)
        {
            return 0;
        }

        var addedAtLookup = await GetAddedAtLookupAsync(cancellationToken);
        var sourceDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<(string GroupKey, string Directory, DateTime AddedAt)>(files.Count);

        foreach (var file in files)
        {
            var directory = !string.IsNullOrWhiteSpace(file.Directory)
                ? file.Directory
                : Path.GetDirectoryName(file.FilePath);

            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            var groupKey = BuildFolderCombineGroupKey(file);
            if (string.IsNullOrWhiteSpace(groupKey))
            {
                continue;
            }

            var addedAt = addedAtLookup.TryGetValue(file.FilePath, out var createdAt)
                ? createdAt
                : file.LastModified;

            entries.Add((groupKey, directory, addedAt));
        }

        foreach (var group in entries.GroupBy(entry => entry.GroupKey, StringComparer.OrdinalIgnoreCase))
        {
            var directories = group
                .GroupBy(entry => entry.Directory, StringComparer.OrdinalIgnoreCase)
                .Select(directoryGroup => new
                {
                    Directory = directoryGroup.Key,
                    LatestAddedAt = directoryGroup.Max(entry => entry.AddedAt)
                })
                .OrderByDescending(entry => entry.LatestAddedAt)
                .ThenBy(entry => entry.Directory, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (directories.Count < 2)
            {
                continue;
            }

            foreach (var directory in directories.Skip(1))
            {
                sourceDirectories.Add(directory.Directory);
            }
        }

        return sourceDirectories.Count;
    }

    private async Task<Dictionary<string, DateTime>> GetAddedAtLookupAsync(CancellationToken cancellationToken)
    {
        if (_dbContextFactory is null)
        {
            return new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        }

        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var addedAtEntries = await dbContext.ComicFiles
            .AsNoTracking()
            .Select(file => new { file.FilePath, file.CreatedAt })
            .ToListAsync(cancellationToken);

        return addedAtEntries
            .Where(file => !string.IsNullOrWhiteSpace(file.FilePath))
            .GroupBy(file => file.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Max(file => file.CreatedAt),
                StringComparer.OrdinalIgnoreCase);
    }

    private static string? BuildFolderCombineGroupKey(ComicFile file)
    {
        var seriesName = FirstNonEmpty(
            file.Metadata?.Series,
            ExtractSeriesNameFromFileName(file),
            Path.GetFileName(file.Directory),
            Path.GetFileName(Path.GetDirectoryName(file.FilePath) ?? string.Empty));

        if (string.IsNullOrWhiteSpace(seriesName))
        {
            return null;
        }

        var normalizedSeries = FolderCombineKeySanitizer.Replace(seriesName.Trim().ToLowerInvariant(), "-").Trim('-');
        if (string.IsNullOrWhiteSpace(normalizedSeries))
        {
            return null;
        }

        var volume = file.Metadata?.Volume?.Trim();
        return string.IsNullOrWhiteSpace(volume)
            ? normalizedSeries
            : $"{normalizedSeries}|{volume.ToLowerInvariant()}";
    }

    private static string? ExtractSeriesNameFromFileName(ComicFile file)
    {
        var name = Path.GetFileNameWithoutExtension(
            !string.IsNullOrWhiteSpace(file.FileName)
                ? file.FileName
                : file.FilePath);

        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var candidate = FileNameSeriesSuffixSanitizer.Replace(name, string.Empty)
            .Trim(' ', '-', '_', '.', '#');

        return string.IsNullOrWhiteSpace(candidate)
            ? null
            : candidate.Replace('_', ' ').Trim();
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    [HttpGet("metadata")]
    public async Task<ActionResult<ComicMetadata>> GetMetadata([FromQuery] string filePath)
    {
        try
        {
            if (string.IsNullOrEmpty(filePath))
                return BadRequest("File path is required");

            // Validate path is within watched directory to prevent path traversal attacks
            if (!IsPathSafe(filePath))
            {
                _logger.LogWarning("Attempt to get metadata for file outside watched directory: {FilePath}", LoggingHelper.SanitizeForLog(LoggingHelper.SanitizePathForLog(filePath)));
                return BadRequest("File path is outside the allowed directory");
            }

            var metadata = await _processor.GetMetadataAsync(filePath);
            if (metadata == null)
                return NotFound();
            
            return Ok(metadata);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting metadata for {FilePath}", filePath);
            return StatusCode(500, "Error retrieving metadata");
        }
    }

    [HttpPut("metadata")]
    public async Task<ActionResult> UpdateMetadata([FromQuery] string filePath, [FromBody] ComicMetadata metadata)
    {
        try
        {
            if (string.IsNullOrEmpty(filePath))
                return BadRequest("File path is required");

            // Validate path is within watched directory to prevent path traversal attacks
            if (!IsPathSafe(filePath))
            {
                _logger.LogWarning("Attempt to update metadata for file outside watched directory: {FilePath}", LoggingHelper.SanitizeForLog(LoggingHelper.SanitizePathForLog(filePath)));
                return BadRequest("File path is outside the allowed directory");
            }

            // Capture before state
            var beforeMetadata = await _processor.GetMetadataAsync(filePath);
            
            var success = await _processor.UpdateMetadataAsync(filePath, metadata);
            if (!success)
            {
                await LogHistoryAsync(filePath, "Update Metadata", false, "Failed to update metadata");
                return BadRequest("Failed to update metadata");
            }
            
            // Log with before/after metadata
            var filename = Path.GetFileName(filePath);
            await LogHistoryWithChangesAsync(filePath, "Update Metadata", true, null,
                filename, filename, beforeMetadata, metadata);
            
            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating metadata for {FilePath}", filePath);
            await LogHistoryAsync(filePath, "Update Metadata", false, ex.Message);
            return StatusCode(500, "Error updating metadata");
        }
    }

    [HttpPost("process")]
    public async Task<ActionResult> ProcessFile([FromQuery] string filePath)
    {
        try
        {
            if (string.IsNullOrEmpty(filePath))
                return BadRequest("File path is required");

            // Validate path is within watched directory to prevent path traversal attacks
            if (!IsPathSafe(filePath))
            {
                _logger.LogWarning("Attempt to process file outside watched directory: {FilePath}", LoggingHelper.SanitizeForLog(LoggingHelper.SanitizePathForLog(filePath)));
                return BadRequest("File path is outside the allowed directory");
            }

            var success = await _processor.ProcessFileAsync(filePath);
            if (!success)
                return BadRequest("Failed to process file");
            
            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing file {FilePath}", filePath);
            return StatusCode(500, "Error processing file");
        }
    }

    [HttpPost("process-batch")]
    public async Task<ActionResult<Guid>> ProcessBatch([FromBody] List<string> filePaths)
    {
        try
        {
            var jobId = await _processor.ProcessFilesAsync(filePaths);
            return Ok(new { jobId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting batch processing");
            return StatusCode(500, "Error starting batch processing");
        }
    }

    // Note: Processed status is now computed from renamed && normalized states
    // No longer accepting manual updates to processed status

    [HttpPost("tags")]
    public async Task<ActionResult> UpdateTags([FromBody] UpdateTagsRequest request)
    {
        try
        {
            foreach (var file in request.Files)
            {
                if (!string.IsNullOrEmpty(file))
                {
                    await _processor.UpdateMetadataAsync(file, request.Metadata);
                }
            }
            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating tags");
            return StatusCode(500, "Error updating tags");
        }
    }

    // RESTful endpoint: GET /api/files/scan-unmarked (read operation, no side effects)
    [HttpGet("scan-unmarked")]
    public async Task<ActionResult> ScanUnmarked()
    {
        try
        {
            _logger.LogInformation(LoggingHelper.WithWebsitePrefix("ScanUnmarked: Scan unmarked files requested"));
            _logger.LogDebug(LoggingHelper.WithWebsitePrefix("ScanUnmarked: Starting file count retrieval from file store"));
            
            // Get file counts - materialize collections to avoid multiple enumerations
            var allFilesList = (await _fileStore.GetAllFilesAsync()).ToList();
            _logger.LogDebug(LoggingHelper.WithWebsitePrefix("ScanUnmarked: Retrieved {TotalCount} total files"), allFilesList.Count);
            
            var unmarkedFilesList = (await _fileStore.GetFilteredFilesAsync("unprocessed")).ToList();
            _logger.LogDebug(LoggingHelper.WithWebsitePrefix("ScanUnmarked: Retrieved {UnmarkedCount} unprocessed files after filtering"), unmarkedFilesList.Count);
            
            var markedFilesList = (await _fileStore.GetFilteredFilesAsync("processed")).ToList();
            _logger.LogDebug(LoggingHelper.WithWebsitePrefix("ScanUnmarked: Retrieved {MarkedCount} processed files after filtering"), markedFilesList.Count);
            
            var totalCount = allFilesList.Count;
            var unmarkedCount = unmarkedFilesList.Count;
            var markedCount = markedFilesList.Count;
            
            _logger.LogInformation(LoggingHelper.WithWebsitePrefix("ScanUnmarked: File counts - Total: {TotalCount}, Unmarked: {UnmarkedCount}, Marked: {MarkedCount}"), 
                totalCount, unmarkedCount, markedCount);
            
            return Ok(new { 
                total_count = totalCount,
                unmarked_count = unmarkedCount,
                marked_count = markedCount
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, LoggingHelper.WithWebsitePrefix("ScanUnmarked: Error scanning unmarked files"));
            return StatusCode(500, "Error scanning files");
        }
    }

    // Legacy endpoint for backward compatibility
    [HttpPost("~/api/scan-unmarked")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public async Task<ActionResult> ScanUnmarkedLegacy() => await ScanUnmarked();

    // RESTful endpoint: POST /api/files/{encodedFilePath}/process
    [HttpPost("{encodedFilePath}/process")]
    public async Task<ActionResult> ProcessFileByEncodedPath(string encodedFilePath, [FromQuery] bool forceReprocess = false)
    {
        try
        {
            var filePath = DecodeBase64UrlSafe(encodedFilePath);
            if (string.IsNullOrEmpty(filePath))
                return BadRequest("Invalid file path");

            var success = await _processor.ProcessFileAsync(filePath, forceReprocess);
            return success ? Ok() : BadRequest("Failed to process file");
        }
        catch (Exception ex)
        {
            var sanitizedEncodedPath = LoggingHelper.SanitizeForLog(encodedFilePath);
            _logger.LogError(ex, "Error processing file with encoded path {EncodedPath}", sanitizedEncodedPath);
            return StatusCode(500, "Error processing file");
        }
    }

    // Legacy endpoint for backward compatibility
    [HttpPost("~/api/process-file")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public async Task<ActionResult> ProcessSingleFile([FromQuery] string filePath, [FromQuery] bool forceReprocess = false)
    {
        try
        {
            // Validate path is within watched directory to prevent path traversal attacks
            if (!IsPathSafe(filePath))
            {
                _logger.LogWarning("Attempt to process file outside watched directory: {FilePath}", LoggingHelper.SanitizeForLog(LoggingHelper.SanitizePathForLog(filePath)));
                return BadRequest("File path is outside the allowed directory");
            }

            var success = await _processor.ProcessFileAsync(filePath, forceReprocess);
            return success ? Ok() : BadRequest("Failed to process file");
        }
        catch (Exception ex)
        {
            var sanitizedPath = LoggingHelper.SanitizePathForLog(filePath);
            _logger.LogError(ex, "Error processing file {FilePath}", sanitizedPath);
            return StatusCode(500, "Error processing file");
        }
    }

    // RESTful endpoint: POST /api/files/{encodedFilePath}/rename
    [HttpPost("{encodedFilePath}/rename")]
    public async Task<ActionResult> RenameFileByEncodedPath(string encodedFilePath, [FromQuery] bool forceReprocess = false, CancellationToken cancellationToken = default)
    {
        try
        {
            var filePath = DecodeBase64UrlSafe(encodedFilePath);
            if (string.IsNullOrEmpty(filePath))
                return BadRequest("Invalid file path");

            if (!IsPathSafe(filePath))
                return BadRequest("Invalid file path");

            var sanitizedPath = LoggingHelper.SanitizePathForLog(filePath);
            _logger.LogInformation(LoggingHelper.WithWebsitePrefix("Rename requested for file: {FilePath}"), sanitizedPath);
            
            // Use the batch rename method with a single file
            var jobId = await _processor.RenameFilesAsync(new[] { filePath }, forceReprocess, cancellationToken);
            
            return Ok(new { message = "Rename job started", jobId });
        }
        catch (Exception ex)
        {
            var sanitizedEncodedPath = LoggingHelper.SanitizeForLog(encodedFilePath);
            _logger.LogError(ex, LoggingHelper.WithWebsitePrefix("Error renaming file with encoded path {EncodedPath}"), sanitizedEncodedPath);
            return StatusCode(500, "Error renaming file");
        }
    }

    // Legacy endpoint for backward compatibility
    [HttpPost("~/api/rename-file")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public async Task<ActionResult> RenameSingleFile([FromQuery] string filePath, [FromQuery] bool forceReprocess = false, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!IsPathSafe(filePath))
                return BadRequest("Invalid file path");

            var sanitizedPath = LoggingHelper.SanitizePathForLog(filePath);
            _logger.LogInformation(LoggingHelper.WithWebsitePrefix("Rename requested for file: {FilePath}"), sanitizedPath);
            
            // Use the batch rename method with a single file
            var jobId = await _processor.RenameFilesAsync(new[] { filePath }, forceReprocess, cancellationToken);
            
            return Ok(new { jobId });
        }
        catch (Exception ex)
        {
            var sanitizedPath = LoggingHelper.SanitizePathForLog(filePath);
            _logger.LogError(ex, LoggingHelper.WithWebsitePrefix("Error renaming file {FilePath}"), sanitizedPath);
            return StatusCode(500, "Error renaming file");
        }
    }

    // RESTful endpoint: DELETE /api/files/{encodedFilePath}
    [HttpDelete("~/api/files/{encodedFilePath}")]
    public async Task<ActionResult> DeleteFileByEncodedPath(string encodedFilePath)
    {
        try
        {
            // Decode the base64 URL-safe encoded file path
            var filePath = DecodeBase64UrlSafe(encodedFilePath);
            if (string.IsNullOrEmpty(filePath))
                return BadRequest("Invalid file path");

            // Validate path is within watched directory to prevent path traversal attacks
            if (!IsPathSafe(filePath))
            {
                _logger.LogWarning("Attempt to delete file outside watched directory: {EncodedPath}", LoggingHelper.SanitizeForLog(encodedFilePath));
                return BadRequest("File path is outside the allowed directory");
            }

            if (System.IO.File.Exists(filePath))
            {
                // Remove from file store first (unlikely to fail), then delete physical file
                // This order prevents orphaned file store entries if file deletion fails
                await _fileStore.RemoveFileAsync(filePath);
                System.IO.File.Delete(filePath);
                
                // Log to processing history
                await LogHistoryAsync(filePath, "Delete", true);
                
                return Ok();
            }
            return NotFound();
        }
        catch (Exception ex)
        {
            var sanitizedEncodedPath = LoggingHelper.SanitizeForLog(encodedFilePath);
            _logger.LogError(ex, "Error deleting file with encoded path {EncodedPath}", sanitizedEncodedPath);
            
            // Log to processing history
            var filePath = DecodeBase64UrlSafe(encodedFilePath);
            await LogHistoryAsync(filePath, "Delete", false, ex.Message);
            
            return StatusCode(500, "Error deleting file");
        }
    }

    // Legacy endpoint for backward compatibility: DELETE /api/delete-file?filePath=...
    [HttpDelete("~/api/delete-file")]
    public async Task<ActionResult> DeleteFile([FromQuery] string filePath)
    {
        try
        {
            // Validate path is within watched directory to prevent path traversal attacks
            if (!IsPathSafe(filePath))
            {
                _logger.LogWarning("Attempt to delete file outside watched directory: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                return BadRequest("File path is outside the allowed directory");
            }

            if (System.IO.File.Exists(filePath))
            {
                // Remove from file store first (unlikely to fail), then delete physical file
                // This order prevents orphaned file store entries if file deletion fails
                await _fileStore.RemoveFileAsync(filePath);
                System.IO.File.Delete(filePath);
                
                // Log to processing history
                await LogHistoryAsync(filePath, "Delete", true);
                
                return Ok();
            }
            return NotFound();
        }
        catch (Exception ex)
        {
            var sanitizedPath = LoggingHelper.SanitizePathForLog(filePath);
            _logger.LogError(ex, "Error deleting file {FilePath}", sanitizedPath);
            
            // Log to processing history
            await LogHistoryAsync(filePath, "Delete", false, ex.Message);
            
            return StatusCode(500, "Error deleting file");
        }
    }

    [HttpGet("~/api/files/{encodedFilePath}/tags")]
    public async Task<ActionResult<ComicMetadata>> GetFileTags(string encodedFilePath)
    {
        try
        {
            // Decode the base64 URL-safe encoded file path
            var filePath = DecodeBase64UrlSafe(encodedFilePath);
            if (string.IsNullOrEmpty(filePath))
                return BadRequest("Invalid file path");

            var metadata = await _processor.GetMetadataAsync(filePath);
            return metadata != null ? Ok(metadata) : NotFound();
        }
        catch (Exception ex)
        {
            var sanitizedEncodedPath = LoggingHelper.SanitizeForLog(encodedFilePath);
            _logger.LogError(ex, "Error getting tags for encoded path {EncodedPath}", sanitizedEncodedPath);
            return StatusCode(500, "Error getting tags");
        }
    }

    [HttpPut("~/api/files/{encodedFilePath}/tags")]
    public async Task<ActionResult> UpdateFileTags(string encodedFilePath, [FromBody] ComicMetadata metadata)
    {
        try
        {
            // Decode the base64 URL-safe encoded file path
            var filePath = DecodeBase64UrlSafe(encodedFilePath);
            if (string.IsNullOrEmpty(filePath))
                return BadRequest("Invalid file path");

            var success = await _processor.UpdateMetadataAsync(filePath, metadata);
            return success ? Ok() : BadRequest("Failed to update tags");
        }
        catch (Exception ex)
        {
            var sanitizedEncodedPath = LoggingHelper.SanitizeForLog(encodedFilePath);
            _logger.LogError(ex, "Error updating tags for encoded path {EncodedPath}", sanitizedEncodedPath);
            return StatusCode(500, "Error updating tags");
        }
    }

    private static string DecodeBase64UrlSafe(string input)
    {
        try
        {
            // Convert URL-safe base64 back to standard base64
            var base64 = input.Replace('-', '+').Replace('_', '/');
            // Add padding if necessary
            switch (base64.Length % 4)
            {
                case 2: base64 += "=="; break;
                case 3: base64 += "="; break;
            }
            var bytes = Convert.FromBase64String(base64);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return string.Empty;
        }
    }



    /// <summary>
    /// Helper method to log processing history entries
    /// </summary>
    private async Task LogHistoryAsync(string filePath, string action, bool success, string? errorMessage = null)
    {
        await _historyService.AddHistoryEntryAsync(new ProcessingHistoryEntry
        {
            Id = Guid.NewGuid(),
            FilePath = filePath,
            Action = action,
            Timestamp = DateTime.UtcNow,
            Success = success,
            ErrorMessage = errorMessage
        });
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
        ComicMetadata? afterMetadata)
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
        });
    }

    /// <summary>
    /// Remove stale database entries for files that no longer exist on disk
    /// </summary>
    [HttpPost("cleanup-stale")]
    public async Task<ActionResult<object>> CleanupStaleEntries(CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Cleanup stale entries endpoint called");
            var removedCount = await _fileStore.CleanupStaleEntriesAsync(cancellationToken);
            
            return Ok(new 
            { 
                success = true, 
                removedCount = removedCount,
                message = $"Removed {removedCount} stale database entries"
            });
        }
        catch (OperationCanceledException)
        {
            return StatusCode(499, new { error = "Cleanup operation was cancelled" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during stale entry cleanup");
            return StatusCode(500, new { error = "Failed to cleanup stale entries" });
        }
    }

    public class UpdateTagsRequest
    {
        public List<string> Files { get; set; } = new();
        public ComicMetadata Metadata { get; set; } = new();
    }

    public class ProcessedStatusRequest
    {
        public bool Processed { get; set; }
    }

    /// <summary>
    /// Mark a single file as read or unread
    /// </summary>
    [HttpPost("~/api/files/{encodedFilePath}/read")]
    public async Task<ActionResult> MarkFileRead(string encodedFilePath, [FromBody] ReadStatusRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var filePath = DecodeBase64UrlSafe(encodedFilePath);
            if (string.IsNullOrEmpty(filePath))
                return BadRequest("Invalid file path");

            if (!IsPathSafe(filePath))
                return BadRequest("Invalid file path");

            await _fileStore.MarkFileReadAsync(filePath, request.Read, cancellationToken);
            return Ok();
        }
        catch (Exception ex)
        {
            var sanitizedEncodedPath = LoggingHelper.SanitizeForLog(encodedFilePath);
            _logger.LogError(ex, "Error marking file read status for encoded path {EncodedPath}", sanitizedEncodedPath);
            return StatusCode(500, "Error updating read status");
        }
    }

    /// <summary>
    /// Mark multiple files as read or unread
    /// </summary>
    [HttpPost("~/api/files/read-batch")]
    public async Task<ActionResult> MarkFilesReadBatch([FromBody] ReadBatchRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            if (request.Files == null || !request.Files.Any())
                return BadRequest("No files provided");

            // Validate all paths are safe
            foreach (var filePath in request.Files)
            {
                if (!IsPathSafe(filePath))
                {
                    _logger.LogWarning("Attempt to mark read status for file outside watched directory: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                    return BadRequest("One or more file paths are outside the allowed directory");
                }
            }

            await _fileStore.MarkFilesReadAsync(request.Files, request.Read, cancellationToken);
            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error marking files read status");
            return StatusCode(500, "Error updating read status");
        }
    }

    public class ReadStatusRequest
    {
        public bool Read { get; set; }
    }

    public class ReadBatchRequest
    {
        public List<string> Files { get; set; } = new();
        public bool Read { get; set; }
    }

    private static string? MapFilter(string? filter) => filter switch
    {
        "marked" => "processed",
        "unmarked" => "unprocessed",
        "duplicates" => "duplicates",
        "renamed" => "renamed",
        "normalized" => "normalized",
        "read" => "read",
        "unread" => "unread",
        _ => null
    };
}
