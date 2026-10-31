using System.Globalization;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using ComicMaintainer.WebApi.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace ComicMaintainer.WebApi.Controllers;

/// <summary>
/// Endpoints for the Suwayomi download integration: probe the configured
/// server, look up a series in its library, queue downloads for the issues
/// a local series is missing, and find series on Suwayomi's sources to add
/// to its library.
/// </summary>
[ApiController]
[Route("api/suwayomi")]
[Authorize]
public class SuwayomiController : ControllerBase
{
    private const int MaxSearchTermLength = 200;
    private const int MaxSourcePage = 100;

    private readonly ISuwayomiService _suwayomi;
    private readonly ISuwayomiSeriesAddService _seriesAdder;
    private readonly ILogger<SuwayomiController> _logger;

    public SuwayomiController(
        ISuwayomiService suwayomi,
        ISuwayomiSeriesAddService seriesAdder,
        ILogger<SuwayomiController> logger)
    {
        _suwayomi = suwayomi;
        _seriesAdder = seriesAdder;
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

    /// <summary>Lists the catalogue sources installed on the Suwayomi server.</summary>
    [HttpGet("sources")]
    public async Task<ActionResult<object>> GetSources(CancellationToken cancellationToken)
    {
        try
        {
            var sources = await _suwayomi.GetSourcesAsync(cancellationToken);
            return Ok(new { sources });
        }
        catch (SuwayomiException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Searches one Suwayomi source's site for series. Suwayomi asks the site
    /// itself, so an answer can take a while.
    /// </summary>
    [HttpGet("sources/{sourceId:long:min(1)}/series")]
    [RequestTimeout(RequestTimeoutPolicies.LongRunning)]
    public async Task<ActionResult<SuwayomiSourceSearchResult>> SearchSource(
        long sourceId,
        [FromQuery] string? query,
        [FromQuery] int page = 1,
        CancellationToken cancellationToken = default)
    {
        var term = query?.Trim();
        if (string.IsNullOrEmpty(term))
        {
            return BadRequest(new { error = "A search term is required" });
        }

        if (term.Length > MaxSearchTermLength)
        {
            return BadRequest(new
            {
                error = $"Search terms are limited to {MaxSearchTermLength.ToString(CultureInfo.InvariantCulture)} characters"
            });
        }

        if (page is < 1 or > MaxSourcePage)
        {
            return BadRequest(new
            {
                error = $"Page must be between 1 and {MaxSourcePage.ToString(CultureInfo.InvariantCulture)}"
            });
        }

        try
        {
            return Ok(await _suwayomi.SearchSourceAsync(sourceId, term, page, cancellationToken));
        }
        catch (SuwayomiException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Serves a Suwayomi series' cover, which the Suwayomi server only hands
    /// to clients holding its credentials.
    /// </summary>
    [HttpGet("series/{seriesId:int:min(1)}/thumbnail")]
    public async Task<IActionResult> GetThumbnail(int seriesId, CancellationToken cancellationToken)
    {
        try
        {
            var thumbnail = await _suwayomi.GetThumbnailAsync(seriesId, cancellationToken);
            Response.Headers[HeaderNames.CacheControl] = "private, max-age=86400";
            return File(thumbnail.Content, thumbnail.ContentType);
        }
        catch (SuwayomiException ex)
        {
            // A 404 tells the page to show its placeholder instead of retrying.
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Adds a series to the Suwayomi library and queues downloads for the
    /// chapters this library does not have, leaving out the issues of a local
    /// series with the same title.
    /// </summary>
    [HttpPost("series/{seriesId:int:min(1)}/add")]
    [RequestTimeout(RequestTimeoutPolicies.LongRunning)]
    public async Task<ActionResult<SuwayomiAddSeriesResult>> AddSeries(int seriesId, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _seriesAdder.AddSeriesAsync(seriesId, cancellationToken));
        }
        catch (SuwayomiException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Asks the Suwayomi server to download the given (missing) issue numbers
    /// of a series, searching installed sources when matching library entries
    /// do not contain them. Suwayomi stores downloads in its own download
    /// directory, which is expected to be visible to this service so the
    /// watcher picks the new files up.
    /// </summary>
    [HttpPost("download-missing")]
    [DisableRequestTimeout]
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
                request.AlternateTitles,
                cancellationToken);

            _logger.LogInformation(
                "Suwayomi download requested for {Title}: {Queued} chapter(s) queued, {Unmatched} issue(s) unmatched, {Failures} source check(s) failed",
                LoggingHelper.SanitizeForLog(request.SeriesTitle ?? string.Empty),
                result.QueuedChapters.Count,
                result.UnmatchedIssueNumbers.Count,
                result.DiscoveryFailures.Count);

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

        /// <summary>
        /// Other titles of the local series (canonical title, aliases), tried
        /// because Suwayomi may list the series under a different name.
        /// </summary>
        public List<string>? AlternateTitles { get; set; }

        /// <summary>Optional explicit Suwayomi series id, bypassing title matching.</summary>
        public int? SuwayomiSeriesId { get; set; }

        /// <summary>Whole issue/chapter numbers that are missing locally.</summary>
        public List<int>? IssueNumbers { get; set; }
    }
}
