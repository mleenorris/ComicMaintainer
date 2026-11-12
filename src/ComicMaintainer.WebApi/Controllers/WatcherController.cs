using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.WebApi.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ComicMaintainer.WebApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class WatcherController : ControllerBase
{
    private readonly IFileWatcherService _watcher;

    public WatcherController(IFileWatcherService watcher)
    {
        _watcher = watcher;
    }

    // RESTful endpoint: GET /api/watcher
    [HttpGet]
    [FailureMessage("Error retrieving watcher status")]
    public ActionResult<object> GetWatcher()
    {
        var isRunning = _watcher.IsRunning;
        // Note: Watcher enablement is now controlled by WatcherEnableRename and WatcherEnableNormalize settings
        // This endpoint returns the current running state for display purposes only
        return Ok(new { running = isRunning, enabled = isRunning });
    }

    // Legacy endpoint for backward compatibility
    [HttpGet("status")]
    [ApiExplorerSettings(IgnoreApi = true)]
    [FailureMessage("Error retrieving watcher status")]
    public ActionResult<object> GetStatus() => GetWatcher();
}
