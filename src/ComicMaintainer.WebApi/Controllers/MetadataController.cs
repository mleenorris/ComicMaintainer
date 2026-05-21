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
    private readonly IComicProcessorService? _processor;
    private readonly IFileStoreService? _fileStore;
    private readonly ISeriesLanguagePreferenceRetagService? _languageRetag;
    private readonly ILogger<MetadataController> _logger;

    public MetadataController(
        ISeriesLibraryService library,
        ISeriesMetadataCacheService cache,
        ISeriesMetadataRefreshJobService refreshJobs,
        IExternalSeriesMetadataService externalMetadata,
        ILogger<MetadataController> logger,
        IComicProcessorService? processor = null,
        IFileStoreService? fileStore = null,
        ISeriesLanguagePreferenceRetagService? languageRetag = null)
    {
        _library = library;
        _cache = cache;
        _refreshJobs = refreshJobs;
        _externalMetadata = externalMetadata;
        _logger = logger;
        _processor = processor;
        _fileStore = fileStore;
        _languageRetag = languageRetag;
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

    /// <summary>
    /// Queue an external metadata lookup for every series that does not yet
    /// have a provider match. The job runs in the background using the same
    /// pipeline as <c>refresh-all</c>; this endpoint returns immediately with
    /// the queued job id so the UI is never blocked while the (potentially
    /// slow) lookups run.
    /// </summary>
    [HttpPost("match-unmatched")]
    public async Task<ActionResult<object>> MatchUnmatched(CancellationToken cancellationToken)
    {
        try
        {
            var library = await _library.GetSeriesAsync(
                filter: "unmatched",
                perPage: -1,
                cancellationToken: cancellationToken);
            var titles = library.Series
                .Select(s => s.CanonicalTitle)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (titles.Count == 0)
            {
                _logger.LogInformation(LoggingHelper.WithWebsitePrefix("Metadata match-unmatched requested but no unmatched series were found"));
                return Ok(new { jobId = (Guid?)null, totalSeries = 0 });
            }

            var jobId = await _refreshJobs.StartAsync(titles, cancellationToken);
            _logger.LogInformation(LoggingHelper.WithWebsitePrefix("Metadata match-unmatched queued for {Count} series (job {JobId})"), titles.Count, jobId);
            return Ok(new { jobId, totalSeries = titles.Count });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, LoggingHelper.WithWebsitePrefix("Error queuing metadata match-unmatched"));
            return StatusCode(500, "Error queuing metadata match");
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
            return Ok(new { providers = MergeAniListProviders(providers) });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, LoggingHelper.WithWebsitePrefix("Error retrieving provider health"));
            return StatusCode(500, "Error retrieving provider health");
        }
    }

    /// <summary>
    /// AniList is queried as two separate providers internally
    /// (<c>AniListManga</c> for JP titles and <c>AniListManhwa</c> for KR
    /// titles) because the GraphQL query filters by country of origin. From
    /// the user's perspective there is a single "AniList" backend, so the
    /// provider-health widget collapses them into one entry by combining
    /// their counters and taking the best reachability signal.
    /// </summary>
    private static List<ProviderHealth> MergeAniListProviders(IEnumerable<ProviderHealth> providers)
    {
        var result = new List<ProviderHealth>();
        ProviderHealth? merged = null;
        foreach (var provider in providers)
        {
            if (provider.Name is "AniListManga" or "AniListManhwa" or "AniList")
            {
                if (merged is null)
                {
                    merged = new ProviderHealth
                    {
                        Name = "AniList",
                        Enabled = provider.Enabled,
                        Configured = provider.Configured,
                        Reachable = provider.Reachable,
                        StatusMessage = provider.StatusMessage,
                        LastError = provider.LastError,
                        LastSuccessUtc = provider.LastSuccessUtc,
                        LastFailureUtc = provider.LastFailureUtc,
                        SuccessCount = provider.SuccessCount,
                        FailureCount = provider.FailureCount
                    };
                    result.Add(merged);
                }
                else
                {
                    // Enabled/Configured: either side counts.
                    merged.Enabled = merged.Enabled || provider.Enabled;
                    merged.Configured = merged.Configured || provider.Configured;

                    // Reachability: any successful probe wins, otherwise
                    // an explicit failure beats "unknown" (null).
                    merged.Reachable = CombineReachable(merged.Reachable, provider.Reachable);
                    if (merged.Reachable == true)
                    {
                        merged.StatusMessage = "Reachable";
                    }
                    else if (!string.IsNullOrEmpty(provider.StatusMessage) && string.IsNullOrEmpty(merged.StatusMessage))
                    {
                        merged.StatusMessage = provider.StatusMessage;
                    }

                    merged.SuccessCount += provider.SuccessCount;
                    merged.FailureCount += provider.FailureCount;

                    if (provider.LastSuccessUtc.HasValue &&
                        (!merged.LastSuccessUtc.HasValue || provider.LastSuccessUtc > merged.LastSuccessUtc))
                    {
                        merged.LastSuccessUtc = provider.LastSuccessUtc;
                    }
                    if (provider.LastFailureUtc.HasValue &&
                        (!merged.LastFailureUtc.HasValue || provider.LastFailureUtc > merged.LastFailureUtc))
                    {
                        merged.LastFailureUtc = provider.LastFailureUtc;
                        merged.LastError = provider.LastError ?? merged.LastError;
                    }
                }
            }
            else
            {
                result.Add(provider);
            }
        }
        return result;
    }

    private static bool? CombineReachable(bool? a, bool? b)
    {
        if (a == true || b == true) return true;
        if (a == false || b == false) return false;
        return null;
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
                    match_score = x.score,
                    // Surface the provider-supplied language tags so the
                    // manual-match UI can render each alias alongside its
                    // language (e.g. "en", "ja", "ko") and the user can
                    // tell at a glance which provider alias is English.
                    localized_titles = (x.metadata.LocalizedTitles ?? new List<LocalizedTitle>())
                        .Where(lt => lt is not null && !string.IsNullOrWhiteSpace(lt.Title))
                        .Select(lt => new { title = lt.Title, language = lt.Language })
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
                    ThumbnailUrl = string.IsNullOrWhiteSpace(request.ThumbnailUrl) ? null : request.ThumbnailUrl,
                    // Preserve provider-supplied language tags so the cached
                    // record's LocalizedTitles list keeps per-alias language
                    // info; without this the cache falls back to untagged
                    // entries and the preferred-language resolver can't
                    // promote (e.g.) the English alias as display title.
                    LocalizedTitles = (request.LocalizedTitles ?? new List<LocalizedTitleDto>())
                        .Where(lt => lt is not null && !string.IsNullOrWhiteSpace(lt.Title))
                        .Select(lt => new LocalizedTitle(lt.Title.Trim(), string.IsNullOrWhiteSpace(lt.Language) ? null : lt.Language!.Trim()))
                        .ToList()
                },
                cancellationToken);

            // After adopting the match, queue a background normalize-and-rename
            // job so every file currently belonging to this series has its
            // ComicInfo.xml series field and filename updated to reflect the
            // newly-matched canonical title. This is best-effort: failures are
            // logged and swallowed so the apply-match response still succeeds.
            // The job's progress is reported via the standard SSE job-update
            // broadcast, so we don't need to surface the id in the response
            // payload (which would break existing API consumers that expect a
            // SeriesMetadataCacheRecord).
            try
            {
                await QueueSeriesNormalizeRenameJobAsync(seriesTitle, record, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, LoggingHelper.WithWebsitePrefix("Failed to queue normalize-and-rename job after applying match for {SeriesTitle}"),
                    LoggingHelper.SanitizeForLog(seriesTitle));
            }

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

    /// <summary>
    /// Set (or clear, by passing null/empty) the user's preferred display
    /// language for a series. Accepts <c>en</c>, <c>ja</c>, <c>ko</c>, or
    /// <c>zh</c>.
    /// </summary>
    [HttpPut("series/{seriesTitle}/preferred-language")]
    public async Task<ActionResult<SeriesMetadataCacheRecord>> SetPreferredLanguage(
        string seriesTitle,
        [FromBody] PreferredLanguageRequest? request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(seriesTitle))
        {
            return BadRequest("Series title is required");
        }

        try
        {
            var record = await _cache.SetPreferredLanguageAsync(
                seriesTitle,
                request?.Language,
                cancellationToken);

            // Fire-and-forget: enqueue per-file re-normalization so every
            // ComicInfo.xml in this series gets its <Series> rewritten to
            // match the new language preference. We do not block the API
            // response on the job; the standard processing-job/SSE pipeline
            // surfaces progress.
            if (_languageRetag is not null)
            {
                try
                {
                    var retagJobId = await _languageRetag.QueueRetagForSeriesAsync(record, cancellationToken);
                    if (retagJobId is not null)
                    {
                        _logger.LogInformation(
                            LoggingHelper.WithWebsitePrefix("Queued per-file retag job {JobId} after preferred-language change for {SeriesTitle}"),
                            retagJobId.Value, LoggingHelper.SanitizeForLog(seriesTitle));
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        LoggingHelper.WithWebsitePrefix("Failed to queue per-file retag after preferred-language change for {SeriesTitle}"),
                        LoggingHelper.SanitizeForLog(seriesTitle));
                }
            }

            return Ok(record);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, LoggingHelper.WithWebsitePrefix("Error setting preferred language for {SeriesTitle}"), LoggingHelper.SanitizeForLog(seriesTitle));
            return StatusCode(500, "Error setting preferred language");
        }
    }

    public class PreferredLanguageRequest
    {
        /// <summary>
        /// BCP-47 language tag (e.g. <c>en</c>, <c>ja</c>, <c>ko</c>,
        /// <c>zh</c>). Pass null or empty to clear and revert to the global
        /// default.
        /// </summary>
        public string? Language { get; set; }
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

        /// <summary>
        /// Optional language-tagged titles to preserve from the candidate
        /// shown in the manual-match UI. When supplied, these flow through
        /// to the cached record so the per-series preferred-language
        /// resolver can later swap the display title to the user's chosen
        /// language without having to re-query the provider.
        /// </summary>
        public List<LocalizedTitleDto>? LocalizedTitles { get; set; }
    }

    public class LocalizedTitleDto
    {
        public string Title { get; set; } = string.Empty;
        public string? Language { get; set; }
    }

    /// <summary>
    /// Locate every file currently belonging to <paramref name="seriesTitle"/>
    /// (matched by folder-derived series name, current ComicInfo.xml series,
    /// or any cached alias of the new canonical title) and queue a background
    /// normalize-and-rename job so each file's metadata is rewritten to the
    /// freshly-matched canonical title. Returns null when there are no files
    /// to update or the processor/file-store dependencies are missing.
    /// </summary>
    private async Task<Guid?> QueueSeriesNormalizeRenameJobAsync(
        string seriesTitle,
        SeriesMetadataCacheRecord record,
        CancellationToken cancellationToken)
    {
        if (_processor is null || _fileStore is null)
        {
            return null;
        }

        // Build a case-insensitive set of names that should map to this series.
        // We include the requested title (which is typically the current folder
        // name), the new canonical title, and every cached alias so files whose
        // metadata still references an older name are picked up.
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                names.Add(value!.Trim());
            }
        }

        Add(seriesTitle);
        Add(record.CanonicalTitle);
        if (record.Aliases is { Count: > 0 })
        {
            foreach (var alias in record.Aliases) Add(alias);
        }
        if (record.UserAliases is { Count: > 0 })
        {
            foreach (var alias in record.UserAliases) Add(alias);
        }

        if (names.Count == 0)
        {
            return null;
        }

        // Normalize comparison keys via the cache so they line up with how the
        // rest of the system identifies series (case- and whitespace-tolerant).
        var keys = new HashSet<string>(
            names.Select(n => _cache.NormalizeKey(n))
                 .Where(k => !string.IsNullOrWhiteSpace(k))!,
            StringComparer.OrdinalIgnoreCase);

        var files = await _fileStore.GetAllFilesAsync(cancellationToken);
        var matchingPaths = new List<string>();
        foreach (var file in files)
        {
            if (string.IsNullOrWhiteSpace(file.FilePath))
            {
                continue;
            }

            // Match on the file's current ComicInfo.xml series (if known).
            var metadataSeries = file.Metadata?.Series;
            if (!string.IsNullOrWhiteSpace(metadataSeries))
            {
                var metaKey = _cache.NormalizeKey(metadataSeries);
                if (!string.IsNullOrWhiteSpace(metaKey) && keys.Contains(metaKey))
                {
                    matchingPaths.Add(file.FilePath);
                    continue;
                }
            }

            // Fall back to the parent-folder-derived series name so that files
            // whose metadata has not yet been written still get updated.
            var folderName = Path.GetFileName(Path.GetDirectoryName(file.FilePath));
            if (string.IsNullOrWhiteSpace(folderName))
            {
                continue;
            }
            var folderSeries = ComicFileProcessor.NormalizeSeriesName(folderName, forComparison: false);
            var folderKey = _cache.NormalizeKey(folderSeries);
            if (!string.IsNullOrWhiteSpace(folderKey) && keys.Contains(folderKey))
            {
                matchingPaths.Add(file.FilePath);
            }
        }

        if (matchingPaths.Count == 0)
        {
            return null;
        }

        var jobId = await _processor.NormalizeAndRenameFilesAsync(matchingPaths, cancellationToken);
        _logger.LogInformation(
            LoggingHelper.WithWebsitePrefix("Queued normalize-and-rename job {JobId} for {FileCount} file(s) after applying match for {SeriesTitle}"),
            jobId,
            matchingPaths.Count,
            LoggingHelper.SanitizeForLog(seriesTitle));
        return jobId;
    }
}
