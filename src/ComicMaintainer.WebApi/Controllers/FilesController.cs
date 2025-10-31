using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
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

    [HttpGet("{*filePath}/metadata")]
    public async Task<ActionResult<ComicMetadata>> GetMetadata(string filePath)
    {
        try
        {
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

    [HttpPut("{*filePath}/metadata")]
    public async Task<ActionResult> UpdateMetadata(string filePath, [FromBody] ComicMetadata metadata)
    {
        try
        {
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

    [HttpPost("{*filePath}/process")]
    public async Task<ActionResult> ProcessFile(string filePath)
    {
        try
        {
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

    [HttpPost("{*filePath}/mark-processed")]
    public async Task<ActionResult> MarkProcessed(string filePath, [FromBody] bool processed)
    {
        try
        {
            await _fileStore.MarkFileProcessedAsync(filePath, processed);
            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error marking file as processed");
            return StatusCode(500, "Error marking file");
        }
    }
}
