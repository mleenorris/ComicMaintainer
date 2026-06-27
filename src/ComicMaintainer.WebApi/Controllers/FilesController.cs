using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Services;
using ComicMaintainer.WebApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Text.RegularExpressions;

namespace ComicMaintainer.WebApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class FilesController : ControllerBase
{
    // Unicode-aware: keep any Unicode letter (\p{L}) or number (\p{N}) so
    // non-ASCII titles (CJK, accented Latin, Cyrillic, etc.) produce rich,
    // distinguishable keys instead of collapsing to a bare digit when every
    // letter gets stripped. Pure-ASCII titles still produce the same output
    // as the previous [^a-z0-9]+ sanitizer.
    private static readonly Regex FolderCombineKeySanitizer = new(@"[^\p{L}\p{N}]+", RegexOptions.Compiled);
    private static readonly Regex FileNameSeriesSuffixSanitizer = new(
        @"\s*(?:-|_)?\s*(?:ch|chapter|issue|#)?\s*\d+(?:\.\d+)?[a-z]?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly IFileStoreService _fileStore;
    private readonly IComicProcessorService _processor;
    private readonly IProcessingHistoryService _historyService;
    private readonly ISeriesLibraryService _seriesLibrary;
    private readonly ILogger<FilesController> _logger;
    private readonly IOptionsMonitor<AppSettings> _settings;
    private readonly IDbContextFactory<ComicMaintainerDbContext>? _dbContextFactory;
    private readonly IEventBroadcaster? _eventBroadcaster;
    private readonly ISeriesMetadataCacheService? _metadataCache;
    private readonly ISeriesNameResolver? _seriesNameResolver;
    private readonly ScheduledJobsHostedService? _scheduledJobsHostedService;

    public FilesController(
        IFileStoreService fileStore,
        IComicProcessorService processor,
        IProcessingHistoryService historyService,
        ISeriesLibraryService seriesLibrary,
        ILogger<FilesController> logger,
        IOptionsMonitor<AppSettings> settings,
        IDbContextFactory<ComicMaintainerDbContext>? dbContextFactory = null,
        IEventBroadcaster? eventBroadcaster = null,
        ISeriesMetadataCacheService? metadataCache = null,
        ISeriesNameResolver? seriesNameResolver = null,
        ScheduledJobsHostedService? scheduledJobsHostedService = null)
    {
        _fileStore = fileStore;
        _processor = processor;
        _historyService = historyService;
        _seriesLibrary = seriesLibrary;
        _logger = logger;
        _settings = settings;
        _dbContextFactory = dbContextFactory;
        _eventBroadcaster = eventBroadcaster;
        _metadataCache = metadataCache;
        _seriesNameResolver = seriesNameResolver;
        _scheduledJobsHostedService = scheduledJobsHostedService;
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
                    var groups = await BuildCombinableFolderGroupsAsync(cancellationToken);
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

    [HttpGet("counts")]
    public async Task<ActionResult<object>> GetFileCounts()
    {
        try
        {
            var (total, processed, unprocessed, duplicates) = await _fileStore.GetFileCountsAsync();
            var combinableFolders = 0;

            try
            {
                combinableFolders = await GetCombinableFolderCountAsync();
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

    private async Task<int> GetCombinableFolderCountAsync(CancellationToken cancellationToken = default)
    {
        var groups = await BuildCombinableFolderGroupsAsync(cancellationToken);
        // Mirror the previous behavior: every folder beyond the suggested destination counts as a source.
        return groups.Sum(g => Math.Max(0, g.Folders.Count - 1));
    }

    private async Task<List<CombinableFolderGroup>> BuildCombinableFolderGroupsAsync(CancellationToken cancellationToken = default)
    {
        var files = ((await _fileStore.GetAllFilesAsync(cancellationToken)) ?? Enumerable.Empty<ComicFile>())
            .Where(file => !string.IsNullOrWhiteSpace(file.FilePath))
            .ToList();

        if (files.Count < 2)
        {
            return new List<CombinableFolderGroup>();
        }

        var addedAtLookup = await GetAddedAtLookupAsync(cancellationToken);

        // Build a directory-level index of every tracked file. This is reused
        // by the series-library and parenthetical-suffix passes below so they
        // can compute CombinableFolder entries for directories that weren't
        // bucketed by the alias-index pass (e.g. single-folder series).
        // The directory bucket uses an ordinal (case-sensitive) comparer for
        // the same reason as the alias-index pass: two on-disk folders that
        // differ only by capitalization on case-sensitive filesystems are
        // genuinely distinct directories.
        var filesByDirectory = new Dictionary<string, List<(ComicFile File, DateTime AddedAt)>>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var directory = !string.IsNullOrWhiteSpace(file.Directory)
                ? file.Directory
                : Path.GetDirectoryName(file.FilePath);

            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            var addedAt = addedAtLookup.TryGetValue(file.FilePath, out var createdAt)
                ? createdAt
                : file.LastModified;

            if (!filesByDirectory.TryGetValue(directory, out var dirFiles))
            {
                dirFiles = new List<(ComicFile, DateTime)>();
                filesByDirectory[directory] = dirFiles;
            }
            dirFiles.Add((file, addedAt));
        }

        // Load the persistent series-metadata cache so we can collapse folders
        // that share canonical/provider/user aliases into a single combinable
        // group. Without this, two folders that resolve to the same series via
        // an alias (e.g. "Batman" and "The Dark Knight") would be reported as
        // distinct groups even though the user has explicitly told us they are
        // the same series.
        var aliasIndex = await BuildFolderCombineAliasIndexAsync(cancellationToken);

        // Bucket files by (groupKey -> directory -> list of files). See above
        // for why the inner dictionary is case-sensitive.
        var byGroup = new Dictionary<string, Dictionary<string, List<(ComicFile File, DateTime AddedAt)>>>(StringComparer.OrdinalIgnoreCase);
        var groupDisplay = new Dictionary<string, (string SeriesName, string? Volume)>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            var directory = !string.IsNullOrWhiteSpace(file.Directory)
                ? file.Directory
                : Path.GetDirectoryName(file.FilePath);

            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            var groupKey = BuildFolderCombineGroupKey(file, aliasIndex);
            if (string.IsNullOrWhiteSpace(groupKey))
            {
                continue;
            }

            var addedAt = addedAtLookup.TryGetValue(file.FilePath, out var createdAt)
                ? createdAt
                : file.LastModified;

            if (!byGroup.TryGetValue(groupKey, out var dirMap))
            {
                dirMap = new Dictionary<string, List<(ComicFile, DateTime)>>(StringComparer.Ordinal);
                byGroup[groupKey] = dirMap;
                groupDisplay[groupKey] = (
                    BuildFolderCombineSeriesDisplayName(file, aliasIndex) ?? groupKey,
                    string.IsNullOrWhiteSpace(file.Metadata?.Volume) ? null : file.Metadata!.Volume!.Trim());
            }

            if (!dirMap.TryGetValue(directory, out var dirFiles))
            {
                dirFiles = new List<(ComicFile, DateTime)>();
                dirMap[directory] = dirFiles;
            }

            dirFiles.Add((file, addedAt));
        }

        var result = new List<CombinableFolderGroup>();
        foreach (var (groupKey, dirMap) in byGroup)
        {
            if (dirMap.Count < 2)
            {
                continue;
            }

            var folders = dirMap
                .Select(kvp => BuildCombinableFolder(kvp.Key, kvp.Value))
                .OrderByDescending(f => f.NewestFileAddedAt)
                .ThenBy(f => f.Directory, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var suggested = folders[0];
            var display = groupDisplay[groupKey];
            result.Add(new CombinableFolderGroup
            {
                GroupKey = groupKey,
                SeriesName = display.SeriesName,
                Volume = display.Volume,
                Folders = folders,
                SuggestedDestinationDirectory = suggested.Directory,
                SuggestionReason = $"Contains the most recently added file ({suggested.NewestFileAddedAt:yyyy-MM-dd}).",
                TotalFileCount = folders.Sum(f => f.FileCount)
            });
        }

        // Pass 2 (Change A): reconcile with the authoritative series-library
        // grouping. Any series that the SeriesLibraryService groups into a
        // single card but the alias-index pass split across multiple groups
        // (or never recognized as combinable at all) gets a synthetic
        // `series:<id>` group so the user can merge from the per-series UI.
        await ExtendWithSeriesLibraryGroupsAsync(result, filesByDirectory, cancellationToken);

        // Pass 3 (Change B): surface folder pairs that differ only by a
        // non-distinguishing parenthetical suffix (e.g. "Tomb Raider King" vs
        // "Tomb Raider King (Official)").
        ExtendWithParentheticalSuffixGroups(result, filesByDirectory);

        return result
            .OrderByDescending(g => g.Folders.Max(f => f.NewestFileAddedAt))
            .ThenBy(g => g.SeriesName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static CombinableFolder BuildCombinableFolder(
        string directory,
        IReadOnlyList<(ComicFile File, DateTime AddedAt)> dirFiles)
    {
        var newestAdded = dirFiles.Max(e => e.AddedAt);
        var oldestAdded = dirFiles.Min(e => e.AddedAt);
        var newestModified = dirFiles.Max(e => e.File.LastModified);
        var totalSize = dirFiles.Sum(e => e.File.FileSize);
        var sample = dirFiles
            .OrderBy(e => e.File.FileName, StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .Select(e => e.File.FileName)
            .ToList();
        var allFilePaths = dirFiles
            .Select(e => e.File.FilePath)
            .ToList();
        return new CombinableFolder
        {
            Directory = directory,
            FileCount = dirFiles.Count,
            TotalSize = totalSize,
            NewestFileAddedAt = newestAdded,
            OldestFileAddedAt = oldestAdded,
            NewestFileModifiedAt = newestModified,
            SampleFileNames = sample,
            FilePaths = allFilePaths
        };
    }

    /// <summary>
    /// Pass that uses the authoritative <see cref="ISeriesLibraryService"/>
    /// grouping to reconcile combinable folder groups. For every series that
    /// the library reports as spanning >= 2 distinct directories, we ensure
    /// a single combinable group exists keyed <c>series:&lt;seriesId&gt;</c>
    /// that contains every one of those directories. Any pre-existing
    /// combinable groups that overlap with the series are merged into the
    /// synthetic group.
    /// </summary>
    internal const string SeriesGroupKeyPrefix = "series:";

    private async Task ExtendWithSeriesLibraryGroupsAsync(
        List<CombinableFolderGroup> groups,
        IReadOnlyDictionary<string, List<(ComicFile File, DateTime AddedAt)>> filesByDirectory,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<SeriesFoldersResult> seriesGroups;
        try
        {
            seriesGroups = await _seriesLibrary.GetAllSeriesFolderGroupsAsync(filter: null, cancellationToken)
                ?? Array.Empty<SeriesFoldersResult>();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to load series-library folder groups for combinable-folder reconciliation");
            return;
        }

        foreach (var series in seriesGroups)
        {
            if (series.Folders.Count < 2)
            {
                continue;
            }

            // Resolve every series-library directory back to its tracked-file
            // bucket. We skip the series entirely if none of the directories
            // have any tracked files (defensive — shouldn't happen).
            var directoryFiles = new Dictionary<string, List<(ComicFile File, DateTime AddedAt)>>(StringComparer.Ordinal);
            foreach (var folder in series.Folders)
            {
                if (string.IsNullOrWhiteSpace(folder.Directory)) continue;
                if (filesByDirectory.TryGetValue(folder.Directory, out var dirFiles) && dirFiles.Count > 0)
                {
                    directoryFiles[folder.Directory] = dirFiles;
                }
            }

            if (directoryFiles.Count < 2)
            {
                continue;
            }

            var synthGroupKey = SeriesGroupKeyPrefix + series.Id;

            // Find every existing combinable group that overlaps with these
            // directories. Comparison is ordinal because the directories
            // produced by both code paths are absolute paths from the file
            // store (case-sensitive on Linux/Docker bind mounts).
            var overlapping = groups
                .Where(g => g.Folders.Any(f => directoryFiles.ContainsKey(f.Directory)))
                .ToList();

            // Build the merged folder set: include every directory in the
            // series-library view plus any directories already attached to an
            // overlapping group (those may be additional folders the alias
            // index discovered).
            var mergedDirectoryFiles = new Dictionary<string, List<(ComicFile File, DateTime AddedAt)>>(directoryFiles, StringComparer.Ordinal);
            foreach (var overlap in overlapping)
            {
                foreach (var folder in overlap.Folders)
                {
                    if (string.IsNullOrWhiteSpace(folder.Directory)) continue;
                    if (mergedDirectoryFiles.ContainsKey(folder.Directory)) continue;
                    if (filesByDirectory.TryGetValue(folder.Directory, out var dirFiles) && dirFiles.Count > 0)
                    {
                        mergedDirectoryFiles[folder.Directory] = dirFiles;
                    }
                }
            }

            // If the only overlap is a single group whose folders are exactly
            // this series's folders (no extras, none missing), leave it alone
            // to preserve the original group key / reason / display name.
            if (overlapping.Count == 1
                && overlapping[0].Folders.Count == mergedDirectoryFiles.Count
                && overlapping[0].Folders.All(f => mergedDirectoryFiles.ContainsKey(f.Directory)))
            {
                continue;
            }

            // Remove every overlapping group; we're about to replace them
            // with a single synthetic series group.
            foreach (var overlap in overlapping)
            {
                groups.Remove(overlap);
            }

            var mergedFolders = mergedDirectoryFiles
                .Select(kvp => BuildCombinableFolder(kvp.Key, kvp.Value))
                .OrderByDescending(f => f.NewestFileAddedAt)
                .ThenBy(f => f.Directory, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var suggested = mergedFolders[0];
            var seriesName = !string.IsNullOrWhiteSpace(series.Title)
                ? series.Title
                : (overlapping.FirstOrDefault()?.SeriesName ?? series.Id);

            groups.Add(new CombinableFolderGroup
            {
                GroupKey = synthGroupKey,
                SeriesName = seriesName,
                Volume = null,
                Folders = mergedFolders,
                SuggestedDestinationDirectory = suggested.Directory,
                SuggestionReason = overlapping.Count == 0
                    ? "These folders are all attributed to the same series."
                    : $"Contains the most recently added file ({suggested.NewestFileAddedAt:yyyy-MM-dd}).",
                TotalFileCount = mergedFolders.Sum(f => f.FileCount)
            });
        }
    }

    // Whitelist of parenthetical suffixes that are considered
    // non-distinguishing (i.e. a folder that differs from another only by
    // this suffix is likely the same series). Kept intentionally narrow to
    // avoid false-positive merges (e.g. year suffixes like "(2018)" are NOT
    // included because they often represent distinct print runs).
    private static readonly HashSet<string> NonDistinguishingSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "official",
        "webtoon",
        "manga",
        "manhwa",
        "manhua",
        "colorized",
        "colored",
        "colour",
        "coloured",
        "digital",
        "remastered",
        "complete",
        "omnibus",
        "tpb",
    };

    private static readonly Regex ParentheticalSuffixPattern = new(
        @"\s*\(([^()]+)\)\s*$",
        RegexOptions.Compiled);

    /// <summary>
    /// Returns the "base" series name with any trailing whitelisted
    /// parenthetical suffix stripped, e.g.
    /// "Tomb Raider King (Official)" -> "Tomb Raider King".
    /// Returns null when there is no whitelisted suffix (so callers can keep
    /// the original name unchanged).
    /// </summary>
    private static string? TryStripWhitelistedParentheticalSuffix(string folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName))
        {
            return null;
        }

        var match = ParentheticalSuffixPattern.Match(folderName);
        if (!match.Success)
        {
            return null;
        }

        var inside = match.Groups[1].Value.Trim();
        if (string.IsNullOrEmpty(inside))
        {
            return null;
        }

        if (!NonDistinguishingSuffixes.Contains(inside))
        {
            return null;
        }

        var stripped = folderName.Substring(0, match.Index).Trim();
        return string.IsNullOrEmpty(stripped) ? null : stripped;
    }

    /// <summary>
    /// Pass that surfaces folder pairs differing only by a non-distinguishing
    /// parenthetical suffix from <see cref="NonDistinguishingSuffixes"/>. The
    /// match key is the folder name itself (case-insensitive) rather than the
    /// in-archive metadata series name, because the canonical use case is
    /// folders like "Tomb Raider King" vs "Tomb Raider King (Official)" that
    /// the library has not yet learned share a series.
    /// </summary>
    private void ExtendWithParentheticalSuffixGroups(
        List<CombinableFolderGroup> groups,
        IReadOnlyDictionary<string, List<(ComicFile File, DateTime AddedAt)>> filesByDirectory)
    {
        // Bucket every tracked directory by its normalized "base" folder name
        // (folder name with whitelisted parenthetical suffix stripped). When
        // two or more directories share a base name AND at least one of them
        // had the suffix originally, they're candidates to merge.
        var byBaseName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var hadSuffixByDirectory = new Dictionary<string, bool>(StringComparer.Ordinal);

        foreach (var directory in filesByDirectory.Keys)
        {
            var folderName = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(folderName))
            {
                continue;
            }

            var stripped = TryStripWhitelistedParentheticalSuffix(folderName);
            var baseName = stripped ?? folderName;
            hadSuffixByDirectory[directory] = stripped is not null;

            var normalized = NormalizeFolderCombineKey(baseName);
            if (string.IsNullOrWhiteSpace(normalized) || IsAmbiguousNormalizedKey(normalized))
            {
                continue;
            }

            if (!byBaseName.TryGetValue(normalized, out var list))
            {
                list = new List<string>();
                byBaseName[normalized] = list;
            }
            list.Add(directory);
        }

        foreach (var (_, directories) in byBaseName)
        {
            if (directories.Count < 2)
            {
                continue;
            }

            // At least one directory in the bucket must have actually had a
            // whitelisted suffix (otherwise this is just two folders that
            // share a name with no whitelist trigger — those should already
            // be picked up by earlier passes or stay separate).
            if (!directories.Any(d => hadSuffixByDirectory.TryGetValue(d, out var had) && had))
            {
                continue;
            }

            // If every directory in the bucket is already covered by a single
            // existing combinable group, leave that group alone.
            var coveringGroups = groups
                .Where(g => g.Folders.Any(f => directories.Contains(f.Directory, StringComparer.Ordinal)))
                .ToList();

            // Merge the directories with any overlapping groups' directories.
            var mergedDirectoryFiles = new Dictionary<string, List<(ComicFile File, DateTime AddedAt)>>(StringComparer.Ordinal);
            foreach (var dir in directories)
            {
                if (filesByDirectory.TryGetValue(dir, out var dirFiles) && dirFiles.Count > 0)
                {
                    mergedDirectoryFiles[dir] = dirFiles;
                }
            }
            foreach (var overlap in coveringGroups)
            {
                foreach (var folder in overlap.Folders)
                {
                    if (string.IsNullOrWhiteSpace(folder.Directory)) continue;
                    if (mergedDirectoryFiles.ContainsKey(folder.Directory)) continue;
                    if (filesByDirectory.TryGetValue(folder.Directory, out var dirFiles) && dirFiles.Count > 0)
                    {
                        mergedDirectoryFiles[folder.Directory] = dirFiles;
                    }
                }
            }

            if (mergedDirectoryFiles.Count < 2)
            {
                continue;
            }

            // If a single existing group already covers exactly this set, no
            // change is needed.
            if (coveringGroups.Count == 1
                && coveringGroups[0].Folders.Count == mergedDirectoryFiles.Count
                && coveringGroups[0].Folders.All(f => mergedDirectoryFiles.ContainsKey(f.Directory)))
            {
                continue;
            }

            foreach (var overlap in coveringGroups)
            {
                groups.Remove(overlap);
            }

            var mergedFolders = mergedDirectoryFiles
                .Select(kvp => BuildCombinableFolder(kvp.Key, kvp.Value))
                .OrderByDescending(f => f.NewestFileAddedAt)
                .ThenBy(f => f.Directory, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var suggested = mergedFolders[0];

            // Surface the suffix that triggered the merge in the suggestion
            // reason so the user can tell at a glance why the system is
            // recommending this combine.
            var triggerFolderName = directories
                .Select(d => Path.GetFileName(d.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))
                .FirstOrDefault(name => TryStripWhitelistedParentheticalSuffix(name ?? string.Empty) is not null)
                ?? string.Empty;
            var triggerMatch = ParentheticalSuffixPattern.Match(triggerFolderName);
            var suffixDisplay = triggerMatch.Success ? $"({triggerMatch.Groups[1].Value.Trim()})" : "(suffix)";
            var baseFolderName = Path.GetFileName(directories
                .OrderBy(d => Path.GetFileName(d.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))?.Length ?? 0)
                .First()
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? string.Empty;

            var groupKey = "suffix:" + (NormalizeFolderCombineKey(baseFolderName) ?? baseFolderName);

            groups.Add(new CombinableFolderGroup
            {
                GroupKey = groupKey,
                SeriesName = baseFolderName,
                Volume = null,
                Folders = mergedFolders,
                SuggestedDestinationDirectory = suggested.Directory,
                SuggestionReason = $"Folder name differs only by a non-distinguishing suffix: {suffixDisplay}.",
                TotalFileCount = mergedFolders.Sum(f => f.FileCount)
            });
        }
    }

    [HttpGet("combinable-folders")]
    public async Task<ActionResult<object>> GetCombinableFolders(CancellationToken cancellationToken = default)
    {
        try
        {
            var groups = await BuildCombinableFolderGroupsAsync(cancellationToken);
            var dtos = groups.Select(ToCombinableFolderGroupDto).ToList();
            return Ok(new { groups = dtos });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving combinable folders");
            return StatusCode(500, "Error retrieving combinable folders");
        }
    }

    [HttpPost("combine-folders/preview")]
    public async Task<ActionResult<object>> PreviewCombineFolders(
        [FromBody] CombineFoldersRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var (error, plan) = await BuildCombineFoldersPlanAsync(request, cancellationToken);
            if (error != null)
            {
                return BadRequest(new { error });
            }

            return Ok(new
            {
                destination = plan!.Destination,
                sources = plan.SourceDirectories,
                moves = plan.Moves.Select(m => new
                {
                    sourcePath = m.SourcePath,
                    destinationPath = m.DestinationPath,
                    conflict = m.HadConflict,
                    skipped = m.Skipped,
                    reason = m.Reason
                })
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error building combine-folders preview");
            return StatusCode(500, "Error building combine preview");
        }
    }

    [HttpPost("combine-folders")]
    public async Task<ActionResult<object>> CombineFolders(
        [FromBody] CombineFoldersRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var (error, plan) = await BuildCombineFoldersPlanAsync(request, cancellationToken);
            if (error != null)
            {
                return BadRequest(new { error });
            }

            // Skipped entries are decided synchronously when the plan was built;
            // they need no work and can be reported in the immediate response.
            var skippedResults = plan!.Moves
                .Where(m => m.Skipped)
                .Select(m => new { sourcePath = m.SourcePath, destinationPath = m.DestinationPath, status = "skipped", reason = m.Reason })
                .ToList();

            var nonSkippedMoves = plan.Moves.Where(m => !m.Skipped).ToList();

            // Nothing to move — return immediately with the skipped results so
            // the UI doesn't have to track an empty job.
            if (nonSkippedMoves.Count == 0)
            {
                return Accepted(new
                {
                    destination = plan.Destination,
                    sources = plan.SourceDirectories,
                    moved = 0,
                    skipped = skippedResults.Count,
                    failed = 0,
                    totalItems = 0,
                    results = skippedResults
                });
            }

            // Capture in-memory series metadata for the files about to be moved so
            // we can persist their existing series names (which the user has
            // implicitly endorsed by combining the folders) as aliases on the
            // destination series. This must happen *before* the moves because the
            // in-memory ComicFile list keys off the original source paths.
            var preMoveSeriesBySourcePath = await GetPreMoveSeriesNamesAsync(
                nonSkippedMoves.Select(m => m.SourcePath),
                cancellationToken);

            // Index moves by source path so the per-item job operation can look
            // up the destination without rebuilding the plan.
            var movesBySource = nonSkippedMoves.ToDictionary(
                m => m.SourcePath,
                m => m,
                StringComparer.OrdinalIgnoreCase);

            // Concurrent collection — File.Move on distinct files is safe in
            // parallel, but the result list is mutated from worker threads.
            var movedDestinationPaths = new System.Collections.Concurrent.ConcurrentBag<string>();

            var trackedItems = nonSkippedMoves.Select(m => m.SourcePath).ToList();

            var jobId = await _processor.RunCustomBatchJobAsync(
                operationName: "CombineFoldersJob",
                trackedItems: trackedItems,
                itemOperation: async (sourcePath, token) =>
                {
                    if (!movesBySource.TryGetValue(sourcePath, out var move))
                    {
                        return false;
                    }
                    try
                    {
                        var destDir = Path.GetDirectoryName(move.DestinationPath);
                        if (!string.IsNullOrEmpty(destDir))
                        {
                            System.IO.Directory.CreateDirectory(destDir);
                        }

                        System.IO.File.Move(move.SourcePath, move.DestinationPath);
                        // Suppress per-file broadcasts: combining a folder with many files would
                        // otherwise emit one file_list_updated SSE event per move, flooding the
                        // browser and hanging the site. We emit a single broadcast after the loop.
                        await _fileStore.UpdateFilePathAsync(move.SourcePath, move.DestinationPath, token, broadcastUpdate: false);
                        await LogHistoryAsync(move.SourcePath, "Combine Folder", true,
                            $"Moved to {move.DestinationPath}");
                        movedDestinationPaths.Add(move.DestinationPath);
                        return true;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        _logger.LogWarning(ex, "Failed to move {Source} to {Destination} during folder combine",
                            LoggingHelper.SanitizePathForLog(move.SourcePath),
                            LoggingHelper.SanitizePathForLog(move.DestinationPath));
                        await LogHistoryAsync(move.SourcePath, "Combine Folder", false, ex.Message);
                        return false;
                    }
                },
                failureMessage: "Combine folder move failed",
                postLoopAsync: async (token) =>
                {
                    // Remove source directories the files were moved out of. A
                    // directory is removed when it no longer contains any comic
                    // archives (even if leftover non-comic files such as cover
                    // images or ComicInfo.xml remain). If a comic failed to move
                    // it stays behind, so we keep the folder for that case.
                    var removedDirectories = new List<string>();
                    foreach (var sourceDir in plan.SourceDirectories)
                    {
                        try
                        {
                            if (System.IO.Directory.Exists(sourceDir) &&
                                !DirectoryContainsComicArchives(sourceDir))
                            {
                                System.IO.Directory.Delete(sourceDir, recursive: true);
                                removedDirectories.Add(sourceDir);
                            }
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            _logger.LogDebug(ex, "Could not remove source directory {Directory} after folder combine",
                                LoggingHelper.SanitizePathForLog(sourceDir));
                        }
                    }

                    // Emit a single file list update broadcast for the entire combine operation
                    // so that connected clients refresh once instead of once per file.
                    if ((!movedDestinationPaths.IsEmpty || removedDirectories.Count > 0) && _eventBroadcaster != null)
                    {
                        try
                        {
                            await _eventBroadcaster.BroadcastFileListUpdateAsync();
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to broadcast file list update after folder combine");
                        }
                    }

                    // Persist user-canonical / alias info on the destination
                    // series and queue a normalize-then-rename batch job so
                    // every file in the combined folder reflects the new
                    // series name / file format.
                    if (!movedDestinationPaths.IsEmpty)
                    {
                        try
                        {
                            await PersistFolderCombineAliasesAsync(plan, preMoveSeriesBySourcePath, token);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to persist folder-combine aliases for {Destination}",
                                LoggingHelper.SanitizePathForLog(plan.Destination));
                        }

                        var pathsToProcess = new HashSet<string>(movedDestinationPaths, StringComparer.OrdinalIgnoreCase);
                        try
                        {
                            var existingDestinationFiles = await GetFilesInDirectoryAsync(plan.Destination, token);
                            foreach (var existingPath in existingDestinationFiles)
                            {
                                if (!string.IsNullOrWhiteSpace(existingPath))
                                {
                                    pathsToProcess.Add(existingPath);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Failed to enumerate existing destination files for {Destination}; only moved files will be re-normalized",
                                LoggingHelper.SanitizePathForLog(plan.Destination));
                        }

                        try
                        {
                            await _processor.NormalizeAndRenameFilesAsync(pathsToProcess, token);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to queue normalize-and-rename job after folder combine");
                        }
                    }
                },
                cancellationToken: cancellationToken);

            // Return immediately with the job id and the plan summary so the UI
            // can attach its progress modal via the standard SSE pipeline. The
            // actual moved/failed counts come from job_updated events.
            return Accepted(new
            {
                jobId = jobId.ToString(),
                job_id = jobId.ToString(),
                totalItems = trackedItems.Count,
                total_items = trackedItems.Count,
                destination = plan.Destination,
                sources = plan.SourceDirectories,
                skipped = skippedResults.Count,
                skippedResults
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error combining folders");
            return StatusCode(500, "Error combining folders");
        }
    }

    private const int MaxFileRenameAttempts = 1000;

    private async Task<(string? Error, CombineFoldersPlan? Plan)> BuildCombineFoldersPlanAsync(
        CombineFoldersRequest request,
        CancellationToken cancellationToken)
    {
        if (request == null)
        {
            return ("Request body is required.", null);
        }

        if (string.IsNullOrWhiteSpace(request.DestinationDirectory))
        {
            return ("Destination directory is required.", null);
        }

        var destination = Path.GetFullPath(request.DestinationDirectory);
        if (!IsPathSafe(destination))
        {
            _logger.LogWarning("Attempt to combine folders into a destination outside watched directory: {Destination}",
                LoggingHelper.SanitizePathForLog(destination));
            return ("Destination directory is outside the allowed directory.", null);
        }

        var groups = await BuildCombinableFolderGroupsAsync(cancellationToken);
        CombinableFolderGroup? group = null;
        if (!string.IsNullOrWhiteSpace(request.GroupKey))
        {
            group = groups.FirstOrDefault(g => string.Equals(g.GroupKey, request.GroupKey, StringComparison.OrdinalIgnoreCase));

            // Synthetic `series:<id>` group keys are not guaranteed to be in
            // the combinable-folder list (BuildCombinableFolderGroupsAsync
            // skips series with a single folder, the cache may have shifted
            // between requests, etc.). Fall back to resolving via the series
            // library so the per-series Manage Folders UI can always combine.
            if (group is null
                && request.GroupKey!.StartsWith(SeriesGroupKeyPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var seriesId = request.GroupKey.Substring(SeriesGroupKeyPrefix.Length);
                group = await BuildSeriesScopedCombinableGroupAsync(seriesId, cancellationToken);
            }
        }
        else
        {
            // Infer group from destination + sources by finding a group that contains all directories.
            // Directory comparisons here are case-sensitive (ordinal) so that two
            // on-disk folders differing only by capitalization (possible on
            // case-sensitive filesystems) are treated as distinct paths and the
            // right group is selected.
            var allDirs = new HashSet<string>(StringComparer.Ordinal) { destination };
            foreach (var src in request.SourceDirectories ?? new List<string>())
            {
                allDirs.Add(Path.GetFullPath(src));
            }
            group = groups.FirstOrDefault(g => allDirs.All(d => g.Folders.Any(f => string.Equals(f.Directory, d, StringComparison.Ordinal))));
        }

        if (group == null)
        {
            return ("No combinable folder group matches the request. The library may have changed; please reload.", null);
        }

        // Determine source folders. If the caller specified them, validate; otherwise use all-but-destination.
        List<CombinableFolder> sourceFolders;
        if (request.SourceDirectories != null && request.SourceDirectories.Count > 0)
        {
            sourceFolders = new List<CombinableFolder>();
            foreach (var src in request.SourceDirectories)
            {
                var srcFull = Path.GetFullPath(src);
                if (!IsPathSafe(srcFull))
                {
                    _logger.LogWarning("Attempt to combine from source outside watched directory: {Source}",
                        LoggingHelper.SanitizePathForLog(srcFull));
                    return ("One or more source directories are outside the allowed directory.", null);
                }
                if (string.Equals(srcFull, destination, StringComparison.Ordinal))
                {
                    continue;
                }
                var folder = group.Folders.FirstOrDefault(f => string.Equals(f.Directory, srcFull, StringComparison.Ordinal));
                if (folder == null)
                {
                    return ($"Source directory '{src}' is not part of this group.", null);
                }
                sourceFolders.Add(folder);
            }
        }
        else
        {
            sourceFolders = group.Folders
                .Where(f => !string.Equals(f.Directory, destination, StringComparison.Ordinal))
                .ToList();
        }

        if (sourceFolders.Count == 0)
        {
            return ("No source folders were selected for the combine operation.", null);
        }

        // Verify destination belongs to the group.
        if (!group.Folders.Any(f => string.Equals(f.Directory, destination, StringComparison.Ordinal)))
        {
            return ("Destination directory is not part of this group.", null);
        }

        // Build the move plan, handling name collisions by appending a numeric suffix.
        var moves = new List<CombineFoldersMove>();
        var reservedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (System.IO.Directory.Exists(destination))
        {
            foreach (var existing in System.IO.Directory.EnumerateFiles(destination))
            {
                reservedNames.Add(Path.GetFileName(existing));
            }
        }

        foreach (var folder in sourceFolders)
        {
            foreach (var sourcePath in folder.FilePaths)
            {
                if (string.IsNullOrWhiteSpace(sourcePath))
                {
                    continue;
                }
                var name = Path.GetFileName(sourcePath);
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }
                var hadConflict = reservedNames.Contains(name);
                var finalName = name;
                if (hadConflict)
                {
                    var baseName = Path.GetFileNameWithoutExtension(name);
                    var extension = Path.GetExtension(name);
                    var attempt = 1;
                    while (reservedNames.Contains(finalName))
                    {
                        finalName = $"{baseName} ({attempt}){extension}";
                        attempt++;
                        if (attempt > MaxFileRenameAttempts)
                        {
                            break;
                        }
                    }
                }
                reservedNames.Add(finalName);
                var destPath = Path.Combine(destination, finalName);
                // Use an ordinal (case-sensitive) comparison so we don't
                // skip moves between two folders that differ only by
                // capitalization on case-sensitive filesystems.
                var skipped = string.Equals(sourcePath, destPath, StringComparison.Ordinal);
                moves.Add(new CombineFoldersMove
                {
                    SourcePath = sourcePath,
                    DestinationPath = destPath,
                    HadConflict = hadConflict,
                    Skipped = skipped,
                    Reason = skipped ? "Source and destination are the same file." : null
                });
            }
        }

        return (null, new CombineFoldersPlan
        {
            Destination = destination,
            SourceDirectories = sourceFolders.Select(f => f.Directory).ToList(),
            Moves = moves
        });
    }

    /// <summary>
    /// Returns true if the directory (or any of its subdirectories) still
    /// contains at least one comic archive. Used by the folder-combine flow to
    /// decide whether a source directory can be safely deleted once its comics
    /// have been moved out; leftover non-comic files (cover images,
    /// ComicInfo.xml, etc.) do not block removal.
    /// </summary>
    private static bool DirectoryContainsComicArchives(string directory)
    {
        return System.IO.Directory
            .EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Any(ComicFileExtensions.IsComicArchive);
    }

    /// <summary>
    /// Returns a map of source-file path -> existing in-memory Series metadata for
    /// the supplied paths, captured *before* a folder-combine move so the values
    /// survive the rename.
    /// </summary>
    private async Task<Dictionary<string, string>> GetPreMoveSeriesNamesAsync(
        IEnumerable<string> sourcePaths,
        CancellationToken cancellationToken)
    {
        var pathSet = new HashSet<string>(sourcePaths, StringComparer.OrdinalIgnoreCase);
        if (pathSet.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var files = await _fileStore.GetAllFilesAsync(cancellationToken);
            if (files == null)
            {
                return result;
            }

            foreach (var file in files)
            {
                if (string.IsNullOrWhiteSpace(file.FilePath)) continue;
                if (!pathSet.Contains(file.FilePath)) continue;
                var series = file.Metadata?.Series;
                if (!string.IsNullOrWhiteSpace(series))
                {
                    result[file.FilePath] = series!.Trim();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to capture pre-move series metadata for folder combine");
        }
        return result;
    }

    /// <summary>
    /// Enumerates the absolute file paths of every tracked file whose parent
    /// directory matches <paramref name="directory"/> (case-insensitive,
    /// trailing-separator tolerant). Used by the folder-combine flow to scoop
    /// up the destination folder's pre-existing files when queuing the post-
    /// combine normalize-and-rename batch.
    /// </summary>
    private async Task<List<string>> GetFilesInDirectoryAsync(string directory, CancellationToken cancellationToken)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(directory))
        {
            return result;
        }

        var normalizedDir = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var files = await _fileStore.GetAllFilesAsync(cancellationToken);
        foreach (var file in files)
        {
            if (string.IsNullOrWhiteSpace(file.FilePath))
            {
                continue;
            }
            var parent = Path.GetDirectoryName(file.FilePath);
            if (string.IsNullOrEmpty(parent))
            {
                continue;
            }
            var trimmedParent = parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(trimmedParent, normalizedDir, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(file.FilePath);
            }
        }
        return result;
    }

    /// <summary>
    /// Builds a synthetic combinable folder group for the given series id by
    /// asking the authoritative <see cref="ISeriesLibraryService"/> which
    /// folders contribute to the series, then materialising
    /// <see cref="CombinableFolder"/> entries from the file store. Returns
    /// null when the series is unknown or has fewer than two folders.
    /// </summary>
    private async Task<CombinableFolderGroup?> BuildSeriesScopedCombinableGroupAsync(
        string seriesId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(seriesId))
        {
            return null;
        }

        SeriesFoldersResult? seriesFolders;
        try
        {
            seriesFolders = await _seriesLibrary.GetFoldersForSeriesIdAsync(seriesId, filter: null, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to resolve series-scoped folders for {SeriesId}",
                LoggingHelper.SanitizeForLog(seriesId));
            return null;
        }

        if (seriesFolders is null || seriesFolders.Folders.Count < 2)
        {
            return null;
        }

        var files = ((await _fileStore.GetAllFilesAsync(cancellationToken)) ?? Enumerable.Empty<ComicFile>())
            .Where(file => !string.IsNullOrWhiteSpace(file.FilePath))
            .ToList();
        if (files.Count == 0)
        {
            return null;
        }

        var addedAtLookup = await GetAddedAtLookupAsync(cancellationToken);

        var seriesDirSet = new HashSet<string>(
            seriesFolders.Folders.Select(f => f.Directory),
            StringComparer.Ordinal);

        var filesByDirectory = new Dictionary<string, List<(ComicFile File, DateTime AddedAt)>>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var directory = !string.IsNullOrWhiteSpace(file.Directory)
                ? file.Directory
                : Path.GetDirectoryName(file.FilePath);
            if (string.IsNullOrWhiteSpace(directory) || !seriesDirSet.Contains(directory))
            {
                continue;
            }

            var addedAt = addedAtLookup.TryGetValue(file.FilePath, out var createdAt)
                ? createdAt
                : file.LastModified;

            if (!filesByDirectory.TryGetValue(directory, out var dirFiles))
            {
                dirFiles = new List<(ComicFile, DateTime)>();
                filesByDirectory[directory] = dirFiles;
            }
            dirFiles.Add((file, addedAt));
        }

        if (filesByDirectory.Count < 2)
        {
            return null;
        }

        var folders = filesByDirectory
            .Select(kvp => BuildCombinableFolder(kvp.Key, kvp.Value))
            .OrderByDescending(f => f.NewestFileAddedAt)
            .ThenBy(f => f.Directory, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var suggested = folders[0];
        return new CombinableFolderGroup
        {
            GroupKey = SeriesGroupKeyPrefix + seriesId,
            SeriesName = !string.IsNullOrWhiteSpace(seriesFolders.Title) ? seriesFolders.Title : seriesId,
            Volume = null,
            Folders = folders,
            SuggestedDestinationDirectory = suggested.Directory,
            SuggestionReason = "These folders are all attributed to the same series.",
            TotalFileCount = folders.Sum(f => f.FileCount)
        };
    }

    /// <summary>
    /// Persists the source-folder series names (and any distinct file-level
    /// series names) as user aliases on the destination's series cache record,
    /// with the destination folder name set as the user-canonical title. This
    /// is best-effort: a cache failure is logged and swallowed so it never
    /// breaks the combine response.
    /// </summary>
    private async Task PersistFolderCombineAliasesAsync(
        CombineFoldersPlan plan,
        IReadOnlyDictionary<string, string> preMoveSeriesBySourcePath,
        CancellationToken cancellationToken)
    {
        if (_metadataCache is null || plan is null)
        {
            return;
        }

        try
        {
            var destFolderName = Path.GetFileName(plan.Destination.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(destFolderName))
            {
                return;
            }

            var destSeriesName = ComicFileProcessor.NormalizeSeriesName(destFolderName, forComparison: false);
            if (string.IsNullOrWhiteSpace(destSeriesName))
            {
                return;
            }

            // If the destination already has a matched canonical title in the
            // cache (either via an applied external match or a user override),
            // prefer that as the canonical-title override. This ensures the
            // combined folder's metadata converges on the matched name rather
            // than the raw folder name.
            var canonicalOverride = destSeriesName;
            try
            {
                var existingKeyForLookup = _metadataCache.NormalizeKey(destSeriesName);
                if (!string.IsNullOrWhiteSpace(existingKeyForLookup))
                {
                    var existing = await _metadataCache.GetAsync(existingKeyForLookup, cancellationToken);
                    if (existing is not null
                        && !string.IsNullOrWhiteSpace(existing.CanonicalTitle)
                        && (!string.IsNullOrWhiteSpace(existing.SeriesName)
                            || string.Equals(existing.LookupStatus, "success", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(existing.LookupStatus, "manual_match", StringComparison.OrdinalIgnoreCase)))
                    {
                        canonicalOverride = existing.CanonicalTitle.Trim();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to consult existing canonical title for destination {Destination}",
                    LoggingHelper.SanitizePathForLog(plan.Destination));
            }

            // Build the set of alias candidates from:
            //   1. Each source folder's folder-name-derived series.
            //   2. Any distinct Series values captured from the in-memory
            //      metadata of files that were just moved.
            var candidates = new List<string>();
            foreach (var sourceDir in plan.SourceDirectories ?? new List<string>())
            {
                if (string.IsNullOrWhiteSpace(sourceDir)) continue;
                var folderName = Path.GetFileName(sourceDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrWhiteSpace(folderName)) continue;
                var derived = ComicFileProcessor.NormalizeSeriesName(folderName, forComparison: false);
                if (!string.IsNullOrWhiteSpace(derived))
                {
                    candidates.Add(derived);
                }
            }

            foreach (var series in preMoveSeriesBySourcePath.Values)
            {
                if (!string.IsNullOrWhiteSpace(series))
                {
                    candidates.Add(series.Trim());
                }
            }

            // De-duplicate (case-insensitive) and drop anything equal to the
            // canonical-title override itself. Also include the destination
            // folder-derived name as an alias when it differs from the chosen
            // canonical override (so the cache learns the folder name too).
            if (!string.Equals(destSeriesName, canonicalOverride, StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add(destSeriesName);
            }

            var filtered = candidates
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Select(c => c.Trim())
                .Where(c => !string.Equals(c, canonicalOverride, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Merge with any existing user aliases so we never lose what the
            // user has previously taught the system.
            var existingKey = _metadataCache.NormalizeKey(canonicalOverride);
            if (!string.IsNullOrWhiteSpace(existingKey))
            {
                try
                {
                    var existingRecord = await _metadataCache.GetAsync(existingKey, cancellationToken);
                    if (existingRecord?.UserAliases is { Count: > 0 } existingAliases)
                    {
                        var merged = new HashSet<string>(filtered, StringComparer.OrdinalIgnoreCase);
                        foreach (var existing in existingAliases)
                        {
                            if (!string.IsNullOrWhiteSpace(existing)
                                && !string.Equals(existing, canonicalOverride, StringComparison.OrdinalIgnoreCase))
                            {
                                merged.Add(existing.Trim());
                            }
                        }
                        filtered = merged.ToList();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to merge existing user aliases for series {Series}",
                        LoggingHelper.SanitizeForLog(canonicalOverride));
                }
            }

            await _metadataCache.SetUserAliasesAsync(
                canonicalOverride,
                filtered,
                cancellationToken);

            _logger.LogInformation(
                "Persisted {AliasCount} user alias(es) and set canonical title to '{Canonical}' after folder combine",
                filtered.Count,
                LoggingHelper.SanitizeForLog(canonicalOverride));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist folder-combine aliases for destination {Destination}",
                LoggingHelper.SanitizePathForLog(plan.Destination));
        }
    }

    private static CombinableFolderGroupDto ToCombinableFolderGroupDto(CombinableFolderGroup group)
    {
        return new CombinableFolderGroupDto
        {
            GroupKey = group.GroupKey,
            SeriesName = group.SeriesName,
            Volume = group.Volume,
            TotalFileCount = group.TotalFileCount,
            SuggestedDestinationDirectory = group.SuggestedDestinationDirectory,
            SuggestionReason = group.SuggestionReason,
            Folders = group.Folders.Select(f => new CombinableFolderDto
            {
                Directory = f.Directory,
                FileCount = f.FileCount,
                TotalSize = f.TotalSize,
                NewestFileAddedAt = f.NewestFileAddedAt,
                OldestFileAddedAt = f.OldestFileAddedAt,
                NewestFileModifiedAt = f.NewestFileModifiedAt,
                SampleFileNames = f.SampleFileNames,
                IsSuggested = string.Equals(f.Directory, group.SuggestedDestinationDirectory, StringComparison.OrdinalIgnoreCase)
            }).ToList()
        };
    }

    private static string? BuildFolderCombineSeriesDisplayName(ComicFile file, IReadOnlyDictionary<string, FolderCombineAliasEntry>? aliasIndex = null)
    {
        // When the file's series name maps (via alias) to a known cache record,
        // prefer that record's canonical title so every folder in the group is
        // labelled consistently.
        if (aliasIndex is not null)
        {
            foreach (var candidate in EnumerateSeriesNameCandidates(file))
            {
                var key = NormalizeFolderCombineKey(candidate);
                if (key is not null
                    && aliasIndex.TryGetValue(key, out var entry)
                    && !string.IsNullOrWhiteSpace(entry.DisplayTitle))
                {
                    return entry.DisplayTitle;
                }
            }
        }

        return FirstNonEmpty(
            file.Metadata?.Series,
            ExtractSeriesNameFromFileName(file),
            Path.GetFileName(file.Directory),
            Path.GetFileName(Path.GetDirectoryName(file.FilePath) ?? string.Empty));
    }

    private async Task<Dictionary<string, DateTime>> GetAddedAtLookupAsync(CancellationToken cancellationToken)
    {
        if (_dbContextFactory is null)
        {
            return new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        }

        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var addedAtEntries = await dbContext.ComicFiles
            .AsNoTracking()
            .Select(file => new { file.FilePath, file.CreatedAt })
            .ToListAsync(cancellationToken);

        return addedAtEntries
            .Where(file => !string.IsNullOrWhiteSpace(file.FilePath))
            .GroupBy(file => file.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Max(file => file.CreatedAt),
                StringComparer.OrdinalIgnoreCase);
    }

    private static string? BuildFolderCombineGroupKey(ComicFile file, IReadOnlyDictionary<string, FolderCombineAliasEntry>? aliasIndex = null)
    {
        var seriesName = FirstNonEmpty(
            file.Metadata?.Series,
            ExtractSeriesNameFromFileName(file),
            Path.GetFileName(file.Directory),
            Path.GetFileName(Path.GetDirectoryName(file.FilePath) ?? string.Empty));

        if (string.IsNullOrWhiteSpace(seriesName))
        {
            return null;
        }

        var normalizedSeries = NormalizeFolderCombineKey(seriesName);
        if (string.IsNullOrWhiteSpace(normalizedSeries))
        {
            return null;
        }

        // Collapse aliases (provider + user) to the canonical record key so two
        // folders whose names map to the same series via aliases share a key.
        if (aliasIndex is not null)
        {
            foreach (var candidate in EnumerateSeriesNameCandidates(file))
            {
                var key = NormalizeFolderCombineKey(candidate);
                // Skip degenerate keys (e.g. CJK-only titles that collapse to a
                // bare number) so we don't accidentally cross-resolve to an
                // unrelated record whose canonical key happens to share that digit.
                if (key is null || IsAmbiguousNormalizedKey(key))
                {
                    continue;
                }
                if (aliasIndex.TryGetValue(key, out var entry))
                {
                    normalizedSeries = entry.CanonicalKey;
                    break;
                }
            }
        }

        var volume = file.Metadata?.Volume?.Trim();
        return string.IsNullOrWhiteSpace(volume)
            ? normalizedSeries
            : $"{normalizedSeries}|{volume.ToLowerInvariant()}";
    }

    private static string? NormalizeFolderCombineKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = FolderCombineKeySanitizer.Replace(value.Trim().ToLowerInvariant(), "-").Trim('-');
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    /// <summary>
    /// Returns true if a normalized folder-combine key carries no real identifying
    /// signal and therefore must not be used to bridge two different cache records.
    /// A key is ambiguous when it is empty, shorter than two characters, or
    /// contains no letters at all (i.e. consists only of digits/dashes). With the
    /// Unicode-aware sanitizer, "letters" here means any Unicode letter, so a
    /// genuine CJK alias like "怪獣8号" survives as a non-ambiguous key while
    /// the digit-only collapse "8" still gets rejected as a cross-record bridge.
    /// </summary>
    private static bool IsAmbiguousNormalizedKey(string? normalized)
    {
        if (string.IsNullOrEmpty(normalized) || normalized.Length < 2)
        {
            return true;
        }

        for (var i = 0; i < normalized.Length; i++)
        {
            if (char.IsLetter(normalized[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static IEnumerable<string> EnumerateSeriesNameCandidates(ComicFile file)
    {
        if (!string.IsNullOrWhiteSpace(file.Metadata?.Series)) yield return file.Metadata!.Series!;

        var fromName = ExtractSeriesNameFromFileName(file);
        if (!string.IsNullOrWhiteSpace(fromName)) yield return fromName!;

        var folder = Path.GetFileName(file.Directory);
        if (!string.IsNullOrWhiteSpace(folder)) yield return folder!;

        var parent = Path.GetFileName(Path.GetDirectoryName(file.FilePath) ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(parent)) yield return parent!;
    }

    /// <summary>
    /// Builds a lookup that maps every known title for a series (canonical
    /// title + provider aliases + user aliases), normalized using the
    /// folder-combine key normalizer, to a shared canonical key plus a
    /// preferred display title. Returns an empty dictionary when no metadata
    /// cache is wired up.
    /// </summary>
    /// <remarks>
    /// Uses union-find so that two cache records linked through a shared
    /// alias collapse into a single group. For example, when record "X" lists
    /// "Y" as a user alias and a separate cache record exists for "Y" (e.g.
    /// from an earlier external lookup), every title belonging to either
    /// record resolves to the same canonical key. Without this, a naive
    /// last-writer-wins map would let record "Y" overwrite the alias entry
    /// produced by record "X" and leave the two folders in separate groups,
    /// even though the user has explicitly told us they are the same series.
    /// </remarks>
    private async Task<Dictionary<string, FolderCombineAliasEntry>> BuildFolderCombineAliasIndexAsync(CancellationToken cancellationToken)
    {
        var index = new Dictionary<string, FolderCombineAliasEntry>(StringComparer.OrdinalIgnoreCase);
        if (_metadataCache is null)
        {
            return index;
        }

        IReadOnlyList<SeriesMetadataCacheRecord> records;
        try
        {
            records = await _metadataCache.GetAllAsync(cancellationToken) ?? Array.Empty<SeriesMetadataCacheRecord>();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            _logger.LogWarning(ex, "Failed to load series metadata cache for folder-combine alias index");
            return index;
        }

        var unionFind = new UnionFind<string>(StringComparer.OrdinalIgnoreCase);

        // First pass: for every record, derive a canonical key and union it
        // with the normalized form of every title it knows about. Any title
        // shared between two records causes the two records' canonical keys to
        // be merged into the same connected component.
        var recordInfos = new List<(string CanonicalKey, string DisplayTitle, List<string> TitleKeys)>();
        foreach (var record in records)
        {
            var titles = EnumerateRecordTitles(record).ToList();
            if (titles.Count == 0)
            {
                continue;
            }

            var canonicalKey = NormalizeFolderCombineKey(record.CanonicalTitle)
                ?? NormalizeFolderCombineKey(titles[0])
                ?? record.NormalizedKey;

            var displayTitle = !string.IsNullOrWhiteSpace(record.CanonicalTitle)
                ? record.CanonicalTitle!
                : titles[0];

            var titleKeys = titles
                .Select(NormalizeFolderCombineKey)
                .Where(k => k is not null)
                .Select(k => k!)
                .Where(k => !IsAmbiguousNormalizedKey(k))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // If the record's own canonical key is degenerate (e.g. a record
            // whose only title is CJK and collapses to a bare digit), skip
            // it entirely from the cross-record bridge. Otherwise two such
            // records would share the same canonical-key string in the union-
            // find and get falsely merged. The record's files will still group
            // together on their own via NormalizeFolderCombineKey returning
            // the same string outside the alias-index path.
            if (IsAmbiguousNormalizedKey(canonicalKey))
            {
                continue;
            }

            unionFind.Add(canonicalKey);
            foreach (var key in titleKeys)
            {
                unionFind.Union(canonicalKey, key);
            }

            recordInfos.Add((canonicalKey, displayTitle, titleKeys));
        }

        // Second pass: per connected component, pick a stable display title
        // (preferring an entry whose own canonical key is the component root,
        // which corresponds to the record the user most likely wants surfaced).
        var rootDisplay = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var info in recordInfos)
        {
            var root = unionFind.Find(info.CanonicalKey);
            if (string.Equals(info.CanonicalKey, root, StringComparison.OrdinalIgnoreCase))
            {
                rootDisplay[root] = info.DisplayTitle;
            }
            else
            {
                rootDisplay.TryAdd(root, info.DisplayTitle);
            }
        }

        // Third pass: index every known title to the component root + chosen
        // display title. Also seed the canonical-key entry itself so lookups
        // by the record's own key resolve cleanly.
        foreach (var info in recordInfos)
        {
            var root = unionFind.Find(info.CanonicalKey);
            var display = rootDisplay.TryGetValue(root, out var d) ? d : info.DisplayTitle;
            var entry = new FolderCombineAliasEntry(root, display);

            index[info.CanonicalKey] = entry;
            foreach (var key in info.TitleKeys)
            {
                index[key] = entry;
            }
        }

        return index;
    }

    private static IEnumerable<string> EnumerateRecordTitles(SeriesMetadataCacheRecord record)
    {
        if (!string.IsNullOrWhiteSpace(record.CanonicalTitle)) yield return record.CanonicalTitle!;
        foreach (var alias in record.Aliases ?? Enumerable.Empty<string>())
        {
            if (!string.IsNullOrWhiteSpace(alias)) yield return alias;
        }
        foreach (var alias in record.UserAliases ?? Enumerable.Empty<string>())
        {
            if (!string.IsNullOrWhiteSpace(alias)) yield return alias;
        }
    }

    private sealed record FolderCombineAliasEntry(string CanonicalKey, string DisplayTitle);

    private static string? ExtractSeriesNameFromFileName(ComicFile file)
    {
        var name = Path.GetFileNameWithoutExtension(
            !string.IsNullOrWhiteSpace(file.FileName)
                ? file.FileName
                : file.FilePath);

        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var candidate = FileNameSeriesSuffixSanitizer.Replace(name, string.Empty)
            .Trim(' ', '-', '_', '.', '#');

        return string.IsNullOrWhiteSpace(candidate)
            ? null
            : candidate.Replace('_', ' ').Trim();
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    [HttpGet("metadata")]
    public async Task<ActionResult<ComicMetadata>> GetMetadata([FromQuery] string filePath)
    {
        try
        {
            if (string.IsNullOrEmpty(filePath))
                return BadRequest("File path is required");

            // Validate path is within watched directory to prevent path traversal attacks
            if (!IsPathSafe(filePath))
            {
                _logger.LogWarning("Attempt to get metadata for file outside watched directory: {FilePath}", LoggingHelper.SanitizeForLog(LoggingHelper.SanitizePathForLog(filePath)));
                return BadRequest("File path is outside the allowed directory");
            }

            var metadata = await _processor.GetMetadataAsync(filePath);
            if (metadata == null)
                return NotFound();
            
            return Ok(metadata);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting metadata for {FilePath}", filePath);
            return StatusCode(500, "Error retrieving metadata");
        }
    }

    [HttpGet("series-resolution")]
    public async Task<ActionResult<object>> GetSeriesResolution([FromQuery] string filePath, CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrEmpty(filePath))
                return BadRequest("File path is required");

            if (!IsPathSafe(filePath))
            {
                _logger.LogWarning("Attempt to resolve series for file outside watched directory: {FilePath}",
                    LoggingHelper.SanitizeForLog(LoggingHelper.SanitizePathForLog(filePath)));
                return BadRequest("File path is outside the allowed directory");
            }

            if (_seriesNameResolver is null)
            {
                return StatusCode(503, "Series name resolver not available");
            }

            // Read the file's current metadata so the resolver can also
            // evaluate the file's existing <Series> tag as a candidate.
            // Missing/unreadable metadata is fine — the resolver falls back
            // to the folder name in that case.
            var metadata = await _processor.GetMetadataAsync(filePath, cancellationToken);

            // mutateCache:false — this endpoint is a read-only preview and
            // must not side-effect the alias index just by being polled.
            var resolution = await _seriesNameResolver.ResolveAsync(
                filePath,
                metadata,
                mutateCache: false,
                cancellationToken);

            return Ok(new
            {
                filePath,
                actualSeries = metadata?.Series,
                resolvedSeries = resolution.ResolvedSeries,
                winningStep = resolution.WinningStep.ToString(),
                candidates = resolution.Candidates,
                folderSeries = resolution.FolderSeries,
                matchedCacheKey = resolution.MatchedCacheKey,
                appliedLanguage = resolution.AppliedLanguage,
                explanation = resolution.Explanation,
                matchesActual = !string.IsNullOrWhiteSpace(metadata?.Series)
                    && string.Equals(metadata!.Series, resolution.ResolvedSeries, StringComparison.OrdinalIgnoreCase)
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resolving series for {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            return StatusCode(500, "Error resolving series");
        }
    }

    [HttpPut("metadata")]
    public async Task<ActionResult> UpdateMetadata([FromQuery] string filePath, [FromBody] ComicMetadata metadata)
    {
        try
        {
            if (string.IsNullOrEmpty(filePath))
                return BadRequest("File path is required");

            // Validate path is within watched directory to prevent path traversal attacks
            if (!IsPathSafe(filePath))
            {
                _logger.LogWarning("Attempt to update metadata for file outside watched directory: {FilePath}", LoggingHelper.SanitizeForLog(LoggingHelper.SanitizePathForLog(filePath)));
                return BadRequest("File path is outside the allowed directory");
            }

            // Capture before state
            var beforeMetadata = await _processor.GetMetadataAsync(filePath);
            
            var success = await _processor.UpdateMetadataAsync(filePath, metadata);
            if (!success)
            {
                await LogHistoryAsync(filePath, "Update Metadata", false, "Failed to update metadata");
                if (_eventBroadcaster is not null)
                {
                    try { await _eventBroadcaster.BroadcastFileProcessedAsync(Path.GetFileName(filePath), false, "Failed to update metadata"); }
                    catch (Exception bex) { _logger.LogDebug(bex, "Failed to broadcast file_processed for metadata update failure"); }
                }
                return BadRequest("Failed to update metadata");
            }
            
            // Log with before/after metadata
            var filename = Path.GetFileName(filePath);
            await LogHistoryWithChangesAsync(filePath, "Update Metadata", true, null,
                filename, filename, beforeMetadata, metadata);

            // Broadcast a file_processed event so the UI patches the affected
            // folder/series detail in place instead of waiting for a full
            // file_list_updated debounce.
            if (_eventBroadcaster is not null)
            {
                try { await _eventBroadcaster.BroadcastFileProcessedAsync(filename, true); }
                catch (Exception bex) { _logger.LogDebug(bex, "Failed to broadcast file_processed for metadata update"); }
            }
            
            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating metadata for {FilePath}", filePath);
            await LogHistoryAsync(filePath, "Update Metadata", false, ex.Message);
            return StatusCode(500, "Error updating metadata");
        }
    }


    [HttpPatch("metadata")]
    public async Task<ActionResult> PatchMetadata([FromQuery] string filePath, [FromBody] PatchMetadataRequest body, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrEmpty(filePath))
                return BadRequest("File path is required");

            if (!IsPathSafe(filePath))
            {
                _logger.LogWarning("Attempt to patch metadata for file outside watched directory: {FilePath}", LoggingHelper.SanitizeForLog(LoggingHelper.SanitizePathForLog(filePath)));
                return BadRequest("File path is outside the allowed directory");
            }

            if (body?.Metadata is null)
                return BadRequest("Metadata patch is required");

            var patch = body.Metadata;
            var lockFields = DetermineEditedFields(patch);
            if (body.LockFields is not null)
            {
                foreach (var field in body.LockFields)
                {
                    lockFields |= ParseMetadataField(field);
                }
            }

            await _fileStore.ApplyUserMetadataEditAsync(filePath, patch, lockFields, ct);

            if (_scheduledJobsHostedService is not null)
            {
                _ = _scheduledJobsHostedService.RunNowAsync(MetadataBackfillJobHandler.Key, CancellationToken.None);
            }

            var updated = await _fileStore.GetFileAsync(filePath, ct);
            return Accepted(new { filePath, metadataVersion = updated?.MetadataVersion ?? 0 });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Cannot patch metadata for {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            return NotFound("File is not tracked");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error patching metadata for {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            return StatusCode(500, "Error patching metadata");
        }
    }

    private static ComicMetadataFieldFlags DetermineEditedFields(ComicMetadata metadata)
    {
        var flags = ComicMetadataFieldFlags.None;
        if (metadata.Series is not null) flags |= ComicMetadataFieldFlags.Series;
        if (metadata.Title is not null) flags |= ComicMetadataFieldFlags.Title;
        if (metadata.Issue is not null) flags |= ComicMetadataFieldFlags.Issue;
        if (metadata.Volume is not null) flags |= ComicMetadataFieldFlags.Volume;
        if (metadata.Publisher is not null) flags |= ComicMetadataFieldFlags.Publisher;
        if (metadata.Year.HasValue) flags |= ComicMetadataFieldFlags.Year;
        if (metadata.Summary is not null) flags |= ComicMetadataFieldFlags.Summary;
        if (metadata.Authors.Count > 0) flags |= ComicMetadataFieldFlags.Authors;
        if (metadata.Tags.Count > 0) flags |= ComicMetadataFieldFlags.Tags;
        return flags;
    }

    private static ComicMetadataFieldFlags ParseMetadataField(string? field)
    {
        if (string.IsNullOrWhiteSpace(field)) return ComicMetadataFieldFlags.None;
        return field.Trim().ToLowerInvariant() switch
        {
            "series" => ComicMetadataFieldFlags.Series,
            "title" => ComicMetadataFieldFlags.Title,
            "issue" or "number" => ComicMetadataFieldFlags.Issue,
            "volume" => ComicMetadataFieldFlags.Volume,
            "publisher" => ComicMetadataFieldFlags.Publisher,
            "year" => ComicMetadataFieldFlags.Year,
            "summary" or "description" => ComicMetadataFieldFlags.Summary,
            "authors" or "writer" or "writers" => ComicMetadataFieldFlags.Authors,
            "tags" => ComicMetadataFieldFlags.Tags,
            _ => ComicMetadataFieldFlags.None
        };
    }

    [HttpPost("process")]
    public async Task<ActionResult> ProcessFile([FromQuery] string filePath)
    {
        try
        {
            if (string.IsNullOrEmpty(filePath))
                return BadRequest("File path is required");

            // Validate path is within watched directory to prevent path traversal attacks
            if (!IsPathSafe(filePath))
            {
                _logger.LogWarning("Attempt to process file outside watched directory: {FilePath}", LoggingHelper.SanitizeForLog(LoggingHelper.SanitizePathForLog(filePath)));
                return BadRequest("File path is outside the allowed directory");
            }

            var success = await _processor.ProcessFileAsync(filePath);
            if (!success)
                return BadRequest("Failed to process file");
            
            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing file {FilePath}", filePath);
            return StatusCode(500, "Error processing file");
        }
    }

    [HttpPost("process-batch")]
    public async Task<ActionResult<Guid>> ProcessBatch([FromBody] List<string> filePaths)
    {
        try
        {
            var jobId = await _processor.ProcessFilesAsync(filePaths);
            return Ok(new { jobId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting batch processing");
            return StatusCode(500, "Error starting batch processing");
        }
    }

    // Note: Processed status is now computed from renamed && normalized states
    // No longer accepting manual updates to processed status

    [HttpPost("tags")]
    public async Task<ActionResult> UpdateTags([FromBody] UpdateTagsRequest request)
    {
        try
        {
            foreach (var file in request.Files)
            {
                if (!string.IsNullOrEmpty(file))
                {
                    await _processor.UpdateMetadataAsync(file, request.Metadata);
                }
            }
            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating tags");
            return StatusCode(500, "Error updating tags");
        }
    }

    // RESTful endpoint: GET /api/files/scan-unmarked (read operation, no side effects)
    [HttpGet("scan-unmarked")]
    public async Task<ActionResult> ScanUnmarked()
    {
        try
        {
            _logger.LogInformation(LoggingHelper.WithWebsitePrefix("ScanUnmarked: Scan unmarked files requested"));
            _logger.LogDebug(LoggingHelper.WithWebsitePrefix("ScanUnmarked: Starting file count retrieval from file store"));
            
            // Get file counts - materialize collections to avoid multiple enumerations
            var allFilesList = (await _fileStore.GetAllFilesAsync()).ToList();
            _logger.LogDebug(LoggingHelper.WithWebsitePrefix("ScanUnmarked: Retrieved {TotalCount} total files"), allFilesList.Count);
            
            var unmarkedFilesList = (await _fileStore.GetFilteredFilesAsync("unprocessed")).ToList();
            _logger.LogDebug(LoggingHelper.WithWebsitePrefix("ScanUnmarked: Retrieved {UnmarkedCount} unprocessed files after filtering"), unmarkedFilesList.Count);
            
            var markedFilesList = (await _fileStore.GetFilteredFilesAsync("processed")).ToList();
            _logger.LogDebug(LoggingHelper.WithWebsitePrefix("ScanUnmarked: Retrieved {MarkedCount} processed files after filtering"), markedFilesList.Count);
            
            var totalCount = allFilesList.Count;
            var unmarkedCount = unmarkedFilesList.Count;
            var markedCount = markedFilesList.Count;
            
            _logger.LogInformation(LoggingHelper.WithWebsitePrefix("ScanUnmarked: File counts - Total: {TotalCount}, Unmarked: {UnmarkedCount}, Marked: {MarkedCount}"), 
                totalCount, unmarkedCount, markedCount);
            
            return Ok(new { 
                total_count = totalCount,
                unmarked_count = unmarkedCount,
                marked_count = markedCount
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, LoggingHelper.WithWebsitePrefix("ScanUnmarked: Error scanning unmarked files"));
            return StatusCode(500, "Error scanning files");
        }
    }

    // Legacy endpoint for backward compatibility
    [HttpPost("~/api/scan-unmarked")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public async Task<ActionResult> ScanUnmarkedLegacy() => await ScanUnmarked();

    // RESTful endpoint: POST /api/files/{encodedFilePath}/process
    [HttpPost("{encodedFilePath}/process")]
    public async Task<ActionResult> ProcessFileByEncodedPath(string encodedFilePath, [FromQuery] bool forceReprocess = false)
    {
        try
        {
            var filePath = DecodeBase64UrlSafe(encodedFilePath);
            if (string.IsNullOrEmpty(filePath))
                return BadRequest("Invalid file path");

            var success = await _processor.ProcessFileAsync(filePath, forceReprocess);
            return success ? Ok() : BadRequest("Failed to process file");
        }
        catch (Exception ex)
        {
            var sanitizedEncodedPath = LoggingHelper.SanitizeForLog(encodedFilePath);
            _logger.LogError(ex, "Error processing file with encoded path {EncodedPath}", sanitizedEncodedPath);
            return StatusCode(500, "Error processing file");
        }
    }

    // Legacy endpoint for backward compatibility
    [HttpPost("~/api/process-file")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public async Task<ActionResult> ProcessSingleFile([FromQuery] string filePath, [FromQuery] bool forceReprocess = false)
    {
        try
        {
            // Validate path is within watched directory to prevent path traversal attacks
            if (!IsPathSafe(filePath))
            {
                _logger.LogWarning("Attempt to process file outside watched directory: {FilePath}", LoggingHelper.SanitizeForLog(LoggingHelper.SanitizePathForLog(filePath)));
                return BadRequest("File path is outside the allowed directory");
            }

            var success = await _processor.ProcessFileAsync(filePath, forceReprocess);
            return success ? Ok() : BadRequest("Failed to process file");
        }
        catch (Exception ex)
        {
            var sanitizedPath = LoggingHelper.SanitizePathForLog(filePath);
            _logger.LogError(ex, "Error processing file {FilePath}", sanitizedPath);
            return StatusCode(500, "Error processing file");
        }
    }

    // RESTful endpoint: POST /api/files/{encodedFilePath}/rename
    [HttpPost("{encodedFilePath}/rename")]
    public async Task<ActionResult> RenameFileByEncodedPath(string encodedFilePath, [FromQuery] bool forceReprocess = false, CancellationToken cancellationToken = default)
    {
        try
        {
            var filePath = DecodeBase64UrlSafe(encodedFilePath);
            if (string.IsNullOrEmpty(filePath))
                return BadRequest("Invalid file path");

            if (!IsPathSafe(filePath))
                return BadRequest("Invalid file path");

            var sanitizedPath = LoggingHelper.SanitizePathForLog(filePath);
            _logger.LogInformation(LoggingHelper.WithWebsitePrefix("Rename requested for file: {FilePath}"), sanitizedPath);
            
            // Use the batch rename method with a single file
            var jobId = await _processor.RenameFilesAsync(new[] { filePath }, forceReprocess, cancellationToken);
            
            return Ok(new { message = "Rename job started", jobId });
        }
        catch (Exception ex)
        {
            var sanitizedEncodedPath = LoggingHelper.SanitizeForLog(encodedFilePath);
            _logger.LogError(ex, LoggingHelper.WithWebsitePrefix("Error renaming file with encoded path {EncodedPath}"), sanitizedEncodedPath);
            return StatusCode(500, "Error renaming file");
        }
    }

    // Legacy endpoint for backward compatibility
    [HttpPost("~/api/rename-file")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public async Task<ActionResult> RenameSingleFile([FromQuery] string filePath, [FromQuery] bool forceReprocess = false, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!IsPathSafe(filePath))
                return BadRequest("Invalid file path");

            var sanitizedPath = LoggingHelper.SanitizePathForLog(filePath);
            _logger.LogInformation(LoggingHelper.WithWebsitePrefix("Rename requested for file: {FilePath}"), sanitizedPath);
            
            // Use the batch rename method with a single file
            var jobId = await _processor.RenameFilesAsync(new[] { filePath }, forceReprocess, cancellationToken);
            
            return Ok(new { jobId });
        }
        catch (Exception ex)
        {
            var sanitizedPath = LoggingHelper.SanitizePathForLog(filePath);
            _logger.LogError(ex, LoggingHelper.WithWebsitePrefix("Error renaming file {FilePath}"), sanitizedPath);
            return StatusCode(500, "Error renaming file");
        }
    }

    // RESTful endpoint: DELETE /api/files/{encodedFilePath}
    [HttpDelete("~/api/files/{encodedFilePath}")]
    public async Task<ActionResult> DeleteFileByEncodedPath(string encodedFilePath)
    {
        try
        {
            // Decode the base64 URL-safe encoded file path
            var filePath = DecodeBase64UrlSafe(encodedFilePath);
            if (string.IsNullOrEmpty(filePath))
                return BadRequest("Invalid file path");

            // Validate path is within watched directory to prevent path traversal attacks
            if (!IsPathSafe(filePath))
            {
                _logger.LogWarning("Attempt to delete file outside watched directory: {EncodedPath}", LoggingHelper.SanitizeForLog(encodedFilePath));
                return BadRequest("File path is outside the allowed directory");
            }

            if (System.IO.File.Exists(filePath))
            {
                // Remove from file store first (unlikely to fail), then delete physical file
                // This order prevents orphaned file store entries if file deletion fails
                await _fileStore.RemoveFileAsync(filePath);
                System.IO.File.Delete(filePath);
                
                // Log to processing history
                await LogHistoryAsync(filePath, "Delete", true);
                
                return Ok();
            }
            return NotFound();
        }
        catch (Exception ex)
        {
            var sanitizedEncodedPath = LoggingHelper.SanitizeForLog(encodedFilePath);
            _logger.LogError(ex, "Error deleting file with encoded path {EncodedPath}", sanitizedEncodedPath);
            
            // Log to processing history
            var filePath = DecodeBase64UrlSafe(encodedFilePath);
            await LogHistoryAsync(filePath, "Delete", false, ex.Message);
            
            return StatusCode(500, "Error deleting file");
        }
    }

    // Legacy endpoint for backward compatibility: DELETE /api/delete-file?filePath=...
    [HttpDelete("~/api/delete-file")]
    public async Task<ActionResult> DeleteFile([FromQuery] string filePath)
    {
        try
        {
            // Validate path is within watched directory to prevent path traversal attacks
            if (!IsPathSafe(filePath))
            {
                _logger.LogWarning("Attempt to delete file outside watched directory: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                return BadRequest("File path is outside the allowed directory");
            }

            if (System.IO.File.Exists(filePath))
            {
                // Remove from file store first (unlikely to fail), then delete physical file
                // This order prevents orphaned file store entries if file deletion fails
                await _fileStore.RemoveFileAsync(filePath);
                System.IO.File.Delete(filePath);
                
                // Log to processing history
                await LogHistoryAsync(filePath, "Delete", true);
                
                return Ok();
            }
            return NotFound();
        }
        catch (Exception ex)
        {
            var sanitizedPath = LoggingHelper.SanitizePathForLog(filePath);
            _logger.LogError(ex, "Error deleting file {FilePath}", sanitizedPath);
            
            // Log to processing history
            await LogHistoryAsync(filePath, "Delete", false, ex.Message);
            
            return StatusCode(500, "Error deleting file");
        }
    }

    [HttpGet("~/api/files/{encodedFilePath}/tags")]
    public async Task<ActionResult<ComicMetadata>> GetFileTags(string encodedFilePath)
    {
        try
        {
            // Decode the base64 URL-safe encoded file path
            var filePath = DecodeBase64UrlSafe(encodedFilePath);
            if (string.IsNullOrEmpty(filePath))
                return BadRequest("Invalid file path");

            var metadata = await _processor.GetMetadataAsync(filePath);
            return metadata != null ? Ok(metadata) : NotFound();
        }
        catch (Exception ex)
        {
            var sanitizedEncodedPath = LoggingHelper.SanitizeForLog(encodedFilePath);
            _logger.LogError(ex, "Error getting tags for encoded path {EncodedPath}", sanitizedEncodedPath);
            return StatusCode(500, "Error getting tags");
        }
    }

    [HttpPut("~/api/files/{encodedFilePath}/tags")]
    public async Task<ActionResult> UpdateFileTags(string encodedFilePath, [FromBody] ComicMetadata metadata)
    {
        try
        {
            // Decode the base64 URL-safe encoded file path
            var filePath = DecodeBase64UrlSafe(encodedFilePath);
            if (string.IsNullOrEmpty(filePath))
                return BadRequest("Invalid file path");

            var success = await _processor.UpdateMetadataAsync(filePath, metadata);
            if (success && _eventBroadcaster is not null)
            {
                try { await _eventBroadcaster.BroadcastFileProcessedAsync(Path.GetFileName(filePath), true); }
                catch (Exception bex) { _logger.LogDebug(bex, "Failed to broadcast file_processed for tag update"); }
            }
            return success ? Ok() : BadRequest("Failed to update tags");
        }
        catch (Exception ex)
        {
            var sanitizedEncodedPath = LoggingHelper.SanitizeForLog(encodedFilePath);
            _logger.LogError(ex, "Error updating tags for encoded path {EncodedPath}", sanitizedEncodedPath);
            return StatusCode(500, "Error updating tags");
        }
    }

    // RESTful endpoint: DELETE /api/files/{encodedFilePath}/metadata
    // Removes the embedded ComicInfo.xml from the archive and clears the
    // renamed/normalized/processed flags so the file will be re-evaluated on
    // the next processing run.
    [HttpDelete("~/api/files/{encodedFilePath}/metadata")]
    public async Task<ActionResult> RemoveFileMetadata(string encodedFilePath, CancellationToken cancellationToken = default)
    {
        try
        {
            var filePath = DecodeBase64UrlSafe(encodedFilePath);
            if (string.IsNullOrEmpty(filePath))
                return BadRequest("Invalid file path");

            if (!IsPathSafe(filePath))
            {
                _logger.LogWarning("Attempt to remove metadata from file outside watched directory: {EncodedPath}", LoggingHelper.SanitizeForLog(encodedFilePath));
                return BadRequest("File path is outside the allowed directory");
            }

            if (!System.IO.File.Exists(filePath))
            {
                return NotFound();
            }

            var sanitizedPath = LoggingHelper.SanitizePathForLog(filePath);
            _logger.LogInformation(LoggingHelper.WithWebsitePrefix("Remove metadata requested for file: {FilePath}"), sanitizedPath);

            var success = await _processor.RemoveMetadataAsync(filePath, cancellationToken);
            if (!success)
            {
                await LogHistoryAsync(filePath, "RemoveMetadata", false, "RemoveMetadataAsync returned false");
                return BadRequest("Failed to remove metadata");
            }

            // Mark the file as unprocessed so the rename/normalize pipeline will
            // re-evaluate it on the next run.
            var cleared = await _fileStore.ClearProcessedStatusAsync(new[] { filePath }, cancellationToken);

            await LogHistoryAsync(filePath, "RemoveMetadata", true);

            // Broadcast a file_processed event so the UI updates the affected
            // folder / series detail in place rather than waiting for the
            // debounced file_list_updated.
            if (_eventBroadcaster is not null)
            {
                try { await _eventBroadcaster.BroadcastFileProcessedAsync(Path.GetFileName(filePath), true); }
                catch (Exception bex) { _logger.LogDebug(bex, "Failed to broadcast file_processed for metadata removal"); }
            }

            return Ok(new { success = true, cleared });
        }
        catch (Exception ex)
        {
            var sanitizedEncodedPath = LoggingHelper.SanitizeForLog(encodedFilePath);
            _logger.LogError(ex, "Error removing metadata from file with encoded path {EncodedPath}", sanitizedEncodedPath);

            var filePath = DecodeBase64UrlSafe(encodedFilePath);
            if (!string.IsNullOrEmpty(filePath))
            {
                await LogHistoryAsync(filePath, "RemoveMetadata", false, ex.Message);
            }

            return StatusCode(500, "Error removing metadata");
        }
    }

    private static string DecodeBase64UrlSafe(string input)
    {
        try
        {
            // Convert URL-safe base64 back to standard base64
            var base64 = input.Replace('-', '+').Replace('_', '/');
            // Add padding if necessary
            switch (base64.Length % 4)
            {
                case 2: base64 += "=="; break;
                case 3: base64 += "="; break;
            }
            var bytes = Convert.FromBase64String(base64);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return string.Empty;
        }
    }



    /// <summary>
    /// Helper method to log processing history entries
    /// </summary>
    private async Task LogHistoryAsync(string filePath, string action, bool success, string? errorMessage = null)
    {
        await _historyService.AddHistoryEntryAsync(new ProcessingHistoryEntry
        {
            Id = Guid.NewGuid(),
            FilePath = filePath,
            Action = action,
            Timestamp = DateTime.UtcNow,
            Success = success,
            ErrorMessage = errorMessage
        });
    }

    /// <summary>
    /// Helper method to log processing history entries with before/after metadata
    /// </summary>
    private async Task LogHistoryWithChangesAsync(
        string filePath, 
        string action, 
        bool success, 
        string? errorMessage,
        string? beforeFilename,
        string? afterFilename,
        ComicMetadata? beforeMetadata,
        ComicMetadata? afterMetadata)
    {
        await _historyService.AddHistoryEntryAsync(new ProcessingHistoryEntry
        {
            Id = Guid.NewGuid(),
            FilePath = filePath,
            Action = action,
            Timestamp = DateTime.UtcNow,
            Success = success,
            ErrorMessage = errorMessage,
            BeforeFilename = beforeFilename,
            AfterFilename = afterFilename,
            BeforeTitle = beforeMetadata?.Title,
            AfterTitle = afterMetadata?.Title,
            BeforeSeries = beforeMetadata?.Series,
            AfterSeries = afterMetadata?.Series,
            BeforeIssue = beforeMetadata?.Issue,
            AfterIssue = afterMetadata?.Issue,
            BeforePublisher = beforeMetadata?.Publisher,
            AfterPublisher = afterMetadata?.Publisher,
            BeforeYear = beforeMetadata?.Year,
            AfterYear = afterMetadata?.Year,
            BeforeVolume = beforeMetadata?.Volume,
            AfterVolume = afterMetadata?.Volume
        });
    }

    /// <summary>
    /// Remove stale database entries for files that no longer exist on disk
    /// </summary>
    [HttpPost("cleanup-stale")]
    public async Task<ActionResult<object>> CleanupStaleEntries(CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Cleanup stale entries endpoint called");
            var removedCount = await _fileStore.CleanupStaleEntriesAsync(cancellationToken);
            
            return Ok(new 
            { 
                success = true, 
                removedCount = removedCount,
                message = $"Removed {removedCount} stale database entries"
            });
        }
        catch (OperationCanceledException)
        {
            return StatusCode(499, new { error = "Cleanup operation was cancelled" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during stale entry cleanup");
            return StatusCode(500, new { error = "Failed to cleanup stale entries" });
        }
    }

    public class UpdateTagsRequest
    {
        public List<string> Files { get; set; } = new();
        public ComicMetadata Metadata { get; set; } = new();
    }

    public class ProcessedStatusRequest
    {
        public bool Processed { get; set; }
    }

    public class CombineFoldersRequest
    {
        public string? GroupKey { get; set; }
        public string DestinationDirectory { get; set; } = string.Empty;
        public List<string>? SourceDirectories { get; set; }
    }

    private sealed class CombinableFolder
    {
        public string Directory { get; set; } = string.Empty;
        public int FileCount { get; set; }
        public long TotalSize { get; set; }
        public DateTime NewestFileAddedAt { get; set; }
        public DateTime OldestFileAddedAt { get; set; }
        public DateTime NewestFileModifiedAt { get; set; }
        public List<string> SampleFileNames { get; set; } = new();
        public List<string> FilePaths { get; set; } = new();
    }

    private sealed class CombinableFolderGroup
    {
        public string GroupKey { get; set; } = string.Empty;
        public string SeriesName { get; set; } = string.Empty;
        public string? Volume { get; set; }
        public int TotalFileCount { get; set; }
        public string SuggestedDestinationDirectory { get; set; } = string.Empty;
        public string SuggestionReason { get; set; } = string.Empty;
        public List<CombinableFolder> Folders { get; set; } = new();
    }

    private sealed class CombineFoldersMove
    {
        public string SourcePath { get; set; } = string.Empty;
        public string DestinationPath { get; set; } = string.Empty;
        public bool HadConflict { get; set; }
        public bool Skipped { get; set; }
        public string? Reason { get; set; }
    }

    private sealed class CombineFoldersPlan
    {
        public string Destination { get; set; } = string.Empty;
        public List<string> SourceDirectories { get; set; } = new();
        public List<CombineFoldersMove> Moves { get; set; } = new();
    }

    public class CombinableFolderDto
    {
        public string Directory { get; set; } = string.Empty;
        public int FileCount { get; set; }
        public long TotalSize { get; set; }
        public DateTime NewestFileAddedAt { get; set; }
        public DateTime OldestFileAddedAt { get; set; }
        public DateTime NewestFileModifiedAt { get; set; }
        public List<string> SampleFileNames { get; set; } = new();
        public bool IsSuggested { get; set; }
    }

    public class CombinableFolderGroupDto
    {
        public string GroupKey { get; set; } = string.Empty;
        public string SeriesName { get; set; } = string.Empty;
        public string? Volume { get; set; }
        public int TotalFileCount { get; set; }
        public string SuggestedDestinationDirectory { get; set; } = string.Empty;
        public string SuggestionReason { get; set; } = string.Empty;
        public List<CombinableFolderDto> Folders { get; set; } = new();
    }

    /// <summary>
    /// Mark a single file as read or unread
    /// </summary>
    [HttpPost("~/api/files/{encodedFilePath}/read")]
    public async Task<ActionResult> MarkFileRead(string encodedFilePath, [FromBody] ReadStatusRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var filePath = DecodeBase64UrlSafe(encodedFilePath);
            if (string.IsNullOrEmpty(filePath))
                return BadRequest("Invalid file path");

            if (!IsPathSafe(filePath))
                return BadRequest("Invalid file path");

            await _fileStore.MarkFileReadAsync(filePath, request.Read, cancellationToken);
            return Ok();
        }
        catch (Exception ex)
        {
            var sanitizedEncodedPath = LoggingHelper.SanitizeForLog(encodedFilePath);
            _logger.LogError(ex, "Error marking file read status for encoded path {EncodedPath}", sanitizedEncodedPath);
            return StatusCode(500, "Error updating read status");
        }
    }

    /// <summary>
    /// Mark multiple files as read or unread
    /// </summary>
    [HttpPost("~/api/files/read-batch")]
    public async Task<ActionResult> MarkFilesReadBatch([FromBody] ReadBatchRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            if (request.Files == null || !request.Files.Any())
                return BadRequest("No files provided");

            // Validate all paths are safe
            foreach (var filePath in request.Files)
            {
                if (!IsPathSafe(filePath))
                {
                    _logger.LogWarning("Attempt to mark read status for file outside watched directory: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
                    return BadRequest("One or more file paths are outside the allowed directory");
                }
            }

            await _fileStore.MarkFilesReadAsync(request.Files, request.Read, cancellationToken);
            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error marking files read status");
            return StatusCode(500, "Error updating read status");
        }
    }

    public class ReadStatusRequest
    {
        public bool Read { get; set; }
    }

    public class ReadBatchRequest
    {
        public List<string> Files { get; set; } = new();
        public bool Read { get; set; }
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

public class PatchMetadataRequest
{
    public ComicMetadata? Metadata { get; set; }
    public List<string>? LockFields { get; set; }
}
