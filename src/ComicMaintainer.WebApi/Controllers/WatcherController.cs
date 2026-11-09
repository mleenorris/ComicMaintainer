using ComicMaintainer.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ComicMaintainer.WebApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
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
            // Note: Watcher enablement is now controlled by WatcherEnableRename and WatcherEnableNormalize settings
            // This endpoint returns the current running state for display purposes only
            return Ok(new { running = isRunning, enabled = isRunning });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Error getting watcher status");
            return StatusCode(500, "Error retrieving watcher status");
        }
    }

    // Legacy endpoint for backward compatibility
    [HttpGet("status")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public ActionResult<object> GetStatus() => GetWatcher();
}
