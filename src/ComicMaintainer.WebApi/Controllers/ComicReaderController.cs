using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Reader.Interfaces;
using ComicMaintainer.Core.Reader.Models;
using ComicMaintainer.Core.Reader.Services;
using ComicMaintainer.Core.Utilities;
using ComicMaintainer.WebApi.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Security.Claims;

namespace ComicMaintainer.WebApi.Controllers;

/// <summary>
/// Controller for reading comic archives (CBZ/CBR) and viewing pages
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ComicReaderController : ControllerBase
{
    /// <summary>
    /// Cache policy for pages and covers. They are only served to authenticated
    /// callers, so a shared cache (a reverse proxy's cache, a CDN, a corporate
    /// proxy) must never store them and hand them to someone else: <c>private</c>
    /// limits storage to the requesting browser and the PWA's service worker,
    /// which is all the reader and the library grid rely on.
    /// </summary>
    internal const string PrivateCacheControl = "private, max-age=3600";

    private readonly IComicReaderService _readerService;
    private readonly IFileStoreService _fileStore;
    private readonly IFileCoverCacheService _coverCache;
    private readonly ILogger<ComicReaderController> _logger;
    private readonly IOptionsMonitor<AppSettings> _settings;
    private readonly IReadingProgressService _readingProgress;
    private readonly ISeriesLibraryService _seriesLibrary;

    public ComicReaderController(
        IComicReaderService readerService,
        IFileStoreService fileStore,
        IFileCoverCacheService coverCache,
        ILogger<ComicReaderController> logger,
        IOptionsMonitor<AppSettings> settings,
        IReadingProgressService readingProgress,
        ISeriesLibraryService seriesLibrary)
    {
        _readerService = readerService;
        _fileStore = fileStore;
        _coverCache = coverCache;
        _logger = logger;
        _settings = settings;
        _readingProgress = readingProgress;
        _seriesLibrary = seriesLibrary;
    }

    /// <summary>
    /// Validates that a file path is within the watched directory to prevent path
    /// traversal attacks. Symlinks and junctions are resolved first — on the path
    /// itself and on every ancestor directory — and the comparison is
    /// separator-aware, so neither a link nor a sibling directory sharing the
    /// watched directory's name prefix can reach files outside it.
    /// </summary>
    private bool IsPathSafe(string filePath) => TryResolveSafePath(filePath, out _);

    /// <summary>
    /// Variant of <see cref="IsPathSafe"/> that also returns the validated
    /// physical path. Archive access must use it, so the file that was checked
    /// is the file that is read; library records keep the requested path.
    /// </summary>
    private bool TryResolveSafePath(string filePath, out string resolvedPath) =>
        LibraryPathValidator.TryResolveWithin(
            filePath,
            out resolvedPath,
            _settings.CurrentValue.WatchedDirectory);

    /// <summary>
    /// The answer for an archive the read limits refused (too many entries, or
    /// a page that is, or expands to, more than the per-entry cap): 422 with the
    /// reason, which names the entry and the limit. The reader service has
    /// already logged it.
    /// </summary>
    private UnprocessableEntityObjectResult ArchiveRefused(ArchiveLimitExceededException ex) =>
        UnprocessableEntity(new { error = ex.Message });

    /// <summary>
    /// Get comic information including page count
    /// </summary>
    /// <param name="filePath">Path to the comic file</param>
    [HttpGet("info")]
    public async Task<ActionResult<object>> GetComicInfo([FromQuery] string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return BadRequest(new { error = "File path is required" });
        }

        if (!TryResolveSafePath(filePath, out var resolvedPath))
        {
            _logger.LogWarning("Attempt to access file outside watched directory: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            return BadRequest(new { error = "File path is outside the allowed directory" });
        }

        if (!System.IO.File.Exists(resolvedPath))
        {
            return NotFound(new { error = "File not found" });
        }

        try
        {
            var pageCount = await _readerService.GetPageCountAsync(resolvedPath);
            var fileName = Path.GetFileName(filePath);

            return Ok(new
            {
                fileName,
                filePath,
                pageCount
            });
        }
        catch (ArchiveLimitExceededException ex)
        {
            return ArchiveRefused(ex);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Error getting comic info for {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            return StatusCode(500, new { error = "Error reading comic file" });
        }
    }

    /// <summary>
    /// Get a specific page from a comic as an image.
    /// </summary>
    /// <remarks>
    /// Pages are not cached on the server as responses: they are served only to
    /// authenticated callers, the browser and the PWA's service worker already
    /// keep the pages a reader has seen, and an archive's sorted page list is
    /// cached by <see cref="IComicReaderService"/>, so a page costs one entry
    /// read. Revalidation is answered with 304 from the file's length and
    /// last-write time, without opening the archive.
    /// </remarks>
    /// <param name="filePath">Path to the comic file</param>
    /// <param name="page">Page number (1-based index)</param>
    [HttpGet("page")]
    public async Task<IActionResult> GetPage([FromQuery] string filePath, [FromQuery] int page = 1)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return BadRequest(new { error = "File path is required" });
        }

        if (!TryResolveSafePath(filePath, out var resolvedPath))
        {
            _logger.LogWarning("Attempt to access file outside watched directory: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            return BadRequest(new { error = "File path is outside the allowed directory" });
        }

        var info = new FileInfo(resolvedPath);
        if (!info.Exists)
        {
            return NotFound(new { error = "File not found" });
        }

        if (page < 1)
        {
            return BadRequest(new { error = "Page number must be 1 or greater" });
        }

        try
        {
            // A page is a function of the archive's content, so the file's length
            // and last-write time (read before the page, so a concurrent rewrite
            // can only make the tag older than the bytes, never newer) validate it.
            var etag = BuildPageETag(info.Length, info.LastWriteTimeUtc, page);
            if (ConditionalRequests.IfNoneMatchSatisfied(Request.Headers.IfNoneMatch, etag))
            {
                Response.Headers.ETag = etag;
                Response.Headers.CacheControl = PrivateCacheControl;
                return StatusCode(StatusCodes.Status304NotModified);
            }

            var result = await _readerService.GetPageAsync(resolvedPath, page);

            if (result == null)
            {
                return NotFound(new { error = "Page not found" });
            }

            var (data, contentType) = result.Value;

            Response.Headers.ETag = etag;
            Response.Headers.CacheControl = PrivateCacheControl;
            Response.Headers.LastModified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero).ToString("R");

            return File(data, contentType);
        }
        catch (ArchiveLimitExceededException ex)
        {
            return ArchiveRefused(ex);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Error getting page {Page} from {FilePath}", page, LoggingHelper.SanitizePathForLog(filePath));
            return StatusCode(500, new { error = "Error reading page" });
        }
    }

    /// <summary>
    /// Builds the strong ETag for one page of one version of an archive: the
    /// page number and the file's last-write time and length.
    /// </summary>
    [NonAction]
    public static string BuildPageETag(long fileLength, DateTime lastWriteTimeUtc, int page) =>
        "\"p" + page.ToString(CultureInfo.InvariantCulture)
            + "-" + lastWriteTimeUtc.Ticks.ToString("x", CultureInfo.InvariantCulture)
            + "-" + fileLength.ToString("x", CultureInfo.InvariantCulture) + "\"";

    /// <summary>
    /// Get the cached cover (page 1) of a comic file as an image. Backed by
    /// the persistent on-disk file-cover cache so the archive is only opened
    /// the first time a file is requested (and again whenever its
    /// last-write-time or length changes). Supports <c>If-None-Match</c> for
    /// conditional revalidation, answered from the file's metadata alone: a
    /// 304 reads neither the archive nor the cached thumbnail.
    /// </summary>
    /// <param name="filePath">Path to the comic file</param>
    [HttpGet("cover")]
    public async Task<IActionResult> GetCover([FromQuery] string filePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return BadRequest(new { error = "File path is required" });
        }

        if (!TryResolveSafePath(filePath, out var resolvedPath))
        {
            _logger.LogWarning("Attempt to access file outside watched directory: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            return BadRequest(new { error = "File path is outside the allowed directory" });
        }

        try
        {
            var validator = _coverCache.GetValidator(resolvedPath);
            if (validator is null)
            {
                return NotFound(new { error = "File not found" });
            }

            var etag = BuildCoverETag(validator.Value);
            if (ConditionalRequests.IfNoneMatchSatisfied(Request.Headers.IfNoneMatch, etag))
            {
                Response.Headers.ETag = etag;
                Response.Headers.CacheControl = PrivateCacheControl;
                return StatusCode(StatusCodes.Status304NotModified);
            }

            var entry = await _coverCache.GetOrCreateAsync(resolvedPath, cancellationToken);
            if (entry is null)
            {
                return NotFound(new { error = "Cover not available" });
            }

            // Label the bytes with the version they were generated from, which is
            // newer than the validator above if the file changed in between.
            var served = entry.Value.Validator;
            Response.Headers.ETag = BuildCoverETag(served);
            Response.Headers.CacheControl = PrivateCacheControl;
            Response.Headers.LastModified = served.SourceLastModified.ToString("R");
            return File(entry.Value.Data, entry.Value.ContentType);
        }
        catch (ArchiveLimitExceededException ex)
        {
            return ArchiveRefused(ex);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Error getting cover for {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            return StatusCode(500, new { error = "Error reading cover" });
        }
    }

    /// <summary>
    /// Builds the strong ETag for a cover from its validator: the source file's
    /// last-write time and length, and the thumbnail variant so browsers
    /// revalidate after the thumbnail settings change.
    /// </summary>
    [NonAction]
    public static string BuildCoverETag(FileCoverValidator validator)
    {
        var variant = string.IsNullOrEmpty(validator.Variant) ? "orig" : validator.Variant;
        // Keep the opaque-tag to safe token characters.
        var safeVariant = new string(variant.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.').ToArray());
        return "\"" + validator.SourceLastModified.UtcTicks.ToString("x") + "-" + validator.SourceLength.ToString("x") + "-" + safeVariant + "\"";
    }

    /// <summary>
    /// Get all page names from a comic
    /// </summary>
    /// <param name="filePath">Path to the comic file</param>
    [HttpGet("pages")]
    public async Task<ActionResult<object>> GetPages([FromQuery] string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return BadRequest(new { error = "File path is required" });
        }

        if (!TryResolveSafePath(filePath, out var resolvedPath))
        {
            _logger.LogWarning("Attempt to access file outside watched directory: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            return BadRequest(new { error = "File path is outside the allowed directory" });
        }

        if (!System.IO.File.Exists(resolvedPath))
        {
            return NotFound(new { error = "File not found" });
        }

        try
        {
            var pages = await _readerService.GetPageNamesAsync(resolvedPath);
            return Ok(new { pages, count = pages.Count });
        }
        catch (ArchiveLimitExceededException ex)
        {
            return ArchiveRefused(ex);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Error getting pages from {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            return StatusCode(500, new { error = "Error reading comic file" });
        }
    }

    /// <summary>
    /// Get the next or previous comic file in the directory
    /// </summary>
    /// <param name="filePath">Current file path</param>
    /// <param name="direction">"next" or "prev"</param>
    [HttpGet("adjacent")]
    public async Task<ActionResult<object>> GetAdjacentFile([FromQuery] string filePath, [FromQuery] string direction = "next")
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return BadRequest(new { error = "File path is required" });
        }

        if (!IsPathSafe(filePath))
        {
            _logger.LogWarning("Attempt to access file outside watched directory: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            return BadRequest(new { error = "File path is outside the allowed directory" });
        }

        try
        {
            var adjacent = await _seriesLibrary.GetAdjacentIssueAsync(filePath, direction);

            if (!adjacent.Found)
            {
                return NotFound(new { error = "Current file not found in library" });
            }

            if (!adjacent.HasAdjacent)
            {
                return Ok(new { hasAdjacent = false, filePath = default(string?), fileName = default(string?) });
            }

            return Ok(new
            {
                hasAdjacent = true,
                filePath = adjacent.FilePath,
                fileName = adjacent.FileName
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Error getting adjacent file for {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            return StatusCode(500, new { error = "Error finding adjacent file" });
        }
    }

    /// <summary>
    /// Mark a file as read
    /// </summary>
    /// <param name="filePath">Path to the comic file</param>
    [HttpPost("mark-read")]
    public async Task<ActionResult> MarkAsRead([FromQuery] string filePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return BadRequest(new { error = "File path is required" });
        }

        if (!TryResolveSafePath(filePath, out var resolvedPath))
        {
            _logger.LogWarning("Attempt to mark file read outside watched directory: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            return BadRequest(new { error = "File path is outside the allowed directory" });
        }

        try
        {
            await _fileStore.MarkFileReadAsync(filePath, true, cancellationToken);
            await RecordUserProgressAsync(filePath, resolvedPath, markComplete: true, page: null, cancellationToken);
            _logger.LogDebug("Marked file as read: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            return Ok();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Error marking file as read: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            return StatusCode(500, new { error = "Error marking file as read" });
        }
    }

    /// <summary>
    /// Save reading progress for a comic file
    /// </summary>
    /// <param name="filePath">Path to the comic file</param>
    /// <param name="page">Current page number</param>
    [HttpPost("progress")]
    public async Task<ActionResult> SaveProgress([FromQuery] string filePath, [FromQuery] int page, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return BadRequest(new { error = "File path is required" });
        }

        if (page < 1)
        {
            return BadRequest(new { error = "Page number must be 1 or greater" });
        }

        if (!TryResolveSafePath(filePath, out var resolvedPath))
        {
            _logger.LogWarning("Attempt to save progress for file outside watched directory: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            return BadRequest(new { error = "File path is outside the allowed directory" });
        }

        try
        {
            await _fileStore.SaveReadingProgressAsync(filePath, page, cancellationToken);
            await RecordUserProgressAsync(filePath, resolvedPath, markComplete: false, page: page, cancellationToken);
            _logger.LogDebug("Saved reading progress for {FilePath}: page {Page}", LoggingHelper.SanitizePathForLog(filePath), page);
            return Ok();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Error saving reading progress for {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            return StatusCode(500, new { error = "Error saving reading progress" });
        }
    }

    /// <summary>
    /// Get reading progress for a comic file
    /// </summary>
    /// <param name="filePath">Path to the comic file</param>
    [HttpGet("progress")]
    public async Task<ActionResult<object>> GetProgress([FromQuery] string filePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return BadRequest(new { error = "File path is required" });
        }

        if (!IsPathSafe(filePath))
        {
            _logger.LogWarning("Attempt to get progress for file outside watched directory: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            return BadRequest(new { error = "File path is outside the allowed directory" });
        }

        try
        {
            var progress = await _fileStore.GetReadingProgressAsync(filePath, cancellationToken);
            return Ok(new { currentPage = progress });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Error getting reading progress for {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            return StatusCode(500, new { error = "Error getting reading progress" });
        }
    }

    /// <summary>
    /// Records durable per-user reading progress for the authenticated user.
    /// ContentId follows the reader convention of using the requested file
    /// path; the page count is read from the validated physical
    /// <paramref name="resolvedPath"/>. This powers the per-user
    /// "Continue Reading" overview row and is best-effort: failures here never
    /// fail the originating request.
    /// </summary>
    private async Task RecordUserProgressAsync(string filePath, string resolvedPath, bool markComplete, int? page, CancellationToken cancellationToken)
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId))
        {
            return;
        }

        try
        {
            var totalPages = await _readerService.GetPageCountAsync(resolvedPath);
            if (totalPages <= 0)
            {
                return;
            }

            var existing = await _readingProgress.GetProgressAsync(userId, filePath, cancellationToken)
                ?? new ReadingProgress
                {
                    UserId = userId,
                    ContentId = filePath,
                    TotalPages = totalPages
                };

            existing.TotalPages = totalPages;
            var targetPage = markComplete
                ? totalPages
                : Math.Clamp(page ?? existing.CurrentPage, 1, totalPages);

            ReadingProgressCalculator.ApplyProgress(existing, targetPage, DateTime.UtcNow);
            await _readingProgress.SaveProgressAsync(existing, cancellationToken);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Failed to record per-user reading progress for {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
        }
    }
}
