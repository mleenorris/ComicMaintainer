using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Utilities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ComicMaintainer.WebApi.Controllers;

/// <summary>
/// Endpoints for asking a Suwayomi (Tachidesk) sidecar to download missing
/// issues of a series. The download itself is handled by Suwayomi.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class SuwayomiController : ControllerBase
{
    private readonly ISuwayomiDownloadService _suwayomi;
    private readonly ISeriesLibraryService _seriesLibrary;
    private readonly ILogger<SuwayomiController> _logger;

    public SuwayomiController(
        ISuwayomiDownloadService suwayomi,
        ISeriesLibraryService seriesLibrary,
        ILogger<SuwayomiController> logger)
    {
        _suwayomi = suwayomi;
        _seriesLibrary = seriesLibrary;
        _logger = logger;
    }

    /// <summary>
    /// Reports whether the Suwayomi integration is available so the UI can
    /// show/hide the download actions. Pass <c>?probe=true</c> to also test
    /// reachability of the configured server.
    /// </summary>
    [HttpGet("status")]
    public async Task<ActionResult<object>> GetStatus([FromQuery] bool probe = false, CancellationToken cancellationToken = default)
    {
        var availability = await _suwayomi.GetAvailabilityAsync(probe, cancellationToken);
        return Ok(new
        {
            enabled = availability.Enabled,
            configured = availability.Configured,
            reachable = availability.Reachable,
            status_message = availability.StatusMessage
        });
    }

    /// <summary>
    /// Asks Suwayomi to download the supplied issues of a series. The series is
    /// matched to a tracked manga in Suwayomi's library by title; the bound
    /// source/extension is used for the downloads.
    /// </summary>
    [HttpPost("series/{seriesId}/download")]
    public async Task<ActionResult<object>> DownloadIssues(
        string seriesId,
        [FromBody] SuwayomiDownloadRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(seriesId))
        {
            return BadRequest(new { error = "Series id is required" });
        }

        if (request?.Issues is null || request.Issues.Count == 0)
        {
            return BadRequest(new { error = "At least one issue number is required" });
        }

        try
        {
            var titles = await _seriesLibrary.GetTitlesForSeriesIdAsync(seriesId, filter: null, cancellationToken);
            if (titles.Count == 0)
            {
                return NotFound(new { error = "Series not found" });
            }

            var result = await _suwayomi.DownloadIssuesAsync(titles, request.Issues, cancellationToken);
            return Ok(new
            {
                success = result.Success,
                message = result.Message,
                matched_series = result.Match is null ? null : new
                {
                    title = result.Match.Title,
                    source = result.Match.SourceName,
                    match_score = result.Match.MatchScore
                },
                issues = result.Issues.Select(i => new
                {
                    issue = i.Issue,
                    enqueued = i.Enqueued,
                    status = i.Status
                })
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download issues via Suwayomi for series {SeriesId}", LoggingHelper.SanitizeForLog(seriesId));
            return StatusCode(500, new { error = "Failed to request downloads from Suwayomi" });
        }
    }

    public class SuwayomiDownloadRequest
    {
        /// <summary>Issue/chapter numbers to download (e.g. ["3", "4", "7"]).</summary>
        public List<string> Issues { get; set; } = new();
    }
}
