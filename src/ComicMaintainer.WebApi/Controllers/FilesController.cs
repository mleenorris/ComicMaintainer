using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using Microsoft.AspNetCore.Mvc;

namespace ComicMaintainer.WebApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class FilesController : ControllerBase
{
    private readonly IFileStoreService _fileStore;
    private readonly IComicProcessorService _processor;
    private readonly ILogger<FilesController> _logger;

    public FilesController(
        IFileStoreService fileStore,
        IComicProcessorService processor,
        ILogger<FilesController> logger)
    {
        _fileStore = fileStore;
        _processor = processor;
        _logger = logger;
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
                return Ok(new
                {
                    files = filesList,
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
                return BadRequest("Failed to update metadata");
            
            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating metadata for {FilePath}", filePath);
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

    [HttpPost("mark-processed")]
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

    [HttpPost("~/api/scan-unmarked")]
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

    [HttpPost("~/api/process-file")]
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

    [HttpPost("~/api/rename-file")]
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

    [HttpDelete("~/api/delete-file")]
    public async Task<ActionResult> DeleteFile([FromQuery] string filePath)
    {
        try
        {
            if (System.IO.File.Exists(filePath))
            {
                System.IO.File.Delete(filePath);
                await _fileStore.RemoveFileAsync(filePath);
                return Ok();
            }
            return NotFound();
        }
        catch (Exception ex)
        {
            var sanitizedPath = LoggingHelper.SanitizePathForLog(filePath);
            _logger.LogError(ex, "Error deleting file {FilePath}", sanitizedPath);
            return StatusCode(500, "Error deleting file");
        }
    }

    [HttpGet("~/api/file-tags")]
    public async Task<ActionResult<ComicMetadata>> GetFileTags([FromQuery] string filePath)
    {
        try
        {
            var metadata = await _processor.GetMetadataAsync(filePath);
            return metadata != null ? Ok(metadata) : NotFound();
        }
        catch (Exception ex)
        {
            var sanitizedPath = LoggingHelper.SanitizePathForLog(filePath);
            _logger.LogError(ex, "Error getting tags for file {FilePath}", sanitizedPath);
            return StatusCode(500, "Error getting tags");
        }
    }

    [HttpPut("~/api/file-tags")]
    public async Task<ActionResult> UpdateFileTags([FromQuery] string filePath, [FromBody] ComicMetadata metadata)
    {
        try
        {
            var success = await _processor.UpdateMetadataAsync(filePath, metadata);
            return success ? Ok() : BadRequest("Failed to update tags");
        }
        catch (Exception ex)
        {
            var sanitizedPath = LoggingHelper.SanitizePathForLog(filePath);
            _logger.LogError(ex, "Error updating tags for file {FilePath}", sanitizedPath);
            return StatusCode(500, "Error updating tags");
        }
    }

    public class UpdateTagsRequest
    {
        public List<string> Files { get; set; } = new();
        public ComicMetadata Metadata { get; set; } = new();
    }
}
