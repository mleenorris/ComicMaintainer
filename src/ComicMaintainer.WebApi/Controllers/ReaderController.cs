using System.Security.Claims;
using ComicMaintainer.Core.Reader.Interfaces;
using ComicMaintainer.Core.Reader.Models;
using ComicMaintainer.Core.Reader.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ComicMaintainer.WebApi.Controllers;

[ApiController]
[Route("api/reader")]
[Authorize]
public class ReaderController : ControllerBase
{
    private readonly IReadingProgressService _progressService;
    private readonly IReaderPreferenceService _preferenceService;
    private readonly IReadingSessionService _sessionService;
    private readonly ILogger<ReaderController> _logger;

    public ReaderController(
        IReadingProgressService progressService,
        IReaderPreferenceService preferenceService,
        IReadingSessionService sessionService,
        ILogger<ReaderController> logger)
    {
        _progressService = progressService;
        _preferenceService = preferenceService;
        _sessionService = sessionService;
        _logger = logger;
    }

    // ─── Progress ──────────────────────────────────────────────────────────────

    [HttpGet("progress/{contentId}")]
    public async Task<ActionResult<ReadingProgress>> GetProgress(string contentId, CancellationToken cancellationToken = default)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var progress = await _progressService.GetProgressAsync(userId, contentId, cancellationToken);
        if (progress is null) return NotFound();

        return Ok(progress);
    }

    [HttpPut("progress/{contentId}")]
    public async Task<ActionResult> PutProgress(string contentId, [FromBody] PutProgressRequest request, CancellationToken cancellationToken = default)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        if (request.TotalPages <= 0)
            return BadRequest(new { error = "TotalPages must be greater than zero." });

        if (request.CurrentPage < 1 || request.CurrentPage > request.TotalPages)
            return BadRequest(new { error = "CurrentPage must be between 1 and TotalPages." });

        var existing = await _progressService.GetProgressAsync(userId, contentId, cancellationToken)
            ?? new ReadingProgress
            {
                UserId = userId,
                ContentId = contentId,
                TotalPages = request.TotalPages,
                LastReadAt = DateTime.UtcNow,
                LastReaderMode = request.ReaderMode,
                ReadingDirection = request.ReadingDirection
            };

        existing.TotalPages = request.TotalPages;
        existing.LastReaderMode = request.ReaderMode;
        existing.ReadingDirection = request.ReadingDirection;
        ReadingProgressCalculator.ApplyProgress(existing, request.CurrentPage, DateTime.UtcNow);

        await _progressService.SaveProgressAsync(existing, cancellationToken);
        return NoContent();
    }

    // ─── Preferences ───────────────────────────────────────────────────────────

    [HttpGet("preferences")]
    public async Task<ActionResult<ReaderPreferences>> GetPreferences(CancellationToken cancellationToken = default)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var prefs = await _preferenceService.GetPreferencesAsync(userId, cancellationToken);
        return Ok(prefs);
    }

    [HttpPut("preferences")]
    public async Task<ActionResult> PutPreferences([FromBody] ReaderPreferences preferences, CancellationToken cancellationToken = default)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        preferences.UserId = userId;
        await _preferenceService.SavePreferencesAsync(preferences, cancellationToken);
        return NoContent();
    }

    // ─── Sessions ──────────────────────────────────────────────────────────────

    [HttpPost("session/start")]
    public async Task<ActionResult<ReadingSession>> StartSession([FromBody] StartSessionRequest request, CancellationToken cancellationToken = default)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        if (string.IsNullOrWhiteSpace(request.ContentId))
            return BadRequest(new { error = "ContentId is required." });

        if (request.StartPage < 1)
            return BadRequest(new { error = "StartPage must be at least 1." });

        var session = await _sessionService.StartSessionAsync(
            userId, request.ContentId, request.StartPage, request.Incognito, cancellationToken);

        return Ok(session);
    }

    [HttpPost("session/end")]
    public async Task<ActionResult<ReadingSession>> EndSession([FromBody] EndSessionRequest request, CancellationToken cancellationToken = default)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        if (request.EndPage < 1)
            return BadRequest(new { error = "EndPage must be at least 1." });

        var session = await _sessionService.EndSessionAsync(request.SessionId, request.EndPage, cancellationToken);
        if (session is null) return NotFound(new { error = "Session not found." });

        return Ok(session);
    }

    // ─── Helpers ───────────────────────────────────────────────────────────────

    private string? GetUserId() =>
        User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

    // ─── Request DTOs ──────────────────────────────────────────────────────────

    public class PutProgressRequest
    {
        public int CurrentPage { get; set; }
        public int TotalPages { get; set; }
        public ReaderMode ReaderMode { get; set; }
        public ReadingDirection ReadingDirection { get; set; }
    }

    public class StartSessionRequest
    {
        public string ContentId { get; set; } = string.Empty;
        public int StartPage { get; set; } = 1;
        public bool Incognito { get; set; }
    }

    public class EndSessionRequest
    {
        public Guid SessionId { get; set; }
        public int EndPage { get; set; }
    }
}
