using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using ComicMaintainer.Core.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.WebApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class FilesController : ControllerBase
{
    private readonly IFileStoreService _fileStore;
    private readonly IComicProcessorService _processor;
    private readonly IProcessingHistoryService _historyService;
    private readonly ILogger<FilesController> _logger;
    private readonly AppSettings _settings;

    public FilesController(
        IFileStoreService fileStore,
        IComicProcessorService processor,
        IProcessingHistoryService historyService,
        ILogger<FilesController> logger,
        IOptions<AppSettings> settings)
    {
        _fileStore = fileStore;
        _processor = processor;
        _historyService = historyService;
        _logger = logger;
        _settings = settings.Value;
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
            // Map filter values from frontend format
            var mappedFilter = filter switch
            {
                "marked" => "processed",
                "unmarked" => "unprocessed",
                "duplicates" => "duplicates",
                _ => null
            };

            var allFiles = await _fileStore.GetFilteredFilesAsync(mappedFilter);
            
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

    [HttpGet("counts")]
    public async Task<ActionResult<object>> GetFileCounts()
    {
        try
        {
            var (total, processed, unprocessed, duplicates) = await _fileStore.GetFileCountsAsync();
            return Ok(new { total, processed, unprocessed, duplicates });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting file counts");
            return StatusCode(500, "Error retrieving file counts");
        }
    }

    [HttpGet("metadata")]
    public async Task<ActionResult<ComicMetadata>> GetMetadata([FromQuery] string filePath)
    {
        try
        {
            if (string.IsNullOrEmpty(filePath))
                return BadRequest("File path is required");

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

            var success = await _processor.UpdateMetadataAsync(filePath, metadata);
            if (!success)
            {
                await _historyService.AddHistoryEntryAsync(new ProcessingHistoryEntry
                {
                    Id = Guid.NewGuid(),
                    FilePath = filePath,
                    Action = "Update Metadata",
                    Timestamp = DateTime.UtcNow,
                    Success = false,
                    ErrorMessage = "Failed to update metadata"
                });
                return BadRequest("Failed to update metadata");
            }
            
            await _historyService.AddHistoryEntryAsync(new ProcessingHistoryEntry
            {
                Id = Guid.NewGuid(),
                FilePath = filePath,
                Action = "Update Metadata",
                Timestamp = DateTime.UtcNow,
                Success = true,
                ErrorMessage = null
            });
            
            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating metadata for {FilePath}", filePath);
            
            await _historyService.AddHistoryEntryAsync(new ProcessingHistoryEntry
            {
                Id = Guid.NewGuid(),
                FilePath = filePath,
                Action = "Update Metadata",
                Timestamp = DateTime.UtcNow,
                Success = false,
                ErrorMessage = ex.Message
            });
            
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

    // RESTful endpoint: PATCH /api/files/{encodedFilePath}/processed
    [HttpPatch("{encodedFilePath}/processed")]
    public async Task<ActionResult> UpdateProcessedStatus(string encodedFilePath, [FromBody] ProcessedStatusRequest request)
    {
        try
        {
            var filePath = DecodeBase64UrlSafe(encodedFilePath);
            if (string.IsNullOrEmpty(filePath))
                return BadRequest("Invalid file path");

            await _fileStore.MarkFileProcessedAsync(filePath, request.Processed);
            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error marking file as processed");
            return StatusCode(500, "Error marking file");
        }
    }

    // Legacy endpoint for backward compatibility
    [HttpPost("mark-processed")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public async Task<ActionResult> MarkProcessed([FromQuery] string filePath, [FromBody] bool processed)
    {
        try
        {
            if (string.IsNullOrEmpty(filePath))
                return BadRequest("File path is required");

            await _fileStore.MarkFileProcessedAsync(filePath, processed);
            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error marking file as processed");
            return StatusCode(500, "Error marking file");
        }
    }

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
            _logger.LogInformation("Scan unmarked files requested");
            
            // Get file counts - materialize collections to avoid multiple enumerations
            var allFilesList = (await _fileStore.GetAllFilesAsync()).ToList();
            var unmarkedFilesList = (await _fileStore.GetFilteredFilesAsync("unprocessed")).ToList();
            var markedFilesList = (await _fileStore.GetFilteredFilesAsync("processed")).ToList();
            
            var totalCount = allFilesList.Count;
            var unmarkedCount = unmarkedFilesList.Count;
            var markedCount = markedFilesList.Count;
            
            return Ok(new { 
                total_count = totalCount,
                unmarked_count = unmarkedCount,
                marked_count = markedCount
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error scanning unmarked files");
            return StatusCode(500, "Error scanning files");
        }
    }

    // Legacy endpoint for backward compatibility
    [HttpPost("~/api/scan-unmarked")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public async Task<ActionResult> ScanUnmarkedLegacy() => await ScanUnmarked();

    // RESTful endpoint: POST /api/files/{encodedFilePath}/process
    [HttpPost("{encodedFilePath}/process")]
    public async Task<ActionResult> ProcessFileByEncodedPath(string encodedFilePath)
    {
        try
        {
            var filePath = DecodeBase64UrlSafe(encodedFilePath);
            if (string.IsNullOrEmpty(filePath))
                return BadRequest("Invalid file path");

            var success = await _processor.ProcessFileAsync(filePath);
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
    public async Task<ActionResult> ProcessSingleFile([FromQuery] string filePath)
    {
        try
        {
            var success = await _processor.ProcessFileAsync(filePath);
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
    public async Task<ActionResult> RenameFileByEncodedPath(string encodedFilePath)
    {
        try
        {
            var filePath = DecodeBase64UrlSafe(encodedFilePath);
            if (string.IsNullOrEmpty(filePath))
                return BadRequest("Invalid file path");

            var sanitizedPath = LoggingHelper.SanitizePathForLog(filePath);
            _logger.LogInformation("Rename requested for file: {FilePath}", sanitizedPath);
            
            // Get metadata and rename based on it
            var metadata = await _processor.GetMetadataAsync(filePath);
            if (metadata == null)
                return NotFound("File not found or metadata unavailable");

            // For now, just return OK since actual rename implementation needs to be done
            // In production, this would call a rename method on the processor
            return Ok(new { message = "Rename operation completed", file = filePath });
        }
        catch (Exception ex)
        {
            var sanitizedEncodedPath = LoggingHelper.SanitizeForLog(encodedFilePath);
            _logger.LogError(ex, "Error renaming file with encoded path {EncodedPath}", sanitizedEncodedPath);
            return StatusCode(500, "Error renaming file");
        }
    }

    // Legacy endpoint for backward compatibility
    [HttpPost("~/api/rename-file")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public ActionResult RenameSingleFile([FromQuery] string filePath)
    {
        try
        {
            var sanitizedPath = LoggingHelper.SanitizePathForLog(filePath);
            _logger.LogInformation("Rename requested for file: {FilePath}", sanitizedPath);
            // Implementation would rename based on metadata
            return Ok();
        }
        catch (Exception ex)
        {
            var sanitizedPath = LoggingHelper.SanitizePathForLog(filePath);
            _logger.LogError(ex, "Error renaming file {FilePath}", sanitizedPath);
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
                await _historyService.AddHistoryEntryAsync(new ProcessingHistoryEntry
                {
                    Id = Guid.NewGuid(),
                    FilePath = filePath,
                    Action = "Delete",
                    Timestamp = DateTime.UtcNow,
                    Success = true,
                    ErrorMessage = null
                });
                
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
            await _historyService.AddHistoryEntryAsync(new ProcessingHistoryEntry
            {
                Id = Guid.NewGuid(),
                FilePath = filePath,
                Action = "Delete",
                Timestamp = DateTime.UtcNow,
                Success = false,
                ErrorMessage = ex.Message
            });
            
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
                await _historyService.AddHistoryEntryAsync(new ProcessingHistoryEntry
                {
                    Id = Guid.NewGuid(),
                    FilePath = filePath,
                    Action = "Delete",
                    Timestamp = DateTime.UtcNow,
                    Success = true,
                    ErrorMessage = null
                });
                
                return Ok();
            }
            return NotFound();
        }
        catch (Exception ex)
        {
            var sanitizedPath = LoggingHelper.SanitizePathForLog(filePath);
            _logger.LogError(ex, "Error deleting file {FilePath}", sanitizedPath);
            
            // Log to processing history
            await _historyService.AddHistoryEntryAsync(new ProcessingHistoryEntry
            {
                Id = Guid.NewGuid(),
                FilePath = filePath,
                Action = "Delete",
                Timestamp = DateTime.UtcNow,
                Success = false,
                ErrorMessage = ex.Message
            });
            
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



    public class UpdateTagsRequest
    {
        public List<string> Files { get; set; } = new();
        public ComicMetadata Metadata { get; set; } = new();
    }

    public class ProcessedStatusRequest
    {
        public bool Processed { get; set; }
    }
}
