using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
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

    /// <summary>Synchronously refresh a single series (used by per-card action).</summary>
    [HttpPost("refresh/{seriesTitle}")]
    public async Task<ActionResult<SeriesMetadataCacheRecord>> RefreshOne(string seriesTitle, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(seriesTitle))
        {
            return BadRequest("Series title is required");
        }

        try
        {
            var record = await _cache.RefreshAsync(seriesTitle, cancellationToken);
            return Ok(record);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, LoggingHelper.WithWebsitePrefix("Error refreshing metadata for {SeriesTitle}"), seriesTitle);
            return StatusCode(500, "Error refreshing metadata");
        }
    }

    /// <summary>Get the status of a refresh job.</summary>
    [HttpGet("refresh/job/{jobId:guid}")]
    public ActionResult<MetadataRefreshJob> GetJob(Guid jobId)
    {
        var job = _refreshJobs.GetJob(jobId);
        return job is null ? NotFound() : Ok(job);
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
            return Ok(new
            {
                query,
                results = results.Select(r => new
                {
                    canonical_title = r.CanonicalTitle,
                    aliases = r.Aliases ?? new List<string>(),
                    source = r.Source
                })
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, LoggingHelper.WithWebsitePrefix("Error searching external metadata for {Query}"), query);
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
            _logger.LogError(ex, LoggingHelper.WithWebsitePrefix("Error updating aliases for {SeriesTitle}"), seriesTitle);
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

    public class RefreshSelectedRequest
    {
        public List<string> Series { get; set; } = new();
    }

    public class SetAliasesRequest
    {
        public List<string> Aliases { get; set; } = new();
        public string? CanonicalTitle { get; set; }
    }
}
