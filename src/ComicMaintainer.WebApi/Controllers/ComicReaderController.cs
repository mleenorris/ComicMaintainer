using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Reader.Interfaces;
using ComicMaintainer.Core.Reader.Models;
using ComicMaintainer.Core.Reader.Services;
using ComicMaintainer.Core.Utilities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Options;
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
    /// Validates that a file path is within the watched directory to prevent path traversal attacks
    /// </summary>
    private bool IsPathSafe(string filePath)
    {
        try
        {
            var fullPath = Path.GetFullPath(filePath);
            var watchedDir = Path.GetFullPath(_settings.CurrentValue.WatchedDirectory);
            return fullPath.StartsWith(watchedDir, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

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

        if (!IsPathSafe(filePath))
        {
            _logger.LogWarning("Attempt to access file outside watched directory: {FilePath}", filePath);
            return BadRequest(new { error = "File path is outside the allowed directory" });
        }

        if (!System.IO.File.Exists(filePath))
        {
            return NotFound(new { error = "File not found" });
        }

        try
        {
            var pageCount = await _readerService.GetPageCountAsync(filePath);
            var fileName = Path.GetFileName(filePath);

            return Ok(new
            {
                fileName,
                filePath,
                pageCount
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting comic info for {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            return StatusCode(500, new { error = "Error reading comic file" });
        }
    }

    /// <summary>
    /// Get a specific page from a comic as an image
    /// </summary>
    /// <param name="filePath">Path to the comic file</param>
    /// <param name="page">Page number (1-based index)</param>
    [HttpGet("page")]
    [OutputCache(PolicyName = "ComicPages")]
    public async Task<IActionResult> GetPage([FromQuery] string filePath, [FromQuery] int page = 1)
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

        if (!System.IO.File.Exists(filePath))
        {
            return NotFound(new { error = "File not found" });
        }

        if (page < 1)
        {
            return BadRequest(new { error = "Page number must be 1 or greater" });
        }

        try
        {
            var result = await _readerService.GetPageAsync(filePath, page);
            
            if (result == null)
            {
                return NotFound(new { error = "Page not found" });
            }

            var (data, contentType) = result.Value;
            
            // Set cache headers for better performance
            Response.Headers["Cache-Control"] = "public, max-age=3600";
            
            return File(data, contentType);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting page {Page} from {FilePath}", page, LoggingHelper.SanitizePathForLog(filePath));
            return StatusCode(500, new { error = "Error reading page" });
        }
    }

    /// <summary>
    /// Get the cached cover (page 1) of a comic file as an image. Backed by
    /// the persistent on-disk file-cover cache so the archive is only opened
    /// the first time a file is requested (and again whenever its
    /// last-write-time or length changes). Supports <c>If-None-Match</c> for
    /// conditional revalidation.
    /// </summary>
    /// <param name="filePath">Path to the comic file</param>
    [HttpGet("cover")]
    public async Task<IActionResult> GetCover([FromQuery] string filePath, CancellationToken cancellationToken = default)
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

        if (!System.IO.File.Exists(filePath))
        {
            return NotFound(new { error = "File not found" });
        }

        try
        {
            var entry = await _coverCache.GetOrCreateAsync(filePath, cancellationToken);
            if (entry is null)
            {
                return NotFound(new { error = "Cover not available" });
            }

            var etag = "\"" + entry.Value.LastModified.UtcTicks.ToString("x") + "-" + entry.Value.Data.Length.ToString("x") + "\"";
            var ifNoneMatch = Request.Headers.IfNoneMatch.ToString();
            if (!string.IsNullOrEmpty(ifNoneMatch) && ifNoneMatch.Contains(etag, StringComparison.Ordinal))
            {
                Response.Headers.ETag = etag;
                Response.Headers.CacheControl = "public, max-age=3600";
                return StatusCode(StatusCodes.Status304NotModified);
            }

            Response.Headers.ETag = etag;
            Response.Headers.CacheControl = "public, max-age=3600";
            Response.Headers.LastModified = entry.Value.LastModified.ToString("R");
            return File(entry.Value.Data, entry.Value.ContentType);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting cover for {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            return StatusCode(500, new { error = "Error reading cover" });
        }
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

        if (!IsPathSafe(filePath))
        {
            _logger.LogWarning("Attempt to access file outside watched directory: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            return BadRequest(new { error = "File path is outside the allowed directory" });
        }

        if (!System.IO.File.Exists(filePath))
        {
            return NotFound(new { error = "File not found" });
        }

        try
        {
            var pages = await _readerService.GetPageNamesAsync(filePath);
            return Ok(new { pages, count = pages.Count });
        }
        catch (Exception ex)
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
                return Ok(new { hasAdjacent = false, filePath = (string?)null, fileName = (string?)null });
            }

            return Ok(new
            {
                hasAdjacent = true,
                filePath = adjacent.FilePath,
                fileName = adjacent.FileName
            });
        }
        catch (Exception ex)
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

        if (!IsPathSafe(filePath))
        {
            _logger.LogWarning("Attempt to mark file read outside watched directory: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            return BadRequest(new { error = "File path is outside the allowed directory" });
        }

        try
        {
            await _fileStore.MarkFileReadAsync(filePath, true, cancellationToken);
            await RecordUserProgressAsync(filePath, markComplete: true, page: null, cancellationToken);
            _logger.LogDebug("Marked file as read: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            return Ok();
        }
        catch (Exception ex)
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

        if (!IsPathSafe(filePath))
        {
            _logger.LogWarning("Attempt to save progress for file outside watched directory: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            return BadRequest(new { error = "File path is outside the allowed directory" });
        }

        try
        {
            await _fileStore.SaveReadingProgressAsync(filePath, page, cancellationToken);
            await RecordUserProgressAsync(filePath, markComplete: false, page: page, cancellationToken);
            _logger.LogDebug("Saved reading progress for {FilePath}: page {Page}", LoggingHelper.SanitizePathForLog(filePath), page);
            return Ok();
        }
        catch (Exception ex)
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting reading progress for {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            return StatusCode(500, new { error = "Error getting reading progress" });
        }
    }

    /// <summary>
    /// Records durable per-user reading progress for the authenticated user.
    /// ContentId follows the reader convention of using the file path. This
    /// powers the per-user "Continue Reading" overview row and is best-effort:
    /// failures here never fail the originating request.
    /// </summary>
    private async Task RecordUserProgressAsync(string filePath, bool markComplete, int? page, CancellationToken cancellationToken)
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId))
        {
            return;
        }

        try
        {
            var totalPages = await _readerService.GetPageCountAsync(filePath);
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record per-user reading progress for {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
        }
    }
}
