using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Utilities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ComicMaintainer.WebApi.Controllers;

/// <summary>
/// Endpoints for clearing the cached "processed" state of files so that they
/// are re-considered by the rename/normalize pipeline. The processed flag is a
/// computed boolean derived from <c>IsRenamed</c> and <c>IsNormalized</c>; once
/// either is true the corresponding pipeline step short-circuits. Clearing the
/// flags lets users recover from a stale series match or canonical-title
/// change without resetting the entire database.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class StatusController : ControllerBase
{
    private readonly IFileStoreService _fileStore;
    private readonly ISeriesLibraryService _seriesLibrary;
    private readonly ILogger<StatusController> _logger;

    public StatusController(
        IFileStoreService fileStore,
        ISeriesLibraryService seriesLibrary,
        ILogger<StatusController> logger)
    {
        _fileStore = fileStore;
        _seriesLibrary = seriesLibrary;
        _logger = logger;
    }

    /// <summary>
    /// Clears the renamed/normalized/processed flags for an explicit list of
    /// file paths.
    /// </summary>
    [HttpPost("clear-selected")]
    public async Task<ActionResult<object>> ClearSelected([FromBody] ClearFilesRequest request, CancellationToken cancellationToken = default)
    {
        if (request?.Files == null || request.Files.Count == 0)
        {
            return BadRequest(new { error = "No files specified" });
        }

        _logger.LogWarning(
            LoggingHelper.WithWebsitePrefix("ClearSelected: clearing processed status for {Count} file(s)"),
            request.Files.Count);

        var cleared = await _fileStore.ClearProcessedStatusAsync(request.Files, cancellationToken);
        return Ok(new { cleared, requested = request.Files.Count });
    }

    /// <summary>
    /// Clears the renamed/normalized/processed flags for every file that
    /// belongs to the given series card. Useful when a series' metadata or
    /// canonical title has changed and the on-disk files refuse to update
    /// because they are already marked processed.
    /// </summary>
    [HttpPost("clear-series/{seriesId}")]
    public async Task<ActionResult<object>> ClearSeries(string seriesId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(seriesId))
        {
            return BadRequest(new { error = "seriesId is required" });
        }

        // perPage = -1 returns every issue mapped to this series card.
        var issues = await _seriesLibrary.GetSeriesIssuesAsync(seriesId, filter: null, page: 1, perPage: -1, cancellationToken);
        if (issues == null)
        {
            return NotFound(new { error = "Series not found" });
        }

        var filePaths = issues.Issues
            .Select(i => i.FilePath)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();

        _logger.LogWarning(
            LoggingHelper.WithWebsitePrefix("ClearSeries: clearing processed status for series {SeriesId} ({Count} file(s))"),
            LoggingHelper.SanitizeForLog(seriesId),
            filePaths.Count);

        var cleared = await _fileStore.ClearProcessedStatusAsync(filePaths, cancellationToken);
        return Ok(new
        {
            cleared,
            requested = filePaths.Count,
            seriesId,
            seriesTitle = issues.Title
        });
    }

    /// <summary>
    /// Clears the renamed/normalized/processed flags for every tracked file
    /// whose on-disk directory matches (or is below) the supplied folder.
    /// </summary>
    [HttpPost("clear-folder")]
    public async Task<ActionResult<object>> ClearFolder([FromBody] ClearFolderRequest request, CancellationToken cancellationToken = default)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Folder))
        {
            return BadRequest(new { error = "folder is required" });
        }

        string normalizedFolder;
        try
        {
            normalizedFolder = Path.GetFullPath(request.Folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ClearFolder: invalid folder path supplied");
            return BadRequest(new { error = "Invalid folder path" });
        }

        var allFiles = await _fileStore.GetAllFilesAsync(cancellationToken);
        var folderWithSeparator = normalizedFolder + Path.DirectorySeparatorChar;
        var filePaths = allFiles
            .Where(f =>
            {
                if (string.IsNullOrWhiteSpace(f.FilePath))
                {
                    return false;
                }

                // Match files whose directory is exactly the folder or a
                // descendant of it. We compare on Directory (already stored)
                // when available, falling back to FilePath segmentation.
                var dir = string.IsNullOrWhiteSpace(f.Directory)
                    ? Path.GetDirectoryName(f.FilePath) ?? string.Empty
                    : f.Directory;
                if (string.IsNullOrEmpty(dir))
                {
                    return false;
                }
                return string.Equals(dir, normalizedFolder, StringComparison.OrdinalIgnoreCase)
                    || dir.StartsWith(folderWithSeparator, StringComparison.OrdinalIgnoreCase);
            })
            .Select(f => f.FilePath)
            .ToList();

        _logger.LogWarning(
            LoggingHelper.WithWebsitePrefix("ClearFolder: clearing processed status for folder {Folder} ({Count} file(s))"),
            LoggingHelper.SanitizePathForLog(normalizedFolder),
            filePaths.Count);

        var cleared = await _fileStore.ClearProcessedStatusAsync(filePaths, cancellationToken);
        return Ok(new { cleared, requested = filePaths.Count, folder = normalizedFolder });
    }

    public class ClearFilesRequest
    {
        public List<string> Files { get; set; } = new();
    }

    public class ClearFolderRequest
    {
        public string Folder { get; set; } = string.Empty;
    }
}
