using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Utilities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Options;

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
    private readonly ILogger<ComicReaderController> _logger;
    private readonly AppSettings _settings;

    public ComicReaderController(
        IComicReaderService readerService,
        IFileStoreService fileStore,
        ILogger<ComicReaderController> logger,
        IOptions<AppSettings> settings)
    {
        _readerService = readerService;
        _fileStore = fileStore;
        _logger = logger;
        _settings = settings.Value;
    }

    /// <summary>
    /// Validates that a file path is within the watched directory to prevent path traversal attacks
    /// </summary>
    private bool IsPathSafe(string filePath)
    {
        try
        {
            var fullPath = Path.GetFullPath(filePath);
            var watchedDir = Path.GetFullPath(_settings.WatchedDirectory);
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
            var allFiles = await _fileStore.GetFilteredFilesAsync(null);
            var sortedFiles = allFiles.OrderBy(f => f.FileName).ToList();
            
            var currentIndex = sortedFiles.FindIndex(f => f.FilePath == filePath);
            
            if (currentIndex == -1)
            {
                return NotFound(new { error = "Current file not found in library" });
            }

            int adjacentIndex = direction.ToLower() == "next" ? currentIndex + 1 : currentIndex - 1;
            
            if (adjacentIndex < 0 || adjacentIndex >= sortedFiles.Count)
            {
                return Ok(new { hasAdjacent = false, filePath = (string?)null, fileName = (string?)null });
            }

            var adjacentFile = sortedFiles[adjacentIndex];
            return Ok(new { 
                hasAdjacent = true, 
                filePath = adjacentFile.FilePath, 
                fileName = adjacentFile.FileName 
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
            _logger.LogDebug("Marked file as read: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error marking file as read: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            return StatusCode(500, new { error = "Error marking file as read" });
        }
    }
}
