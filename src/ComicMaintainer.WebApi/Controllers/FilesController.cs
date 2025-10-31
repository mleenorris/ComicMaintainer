using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

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
    public async Task<ActionResult<IEnumerable<ComicFile>>> GetFiles([FromQuery] string? filter = null)
    {
        try
        {
            var files = await _fileStore.GetFilteredFilesAsync(filter);
            return Ok(files);
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
    public ActionResult ScanUnmarked()
    {
        try
        {
            _logger.LogInformation("Scan unmarked files requested");
            // Trigger file store to rescan for unmarked files
            return Ok(new { message = "Scan started" });
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
            _logger.LogError(ex, "Error processing file {FilePath}", filePath);
            return StatusCode(500, "Error processing file");
        }
    }

    [HttpPost("~/api/rename-file")]
    public ActionResult RenameSingleFile([FromQuery] string filePath)
    {
        try
        {
            _logger.LogInformation("Rename requested for file: {FilePath}", filePath);
            // Implementation would rename based on metadata
            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error renaming file {FilePath}", filePath);
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
            _logger.LogError(ex, "Error deleting file {FilePath}", filePath);
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
            _logger.LogError(ex, "Error getting tags for file {FilePath}", filePath);
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
            _logger.LogError(ex, "Error updating tags for file {FilePath}", filePath);
            return StatusCode(500, "Error updating tags");
        }
    }

    public class UpdateTagsRequest
    {
        public List<string> Files { get; set; } = new();
        public ComicMetadata Metadata { get; set; } = new();
    }
}
