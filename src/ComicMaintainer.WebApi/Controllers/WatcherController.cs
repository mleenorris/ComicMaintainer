using ComicMaintainer.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace ComicMaintainer.WebApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class WatcherController : ControllerBase
{
    private readonly IFileWatcherService _watcher;
    private readonly ILogger<WatcherController> _logger;

    public WatcherController(
        IFileWatcherService watcher,
        ILogger<WatcherController> logger)
    {
        _watcher = watcher;
        _logger = logger;
    }

    // RESTful endpoint: GET /api/watcher
    [HttpGet]
    public ActionResult<object> GetWatcher()
    {
        try
        {
            var isRunning = _watcher.IsRunning;
            return Ok(new { running = isRunning, enabled = isRunning });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting watcher status");
            return StatusCode(500, "Error retrieving watcher status");
        }
    }

    // Legacy endpoint for backward compatibility
    [HttpGet("status")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public ActionResult<object> GetStatus() => GetWatcher();

    // RESTful endpoint: PUT /api/watcher
    [HttpPut]
    public ActionResult UpdateWatcher([FromBody] WatcherUpdateRequest request)
    {
        try
        {
            _watcher.SetEnabled(request.Enabled);
            return Ok(new { enabled = request.Enabled });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting watcher status");
            return StatusCode(500, "Error setting watcher status");
        }
    }

    // Legacy endpoint for backward compatibility
    [HttpPost("enable")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public ActionResult EnableWatcher([FromBody] bool enabled)
    {
        try
        {
            _watcher.SetEnabled(enabled);
            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting watcher status");
            return StatusCode(500, "Error setting watcher status");
        }
    }

    public class WatcherUpdateRequest
    {
        public bool Enabled { get; set; }
    }
}
