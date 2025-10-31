using ComicMaintainer.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace ComicMaintainer.WebApi.Controllers;

[ApiController]
[Route("api")]
public class ProcessController : ControllerBase
{
    private readonly IComicProcessorService _processor;
    private readonly ILogger<ProcessController> _logger;

    public ProcessController(
        IComicProcessorService processor,
        ILogger<ProcessController> logger)
    {
        _processor = processor;
        _logger = logger;
    }

    [HttpPost("process-all")]
    public ActionResult<object> ProcessAll([FromQuery] bool stream = false)
    {
        try
        {
            var jobId = Guid.NewGuid();
            if (stream)
            {
                _logger.LogInformation("Process all files requested (streaming), job ID: {JobId}", jobId);
                return Ok(new { jobId, streaming = true });
            }

            return Ok(new { jobId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing all files");
            return StatusCode(500, "Error processing files");
        }
    }

    [HttpPost("process-selected")]
    public async Task<ActionResult<object>> ProcessSelected([FromBody] ProcessRequest request, [FromQuery] bool stream = false)
    {
        try
        {
            var jobId = await _processor.ProcessFilesAsync(request.Files);
            return Ok(new { jobId, streaming = stream });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing selected files");
            return StatusCode(500, "Error processing files");
        }
    }

    [HttpPost("rename-all")]
    public ActionResult<object> RenameAll([FromQuery] bool stream = false)
    {
        try
        {
            var jobId = Guid.NewGuid();
            _logger.LogInformation("Rename all files requested, job ID: {JobId}", jobId);
            return Ok(new { jobId, streaming = stream });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error renaming all files");
            return StatusCode(500, "Error renaming files");
        }
    }

    [HttpPost("rename-selected")]
    public ActionResult<object> RenameSelected([FromBody] ProcessRequest request, [FromQuery] bool stream = false)
    {
        try
        {
            var jobId = Guid.NewGuid();
            _logger.LogInformation("Rename selected files requested, job ID: {JobId}", jobId);
            return Ok(new { jobId, streaming = stream });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error renaming selected files");
            return StatusCode(500, "Error renaming files");
        }
    }

    [HttpPost("normalize-all")]
    public ActionResult<object> NormalizeAll([FromQuery] bool stream = false)
    {
        try
        {
            var jobId = Guid.NewGuid();
            _logger.LogInformation("Normalize all files requested, job ID: {JobId}", jobId);
            return Ok(new { jobId, streaming = stream });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error normalizing all files");
            return StatusCode(500, "Error normalizing files");
        }
    }

    [HttpPost("normalize-selected")]
    public ActionResult<object> NormalizeSelected([FromBody] ProcessRequest request, [FromQuery] bool stream = false)
    {
        try
        {
            var jobId = Guid.NewGuid();
            _logger.LogInformation("Normalize selected files requested, job ID: {JobId}", jobId);
            return Ok(new { jobId, streaming = stream });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error normalizing selected files");
            return StatusCode(500, "Error normalizing files");
        }
    }

    public class ProcessRequest
    {
        public List<string> Files { get; set; } = new();
    }
}
