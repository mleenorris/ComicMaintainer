using System.Security.Claims;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.WebApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class PreferencesController : ControllerBase
{
    private const int DefaultPerPage = 100;
    private const string DefaultReadingMode = "manga";
    private const string DefaultFilterMode = "all";
    private const string DefaultSortMode = "name";

    private static readonly string[] ValidThemes = { "light", "dark" };
    private static readonly string[] ValidReadingModes = { "manga", "webcomic" };
    private static readonly string[] ValidLibraryViewModes = { "files", "series" };
    private static readonly string[] ValidSortModes = { "name", "date", "size" };

    // Mirrors the filter options offered by the library header dropdown.
    private static readonly string[] ValidFilterModes =
    {
        "all", "unmarked", "marked", "duplicates", "renamed", "normalized",
        "read", "unread", "matched", "unmatched", "missing"
    };

    private readonly ILogger<PreferencesController> _logger;
    private readonly IOptionsMonitor<AppSettings> _appSettings;
    private readonly IUserPreferencesService _preferencesService;

    public PreferencesController(
        ILogger<PreferencesController> logger,
        IOptionsMonitor<AppSettings> appSettings,
        IUserPreferencesService preferencesService)
    {
        _logger = logger;
        _appSettings = appSettings;
        _preferencesService = preferencesService;
    }

    // RESTful endpoint: GET /api/preferences
    [HttpGet]
    public async Task<ActionResult<object>> GetPreferences(CancellationToken cancellationToken = default)
    {
        var userId = GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var stored = await _preferencesService.GetPreferencesAsync(userId, cancellationToken);
        return Ok(BuildResponse(stored));
    }

    // RESTful endpoint: PUT /api/preferences
    [HttpPut]
    public Task<ActionResult<object>> UpdatePreferences(
        [FromBody] PreferencesRequest preferences,
        CancellationToken cancellationToken = default)
        => SaveAsync(preferences, cancellationToken);

    // Legacy endpoint for backward compatibility
    [HttpPost]
    [ApiExplorerSettings(IgnoreApi = true)]
    public Task<ActionResult<object>> SavePreferences(
        [FromBody] PreferencesRequest preferences,
        CancellationToken cancellationToken = default)
        => SaveAsync(preferences, cancellationToken);

    private async Task<ActionResult<object>> SaveAsync(PreferencesRequest? preferences, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        if (preferences is null)
        {
            return BadRequest(new { error = "A preferences payload is required." });
        }

        if (!TryNormalize(preferences.Theme, ValidThemes, out var theme))
        {
            return BadRequest(new { error = $"Theme must be one of: {string.Join(", ", ValidThemes)}." });
        }

        if (!TryNormalize(preferences.ReadingMode, ValidReadingModes, out var readingMode))
        {
            return BadRequest(new { error = $"ReadingMode must be one of: {string.Join(", ", ValidReadingModes)}." });
        }

        if (!TryNormalize(preferences.LibraryViewMode, ValidLibraryViewModes, out var libraryViewMode))
        {
            return BadRequest(new { error = $"LibraryViewMode must be one of: {string.Join(", ", ValidLibraryViewModes)}." });
        }

        if (!TryNormalize(preferences.FilterMode, ValidFilterModes, out var filterMode))
        {
            return BadRequest(new { error = $"FilterMode must be one of: {string.Join(", ", ValidFilterModes)}." });
        }

        if (!TryNormalize(preferences.SortMode, ValidSortModes, out var sortMode))
        {
            return BadRequest(new { error = $"SortMode must be one of: {string.Join(", ", ValidSortModes)}." });
        }

        if (preferences.PerPage is not null && (preferences.PerPage < 1 || preferences.PerPage > 1000))
        {
            return BadRequest(new { error = "PerPage must be between 1 and 1000." });
        }

        var saved = await _preferencesService.SavePreferencesAsync(new UserPreferences
        {
            UserId = userId,
            Theme = theme,
            PerPage = preferences.PerPage,
            ReadingMode = readingMode,
            LibraryViewMode = libraryViewMode,
            FilterMode = filterMode,
            SortMode = sortMode
        }, cancellationToken);

        _logger.LogDebug("Preferences persisted for the current user.");

        return Ok(BuildResponse(saved));
    }

    /// <summary>
    /// Merges stored per-user preferences over the application-level defaults so
    /// the client always receives a fully-populated preference object.
    /// </summary>
    private object BuildResponse(UserPreferences stored)
    {
        var settings = _appSettings.CurrentValue;

        var defaultView = settings.DefaultLibraryView;
        if (string.IsNullOrWhiteSpace(defaultView) || !ValidLibraryViewModes.Contains(defaultView))
        {
            defaultView = "files";
        }

        return new
        {
            // Theme is deliberately returned as null when the user has never
            // chosen one, so the client can fall back to the OS
            // prefers-color-scheme setting instead of being pinned to a default.
            theme = stored.Theme,
            perPage = stored.PerPage ?? DefaultPerPage,
            filenameFormat = settings.FilenameFormat,
            issueNumberPadding = settings.IssueNumberPadding,
            watcherEnabled = true,
            readingMode = stored.ReadingMode ?? DefaultReadingMode,
            libraryViewMode = stored.LibraryViewMode ?? defaultView,
            filterMode = stored.FilterMode ?? DefaultFilterMode,
            sortMode = stored.SortMode ?? DefaultSortMode
        };
    }

    /// <summary>
    /// Normalizes an optional enum-like preference value. Null/blank means "not
    /// supplied" (left unchanged); any other value must be in <paramref name="allowed"/>.
    /// </summary>
    private static bool TryNormalize(string? value, string[] allowed, out string? normalized)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            normalized = null;
            return true;
        }

        var candidate = value.Trim().ToLowerInvariant();
        if (!allowed.Contains(candidate))
        {
            normalized = null;
            return false;
        }

        normalized = candidate;
        return true;
    }

    private string? GetUserId() => User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

    public class PreferencesRequest
    {
        public string? Theme { get; set; }
        public int? PerPage { get; set; }
        public string? ReadingMode { get; set; } // "manga" or "webcomic"
        public string? LibraryViewMode { get; set; } // "files" or "series"
        public string? FilterMode { get; set; }
        public string? SortMode { get; set; }
    }
}
