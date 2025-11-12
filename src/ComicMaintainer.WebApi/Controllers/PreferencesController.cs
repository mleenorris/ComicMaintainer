using ComicMaintainer.Core.Utilities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ComicMaintainer.WebApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class PreferencesController : ControllerBase
{
    private readonly ILogger<PreferencesController> _logger;

    public PreferencesController(ILogger<PreferencesController> logger)
    {
        _logger = logger;
    }

    // RESTful endpoint: GET /api/preferences
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
            watcherEnabled = true,
            readingMode = "manga" // Default reading mode: "manga" or "webcomic"
        });
    }

    // RESTful endpoint: PUT /api/preferences
    [HttpPut]
    public ActionResult UpdatePreferences([FromBody] PreferencesRequest preferences)
    {
        // For now, just acknowledge the save
        // In the future, this could be persisted to the database
        _logger.LogInformation("Preferences updated: Theme={Theme}, PerPage={PerPage}, ReadingMode={ReadingMode}", 
            LoggingHelper.SanitizeForLog(preferences.Theme ?? "not specified"), 
            preferences.PerPage,
            LoggingHelper.SanitizeForLog(preferences.ReadingMode ?? "not specified"));
        return Ok(new { message = "Preferences updated successfully" });
    }

    // Legacy endpoint for backward compatibility
    [HttpPost]
    [ApiExplorerSettings(IgnoreApi = true)]
    public ActionResult SavePreferences([FromBody] object preferences)
    {
        // For now, just acknowledge the save
        // In the future, this could be persisted to the database
        _logger.LogInformation("Preferences updated");
        return Ok();
    }

    public class PreferencesRequest
    {
        public string? Theme { get; set; }
        public int? PerPage { get; set; }
        public string? FilenameFormat { get; set; }
        public int? IssueNumberPadding { get; set; }
        public bool? WatcherEnabled { get; set; }
        public string? ReadingMode { get; set; } // "manga" or "webcomic"
    }
}
