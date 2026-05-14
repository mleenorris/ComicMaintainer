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
    private readonly ISeriesImageStore _imageStore;
    private readonly ILogger<SeriesImagesController> _logger;

    public SeriesImagesController(
        ISeriesMetadataCacheService cache,
        ISeriesImageStore imageStore,
        ILogger<SeriesImagesController> logger)
    {
        _cache = cache;
        _imageStore = imageStore;
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
}
