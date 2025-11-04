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
            // Note: Watcher enablement is now controlled by WatcherEnableRename and WatcherEnableNormalize settings
            // This endpoint returns the current running state for display purposes only
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
    // DEPRECATED: Watcher is now automatically controlled by WatcherEnableRename and WatcherEnableNormalize settings
    // This endpoint is kept for backward compatibility but does nothing
    [HttpPut]
    [Obsolete("Direct watcher enable/disable is deprecated. Use WatcherEnableRename and WatcherEnableNormalize settings instead.")]
    public ActionResult UpdateWatcher([FromBody] WatcherUpdateRequest request)
    {
        try
        {
            _logger.LogWarning("UpdateWatcher called but is deprecated. Watcher is now controlled by WatcherEnableRename and WatcherEnableNormalize settings.");
            // Return current running state instead of the requested state
            var isRunning = _watcher.IsRunning;
            return Ok(new { enabled = isRunning, message = "Watcher is automatically controlled by rename and normalize settings" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in deprecated watcher endpoint");
            return StatusCode(500, "Error in watcher endpoint");
        }
    }

    // Legacy endpoint for backward compatibility
    // DEPRECATED: Watcher is now automatically controlled by WatcherEnableRename and WatcherEnableNormalize settings
    [HttpPost("enable")]
    [ApiExplorerSettings(IgnoreApi = true)]
    [Obsolete("Direct watcher enable/disable is deprecated. Use WatcherEnableRename and WatcherEnableNormalize settings instead.")]
    public ActionResult EnableWatcher([FromBody] bool enabled)
    {
        try
        {
            _logger.LogWarning("EnableWatcher called but is deprecated. Watcher is now controlled by WatcherEnableRename and WatcherEnableNormalize settings.");
            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in deprecated watcher endpoint");
            return StatusCode(500, "Error in watcher endpoint");
        }
    }

    public class WatcherUpdateRequest
    {
        public bool Enabled { get; set; }
    }
}
