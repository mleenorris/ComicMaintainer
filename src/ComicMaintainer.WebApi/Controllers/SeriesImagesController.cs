using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Utilities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace ComicMaintainer.WebApi.Controllers;

/// <summary>
/// Streams locally-cached series cover images and provides upload/clear
/// endpoints for user-supplied images.
/// </summary>
[ApiController]
[Route("api/series-images")]
[Authorize]
public class SeriesImagesController : ControllerBase
{
    // Cap user uploads at twice the configured per-image limit so larger
    // files fail with a clear error instead of being truncated by the
    // generic ASP.NET request-body limit. The store will further reject
    // anything bigger than AppSettings.SeriesImageMaxBytes.
    private const long MaxUploadBytes = 20L * 1024 * 1024;

    private readonly ISeriesMetadataCacheService _cache;
    private readonly ISeriesLibraryService _library;
    private readonly ISeriesImageStore _imageStore;
    private readonly IExternalSeriesMetadataService _externalMetadata;
    private readonly IComicProcessorService _processor;
    private readonly ILogger<SeriesImagesController> _logger;

    public SeriesImagesController(
        ISeriesMetadataCacheService cache,
        ISeriesLibraryService library,
        ISeriesImageStore imageStore,
        IExternalSeriesMetadataService externalMetadata,
        IComicProcessorService processor,
        ILogger<SeriesImagesController> logger)
    {
        _cache = cache;
        _library = library;
        _imageStore = imageStore;
        _externalMetadata = externalMetadata;
        _processor = processor;
        _logger = logger;
    }

    /// <summary>
    /// Stream the locally cached series image. Returns 404 when no image is
    /// available so the front-end can fall back to the file-based first-page
    /// cover.
    /// </summary>
    [HttpGet("{normalizedKey}")]
    public async Task<IActionResult> Get(string normalizedKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(normalizedKey))
        {
            return BadRequest("Series key is required");
        }

        var record = await _cache.GetAsync(normalizedKey, cancellationToken);
        if (record is null
            || string.IsNullOrEmpty(record.LocalImageFile)
            || string.IsNullOrEmpty(record.ImageContentType)
            || !record.HasImage)
        {
            return NotFound();
        }

        var path = _imageStore.ResolveAbsolutePath(record.LocalImageFile);
        if (path is null)
        {
            return NotFound();
        }

        // Use the file's last-write time as a weak ETag so browsers can avoid
        // re-downloading unchanged images. Image filenames already include a
        // content hash, so the file mtime is a stable revalidation token.
        var fileInfo = new FileInfo(path);
        var lastWriteUtc = fileInfo.LastWriteTimeUtc;
        var etag = $"\"{lastWriteUtc.Ticks:x}-{fileInfo.Length:x}\"";

        var requestEtag = Request.Headers[HeaderNames.IfNoneMatch].ToString();
        if (!string.IsNullOrEmpty(requestEtag)
            && string.Equals(requestEtag, etag, StringComparison.Ordinal))
        {
            Response.Headers[HeaderNames.ETag] = etag;
            return StatusCode(StatusCodes.Status304NotModified);
        }

        Response.Headers[HeaderNames.ETag] = etag;
        Response.Headers[HeaderNames.CacheControl] = "private, max-age=300";
        return PhysicalFile(path, record.ImageContentType, enableRangeProcessing: false);
    }

    /// <summary>
    /// Upload a user-supplied series image. The body must be a single
    /// multipart/form-data file field named "file". The image is validated
    /// (content-type allowlist + magic-byte check) by the image store before
    /// being persisted.
    /// </summary>
    [HttpPut("{seriesTitle}")]
    [RequestSizeLimit(MaxUploadBytes)]
    public async Task<IActionResult> Upload(
        string seriesTitle,
        IFormFile? file,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(seriesTitle))
        {
            return BadRequest("Series title is required");
        }
        if (file is null || file.Length == 0)
        {
            return BadRequest("File is required");
        }
        if (file.Length > MaxUploadBytes)
        {
            return BadRequest("File too large");
        }

        try
        {
            await using var stream = file.OpenReadStream();
            var record = await _cache.SetUserImageAsync(
                seriesTitle,
                stream,
                file.ContentType ?? string.Empty,
                cancellationToken);
            return Ok(record);
        }
        catch (InvalidOperationException ex)
        {
            // Validation failures (size, content-type, magic bytes) — surface
            // a clean 400 to the UI rather than a 500.
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, LoggingHelper.WithWebsitePrefix("Error uploading series image for {SeriesTitle}"),
                LoggingHelper.SanitizeForLog(seriesTitle));
            return StatusCode(500, "Error saving series image");
        }
    }

    /// <summary>
    /// Search every configured external metadata provider for series whose
    /// records include a cover image URL. The front-end uses the returned
    /// candidates to render a thumbnail picker from which a user can select
    /// the image to apply via <see cref="ApplyFromProvider"/>.
    /// </summary>
    [HttpGet("candidates")]
    public async Task<ActionResult<object>> SearchCandidates(
        [FromQuery] string query,
        [FromQuery] int limit = 10,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return BadRequest("Query is required");
        }

        try
        {
            var results = await _externalMetadata.SearchSeriesAsync(
                query,
                Math.Clamp(limit, 1, 25),
                cancellationToken);

            var candidates = results
                .Where(r => !string.IsNullOrWhiteSpace(r.ImageUrl))
                .Select(r => new
                {
                    source = r.Source,
                    canonical_title = r.CanonicalTitle,
                    image_url = r.ImageUrl,
                    thumbnail_url = r.ThumbnailUrl
                })
                .ToList();

            return Ok(new { query, candidates });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, LoggingHelper.WithWebsitePrefix("Error searching provider image candidates for {Query}"),
                LoggingHelper.SanitizeForLog(query));
            return StatusCode(500, "Error searching provider image candidates");
        }
    }

    /// <summary>
    /// Download a series cover image from one of the URLs returned by
    /// <see cref="SearchCandidates"/> and persist it as the cached image for
    /// the series. The URL is re-validated by <see cref="ISeriesImageStore"/>
    /// (scheme allow-list, SSRF guard, content-type and magic-byte checks,
    /// size cap), so unknown / hostile URLs are rejected with a 400.
    /// </summary>
    [HttpPost("{seriesTitle}/from-provider")]
    public async Task<IActionResult> ApplyFromProvider(
        string seriesTitle,
        [FromBody] ApplyFromProviderRequest? request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(seriesTitle))
        {
            return BadRequest("Series title is required");
        }
        if (request is null || string.IsNullOrWhiteSpace(request.ImageUrl))
        {
            return BadRequest("Image URL is required");
        }

        try
        {
            var record = await _cache.ApplyExternalImageAsync(
                seriesTitle,
                request.ImageUrl,
                request.Source,
                cancellationToken);
            return Ok(record);
        }
        catch (InvalidOperationException ex)
        {
            // ImageStore validation failures (bad URL, SSRF, wrong content
            // type, oversize) and the user-image-sticky check both surface
            // as InvalidOperationException. The user-image case maps to a
            // 409 Conflict so the UI can prompt the user to clear it first;
            // everything else is a 400.
            if (ex.Message.Contains("user-uploaded", StringComparison.OrdinalIgnoreCase))
            {
                return Conflict(new { error = ex.Message });
            }
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, LoggingHelper.WithWebsitePrefix("Error applying provider image for {SeriesTitle}"),
                LoggingHelper.SanitizeForLog(seriesTitle));
            return StatusCode(500, "Error applying provider image");
        }
    }

    /// <summary>
    /// Re-apply the current cached cover image to every series in the library,
    /// forcing the folder/first-archive cover writers even when their
    /// automatic-write feature flags are disabled.
    ///
    /// Runs as a background batch job (the same job/SSE pipeline used by
    /// process / rename / normalize) so the UI can show live per-series
    /// embedding progress instead of blocking on a single request. Returns the
    /// job id immediately; progress arrives via <c>job_updated</c> SSE events.
    /// </summary>
    [HttpPost("apply-current/all")]
    public async Task<ActionResult<object>> ApplyCurrentToAll(CancellationToken cancellationToken)
    {
        try
        {
            var library = await _library.GetSeriesAsync(perPage: -1, cancellationToken: cancellationToken);
            var consideredTitles = library.Series
                .Select(s => string.IsNullOrWhiteSpace(s.CanonicalTitle) ? s.Title : s.CanonicalTitle)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Only series that actually have a cached image to apply become job
            // items, so the progress bar reflects real embedding work and is
            // not dominated by no-op "skips" for series without a cover.
            var embeddableTitles = library.Series
                .Where(s => s.HasExternalImage)
                .Select(s => string.IsNullOrWhiteSpace(s.CanonicalTitle) ? s.Title : s.CanonicalTitle)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return await QueueCoverReapplyJobAsync(
                "ApplySeriesCoversAllJob",
                embeddableTitles,
                consideredTitles.Count,
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, LoggingHelper.WithWebsitePrefix("Error re-applying cached series images for all series"));
            return StatusCode(500, "Error updating series covers");
        }
    }

    /// <summary>
    /// Re-apply the current cached cover image to a specific set of series,
    /// forcing the folder/first-archive cover writers even when their
    /// automatic-write feature flags are disabled. Runs as a background batch
    /// job so the UI can show live per-series embedding progress.
    /// </summary>
    [HttpPost("apply-current/selected")]
    public async Task<ActionResult<object>> ApplyCurrentToSelected(
        [FromBody] ApplyCurrentSelectedRequest? request,
        CancellationToken cancellationToken)
    {
        var titles = request?.Series?
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (titles is null || titles.Count == 0)
        {
            return BadRequest("No series specified");
        }

        try
        {
            // Restrict the job to the requested series that actually have a
            // cached image, using the library's image flag as a cheap filter so
            // the progress bar tracks real embedding work.
            var library = await _library.GetSeriesAsync(perPage: -1, cancellationToken: cancellationToken);
            var embeddableTitles = titles
                .Where(title => SeriesHasCachedImage(library.Series, title))
                .ToList();

            return await QueueCoverReapplyJobAsync(
                "ApplySeriesCoversSelectedJob",
                embeddableTitles,
                titles.Count,
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, LoggingHelper.WithWebsitePrefix("Error re-applying cached series images for selected series"));
            return StatusCode(500, "Error updating series covers");
        }
    }

    /// <summary>
    /// True when a series matching <paramref name="title"/> (by canonical
    /// title, display title, or alias) has a cached external/user image to
    /// embed.
    /// </summary>
    private static bool SeriesHasCachedImage(
        IEnumerable<ComicMaintainer.Core.Models.SeriesLibraryDto> series,
        string title)
    {
        return series.Any(s =>
            s.HasExternalImage &&
            (string.Equals(s.CanonicalTitle, title, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(s.Title, title, StringComparison.OrdinalIgnoreCase) ||
             s.Aliases.Any(a => string.Equals(a, title, StringComparison.OrdinalIgnoreCase))));
    }

    /// <summary>
    /// Queue a batch job that re-applies the cached cover image to each title
    /// in <paramref name="embeddableTitles"/>, embedding the first-archive
    /// cover synchronously so the job's progress reflects real embedding work.
    /// </summary>
    private async Task<ActionResult<object>> QueueCoverReapplyJobAsync(
        string operationName,
        IReadOnlyList<string> embeddableTitles,
        int totalConsidered,
        CancellationToken cancellationToken)
    {
        var skipped = totalConsidered - embeddableTitles.Count;

        // Nothing to embed — return a completed-looking response with an empty
        // job id so the UI doesn't have to track an empty job.
        if (embeddableTitles.Count == 0)
        {
            return Ok(new
            {
                job_id = Guid.Empty.ToString(),
                total_items = 0,
                totalSeries = totalConsidered,
                skippedSeries = skipped
            });
        }

        var jobId = await _processor.RunCustomBatchJobAsync(
            operationName: operationName,
            trackedItems: embeddableTitles,
            itemOperation: (title, token) =>
                _cache.ReapplyImageArtifactsAsync(title, embedArchiveInline: true, token),
            failureMessage: "Cover embed failed",
            cancellationToken: cancellationToken);

        return Ok(new
        {
            job_id = jobId.ToString(),
            total_items = embeddableTitles.Count,
            totalSeries = totalConsidered,
            skippedSeries = skipped
        });
    }

    /// <summary>
    /// Delete the cached series image (downloaded or user-uploaded). The next
    /// metadata refresh is then free to re-download an external image.
    /// </summary>
    [HttpDelete("{normalizedKey}")]
    public async Task<IActionResult> Clear(string normalizedKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(normalizedKey))
        {
            return BadRequest("Series key is required");
        }

        try
        {
            var record = await _cache.ClearImageAsync(normalizedKey, cancellationToken);
            return record is null ? NotFound() : Ok(record);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, LoggingHelper.WithWebsitePrefix("Error clearing series image for {Key}"),
                LoggingHelper.SanitizeForLog(normalizedKey));
            return StatusCode(500, "Error clearing series image");
        }
    }

    public class ApplyFromProviderRequest
    {
        public string ImageUrl { get; set; } = string.Empty;
        public string? Source { get; set; }
    }

    public class ApplyCurrentSelectedRequest
    {
        public List<string> Series { get; set; } = new();
    }
}
