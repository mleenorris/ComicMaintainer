using System.Text.RegularExpressions;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.Core.Services;

public class SeriesLibraryService : ISeriesLibraryService
{
    // Unicode-aware: keep any Unicode letter (\p{L}) or number (\p{N}) so
    // non-ASCII titles (CJK, accented Latin, Cyrillic, etc.) produce rich,
    // distinguishable keys instead of collapsing to a bare digit when every
    // letter gets stripped. Pure-ASCII titles still produce the same output
    // as the previous [^a-z0-9]+ sanitizer.
    private static readonly Regex SeriesKeySanitizer = new(@"[^\p{L}\p{N}]+", RegexOptions.Compiled);

    // Series-level filters that operate on the grouped/series record (rather
    // than on individual files in the file store). These are applied AFTER
    // BuildGroupsAsync so the file-store call sees a no-op file filter.
    private const string ProviderMatchedFilter = "matched";
    private const string ProviderUnmatchedFilter = "unmatched";
    private const string MissingIssuesFilter = "missing";

    private readonly IFileStoreService _fileStore;
    private readonly IComicProcessorService _processor;
    private readonly ISeriesMetadataCacheService _metadataCache;
    private readonly IOptionsMonitor<AppSettings> _settings;
    private readonly ILogger<SeriesLibraryService> _logger;
    private readonly ISeriesNameResolver? _seriesNameResolver;

    public SeriesLibraryService(
        IFileStoreService fileStore,
        IComicProcessorService processor,
        ISeriesMetadataCacheService metadataCache,
        IOptionsMonitor<AppSettings> settings,
        ILogger<SeriesLibraryService> logger,
        ISeriesNameResolver? seriesNameResolver = null)
    {
        _fileStore = fileStore;
        _processor = processor;
        _metadataCache = metadataCache;
        _settings = settings;
        _logger = logger;
        _seriesNameResolver = seriesNameResolver;
    }

    public async Task<SeriesLibraryResult> GetSeriesAsync(
        string? filter = null,
        string? search = null,
        int page = 1,
        int perPage = 100,
        string? sort = "name",
        string? direction = "asc",
        CancellationToken cancellationToken = default)
    {
        var (fileFilter, seriesFilter) = SplitFilter(filter);
        var groups = await BuildGroupsAsync(fileFilter, allowDiskRead: true, cancellationToken);

        var groupedSeries = groups.Values
            .Where(accumulator => MatchesSeriesFilter(accumulator, seriesFilter))
            .Select(accumulator =>
            {
                accumulator.Issues = SortIssues(accumulator.Issues);

                return new SeriesLibraryDto
                {
                    Id = accumulator.Id,
                    Title = accumulator.DisplayTitle,
                    CanonicalTitle = accumulator.CanonicalTitle,
                    Aliases = NormalizeAliases(accumulator.Aliases),
                    MetadataSource = accumulator.MetadataSource,
                    IssueCount = accumulator.Issues.Count,
                    TotalSize = accumulator.TotalSize,
                    LatestModified = ToUnixTime(accumulator.LatestModified),
                    CoverFilePath = accumulator.Issues.FirstOrDefault()?.FilePath ?? string.Empty,
                    HasExternalImage = accumulator.HasExternalImage,
                    ExternalImageUrl = BuildExternalImageUrl(accumulator),
                    Issues = accumulator.Issues,
                    Synopsis = accumulator.Synopsis
                };
            })
            .ToList();

        if (!string.IsNullOrWhiteSpace(search))
        {
            groupedSeries = groupedSeries.Where(series =>
                    Contains(series.Title, search)
                    || Contains(series.CanonicalTitle, search)
                    || series.Aliases.Any(alias => Contains(alias, search))
                    || series.Issues.Any(issue =>
                        Contains(issue.Title, search)
                        || Contains(issue.Issue, search)
                        || Contains(issue.FileName, search)))
                .ToList();
        }

        groupedSeries = SortSeries(groupedSeries, sort, direction,
            keyTitle: s => s.Title,
            keyDate: s => s.LatestModified,
            keySize: s => s.TotalSize);

        var totalSeries = groupedSeries.Count;
        var totalPages = perPage == -1 ? 1 : (int)Math.Ceiling((double)totalSeries / Math.Max(1, perPage));
        page = Math.Max(1, Math.Min(page, totalPages == 0 ? 1 : totalPages));

        if (perPage != -1)
        {
            groupedSeries = groupedSeries
                .Skip((page - 1) * perPage)
                .Take(perPage)
                .ToList();
        }

        return new SeriesLibraryResult
        {
            Series = groupedSeries,
            Page = page,
            TotalPages = totalPages,
            TotalSeries = totalSeries
        };
    }

    public async Task<SeriesSummaryResult> GetSeriesSummariesAsync(
        string? filter = null,
        string? search = null,
        int page = 1,
        int perPage = 100,
        string? sort = "name",
        string? direction = "asc",
        int? offset = null,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        var (fileFilter, seriesFilter) = SplitFilter(filter);
        // Summary mode never opens an archive on disk, so even huge libraries
        // stay snappy. Per-issue details are loaded lazily by GetSeriesIssuesAsync.
        var groups = await BuildGroupsAsync(fileFilter, allowDiskRead: false, cancellationToken);

        var summaries = groups.Values
            .Where(accumulator => MatchesSeriesFilter(accumulator, seriesFilter))
            .Select(accumulator =>
            {
                var sortedIssues = SortIssues(accumulator.Issues);
                return new
                {
                    Accumulator = accumulator,
                    Cover = sortedIssues.FirstOrDefault()?.FilePath ?? string.Empty,
                    SortedIssues = sortedIssues,
                    IssueTitleSearch = string.Join("\n", sortedIssues.Select(i => $"{i.Title}\n{i.Issue}\n{i.FileName}"))
                };
            })
            .ToList();

        if (!string.IsNullOrWhiteSpace(search))
        {
            summaries = summaries.Where(item =>
                    Contains(item.Accumulator.DisplayTitle, search)
                    || Contains(item.Accumulator.CanonicalTitle, search)
                    || item.Accumulator.Aliases.Any(alias => Contains(alias, search))
                    || Contains(item.IssueTitleSearch, search))
                .ToList();
        }

        summaries = SortSeries(summaries, sort, direction,
            keyTitle: item => item.Accumulator.DisplayTitle,
            keyDate: item => ToUnixTime(item.Accumulator.LatestModified),
            keySize: item => item.Accumulator.TotalSize);

        var totalSeries = summaries.Count;

        bool offsetMode = offset.HasValue && limit.HasValue && limit.Value > 0;
        int effectiveOffset = 0;
        int totalPages;

        if (offsetMode)
        {
            effectiveOffset = Math.Max(0, offset!.Value);
            var lim = Math.Max(1, limit!.Value);
            summaries = summaries.Skip(effectiveOffset).Take(lim).ToList();
            totalPages = (int)Math.Ceiling((double)totalSeries / lim);
            page = 1;
        }
        else
        {
            totalPages = perPage == -1 ? 1 : (int)Math.Ceiling((double)totalSeries / Math.Max(1, perPage));
            page = Math.Max(1, Math.Min(page, totalPages == 0 ? 1 : totalPages));

            if (perPage != -1)
            {
                summaries = summaries
                    .Skip((page - 1) * perPage)
                    .Take(perPage)
                    .ToList();
            }
        }

        return new SeriesSummaryResult
        {
            Series = summaries.Select(item => new SeriesSummaryDto
            {
                Id = item.Accumulator.Id,
                Title = item.Accumulator.DisplayTitle,
                CanonicalTitle = item.Accumulator.CanonicalTitle,
                Aliases = NormalizeAliases(item.Accumulator.Aliases),
                MetadataSource = item.Accumulator.MetadataSource,
                IssueCount = item.Accumulator.Issues.Count,
                TotalSize = item.Accumulator.TotalSize,
                LatestModified = ToUnixTime(item.Accumulator.LatestModified),
                CoverFilePath = item.Cover,
                HasExternalImage = item.Accumulator.HasExternalImage,
                ExternalImageUrl = BuildExternalImageUrl(item.Accumulator),
                LookupStatus = item.Accumulator.LookupStatus,
                LastLookupUtc = item.Accumulator.LastLookupUtc,
                Synopsis = item.Accumulator.Synopsis
            }).ToList(),
            Page = page,
            TotalPages = totalPages,
            TotalSeries = totalSeries,
            Offset = effectiveOffset
        };
    }

    public async Task<IReadOnlyList<SeriesOverviewEntry>> GetSeriesOverviewEntriesAsync(
        CancellationToken cancellationToken = default)
    {
        // Overview never reads archives on disk; it relies on the cached
        // grouping so the home page stays fast even for huge libraries.
        var groups = await BuildGroupsAsync(filter: null, allowDiskRead: false, cancellationToken);

        var entries = new List<SeriesOverviewEntry>(groups.Count);
        foreach (var accumulator in groups.Values)
        {
            var sortedIssues = SortIssues(accumulator.Issues);
            var cover = sortedIssues.FirstOrDefault()?.FilePath ?? string.Empty;

            entries.Add(new SeriesOverviewEntry
            {
                Summary = new SeriesSummaryDto
                {
                    Id = accumulator.Id,
                    Title = accumulator.DisplayTitle,
                    CanonicalTitle = accumulator.CanonicalTitle,
                    Aliases = NormalizeAliases(accumulator.Aliases),
                    MetadataSource = accumulator.MetadataSource,
                    IssueCount = accumulator.Issues.Count,
                    TotalSize = accumulator.TotalSize,
                    LatestModified = ToUnixTime(accumulator.LatestModified),
                    CoverFilePath = cover,
                    HasExternalImage = accumulator.HasExternalImage,
                    ExternalImageUrl = BuildExternalImageUrl(accumulator),
                    LookupStatus = accumulator.LookupStatus,
                    LastLookupUtc = accumulator.LastLookupUtc,
                    Synopsis = accumulator.Synopsis
                },
                EarliestCreatedAt = accumulator.EarliestCreatedAt == DateTime.MaxValue
                    ? DateTime.MinValue
                    : accumulator.EarliestCreatedAt,
                LatestCreatedAt = accumulator.LatestCreatedAt,
                FilePaths = accumulator.Issues.Select(i => i.FilePath).ToList()
            });
        }

        return entries;
    }

    public async Task<AdjacentIssueResult> GetAdjacentIssueAsync(
        string filePath,
        string direction = "next",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return new AdjacentIssueResult { Found = false };
        }

        // Use the unfiltered grouping so adjacency reflects the whole series
        // regardless of any active library filter, and never reads archives
        // off disk so the reader's next/previous prefetch stays fast.
        var groups = await BuildGroupsAsync(filter: null, allowDiskRead: false, cancellationToken);

        var owningSeries = groups.Values.FirstOrDefault(accumulator =>
            accumulator.Issues.Any(issue =>
                string.Equals(issue.FilePath, filePath, StringComparison.Ordinal)));

        if (owningSeries is null)
        {
            return new AdjacentIssueResult { Found = false };
        }

        var sortedIssues = SortIssues(owningSeries.Issues);
        var currentIndex = sortedIssues.FindIndex(issue =>
            string.Equals(issue.FilePath, filePath, StringComparison.Ordinal));

        if (currentIndex == -1)
        {
            return new AdjacentIssueResult { Found = false };
        }

        var adjacentIndex = string.Equals(direction, "next", StringComparison.OrdinalIgnoreCase)
            ? currentIndex + 1
            : currentIndex - 1;

        if (adjacentIndex < 0 || adjacentIndex >= sortedIssues.Count)
        {
            // First/last issue of the series: there is intentionally no
            // adjacent issue, so the reader shows "end of series".
            return new AdjacentIssueResult { Found = true, HasAdjacent = false };
        }

        var adjacent = sortedIssues[adjacentIndex];
        return new AdjacentIssueResult
        {
            Found = true,
            HasAdjacent = true,
            FilePath = adjacent.FilePath,
            FileName = adjacent.FileName
        };
    }

    public async Task<SeriesIssuesResult?> GetSeriesIssuesAsync(
        string seriesId,
        string? filter = null,
        int page = 1,
        int perPage = 100,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(seriesId))
        {
            return null;
        }

        // Group cache-only first (cheap). If the requested id matches a group,
        // optionally upgrade just that group's metadata via disk reads to fill
        // in per-issue details that may be missing from the DB cache.
        var (fileFilter, _) = SplitFilter(filter);
        var groups = await BuildGroupsAsync(fileFilter, allowDiskRead: false, cancellationToken);
        if (!groups.TryGetValue(seriesId, out var accumulator))
        {
            // The requested series id is not present in the filtered groups.
            // This happens for two reasons when an active file filter is in
            // effect: (1) the filter excludes every file of the series, or
            // (2) the union-find representative used as the series id
            // "drifted" because excluding files changed which file is the
            // first key in the union. Both cases happen during ordinary
            // filter-toggle usage from the series-detail view and must NOT
            // 404 — otherwise the UI shows a "Failed to load issues" error
            // and the user is effectively kicked out of the series view.
            //
            // Resolve the series via the unfiltered groups: if found there,
            // try to map it back to an equivalent filtered group by matching
            // canonical title / aliases (id-drift); otherwise synthesize an
            // empty accumulator that preserves the series metadata so the
            // detail view stays open with an empty issues list.
            if (string.IsNullOrWhiteSpace(fileFilter))
            {
                return null;
            }

            var unfilteredGroups = await BuildGroupsAsync(filter: null, allowDiskRead: false, cancellationToken);
            if (!unfilteredGroups.TryGetValue(seriesId, out var unfilteredAccumulator))
            {
                return null;
            }

            accumulator = FindMatchingFilteredGroup(groups, unfilteredAccumulator)
                ?? new SeriesAccumulator
                {
                    Id = unfilteredAccumulator.Id,
                    DisplayTitle = unfilteredAccumulator.DisplayTitle,
                    CanonicalTitle = unfilteredAccumulator.CanonicalTitle,
                    MetadataSource = unfilteredAccumulator.MetadataSource,
                    LookupStatus = unfilteredAccumulator.LookupStatus,
                    LastLookupUtc = unfilteredAccumulator.LastLookupUtc,
                    Aliases = new List<string>(unfilteredAccumulator.Aliases),
                    HasExternalImage = unfilteredAccumulator.HasExternalImage,
                    ImageNormalizedKey = unfilteredAccumulator.ImageNormalizedKey,
                    Synopsis = unfilteredAccumulator.Synopsis
                    // Issues intentionally left empty: the active filter
                    // excludes every file in this series.
                };
        }

        // Best-effort upgrade: for issues with empty title metadata, try the
        // archive on disk. Bounded to the requested page so we never read more
        // archives than the user actually sees.
        var sortedIssues = SortIssues(accumulator.Issues);
        var totalIssues = sortedIssues.Count;
        var effectivePerPage = perPage == -1 ? totalIssues : Math.Max(1, perPage);
        var totalPages = perPage == -1 ? 1 : (int)Math.Ceiling((double)totalIssues / effectivePerPage);
        page = Math.Max(1, Math.Min(page, totalPages == 0 ? 1 : totalPages));
        var pageIssues = perPage == -1
            ? sortedIssues
            : sortedIssues.Skip((page - 1) * effectivePerPage).Take(effectivePerPage).ToList();

        // Best-effort upgrade: for issues with empty title metadata, try the
        // archive on disk. Bounded to the requested page so we never read more
        // archives than the user actually sees. An additional hard cap from
        // AppSettings.SeriesIssuesMaxArchiveUpgrades protects against
        // pathological requests (e.g. perPage == -1 with a 1000-issue series
        // and no cached metadata) where the upgrade loop would otherwise
        // dominate request latency.
        var upgradeBudget = Math.Max(0, _settings.CurrentValue?.SeriesIssuesMaxArchiveUpgrades ?? 100);

        // Upgrade missing titles by opening just the visible archives.
        for (var i = 0; i < pageIssues.Count; i++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            var issue = pageIssues[i];
            if (!string.IsNullOrWhiteSpace(issue.Title) && !string.IsNullOrWhiteSpace(issue.Issue))
            {
                continue;
            }
            if (upgradeBudget <= 0)
            {
                break;
            }
            upgradeBudget--;

            try
            {
                var meta = await _processor.GetSeriesMetadataAsync(issue.FilePath, cancellationToken);
                if (meta is null) continue;
                pageIssues[i] = new SeriesIssueDto
                {
                    FilePath = issue.FilePath,
                    FileName = issue.FileName,
                    Title = string.IsNullOrWhiteSpace(issue.Title) ? meta.Title : issue.Title,
                    Issue = string.IsNullOrWhiteSpace(issue.Issue) ? meta.Issue : issue.Issue,
                    Volume = string.IsNullOrWhiteSpace(issue.Volume) ? meta.Volume : issue.Volume,
                    Publisher = string.IsNullOrWhiteSpace(issue.Publisher) ? meta.Publisher : issue.Publisher,
                    Year = issue.Year ?? meta.Year,
                    Size = issue.Size,
                    Modified = issue.Modified,
                    Processed = issue.Processed,
                    Renamed = issue.Renamed,
                    Normalized = issue.Normalized,
                    Duplicate = issue.Duplicate,
                    Read = issue.Read
                };
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Issue metadata upgrade failed for {FilePath}",
                    LoggingHelper.SanitizePathForLog(issue.FilePath));
            }
        }

        // Final, cheap, unbounded pass: any issue still missing its number
        // (e.g. files beyond the archive-upgrade budget, or files whose archive
        // has no <Number>) gets its number parsed from the file name in memory.
        // This guarantees the issue badge, ordering, and missing-issue grid are
        // populated for every file in the series, not just the first
        // SeriesIssuesMaxArchiveUpgrades of them.
        for (var i = 0; i < pageIssues.Count; i++)
        {
            var issue = pageIssues[i];
            if (!string.IsNullOrWhiteSpace(issue.Issue))
            {
                continue;
            }
            var parsedIssue = ResolveIssueNumber(issue);
            if (string.IsNullOrWhiteSpace(parsedIssue))
            {
                continue;
            }
            pageIssues[i] = new SeriesIssueDto
            {
                FilePath = issue.FilePath,
                FileName = issue.FileName,
                Title = issue.Title,
                Issue = parsedIssue,
                Volume = issue.Volume,
                Publisher = issue.Publisher,
                Year = issue.Year,
                Size = issue.Size,
                Modified = issue.Modified,
                Processed = issue.Processed,
                Renamed = issue.Renamed,
                Normalized = issue.Normalized,
                Duplicate = issue.Duplicate,
                Read = issue.Read
            };
        }

        return new SeriesIssuesResult
        {
            Id = accumulator.Id,
            Title = accumulator.DisplayTitle,
            CanonicalTitle = accumulator.CanonicalTitle,
            Aliases = NormalizeAliases(accumulator.Aliases),
            MetadataSource = accumulator.MetadataSource,
            CoverFilePath = sortedIssues.FirstOrDefault()?.FilePath ?? string.Empty,
            HasExternalImage = accumulator.HasExternalImage,
            ExternalImageUrl = BuildExternalImageUrl(accumulator),
            IssueCount = totalIssues,
            TotalSize = accumulator.TotalSize,
            Issues = pageIssues,
            Page = page,
            PerPage = effectivePerPage,
            TotalPages = totalPages
        };
    }

    public async Task<IReadOnlyList<string>> GetTitlesForSeriesIdAsync(
        string seriesId,
        string? filter = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(seriesId))
        {
            return Array.Empty<string>();
        }

        var (fileFilter, _) = SplitFilter(filter);
        var groups = await BuildGroupsAsync(fileFilter, allowDiskRead: false, cancellationToken);
        if (!groups.TryGetValue(seriesId, out var accumulator))
        {
            return Array.Empty<string>();
        }

        var titles = new List<string>();
        if (!string.IsNullOrWhiteSpace(accumulator.CanonicalTitle))
        {
            titles.Add(accumulator.CanonicalTitle);
        }
        foreach (var alias in accumulator.Aliases)
        {
            if (!string.IsNullOrWhiteSpace(alias) &&
                !titles.Contains(alias, StringComparer.OrdinalIgnoreCase))
            {
                titles.Add(alias);
            }
        }
        return titles;
    }

    public async Task<SeriesFoldersResult?> GetFoldersForSeriesIdAsync(
        string seriesId,
        string? filter = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(seriesId))
        {
            return null;
        }

        var (fileFilter, _) = SplitFilter(filter);
        var groups = await BuildGroupsAsync(fileFilter, allowDiskRead: false, cancellationToken);
        if (!groups.TryGetValue(seriesId, out var accumulator))
        {
            return null;
        }

        // Bucket the series's issues by their parent directory. We use an
        // ordinal (case-sensitive) comparer because on case-sensitive
        // filesystems (Linux/Docker bind mounts) two folders that differ only
        // by capitalisation are genuinely distinct on disk, and the user
        // should see both so they can decide whether to combine them.
        var byDirectory = new Dictionary<string, (int Count, long Size)>(StringComparer.Ordinal);
        foreach (var issue in accumulator.Issues)
        {
            if (string.IsNullOrWhiteSpace(issue.FilePath))
            {
                continue;
            }

            var directory = Path.GetDirectoryName(issue.FilePath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            if (byDirectory.TryGetValue(directory, out var existing))
            {
                byDirectory[directory] = (existing.Count + 1, existing.Size + issue.Size);
            }
            else
            {
                byDirectory[directory] = (1, issue.Size);
            }
        }

        var folders = byDirectory
            .Select(kvp => new SeriesFolderDto
            {
                Directory = kvp.Key,
                FileCount = kvp.Value.Count,
                TotalSize = kvp.Value.Size
            })
            .OrderByDescending(folder => folder.FileCount)
            .ThenBy(folder => folder.Directory, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new SeriesFoldersResult
        {
            Id = accumulator.Id,
            Title = accumulator.DisplayTitle,
            Folders = folders
        };
    }

    public async Task<IReadOnlyList<SeriesFoldersResult>> GetAllSeriesFolderGroupsAsync(
        string? filter = null,
        CancellationToken cancellationToken = default)
    {
        var (fileFilter, _) = SplitFilter(filter);
        var groups = await BuildGroupsAsync(fileFilter, allowDiskRead: false, cancellationToken);

        var results = new List<SeriesFoldersResult>(groups.Count);
        foreach (var accumulator in groups.Values)
        {
            // Same per-directory bucketing as GetFoldersForSeriesIdAsync; use
            // an ordinal comparer so case-distinct on-disk folders remain
            // separate entries on case-sensitive filesystems.
            var byDirectory = new Dictionary<string, (int Count, long Size)>(StringComparer.Ordinal);
            foreach (var issue in accumulator.Issues)
            {
                if (string.IsNullOrWhiteSpace(issue.FilePath))
                {
                    continue;
                }

                var directory = Path.GetDirectoryName(issue.FilePath);
                if (string.IsNullOrWhiteSpace(directory))
                {
                    continue;
                }

                if (byDirectory.TryGetValue(directory, out var existing))
                {
                    byDirectory[directory] = (existing.Count + 1, existing.Size + issue.Size);
                }
                else
                {
                    byDirectory[directory] = (1, issue.Size);
                }
            }

            var folders = byDirectory
                .Select(kvp => new SeriesFolderDto
                {
                    Directory = kvp.Key,
                    FileCount = kvp.Value.Count,
                    TotalSize = kvp.Value.Size
                })
                .OrderByDescending(folder => folder.FileCount)
                .ThenBy(folder => folder.Directory, StringComparer.OrdinalIgnoreCase)
                .ToList();

            results.Add(new SeriesFoldersResult
            {
                Id = accumulator.Id,
                Title = accumulator.DisplayTitle,
                Folders = folders
            });
        }

        return results;
    }

    public async Task<IReadOnlyList<SeriesFolderDto>> GetFoldersForNormalizedKeyAsync(
        string normalizedKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(normalizedKey))
        {
            return Array.Empty<SeriesFolderDto>();
        }

        var groups = await BuildGroupsAsync(filter: null, allowDiskRead: false, cancellationToken);

        // Match by ImageNormalizedKey first (the authoritative cache key for
        // the cover image); fall back to a case-insensitive match against the
        // accumulator id so callers that pass an unmatched series key still
        // find the right folders in libraries that haven't been linked to
        // external metadata yet.
        var byDirectory = new Dictionary<string, (int Count, long Size)>(StringComparer.Ordinal);
        foreach (var accumulator in groups.Values)
        {
            var matchesImageKey = !string.IsNullOrEmpty(accumulator.ImageNormalizedKey)
                && string.Equals(accumulator.ImageNormalizedKey, normalizedKey, StringComparison.OrdinalIgnoreCase);
            var matchesId = !matchesImageKey
                && string.Equals(accumulator.Id, normalizedKey, StringComparison.OrdinalIgnoreCase);
            if (!matchesImageKey && !matchesId)
            {
                continue;
            }

            foreach (var issue in accumulator.Issues)
            {
                if (string.IsNullOrWhiteSpace(issue.FilePath))
                {
                    continue;
                }
                var directory = Path.GetDirectoryName(issue.FilePath);
                if (string.IsNullOrWhiteSpace(directory))
                {
                    continue;
                }
                if (byDirectory.TryGetValue(directory, out var existing))
                {
                    byDirectory[directory] = (existing.Count + 1, existing.Size + issue.Size);
                }
                else
                {
                    byDirectory[directory] = (1, issue.Size);
                }
            }
        }

        return byDirectory
            .Select(kvp => new SeriesFolderDto
            {
                Directory = kvp.Key,
                FileCount = kvp.Value.Count,
                TotalSize = kvp.Value.Size
            })
            .OrderByDescending(folder => folder.FileCount)
            .ThenBy(folder => folder.Directory, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<string?> GetFirstIssueFilePathForNormalizedKeyAsync(
        string normalizedKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(normalizedKey))
        {
            return null;
        }

        var groups = await BuildGroupsAsync(filter: null, allowDiskRead: false, cancellationToken);

        // Match by ImageNormalizedKey first (the authoritative cache key for
        // the cover image); fall back to a case-insensitive match against the
        // accumulator id so callers that pass an unmatched series key still
        // resolve the right series. Mirrors GetFoldersForNormalizedKeyAsync.
        foreach (var accumulator in groups.Values)
        {
            var matchesImageKey = !string.IsNullOrEmpty(accumulator.ImageNormalizedKey)
                && string.Equals(accumulator.ImageNormalizedKey, normalizedKey, StringComparison.OrdinalIgnoreCase);
            var matchesId = !matchesImageKey
                && string.Equals(accumulator.Id, normalizedKey, StringComparison.OrdinalIgnoreCase);
            if (!matchesImageKey && !matchesId)
            {
                continue;
            }

            var sortedIssues = SortIssues(accumulator.Issues);
            return sortedIssues
                .Select(issue => issue.FilePath)
                .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
        }

        return null;
    }

    /// <summary>
    /// Loads the file store, resolves grouping titles and external cache info,
    /// and returns a dictionary of series accumulators keyed by their union-find
    /// representative (the series id surfaced to the API).
    /// </summary>
    private async Task<Dictionary<string, SeriesAccumulator>> BuildGroupsAsync(
        string? filter,
        bool allowDiskRead,
        CancellationToken cancellationToken)
    {
        var files = (await _fileStore.GetFilteredFilesAsync(filter, cancellationToken)).ToList();
        var fileEntries = new List<(ComicFile File, SeriesMetadata Metadata, string GroupingTitle, List<string> MetadataAliases)>(files.Count);

        foreach (var file in files)
        {
            // Reuse metadata already loaded from the database when available so we
            // don't have to open every archive on disk just to render the library.
            var metadata = BuildSeriesMetadataFromCache(file);
            if (metadata is null && allowDiskRead)
            {
                metadata = await _processor.GetSeriesMetadataAsync(file.FilePath, cancellationToken);
            }
            metadata ??= BuildSeriesMetadataFromFileName(file);
            var groupingTitle = ResolveGroupingTitle(metadata, file);
            var metadataAliases = ResolveMetadataAliases(metadata, groupingTitle);
            fileEntries.Add((file, metadata, groupingTitle, metadataAliases));
        }

        // Load the persistent metadata cache (external lookups + user aliases) and
        // index it by every known alias so we can merge folders without re-querying
        // external providers on every library load.
        var cacheRecords = await _metadataCache.GetAllAsync(cancellationToken);
        var aliasIndex = BuildAliasIndex(cacheRecords);

        var unionFind = new UnionFind<string>(StringComparer.OrdinalIgnoreCase);
        var groupingKeyByFile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in fileEntries)
        {
            var fileKey = NormalizeKey(entry.GroupingTitle);
            unionFind.Add(fileKey);

            UnionWithCacheKey(unionFind, fileKey, entry.GroupingTitle, aliasIndex);
            foreach (var alias in entry.MetadataAliases)
            {
                UnionWithCacheKey(unionFind, fileKey, alias, aliasIndex);
            }
            groupingKeyByFile[entry.File.FilePath] = fileKey;
        }

        foreach (var record in cacheRecords)
        {
            var recordKey = record.NormalizedKey;
            unionFind.Add(recordKey);
            // Link this record only to its OWN title keys. We deliberately do NOT
            // follow the alias index back into other records here — that would
            // transitively merge unrelated series whenever their alias lists
            // overlap (e.g. a provider returning a long list of "also known as"
            // titles, or the alias-adopt UI dumping every external-result alias
            // into the user's record). Cross-record merges should only happen
            // through the file-side loop, where a file's grouping title points
            // (via aliasIndex) at a specific record — that's the user-intent
            // path.
            foreach (var title in EnumerateAllRecordTitles(record))
            {
                if (string.IsNullOrWhiteSpace(title)) continue;
                var titleKey = NormalizeKey(title);
                unionFind.Add(titleKey);
                // Only union when the alias index agrees that this title belongs
                // to this record. If another record has a stronger claim on the
                // title (its canonical or user alias), we skip — that other
                // record's own loop iteration will own the title key.
                if (aliasIndex.TryGetValue(titleKey, out var ownerKey)
                    && string.Equals(ownerKey, recordKey, StringComparison.OrdinalIgnoreCase))
                {
                    unionFind.Union(recordKey, titleKey);
                }
            }
        }

        var groups = new Dictionary<string, SeriesAccumulator>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in fileEntries)
        {
            var file = entry.File;
            var metadata = entry.Metadata;
            var groupingTitle = entry.GroupingTitle;
            var fileKey = groupingKeyByFile[file.FilePath];
            var representative = unionFind.Find(fileKey);

            var record = ResolveRecordForGroup(representative, cacheRecords, unionFind);
            var canonicalTitle = record is null || string.IsNullOrWhiteSpace(record.CanonicalTitle)
                ? groupingTitle
                : record.CanonicalTitle;

            // The DisplayTitle is what the library renders; it honours the
            // per-series preferred-language override (and the global default)
            // by picking the first localized title in that language. The
            // CanonicalTitle remains the unchanged "source of truth" used by
            // tagging / file rename flows. We route through ISeriesNameResolver
            // (when registered) so this surface stays in lockstep with the
            // <Series> value that ComicProcessorService writes to ComicInfo.xml
            // for files reaching the same record — the same rule decides both.
            var displayTitle = record is null
                ? canonicalTitle
                : (_seriesNameResolver is not null
                    ? _seriesNameResolver.ResolveForRecord(record)
                    : SeriesDisplayTitleResolver.Resolve(record, _settings.CurrentValue.DefaultPreferredLanguage));
            if (string.IsNullOrWhiteSpace(displayTitle))
            {
                displayTitle = canonicalTitle;
            }

            if (!groups.TryGetValue(representative, out var accumulator))
            {
                // The cached cover image may live on a *different* record in
                // the same union-find component than the rank-selected "best"
                // record (e.g. an automatic-refresh "success" sibling carries
                // the downloaded image while the user's "manual_match" record
                // — which wins on lookup-status rank — has none). Resolve the
                // image independently so the library card shows the provider
                // image whenever any record in the group has one, matching the
                // Manage Names modal (which looks the image up by title).
                var imageRecord = ResolveImageRecordForGroup(representative, cacheRecords, unionFind)
                    ?? (record is not null && record.HasImage ? record : null);

                accumulator = new SeriesAccumulator
                {
                    Id = representative,
                    DisplayTitle = displayTitle,
                    CanonicalTitle = canonicalTitle,
                    MetadataSource = record?.Source,
                    LookupStatus = record?.LookupStatus,
                    LastLookupUtc = record?.LastLookupUtc,
                    Aliases = new List<string>(),
                    HasExternalImage = imageRecord is not null,
                    ImageNormalizedKey = imageRecord?.NormalizedKey ?? record?.NormalizedKey,
                    Synopsis = record?.Synopsis
                };

                if (record is not null)
                {
                    AddAliasIfNew(accumulator, record.Aliases, canonicalTitle);
                    AddAliasIfNew(accumulator, record.UserAliases, canonicalTitle);
                }

                groups[representative] = accumulator;
            }

            if (!string.Equals(groupingTitle, canonicalTitle, StringComparison.OrdinalIgnoreCase)
                && accumulator.Aliases.All(alias => !string.Equals(alias, groupingTitle, StringComparison.OrdinalIgnoreCase)))
            {
                accumulator.Aliases.Add(groupingTitle);
            }

            foreach (var alias in entry.MetadataAliases)
            {
                if (accumulator.Aliases.All(existing => !string.Equals(existing, alias, StringComparison.OrdinalIgnoreCase))
                    && !string.Equals(alias, canonicalTitle, StringComparison.OrdinalIgnoreCase))
                {
                    accumulator.Aliases.Add(alias);
                }
            }

            accumulator.TotalSize += file.FileSize;
            accumulator.LatestModified = accumulator.LatestModified < file.LastModified
                ? file.LastModified
                : accumulator.LatestModified;
            if (file.CreatedAt < accumulator.EarliestCreatedAt)
            {
                accumulator.EarliestCreatedAt = file.CreatedAt;
            }
            if (file.CreatedAt > accumulator.LatestCreatedAt)
            {
                accumulator.LatestCreatedAt = file.CreatedAt;
            }

            accumulator.Issues.Add(new SeriesIssueDto
            {
                FilePath = file.FilePath,
                FileName = file.FileName,
                Title = metadata.Title,
                Issue = metadata.Issue,
                Volume = metadata.Volume,
                Publisher = metadata.Publisher,
                Year = metadata.Year,
                Size = file.FileSize,
                Modified = ToUnixTime(file.LastModified),
                Processed = file.IsProcessed,
                Renamed = file.IsRenamed,
                Normalized = file.IsNormalized,
                Duplicate = file.IsDuplicate,
                Read = file.IsRead
            });
        }

        // Final safety net: collapse any accumulators that ended up with the
        // same canonical title (case-insensitive, after normalization). This
        // handles the case where two cache records share a canonical title but
        // have different NormalizedKeys — for example when the user matched
        // two distinct source folders to the same external series. Without
        // this pass the series view renders two cards labelled identically,
        // which is what users perceive as "duplicate series".
        return CollapseDuplicateAccumulators(groups);
    }

    /// <summary>
    /// Merges accumulators whose normalized canonical title is identical (and
    /// non-ambiguous) into a single series card. The primary survivor is
    /// chosen by issue count (most files wins), then by lookup-status rank,
    /// then by most recent lookup. Issues, sizes, aliases, and the latest
    /// modified timestamp are merged into the survivor; the secondary
    /// accumulators are dropped. The survivor's id is preserved so its
    /// series-detail URL stays stable across reloads.
    /// </summary>
    private Dictionary<string, SeriesAccumulator> CollapseDuplicateAccumulators(
        Dictionary<string, SeriesAccumulator> groups)
    {
        if (groups.Count < 2)
        {
            return groups;
        }

        var byCanonical = new Dictionary<string, List<SeriesAccumulator>>(StringComparer.OrdinalIgnoreCase);
        foreach (var accumulator in groups.Values)
        {
            // Prefer the canonical title (source-of-truth) for de-dup; fall
            // back to the display title when canonical is missing.
            var titleForDedup = !string.IsNullOrWhiteSpace(accumulator.CanonicalTitle)
                ? accumulator.CanonicalTitle
                : accumulator.DisplayTitle;
            var key = NormalizeKey(titleForDedup);
            if (IsAmbiguousNormalizedKey(key))
            {
                // Don't merge on degenerate keys (e.g. "unknown-series" or
                // digit-only normalized titles); that would risk false
                // positives across unrelated series.
                continue;
            }

            if (!byCanonical.TryGetValue(key, out var bucket))
            {
                bucket = new List<SeriesAccumulator>();
                byCanonical[key] = bucket;
            }
            bucket.Add(accumulator);
        }

        var dropped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var bucket in byCanonical.Values)
        {
            if (bucket.Count < 2)
            {
                continue;
            }

            // Pick the survivor: most issues wins, then highest lookup-status
            // rank, then most recent lookup. Ties break by the existing Id so
            // the choice is deterministic across requests.
            var survivor = bucket
                .OrderByDescending(a => a.Issues.Count)
                .ThenByDescending(a => RankLookupStatus(a.LookupStatus))
                .ThenByDescending(a => a.LastLookupUtc ?? DateTime.MinValue)
                .ThenBy(a => a.Id, StringComparer.OrdinalIgnoreCase)
                .First();

            foreach (var other in bucket)
            {
                if (ReferenceEquals(other, survivor))
                {
                    continue;
                }

                // Merge issues (de-dup by file path so a file that somehow
                // ended up in both groups is not counted twice).
                foreach (var issue in other.Issues)
                {
                    if (string.IsNullOrEmpty(issue.FilePath)
                        || !survivor.Issues.Any(existing => string.Equals(existing.FilePath, issue.FilePath, StringComparison.OrdinalIgnoreCase)))
                    {
                        survivor.Issues.Add(issue);
                    }
                }

                survivor.TotalSize += other.TotalSize;
                if (survivor.LatestModified < other.LatestModified)
                {
                    survivor.LatestModified = other.LatestModified;
                }

                // Merge aliases — include the dropped group's display /
                // canonical title as an alias when it differs, so the user
                // still sees where the merged folders came from.
                AddAliasIfNew(survivor, other.Aliases, survivor.CanonicalTitle);
                if (!string.IsNullOrWhiteSpace(other.CanonicalTitle))
                {
                    AddAliasIfNew(survivor, new[] { other.CanonicalTitle }, survivor.CanonicalTitle);
                }
                if (!string.IsNullOrWhiteSpace(other.DisplayTitle))
                {
                    AddAliasIfNew(survivor, new[] { other.DisplayTitle }, survivor.CanonicalTitle);
                }

                // Promote any external image / metadata source if the
                // survivor doesn't already have one.
                if (!survivor.HasExternalImage && other.HasExternalImage)
                {
                    survivor.HasExternalImage = true;
                    survivor.ImageNormalizedKey = other.ImageNormalizedKey;
                }
                if (string.IsNullOrWhiteSpace(survivor.MetadataSource)
                    && !string.IsNullOrWhiteSpace(other.MetadataSource))
                {
                    survivor.MetadataSource = other.MetadataSource;
                }

                dropped.Add(other.Id);

                _logger.LogDebug(
                    "Collapsed duplicate series card '{DroppedTitle}' (id={DroppedId}, {DroppedCount} issues) into '{SurvivorTitle}' (id={SurvivorId}) — same canonical title",
                    LoggingHelper.SanitizeForLog(other.DisplayTitle),
                    LoggingHelper.SanitizeForLog(other.Id),
                    other.Issues.Count,
                    LoggingHelper.SanitizeForLog(survivor.DisplayTitle),
                    LoggingHelper.SanitizeForLog(survivor.Id));
            }
        }

        if (dropped.Count == 0)
        {
            return groups;
        }

        var collapsed = new Dictionary<string, SeriesAccumulator>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in groups)
        {
            if (!dropped.Contains(kvp.Key))
            {
                collapsed[kvp.Key] = kvp.Value;
            }
        }
        return collapsed;
    }

    /// <summary>
    /// Builds a placeholder <see cref="SeriesMetadata"/> from the file name +
    /// folder when no DB-cached metadata is present and we don't want to open
    /// the archive on disk.
    /// </summary>
    private static SeriesMetadata BuildSeriesMetadataFromFileName(ComicFile file)
    {
        return new SeriesMetadata
        {
            Series = Path.GetFileName(file.Directory ?? Path.GetDirectoryName(file.FilePath) ?? string.Empty),
            SeriesGroup = Path.GetFileName(Path.GetDirectoryName(file.FilePath) ?? string.Empty)
        };
    }

    private static List<SeriesIssueDto> SortIssues(IEnumerable<SeriesIssueDto> issues)
    {
        return issues
            .OrderBy(issue => ExtractIssueSortKey(ResolveIssueNumber(issue)), new NaturalStringComparer())
            .ThenBy(issue => issue.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> NormalizeAliases(IEnumerable<string> aliases)
    {
        return aliases
            .Where(alias => !string.IsNullOrWhiteSpace(alias))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(alias => alias, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<T> SortSeries<T>(
        List<T> source,
        string? sort,
        string? direction,
        Func<T, string> keyTitle,
        Func<T, long> keyDate,
        Func<T, long> keySize)
    {
        return (sort?.ToLowerInvariant(), direction?.ToLowerInvariant()) switch
        {
            ("date", "desc") => source.OrderByDescending(keyDate).ToList(),
            ("date", "asc") => source.OrderBy(keyDate).ToList(),
            ("size", "desc") => source.OrderByDescending(keySize).ToList(),
            ("size", "asc") => source.OrderBy(keySize).ToList(),
            ("name", "desc") => source.OrderByDescending(keyTitle, StringComparer.OrdinalIgnoreCase).ToList(),
            _ => source.OrderBy(keyTitle, StringComparer.OrdinalIgnoreCase).ToList()
        };
    }

    private static SeriesMetadata? BuildSeriesMetadataFromCache(ComicFile file)
    {
        var cached = file.Metadata;
        if (cached is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(cached.Series)
            && string.IsNullOrWhiteSpace(cached.Title)
            && string.IsNullOrWhiteSpace(cached.Issue)
            && string.IsNullOrWhiteSpace(cached.Volume)
            && string.IsNullOrWhiteSpace(cached.Publisher)
            && cached.Year is null)
        {
            return null;
        }

        return new SeriesMetadata
        {
            Series = cached.Series,
            Title = cached.Title,
            Issue = cached.Issue,
            Volume = cached.Volume,
            Publisher = cached.Publisher,
            Year = cached.Year
        };
    }

    private static string ResolveGroupingTitle(SeriesMetadata metadata, ComicFile file)
    {
        return FirstNonEmpty(
                ResolveFolderTitle(file),
                metadata.SeriesGroup,
                metadata.Series,
                metadata.AlternateSeries,
                Path.GetFileNameWithoutExtension(file.FileName))
            ?? "Unknown Series";
    }

    private static List<string> ResolveMetadataAliases(SeriesMetadata metadata, string groupingTitle)
    {
        return new[]
            {
                metadata.SeriesGroup,
                metadata.Series,
                metadata.AlternateSeries
            }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Where(value => !string.Equals(value, groupingTitle, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? ResolveFolderTitle(ComicFile file)
        => FirstNonEmpty(
            Path.GetFileName(file.Directory),
            Path.GetFileName(Path.GetDirectoryName(file.FilePath) ?? string.Empty));

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string NormalizeKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown-series";
        }

        var normalized = SeriesKeySanitizer.Replace(value.ToLowerInvariant(), "-").Trim('-');
        return string.IsNullOrWhiteSpace(normalized) ? "unknown-series" : normalized;
    }

    /// <summary>
    /// Returns true if a normalized series key carries no real identifying
    /// signal and therefore must not be used to bridge two different cache
    /// records. A key is ambiguous when it is empty, "unknown-series", shorter
    /// than two chars, or contains no letters at all (i.e. consists only of
    /// digits/dashes). With the Unicode-aware sanitizer, "letters" here means
    /// any Unicode letter, so a genuine CJK alias like "怪獣8号" survives as a
    /// non-ambiguous key while the digit-only collapse "8" still gets rejected
    /// as a cross-record bridge.
    /// </summary>
    private static bool IsAmbiguousNormalizedKey(string? normalized)
    {
        if (string.IsNullOrEmpty(normalized)
            || normalized.Length < 2
            || string.Equals(normalized, "unknown-series", StringComparison.Ordinal))
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

    private static bool Contains(string? value, string search)
        => !string.IsNullOrWhiteSpace(value) && value.Contains(search, StringComparison.OrdinalIgnoreCase);

    private static long ToUnixTime(DateTime value)
        => value.Kind == DateTimeKind.Utc
            ? new DateTimeOffset(value, TimeSpan.Zero).ToUnixTimeSeconds()
            : new DateTimeOffset(value).ToUnixTimeSeconds();

    private static string ExtractIssueSortKey(string? issue)
        => string.IsNullOrWhiteSpace(issue) ? "~" : issue;

    /// <summary>
    /// Returns the issue number for a DTO, falling back to a cheap, in-memory
    /// parse of the file name when the cached metadata lacks one. This keeps
    /// the issue badge, ordering, and missing-issue grid populated for every
    /// file in a series — including series with more than
    /// <see cref="AppSettings.SeriesIssuesMaxArchiveUpgrades"/> files, where the
    /// best-effort archive-upgrade loop would otherwise stop reading titles
    /// from disk and leave later issues' overlays blank.
    /// </summary>
    private static string? ResolveIssueNumber(SeriesIssueDto issue)
    {
        if (!string.IsNullOrWhiteSpace(issue.Issue))
        {
            return issue.Issue;
        }

        return string.IsNullOrWhiteSpace(issue.FileName)
            ? null
            : ComicFileProcessor.ParseChapterNumber(issue.FileName);
    }

    private static void AddAliasIfNew(SeriesAccumulator accumulator, IEnumerable<string> aliases, string canonicalTitle)
    {
        foreach (var alias in aliases)
        {
            if (string.IsNullOrWhiteSpace(alias)) continue;
            if (string.Equals(alias, canonicalTitle, StringComparison.OrdinalIgnoreCase)) continue;
            if (accumulator.Aliases.Any(existing => string.Equals(existing, alias, StringComparison.OrdinalIgnoreCase))) continue;
            accumulator.Aliases.Add(alias);
        }
    }

    /// <summary>
    /// Maps every alias (canonical + user aliases + provider aliases) of every
    /// cache record to the record's normalized key. Population is intent-prioritized
    /// in three passes so a weaker signal cannot overwrite a stronger one:
    ///   1. Canonical titles (and the record's own normalized key) — strongest claim.
    ///   2. User-supplied aliases — explicit user intent.
    ///   3. Provider-supplied aliases — weakest, often noisy/incorrect.
    /// Within each pass we use TryAdd so the first-seen record wins. This prevents
    /// e.g. record A's provider alias from hijacking record B's canonical title and
    /// causing the two unrelated series to be merged downstream.
    /// </summary>
    private static Dictionary<string, string> BuildAliasIndex(IReadOnlyList<SeriesMetadataCacheRecord> records)
    {
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Pass 1: canonical titles and record keys.
        foreach (var record in records)
        {
            // The record's own normalized key is always addressable so the
            // record can still group its own files; we deliberately do not
            // filter it here even if it happens to be degenerate.
            index.TryAdd(record.NormalizedKey, record.NormalizedKey);
            if (!string.IsNullOrWhiteSpace(record.CanonicalTitle))
            {
                var canonicalKey = NormalizeKey(record.CanonicalTitle);
                // Skip degenerate canonical-title keys (e.g. CJK-only titles
                // that collapse to a bare digit) so they cannot bridge to
                // another record whose alias also reduces to the same digit.
                if (!IsAmbiguousNormalizedKey(canonicalKey))
                {
                    index.TryAdd(canonicalKey, record.NormalizedKey);
                }
            }
        }

        // Pass 2: user aliases (TryAdd — never overwrite a canonical claim from pass 1).
        foreach (var record in records)
        {
            foreach (var alias in record.UserAliases ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(alias)) continue;
                var aliasKey = NormalizeKey(alias);
                if (IsAmbiguousNormalizedKey(aliasKey)) continue;
                index.TryAdd(aliasKey, record.NormalizedKey);
            }
        }

        // Pass 3: provider aliases (TryAdd — weakest signal, must not overwrite anything).
        foreach (var record in records)
        {
            foreach (var alias in record.Aliases ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(alias)) continue;
                var aliasKey = NormalizeKey(alias);
                if (IsAmbiguousNormalizedKey(aliasKey)) continue;
                index.TryAdd(aliasKey, record.NormalizedKey);
            }
        }

        return index;
    }

    private static IEnumerable<string> EnumerateAllRecordTitles(SeriesMetadataCacheRecord record)
    {
        // Yield canonical first, then user aliases, then provider aliases. Combined
        // with TryAdd-based BuildAliasIndex, this preserves intent priority anywhere
        // we iterate titles in declaration order.
        if (!string.IsNullOrWhiteSpace(record.CanonicalTitle)) yield return record.CanonicalTitle;
        foreach (var alias in record.UserAliases ?? Enumerable.Empty<string>())
        {
            if (!string.IsNullOrWhiteSpace(alias)) yield return alias;
        }
        foreach (var alias in record.Aliases ?? Enumerable.Empty<string>())
        {
            if (!string.IsNullOrWhiteSpace(alias)) yield return alias;
        }
    }

    private static void UnionWithCacheKey(UnionFind<string> unionFind, string fileKey, string title, IReadOnlyDictionary<string, string> aliasIndex)
    {
        if (string.IsNullOrWhiteSpace(title)) return;
        var titleKey = NormalizeKey(title);
        unionFind.Add(titleKey);
        unionFind.Union(fileKey, titleKey);
        // Only use the alias index to bridge to a cache record when the title
        // key is not degenerate. Otherwise a CJK-only file title that collapses
        // to a bare digit could resolve to whichever record happened to seed
        // that digit in the index, falsely merging unrelated series.
        if (!IsAmbiguousNormalizedKey(titleKey) && aliasIndex.TryGetValue(titleKey, out var recordKey))
        {
            unionFind.Add(recordKey);
            unionFind.Union(fileKey, recordKey);
        }
    }

    private static SeriesMetadataCacheRecord? ResolveRecordForGroup(
        string representative,
        IReadOnlyList<SeriesMetadataCacheRecord> records,
        UnionFind<string> unionFind)
    {
        // A single series-group component may contain multiple cache records
        // (e.g. a folder refresh issues a separate RefreshAsync for the
        // canonical title AND each alias, so several records can end up
        // pointing at the same logical series — one "success" and one or
        // more "not_found" siblings). If we just returned the first record
        // we encountered, a stale "not_found" sibling could win and the
        // series-list badge would render the yellow "?" even though a
        // successful match exists. Pick the most authoritative record
        // instead: positive-match statuses (manual_match / manual / success)
        // outrank not_found / error / cleared, with the most recent lookup
        // winning ties.
        SeriesMetadataCacheRecord? best = null;
        var bestRank = int.MinValue;
        DateTime? bestLookup = null;

        foreach (var record in records)
        {
            if (!unionFind.Contains(record.NormalizedKey)
                || !string.Equals(unionFind.Find(record.NormalizedKey), representative, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var rank = RankLookupStatus(record.LookupStatus);
            if (rank > bestRank
                || (rank == bestRank && IsMoreRecent(record.LastLookupUtc, bestLookup)))
            {
                best = record;
                bestRank = rank;
                bestLookup = record.LastLookupUtc;
            }
        }

        return best;
    }

    /// <summary>
    /// Picks the cache record in a union-find component that actually carries a
    /// cached cover image. A single logical series can span multiple records
    /// (canonical + alias refreshes), and the image may sit on a different
    /// record than the one chosen by <see cref="ResolveRecordForGroup"/> for
    /// its lookup status. User-uploaded images win over downloaded ones, with
    /// the most recently downloaded/uploaded image breaking ties, so the
    /// library card surfaces the same image the Manage Names modal shows.
    /// </summary>
    private static SeriesMetadataCacheRecord? ResolveImageRecordForGroup(
        string representative,
        IReadOnlyList<SeriesMetadataCacheRecord> records,
        UnionFind<string> unionFind)
    {
        SeriesMetadataCacheRecord? best = null;
        var bestIsUser = false;
        DateTime? bestDownloaded = null;

        foreach (var record in records)
        {
            if (!record.HasImage)
            {
                continue;
            }
            if (!unionFind.Contains(record.NormalizedKey)
                || !string.Equals(unionFind.Find(record.NormalizedKey), representative, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var isUser = record.IsUserImage;
            if (best is null
                || (isUser && !bestIsUser)
                || (isUser == bestIsUser && IsMoreRecent(record.ImageDownloadedUtc, bestDownloaded)))
            {
                best = record;
                bestIsUser = isUser;
                bestDownloaded = record.ImageDownloadedUtc;
            }
        }

        return best;
    }
    /// for representing a series. Higher rank wins when multiple cache
    /// records share a union-find component (see <see cref="ResolveRecordForGroup"/>).
    /// Kept in sync with the front-end badge mapping in <c>main.js</c>:
    /// manual_match / manual / success render the green check; everything
    /// else renders the yellow "?" or grey "✕".
    /// </summary>
    private static int RankLookupStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return 0;
        }

        return status.ToLowerInvariant() switch
        {
            "manual_match" => 5,
            "manual" => 4,
            "success" => 3,
            "not_found" => 2,
            "error" => 1,
            "cleared" => 0,
            _ => 0
        };
    }

    private static bool IsMoreRecent(DateTime? candidate, DateTime? incumbent)
    {
        if (candidate is null) return false;
        if (incumbent is null) return true;
        return candidate.Value > incumbent.Value;
    }

    /// <summary>
    /// Splits the inbound filter token into a file-level filter (passed to the
    /// file store) and an optional series-level provider-match filter
    /// (<c>matched</c> or <c>unmatched</c>) applied after grouping.
    /// </summary>
    private static (string? FileFilter, string? SeriesFilter) SplitFilter(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return (null, null);
        }

        if (string.Equals(filter, ProviderMatchedFilter, StringComparison.OrdinalIgnoreCase)
            || string.Equals(filter, ProviderUnmatchedFilter, StringComparison.OrdinalIgnoreCase)
            || string.Equals(filter, MissingIssuesFilter, StringComparison.OrdinalIgnoreCase))
        {
            return (null, filter.ToLowerInvariant());
        }

        return (filter, null);
    }

    /// <summary>
    /// Returns true when the accumulator passes the requested series-level
    /// filter. Series-level filters operate on the grouped series record rather
    /// than on individual files: "matched"/"unmatched" key off provider-match
    /// state, while "missing" keys off gaps in the issue numbering. Unknown or
    /// empty filter values disable the filter.
    /// </summary>
    private static bool MatchesSeriesFilter(SeriesAccumulator accumulator, string? seriesFilter)
    {
        if (string.IsNullOrWhiteSpace(seriesFilter))
        {
            return true;
        }

        if (string.Equals(seriesFilter, MissingIssuesFilter, StringComparison.OrdinalIgnoreCase))
        {
            return HasMissingIssues(accumulator);
        }

        // A series counts as "matched" when EITHER it has a recorded metadata
        // source (a successful provider lookup) OR the user has explicitly
        // matched it (lookup_status of "manual_match" / "manual"). Without the
        // status check, manual matches that don't persist a source value end
        // up filed under the "unmatched" filter and rendered with the yellow
        // "?" badge, which is incorrect.
        var hasMatch = !string.IsNullOrWhiteSpace(accumulator.MetadataSource)
            || string.Equals(accumulator.LookupStatus, "manual_match", StringComparison.OrdinalIgnoreCase)
            || string.Equals(accumulator.LookupStatus, "manual", StringComparison.OrdinalIgnoreCase);
        return seriesFilter switch
        {
            ProviderMatchedFilter => hasMatch,
            ProviderUnmatchedFilter => !hasMatch,
            _ => true
        };
    }

    /// <summary>
    /// Returns true when the series has at least one gap in its consecutive
    /// whole-number issue numbering (e.g. it has issues 1, 2 and 4 but not 3).
    /// Decimal "specials"/half-chapters (e.g. 2.5) count as a found issue for
    /// their whole number (2), so they can fill an otherwise-missing slot.
    /// Non-numeric issue labels are ignored. A series needs at least two
    /// distinct whole-number issues for a gap to be detectable.
    /// </summary>
    private static bool HasMissingIssues(SeriesAccumulator accumulator)
    {
        var wholeIssues = new HashSet<long>();
        foreach (var issue in accumulator.Issues)
        {
            // Summary mode never opens archives on disk, so issue.Issue is often
            // blank for comics that only encode the chapter number in their file
            // name. Resolve it the same way the badge/ordering/missing-issue grid
            // do (parsing the file name as a fallback); otherwise gap detection
            // would see too few whole numbers and wrongly report no missing
            // issues for the entire library.
            var issueNumber = ResolveIssueNumber(issue);
            if (string.IsNullOrWhiteSpace(issueNumber))
            {
                continue;
            }

            if (!double.TryParse(
                    issueNumber.Trim(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var value))
            {
                continue;
            }

            // A decimal special/half-chapter counts as a found issue for its
            // whole number, so floor the value before recording it.
            wholeIssues.Add((long)Math.Floor(value));
        }

        if (wholeIssues.Count < 2)
        {
            return false;
        }

        var min = wholeIssues.Min();
        var max = wholeIssues.Max();
        for (var i = min; i <= max; i++)
        {
            if (!wholeIssues.Contains(i))
            {
                return true;
            }
        }

        return false;
    }

    private static SeriesAccumulator? FindMatchingFilteredGroup(
        Dictionary<string, SeriesAccumulator> filteredGroups,
        SeriesAccumulator reference)
    {
        if (filteredGroups.Count == 0) return null;

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                keys.Add(value.Trim());
            }
        }
        Add(reference.CanonicalTitle);
        Add(reference.DisplayTitle);
        foreach (var alias in reference.Aliases) Add(alias);
        if (keys.Count == 0) return null;

        foreach (var candidate in filteredGroups.Values)
        {
            if (!string.IsNullOrWhiteSpace(candidate.CanonicalTitle) && keys.Contains(candidate.CanonicalTitle))
            {
                return candidate;
            }
            if (!string.IsNullOrWhiteSpace(candidate.DisplayTitle) && keys.Contains(candidate.DisplayTitle))
            {
                return candidate;
            }
            foreach (var alias in candidate.Aliases)
            {
                if (!string.IsNullOrWhiteSpace(alias) && keys.Contains(alias))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private sealed class SeriesAccumulator
    {
        public string Id { get; set; } = string.Empty;
        public string DisplayTitle { get; set; } = string.Empty;
        public string CanonicalTitle { get; set; } = string.Empty;
        public string? MetadataSource { get; set; }
        public string? LookupStatus { get; set; }
        public DateTime? LastLookupUtc { get; set; }
        public List<string> Aliases { get; set; } = new();
        public List<SeriesIssueDto> Issues { get; set; } = new();
        public long TotalSize { get; set; }
        public DateTime LatestModified { get; set; } = DateTime.MinValue;

        /// <summary>Earliest CreatedAt across this series' files (when the series first appeared).</summary>
        public DateTime EarliestCreatedAt { get; set; } = DateTime.MaxValue;

        /// <summary>Latest CreatedAt across this series' files (most recent file added).</summary>
        public DateTime LatestCreatedAt { get; set; } = DateTime.MinValue;

        /// <summary>Plain-text synopsis from the matched external metadata record, if any.</summary>
        public string? Synopsis { get; set; }

        /// <summary>True when a series-level cover image is cached locally.</summary>
        public bool HasExternalImage { get; set; }

        /// <summary>Normalized cache key for the cached image (used to build the API URL).</summary>
        public string? ImageNormalizedKey { get; set; }
    }

    private static string? BuildExternalImageUrl(SeriesAccumulator accumulator)
    {
        if (!accumulator.HasExternalImage || string.IsNullOrEmpty(accumulator.ImageNormalizedKey))
        {
            return null;
        }
        // Image URL is rendered as a relative path so the front-end can prefix
        // it with the configured BasePath via apiUrl(), the same way every
        // other endpoint reference is built.
        return $"/api/series-images/{Uri.EscapeDataString(accumulator.ImageNormalizedKey)}";
    }
}



