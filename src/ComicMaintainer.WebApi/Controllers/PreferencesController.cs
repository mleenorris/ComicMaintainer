using Microsoft.AspNetCore.Mvc;

namespace ComicMaintainer.WebApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PreferencesController : ControllerBase
{
    private readonly ILogger<PreferencesController> _logger;

    public PreferencesController(ILogger<PreferencesController> logger)
    {
        _logger = logger;
    }

    [HttpGet]
    public ActionResult<object> GetPreferences()
    {
        // Return default preferences
        return Ok(new
        {
            theme = "dark",
            perPage = 100,
            filenameFormat = "{series} - Chapter {issue}",
            issueNumberPadding = 4,
            watcherEnabled = true
        });
    }

    [HttpPost]
    public ActionResult SavePreferences([FromBody] object preferences)
    {
        // For now, just acknowledge the save
        // In the future, this could be persisted to the database
        _logger.LogInformation("Preferences updated");
        return Ok();
    }
}
