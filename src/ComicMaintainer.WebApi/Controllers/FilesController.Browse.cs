using ComicMaintainer.Core.Utilities;
using ComicMaintainer.WebApi.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace ComicMaintainer.WebApi.Controllers;

public partial class FilesController
{
    [LibraryETag]
    [HttpGet]
    public async Task<ActionResult<object>> GetFiles(
        [FromQuery] string? filter = null,
        [FromQuery] string? search = null,
        [FromQuery] int page = 1,
        [FromQuery] int per_page = 100,
        [FromQuery] string? sort = "name",
        [FromQuery] string? direction = "asc",
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("GetFiles: Request received - Filter: {Filter}, Search: {Search}, Page: {Page}, PerPage: {PerPage}, Sort: {Sort}, Direction: {Direction}",
                LoggingHelper.SanitizeForLog(filter), LoggingHelper.SanitizeForLog(search), page, per_page, LoggingHelper.SanitizeForLog(sort), LoggingHelper.SanitizeForLog(direction));

            var mappedFilter = MapFilter(filter);

            _logger.LogDebug("GetFiles: Mapped filter from '{OriginalFilter}' to '{MappedFilter}'", LoggingHelper.SanitizeForLog(filter), LoggingHelper.SanitizeForLog(mappedFilter));

            // DB-backed paged query — filtering, sorting and paging are pushed
            // down to SQL so we don't materialize the full library per request.
            var paged = await _fileStore.GetFilesPageAsync(
                mappedFilter, search, sort, direction, page, per_page, cancellationToken);

            var unmarkedCount = await _fileStore.GetUnmarkedCountAsync(cancellationToken);

            return Ok(new
            {
                files = paged.Files,
                page = paged.Page,
                total_pages = paged.TotalPages,
                total_files = paged.TotalFiles,
                unmarked_count = unmarkedCount
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting files");
            return StatusCode(500, "Error retrieving files");
        }
    }

    [LibraryETag]
    [HttpGet("series")]
    public async Task<ActionResult<object>> GetSeries(
        [FromQuery] string? filter = null,
        [FromQuery] string? search = null,
        [FromQuery] int page = 1,
        [FromQuery] int per_page = 100,
        [FromQuery] string? sort = "name",
        [FromQuery] string? direction = "asc",
        [FromQuery] bool include_issues = false,
        [FromQuery] int? offset = null,
        [FromQuery] int? limit = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var mappedFilter = MapFilter(filter);
            var unmarkedCount = await _fileStore.GetUnmarkedCountAsync(cancellationToken);

            // Default: lightweight summary cards (no per-issue list). Issues are
            // fetched lazily by the per-series endpoint to keep large libraries
            // responsive. Older clients can still request the full payload via
            // ?include_issues=true.
            if (include_issues)
            {
                var fullResult = await _seriesLibrary.GetSeriesAsync(mappedFilter, search, page, per_page, sort, direction, cancellationToken);
                return Ok(new
                {
                    series = fullResult.Series,
                    page = fullResult.Page,
                    total_pages = fullResult.TotalPages,
                    total_series = fullResult.TotalSeries,
                    unmarked_count = unmarkedCount
                });
            }

            bool offsetMode = limit.HasValue && limit.Value > 0;
            var result = await _seriesLibrary.GetSeriesSummariesAsync(
                mappedFilter, search, page, per_page, sort, direction,
                offsetMode ? offset ?? 0 : (int?)null,
                offsetMode ? limit : (int?)null,
                cancellationToken);

            if (offsetMode)
            {
                return Ok(new
                {
                    series = result.Series,
                    page = result.Page,
                    total_pages = result.TotalPages,
                    total_series = result.TotalSeries,
                    offset = result.Offset,
                    limit = limit!.Value,
                    unmarked_count = unmarkedCount
                });
            }

            return Ok(new
            {
                series = result.Series,
                page = result.Page,
                total_pages = result.TotalPages,
                total_series = result.TotalSeries,
                unmarked_count = unmarkedCount
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting series library");
            return StatusCode(500, "Error retrieving series library");
        }
    }

    [LibraryETag]
    [HttpGet("folders")]
    public async Task<ActionResult<object>> GetFolders(
        [FromQuery] string? filter = null,
        [FromQuery] string? search = null,
        [FromQuery] string? sort = "name",
        [FromQuery] string? direction = "asc",
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("GetFolders: Filter={Filter}, Search={Search}, Sort={Sort}, Direction={Direction}, Offset={Offset}, Limit={Limit}",
                LoggingHelper.SanitizeForLog(filter), LoggingHelper.SanitizeForLog(search),
                LoggingHelper.SanitizeForLog(sort), LoggingHelper.SanitizeForLog(direction), offset, limit);

            var mappedFilter = MapFilter(filter);
            var result = await _fileStore.GetFolderSummariesAsync(mappedFilter, search, sort, direction, offset, limit, cancellationToken);

            var unmarkedCount = await _fileStore.GetUnmarkedCountAsync(cancellationToken);

            return Ok(new
            {
                folders = result.Folders,
                offset = result.Offset,
                limit = result.Limit,
                total_folders = result.TotalFolders,
                unmarked_count = unmarkedCount
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting folders");
            return StatusCode(500, "Error retrieving folders");
        }
    }

    [LibraryETag]
    [HttpGet("folders/{encodedPath}/files")]
    public async Task<ActionResult<object>> GetFolderFiles(
        string encodedPath,
        [FromQuery] string? filter = null,
        [FromQuery] string? search = null,
        [FromQuery] string? sort = "name",
        [FromQuery] string? direction = "asc",
        CancellationToken cancellationToken = default)
    {
        try
        {
            var decodedPath = DecodeBase64UrlSafe(encodedPath ?? string.Empty);
            _logger.LogDebug("GetFolderFiles: DecodedPath={DecodedPath}, Filter={Filter}",
                LoggingHelper.SanitizeForLog(decodedPath), LoggingHelper.SanitizeForLog(filter));

            var mappedFilter = MapFilter(filter);
            var dtos = await _fileStore.GetFolderFilesAsync(decodedPath ?? string.Empty, mappedFilter, search, sort, direction, cancellationToken);

            return Ok(new
            {
                path = decodedPath ?? string.Empty,
                files = dtos
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting files for folder");
            return StatusCode(500, "Error retrieving folder files");
        }
    }

    [LibraryETag]
    [HttpGet("series/{seriesId}/issues")]
    public async Task<ActionResult<object>> GetSeriesIssues(
        string seriesId,
        [FromQuery] string? filter = null,
        [FromQuery] int page = 1,
        [FromQuery] int per_page = 100,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var mappedFilter = MapFilter(filter);
            var result = await _seriesLibrary.GetSeriesIssuesAsync(seriesId, mappedFilter, page, per_page, cancellationToken);
            if (result is null)
            {
                return NotFound(new { error = "Series not found" });
            }

            return Ok(new
            {
                id = result.Id,
                title = result.Title,
                canonical_title = result.CanonicalTitle,
                aliases = result.Aliases,
                metadata_source = result.MetadataSource,
                cover_file_path = result.CoverFilePath,
                has_external_image = result.HasExternalImage,
                external_image_url = result.ExternalImageUrl,
                issue_count = result.IssueCount,
                total_size = result.TotalSize,
                issues = result.Issues,
                page = result.Page,
                per_page = result.PerPage,
                total_pages = result.TotalPages
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting issues for series {SeriesId}", LoggingHelper.SanitizeForLog(seriesId));
            return StatusCode(500, "Error retrieving series issues");
        }
    }

    /// <summary>
    /// Returns the distinct on-disk folders that contain files attributed to
    /// the given series, plus (when the folders form a combinable group) the
    /// group key that can be passed to <c>POST /api/files/combine-folders</c>
    /// to merge them. Used by the per-series "Manage Folders" UI so users
    /// can see at a glance whether a series spans multiple folders and merge
    /// them without leaving the series detail view.
    /// </summary>
    [HttpGet("series/{seriesId}/folders")]
    public async Task<ActionResult<object>> GetSeriesFolders(
        string seriesId,
        [FromQuery] string? filter = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var mappedFilter = MapFilter(filter);
            var result = await _seriesLibrary.GetFoldersForSeriesIdAsync(seriesId, mappedFilter, cancellationToken);
            if (result is null)
            {
                return NotFound(new { error = "Series not found" });
            }

            // When the series spans two or more folders, prefer an existing
            // combinable-folder group (which carries a stable
            // SuggestedDestinationDirectory). If none of the alias/series
            // passes produced a matching group, fall back to a synthetic
            // `series:<id>` key so the per-series "Manage Folders" UI can
            // still hand off to the combine flow — the merge is authoritative
            // because every folder is already attributed to the same series
            // card.
            string? combineGroupKey = null;
            string? suggestedDestination = null;
            if (result.Folders.Count >= 2)
            {
                try
                {
                    var seriesDirs = new HashSet<string>(
                        result.Folders.Select(f => f.Directory),
                        StringComparer.OrdinalIgnoreCase);
                    var groups = await GetCombinableFolderGroupsCachedAsync(cancellationToken);
                    var match = groups.FirstOrDefault(g =>
                        g.Folders.Count(f => seriesDirs.Contains(f.Directory)) >= 2);
                    if (match is not null)
                    {
                        combineGroupKey = match.GroupKey;
                        suggestedDestination = match.SuggestedDestinationDirectory;
                    }
                    else
                    {
                        // Synthetic key: BuildCombineFoldersPlanAsync
                        // recognises the `series:` prefix and resolves the
                        // plan against the series-library folder set.
                        combineGroupKey = SeriesGroupKeyPrefix + result.Id;
                        suggestedDestination = result.Folders
                            .OrderByDescending(f => f.FileCount)
                            .ThenBy(f => f.Directory, StringComparer.OrdinalIgnoreCase)
                            .First()
                            .Directory;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to resolve combine group for series {SeriesId}",
                        LoggingHelper.SanitizeForLog(seriesId));
                }
            }

            return Ok(new
            {
                id = result.Id,
                title = result.Title,
                folders = result.Folders,
                combine_group_key = combineGroupKey,
                suggested_destination_directory = suggestedDestination
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting folders for series {SeriesId}", LoggingHelper.SanitizeForLog(seriesId));
            return StatusCode(500, "Error retrieving series folders");
        }
    }

    [LibraryETag]
    [HttpGet("counts")]
    public async Task<ActionResult<object>> GetFileCounts()
    {
        try
        {
            var (total, processed, unprocessed, duplicates) = await _fileStore.GetFileCountsAsync();
            var combinableFolders = 0;

            try
            {
                combinableFolders = await GetCombinableFolderCountAsync(HttpContext?.RequestAborted ?? default);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
            {
                _logger.LogWarning(ex, "Error getting combinable folder count");
            }

            return Ok(new { total, processed, unprocessed, duplicates, combinableFolders });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting file counts");
            return StatusCode(500, "Error retrieving file counts");
        }
    }

    private static string? MapFilter(string? filter) => filter switch
    {
        "marked" => "processed",
        "unmarked" => "unprocessed",
        "duplicates" => "duplicates",
        "renamed" => "renamed",
        "normalized" => "normalized",
        "read" => "read",
        "unread" => "unread",
        // Series-level provider-match filters. The file store treats these as
        // a no-op; SeriesLibraryService applies them after grouping so the
        // series list is filtered by whether an external metadata provider
        // returned a match (or one was set manually).
        "matched" => "matched",
        "unmatched" => "unmatched",
        // Series-level filter for series that have gaps in their issue
        // numbering. Like matched/unmatched, the file store treats this as a
        // no-op; SeriesLibraryService applies it after grouping.
        "missing" => "missing",
        _ => null
    };
}
