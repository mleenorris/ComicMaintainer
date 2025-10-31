using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace ComicMaintainer.WebApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class JobsController : ControllerBase
{
    private readonly IComicProcessorService _processor;
    private readonly ILogger<JobsController> _logger;

    public JobsController(
        IComicProcessorService processor,
        ILogger<JobsController> logger)
    {
        _processor = processor;
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
    public ActionResult<object> ProcessAll()
    {
        try
        {
            // This would get all unprocessed files and process them
            // For now, return a job ID
            var jobId = Guid.NewGuid();
            _logger.LogInformation("Process all files requested, job ID: {JobId}", jobId);
            return Ok(new { jobId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting process all job");
            return StatusCode(500, "Error starting job");
        }
    }

    [HttpPost("process-selected")]
    public async Task<ActionResult<object>> ProcessSelected([FromBody] ProcessSelectedRequest request)
    {
        try
        {
            var jobId = await _processor.ProcessFilesAsync(request.Files);
            return Ok(new { jobId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting process selected job");
            return StatusCode(500, "Error starting job");
        }
    }

    [HttpPost("process-unmarked")]
    public ActionResult<object> ProcessUnmarked()
    {
        try
        {
            var jobId = Guid.NewGuid();
            _logger.LogInformation("Process unmarked files requested, job ID: {JobId}", jobId);
            return Ok(new { jobId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting process unmarked job");
            return StatusCode(500, "Error starting job");
        }
    }

    [HttpPost("rename-unmarked")]
    public ActionResult<object> RenameUnmarked()
    {
        try
        {
            var jobId = Guid.NewGuid();
            _logger.LogInformation("Rename unmarked files requested, job ID: {JobId}", jobId);
            return Ok(new { jobId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting rename unmarked job");
            return StatusCode(500, "Error starting job");
        }
    }

    [HttpPost("normalize-unmarked")]
    public ActionResult<object> NormalizeUnmarked()
    {
        try
        {
            var jobId = Guid.NewGuid();
            _logger.LogInformation("Normalize unmarked files requested, job ID: {JobId}", jobId);
            return Ok(new { jobId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting normalize unmarked job");
            return StatusCode(500, "Error starting job");
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

    public class ProcessSelectedRequest
    {
        public List<string> Files { get; set; } = new();
    }
}
