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
}
