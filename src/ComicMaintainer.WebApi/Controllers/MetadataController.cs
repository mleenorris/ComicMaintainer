using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Core.Utilities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ComicMaintainer.WebApi.Controllers;

[ApiController]
[Route("api/metadata")]
[Authorize]
public class MetadataController : ControllerBase
{
    private readonly ISeriesLibraryService _library;
    private readonly ISeriesMetadataCacheService _cache;
    private readonly ISeriesMetadataRefreshJobService _refreshJobs;
    private readonly IExternalSeriesMetadataService _externalMetadata;
    private readonly ILogger<MetadataController> _logger;

    public MetadataController(
        ISeriesLibraryService library,
        ISeriesMetadataCacheService cache,
        ISeriesMetadataRefreshJobService refreshJobs,
        IExternalSeriesMetadataService externalMetadata,
        ILogger<MetadataController> logger)
    {
        _library = library;
        _cache = cache;
        _refreshJobs = refreshJobs;
        _externalMetadata = externalMetadata;
        _logger = logger;
    }

    /// <summary>Queue an external metadata refresh for every series in the library.</summary>
    [HttpPost("refresh-all")]
    public async Task<ActionResult<object>> RefreshAll(CancellationToken cancellationToken)
    {
        try
        {
            var library = await _library.GetSeriesAsync(perPage: -1, cancellationToken: cancellationToken);
            var titles = library.Series
                .Select(s => s.CanonicalTitle)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var jobId = await _refreshJobs.StartAsync(titles, cancellationToken);
            _logger.LogInformation(LoggingHelper.WithWebsitePrefix("Metadata refresh-all queued for {Count} series (job {JobId})"), titles.Count, jobId);
            return Ok(new { jobId, totalSeries = titles.Count });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, LoggingHelper.WithWebsitePrefix("Error queuing metadata refresh-all"));
            return StatusCode(500, "Error queuing metadata refresh");
        }
    }

    /// <summary>Queue an external metadata refresh for a specific set of series titles.</summary>
    [HttpPost("refresh-selected")]
    public async Task<ActionResult<object>> RefreshSelected([FromBody] RefreshSelectedRequest request, CancellationToken cancellationToken)
    {
        if (request?.Series == null || request.Series.Count == 0)
        {
            return BadRequest("No series specified");
        }

        try
        {
            var jobId = await _refreshJobs.StartAsync(request.Series, cancellationToken);
            return Ok(new { jobId, totalSeries = request.Series.Count });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, LoggingHelper.WithWebsitePrefix("Error queuing metadata refresh-selected"));
            return StatusCode(500, "Error queuing metadata refresh");
        }
    }

    /// <summary>
    /// Refresh metadata for a single series. By default this runs synchronously
    /// and returns the updated cache record. Pass <c>?queue=true</c> to instead
    /// queue the lookup as a background job whose progress can be subscribed to
    /// via the existing job-update event stream — useful when the lookup is
    /// likely to be slow and the UI wants to render incremental status.
    /// </summary>
    [HttpPost("refresh/{seriesTitle}")]
    public async Task<ActionResult<object>> RefreshOne(
        string seriesTitle,
        [FromQuery] bool queue = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(seriesTitle))
        {
            return BadRequest("Series title is required");
        }

        try
        {
            if (queue)
            {
                var jobId = await _refreshJobs.StartAsync(new[] { seriesTitle }, cancellationToken);
                return Accepted(new { jobId, totalSeries = 1 });
            }

            var record = await _cache.RefreshAsync(seriesTitle, cancellationToken);
            return Ok(record);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, LoggingHelper.WithWebsitePrefix("Error refreshing metadata for {SeriesTitle}"), LoggingHelper.SanitizeForLog(seriesTitle));
            return StatusCode(500, "Error refreshing metadata");
        }
    }

    /// <summary>
    /// Queue an external metadata refresh for every title that maps to a single
    /// series-card id (i.e. the series's canonical title plus any folder/alias
    /// titles that group under it). This implements the per-series-folder
    /// refresh requested from the UI.
    /// </summary>
    [HttpPost("refresh/folder")]
    public async Task<ActionResult<object>> RefreshFolder(
        [FromBody] RefreshFolderRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null || (string.IsNullOrWhiteSpace(request.SeriesId) && (request.Titles is null || request.Titles.Count == 0)))
        {
            return BadRequest("Series id or titles required");
        }

        try
        {
            var titles = new List<string>();
            if (!string.IsNullOrWhiteSpace(request.SeriesId))
            {
                var resolved = await _library.GetTitlesForSeriesIdAsync(request.SeriesId, cancellationToken: cancellationToken);
                titles.AddRange(resolved);
            }
            if (request.Titles is { Count: > 0 })
            {
                foreach (var title in request.Titles)
                {
                    if (!string.IsNullOrWhiteSpace(title)
                        && !titles.Contains(title, StringComparer.OrdinalIgnoreCase))
                    {
                        titles.Add(title);
                    }
                }
            }

            if (titles.Count == 0)
            {
                return NotFound(new { error = "Series not found" });
            }

            var jobId = await _refreshJobs.StartAsync(titles, cancellationToken);
            return Accepted(new { jobId, totalSeries = titles.Count, titles });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, LoggingHelper.WithWebsitePrefix("Error queuing metadata refresh for folder"));
            return StatusCode(500, "Error queuing metadata refresh");
        }
    }

    /// <summary>Get the status of a refresh job.</summary>
    [HttpGet("refresh/job/{jobId:guid}")]
    public ActionResult<MetadataRefreshJob> GetJob(Guid jobId)
    {
        var job = _refreshJobs.GetJob(jobId);
        return job is null ? NotFound() : Ok(job);
    }

    /// <summary>
    /// Returns the configuration + recent runtime status of every external
    /// metadata provider, so the UI can display a per-provider health
    /// indicator.
    /// </summary>
    [HttpGet("providers")]
    public async Task<ActionResult<object>> GetProviders(CancellationToken cancellationToken)
    {
        try
        {
            var providers = new List<ProviderHealth>();
            if (_externalMetadata is CompositeExternalSeriesMetadataService composite)
            {
                providers.AddRange(await composite.CheckAllHealthAsync(cancellationToken));
            }
            else
            {
                providers.Add(await _externalMetadata.CheckHealthAsync(cancellationToken));
            }
            return Ok(new { providers });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, LoggingHelper.WithWebsitePrefix("Error retrieving provider health"));
            return StatusCode(500, "Error retrieving provider health");
        }
    }

    /// <summary>Search external providers for series candidates by alternative names.</summary>
    [HttpGet("search")]
    public async Task<ActionResult<object>> Search([FromQuery] string query, [FromQuery] int limit = 10, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return BadRequest("Query parameter is required");
        }

        try
        {
            var results = await _externalMetadata.SearchSeriesAsync(query, Math.Clamp(limit, 1, 50), cancellationToken);
            // Score each candidate against the query so the UI can show a
            // confidence percentage and let the user pick the best one when
            // the automatic match was wrong. Sort descending by score so the
            // most-likely match is presented first.
            var scored = results
                .Select(r => new
                {
                    metadata = r,
                    score = SeriesMatchScorer.Score(query, r.CanonicalTitle, r.Aliases)
                })
                .OrderByDescending(x => x.score)
                .ToList();

            return Ok(new
            {
                query,
                results = scored.Select(x => new
                {
                    canonical_title = x.metadata.CanonicalTitle,
                    aliases = x.metadata.Aliases ?? new List<string>(),
                    source = x.metadata.Source,
                    image_url = x.metadata.ImageUrl,
                    thumbnail_url = x.metadata.ThumbnailUrl,
                    match_score = x.score
                })
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, LoggingHelper.WithWebsitePrefix("Error searching external metadata for {Query}"), LoggingHelper.SanitizeForLog(query));
            return StatusCode(500, "Error searching external metadata");
        }
    }

    /// <summary>Get the cached record for a series (canonical title, aliases, user aliases).</summary>
    [HttpGet("series/{seriesTitle}")]
    public async Task<ActionResult<SeriesMetadataCacheRecord>> GetSeries(string seriesTitle, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(seriesTitle))
        {
            return BadRequest("Series title is required");
        }

        var key = _cache.NormalizeKey(seriesTitle);
        var record = await _cache.GetAsync(key, cancellationToken);
        if (record is null)
        {
            return Ok(new SeriesMetadataCacheRecord
            {
                NormalizedKey = key,
                CanonicalTitle = seriesTitle
            });
        }
        return Ok(record);
    }

    /// <summary>Replace the user-managed alias list (and optional canonical override) for a series.</summary>
    [HttpPut("series/{seriesTitle}/aliases")]
    public async Task<ActionResult<SeriesMetadataCacheRecord>> SetAliases(
        string seriesTitle,
        [FromBody] SetAliasesRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(seriesTitle))
        {
            return BadRequest("Series title is required");
        }

        try
        {
            var record = await _cache.SetUserAliasesAsync(
                seriesTitle,
                request?.Aliases ?? new List<string>(),
                request?.CanonicalTitle,
                cancellationToken);
            return Ok(record);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, LoggingHelper.WithWebsitePrefix("Error updating aliases for {SeriesTitle}"), LoggingHelper.SanitizeForLog(seriesTitle));
            return StatusCode(500, "Error updating aliases");
        }
    }

    /// <summary>Remove a single user alias from a series.</summary>
    [HttpDelete("series/{seriesTitle}/aliases/{alias}")]
    public async Task<ActionResult<SeriesMetadataCacheRecord>> RemoveAlias(
        string seriesTitle,
        string alias,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(seriesTitle) || string.IsNullOrWhiteSpace(alias))
        {
            return BadRequest("Series title and alias are required");
        }

        var key = _cache.NormalizeKey(seriesTitle);
        var record = await _cache.RemoveUserAliasAsync(key, alias, cancellationToken);
        return record is null ? NotFound() : Ok(record);
    }

    /// <summary>
    /// Manually adopt one of the candidates returned by <c>/search</c> as the
    /// cached external metadata for a series. Used when the automatic match
    /// was wrong and the user picks a different candidate from the list.
    /// Preserves any user-overridden canonical title and the user alias list.
    /// </summary>
    [HttpPost("series/{seriesTitle}/apply-match")]
    public async Task<ActionResult<SeriesMetadataCacheRecord>> ApplyMatch(
        string seriesTitle,
        [FromBody] ApplyMatchRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(seriesTitle))
        {
            return BadRequest("Series title is required");
        }
        if (request is null || string.IsNullOrWhiteSpace(request.CanonicalTitle))
        {
            return BadRequest("Candidate canonical title is required");
        }

        try
        {
            var record = await _cache.ApplyExternalMatchAsync(
                seriesTitle,
                new ExternalSeriesMetadata
                {
                    CanonicalTitle = request.CanonicalTitle.Trim(),
                    Aliases = request.Aliases?.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).ToList()
                              ?? new List<string>(),
                    Source = request.Source ?? string.Empty,
                    ImageUrl = string.IsNullOrWhiteSpace(request.ImageUrl) ? null : request.ImageUrl,
                    ThumbnailUrl = string.IsNullOrWhiteSpace(request.ThumbnailUrl) ? null : request.ThumbnailUrl
                },
                cancellationToken);
            return Ok(record);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, LoggingHelper.WithWebsitePrefix("Error applying match for {SeriesTitle}"), LoggingHelper.SanitizeForLog(seriesTitle));
            return StatusCode(500, "Error applying match");
        }
    }

    /// <summary>
    /// Clear the external metadata cached for a single series (provider
    /// aliases, source, last lookup, provider-supplied canonical title, and
    /// any provider-downloaded image). User-managed aliases and user-uploaded
    /// images are preserved.
    /// </summary>
    [HttpDelete("series/{seriesTitle}/external")]
    public async Task<ActionResult<SeriesMetadataCacheRecord>> ClearExternal(
        string seriesTitle,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(seriesTitle))
        {
            return BadRequest("Series title is required");
        }

        try
        {
            var key = _cache.NormalizeKey(seriesTitle);
            var record = await _cache.ClearExternalMetadataAsync(key, cancellationToken);
            return record is null ? NotFound() : Ok(record);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, LoggingHelper.WithWebsitePrefix("Error clearing external metadata for {SeriesTitle}"), LoggingHelper.SanitizeForLog(seriesTitle));
            return StatusCode(500, "Error clearing external metadata");
        }
    }

    public class RefreshSelectedRequest
    {
        public List<string> Series { get; set; } = new();
    }

    public class RefreshFolderRequest
    {
        /// <summary>The series-card id returned by /api/files/series.</summary>
        public string? SeriesId { get; set; }

        /// <summary>
        /// Optional explicit titles to refresh. Combined (deduped) with whatever
        /// titles the series id resolves to.
        /// </summary>
        public List<string>? Titles { get; set; }
    }

    public class SetAliasesRequest
    {
        public List<string> Aliases { get; set; } = new();
        public string? CanonicalTitle { get; set; }
    }

    public class ApplyMatchRequest
    {
        public string CanonicalTitle { get; set; } = string.Empty;
        public List<string>? Aliases { get; set; }
        public string? Source { get; set; }
        public string? ImageUrl { get; set; }
        public string? ThumbnailUrl { get; set; }
    }
}
