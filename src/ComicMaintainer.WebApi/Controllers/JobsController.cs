using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace ComicMaintainer.WebApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class JobsController : ControllerBase
{
    private readonly IComicProcessorService _processor;
    private readonly IFileStoreService _fileStore;
    private readonly ILogger<JobsController> _logger;

    public JobsController(
        IComicProcessorService processor,
        IFileStoreService fileStore,
        ILogger<JobsController> logger)
    {
        _processor = processor;
        _fileStore = fileStore;
        _logger = logger;
    }

    [HttpGet("{jobId}")]
    public ActionResult<ProcessingJob> GetJob(Guid jobId)
    {
        try
        {
            var job = _processor.GetJob(jobId);
            if (job == null)
                return NotFound();
            
            return Ok(job);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting job {JobId}", jobId);
            return StatusCode(500, "Error retrieving job");
        }
    }

    [HttpGet("~/api/active-job")]
    public ActionResult<ProcessingJob> GetActiveJob()
    {
        try
        {
            var job = _processor.GetActiveJob();
            if (job == null)
                return Ok(new { active = false });
            
            return Ok(job);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting active job");
            return StatusCode(500, "Error retrieving active job");
        }
    }

    [HttpPost("process-all")]
    public async Task<ActionResult<object>> ProcessAll()
    {
        try
        {
            return await ProcessUnprocessedFilesAsync("Process all files");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting process all job");
            return StatusCode(500, new { error = "Error starting job" });
        }
    }

    [HttpPost("process-selected")]
    public async Task<ActionResult<object>> ProcessSelected([FromBody] ProcessSelectedRequest request)
    {
        try
        {
            if (request.Files == null || request.Files.Count == 0)
            {
                return BadRequest(new { error = "No files specified" });
            }
            
            var jobId = await _processor.ProcessFilesAsync(request.Files);
            _logger.LogInformation("Process selected files requested, job ID: {JobId}, total files: {TotalFiles}", jobId, request.Files.Count);
            
            return Ok(new { job_id = jobId.ToString(), total_items = request.Files.Count });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting process selected job");
            return StatusCode(500, new { error = "Error starting job" });
        }
    }

    [HttpPost("process-unmarked")]
    public async Task<ActionResult<object>> ProcessUnmarked()
    {
        try
        {
            return await ProcessUnprocessedFilesAsync("Process unmarked files");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting process unmarked job");
            return StatusCode(500, new { error = "Error starting job" });
        }
    }

    [HttpPost("rename-unmarked")]
    public async Task<ActionResult<object>> RenameUnmarked()
    {
        try
        {
            // Get all unprocessed files
            var files = await _fileStore.GetFilteredFilesAsync("unprocessed");
            var filePaths = files.Select(f => f.FilePath).ToList();
            
            if (filePaths.Count == 0)
            {
                _logger.LogInformation("No unmarked files found to rename");
                return Ok(new { job_id = Guid.Empty.ToString(), total_items = 0 });
            }
            
            // Start the rename job
            var jobId = await _processor.RenameFilesAsync(filePaths);
            _logger.LogInformation("Rename unmarked files requested, job ID: {JobId}, total files: {TotalFiles}", jobId, filePaths.Count);
            
            return Ok(new { job_id = jobId.ToString(), total_items = filePaths.Count });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting rename unmarked job");
            return StatusCode(500, new { error = "Error starting job" });
        }
    }

    [HttpPost("normalize-unmarked")]
    public async Task<ActionResult<object>> NormalizeUnmarked()
    {
        try
        {
            // Get all unprocessed files
            var files = await _fileStore.GetFilteredFilesAsync("unprocessed");
            var filePaths = files.Select(f => f.FilePath).ToList();
            
            if (filePaths.Count == 0)
            {
                _logger.LogInformation("No unmarked files found to normalize");
                return Ok(new { job_id = Guid.Empty.ToString(), total_items = 0 });
            }
            
            // Start the normalize job
            var jobId = await _processor.NormalizeFilesAsync(filePaths);
            _logger.LogInformation("Normalize unmarked files requested, job ID: {JobId}, total files: {TotalFiles}", jobId, filePaths.Count);
            
            return Ok(new { job_id = jobId.ToString(), total_items = filePaths.Count });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting normalize unmarked job");
            return StatusCode(500, new { error = "Error starting job" });
        }
    }

    [HttpPost("{jobId}/cancel")]
    public ActionResult CancelJob(Guid jobId)
    {
        try
        {
            _logger.LogInformation("Cancel requested for job {JobId}", jobId);
            // In the future, implement job cancellation
            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cancelling job {JobId}", jobId);
            return StatusCode(500, "Error cancelling job");
        }
    }

    private async Task<ActionResult<object>> ProcessUnprocessedFilesAsync(string logMessage)
    {
        // Get all unprocessed files
        var files = await _fileStore.GetFilteredFilesAsync("unprocessed");
        var filePaths = files.Select(f => f.FilePath).ToList();
        
        if (filePaths.Count == 0)
        {
            _logger.LogInformation("{LogMessage}: No unprocessed files found", logMessage);
            return Ok(new { job_id = Guid.Empty.ToString(), total_items = 0 });
        }
        
        // Start the processing job
        var jobId = await _processor.ProcessFilesAsync(filePaths);
        _logger.LogInformation("{LogMessage} requested, job ID: {JobId}, total files: {TotalFiles}", logMessage, jobId, filePaths.Count);
        
        return Ok(new { job_id = jobId.ToString(), total_items = filePaths.Count });
    }

    public class ProcessSelectedRequest
    {
        public List<string> Files { get; set; } = new();
    }
}
