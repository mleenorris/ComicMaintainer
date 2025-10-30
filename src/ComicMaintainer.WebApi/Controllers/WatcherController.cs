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

    [HttpGet("status")]
    public ActionResult<object> GetStatus()
    {
        try
        {
            return Ok(new { enabled = _watcher.IsRunning });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting watcher status");
            return StatusCode(500, "Error retrieving watcher status");
        }
    }

    [HttpPost("enable")]
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
}
