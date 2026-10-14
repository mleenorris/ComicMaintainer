using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ComicMaintainer.WebApi.Controllers;

/// <summary>
/// Endpoints for the Suwayomi download integration: probe the configured
/// server, look up a series in its library, and queue downloads for the issues
/// a local series is missing.
/// </summary>
[ApiController]
[Route("api/suwayomi")]
[Authorize]
public class SuwayomiController : ControllerBase
{
    private readonly ISuwayomiService _suwayomi;
    private readonly ILogger<SuwayomiController> _logger;

    public SuwayomiController(ISuwayomiService suwayomi, ILogger<SuwayomiController> logger)
    {
        _suwayomi = suwayomi;
        _logger = logger;
    }

    /// <summary>Reports whether the configured Suwayomi server is reachable.</summary>
    [HttpGet("status")]
    public async Task<ActionResult<SuwayomiConnectionStatus>> GetStatus(CancellationToken cancellationToken)
    {
        return Ok(await _suwayomi.GetStatusAsync(cancellationToken));
    }

    /// <summary>Searches the Suwayomi library for series matching a title.</summary>
    [HttpGet("series")]
    public async Task<ActionResult<object>> SearchSeries(
        [FromQuery] string? query,
        [FromQuery] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var series = await _suwayomi.SearchSeriesAsync(query, limit, cancellationToken);
            return Ok(new { series });
        }
        catch (SuwayomiException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Lists the chapters Suwayomi knows about for one of its series.</summary>
    [HttpGet("series/{seriesId:int}/chapters")]
    public async Task<ActionResult<object>> GetChapters(int seriesId, CancellationToken cancellationToken)
    {
        try
        {
            var chapters = await _suwayomi.GetChaptersAsync(seriesId, cancellationToken);
            return Ok(new { chapters });
        }
        catch (SuwayomiException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Asks the Suwayomi server to download the given (missing) issue numbers
    /// of a series. Suwayomi stores them in its own download directory, which
    /// is expected to be visible to this service so the watcher picks the new
    /// files up.
    /// </summary>
    [HttpPost("download-missing")]
    public async Task<ActionResult<SuwayomiDownloadResult>> DownloadMissing(
        [FromBody] DownloadMissingRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BadRequest(new { error = "Request body is required" });
        }

        if (request.IssueNumbers is null || request.IssueNumbers.Count == 0)
        {
            return BadRequest(new { error = "At least one issue number is required" });
        }

        try
        {
            var result = await _suwayomi.DownloadMissingIssuesAsync(
                request.SeriesTitle,
                request.IssueNumbers,
                request.SuwayomiSeriesId,
                cancellationToken);

            _logger.LogInformation(
                "Suwayomi download requested for {Title}: {Queued} chapter(s) queued, {Unmatched} issue(s) unmatched",
                LoggingHelper.SanitizeForLog(request.SeriesTitle ?? string.Empty),
                result.QueuedChapters.Count,
                result.UnmatchedIssueNumbers.Count);

            return Ok(result);
        }
        catch (SuwayomiException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    public class DownloadMissingRequest
    {
        /// <summary>Local series title, used to match a Suwayomi library entry.</summary>
        public string? SeriesTitle { get; set; }

        /// <summary>Optional explicit Suwayomi series id, bypassing title matching.</summary>
        public int? SuwayomiSeriesId { get; set; }

        /// <summary>Whole issue/chapter numbers that are missing locally.</summary>
        public List<int>? IssueNumbers { get; set; }
    }
}
