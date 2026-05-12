using System.Text.RegularExpressions;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace ComicMaintainer.Core.Services;

public class SeriesLibraryService : ISeriesLibraryService
{
    private static readonly Regex SeriesKeySanitizer = new("[^a-z0-9]+", RegexOptions.Compiled);

    private readonly IFileStoreService _fileStore;
    private readonly IComicProcessorService _processor;
    private readonly ISeriesMetadataCacheService _metadataCache;
    private readonly ILogger<SeriesLibraryService> _logger;

    public SeriesLibraryService(
        IFileStoreService fileStore,
        IComicProcessorService processor,
        ISeriesMetadataCacheService metadataCache,
        ILogger<SeriesLibraryService> logger)
    {
        _fileStore = fileStore;
        _processor = processor;
        _metadataCache = metadataCache;
        _logger = logger;
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
        var files = (await _fileStore.GetFilteredFilesAsync(filter, cancellationToken)).ToList();
        var fileEntries = new List<(ComicFile File, SeriesMetadata Metadata, string GroupingTitle, List<string> MetadataAliases)>(files.Count);

        foreach (var file in files)
        {
            // Reuse metadata already loaded from the database when available so we
            // don't have to open every archive on disk just to render the library.
            var metadata = BuildSeriesMetadataFromCache(file)
                ?? await _processor.GetSeriesMetadataAsync(file.FilePath, cancellationToken)
                ?? new SeriesMetadata();
            var groupingTitle = ResolveGroupingTitle(metadata, file);
            var metadataAliases = ResolveMetadataAliases(metadata, groupingTitle);
            fileEntries.Add((file, metadata, groupingTitle, metadataAliases));
        }

        // Load the persistent metadata cache (external lookups + user aliases) and
        // index it by every known alias so we can merge folders without re-querying
        // external providers on every library load.
        var cacheRecords = await _metadataCache.GetAllAsync(cancellationToken);
        var cacheByKey = cacheRecords.ToDictionary(r => r.NormalizedKey, StringComparer.OrdinalIgnoreCase);
        var aliasIndex = BuildAliasIndex(cacheRecords);

        // Union-find groups: every series-title we encounter (file folder titles +
        // cached canonical/alias titles) becomes a node, then we union nodes that
        // share a record id from the cache. The result is that a user-added alias
        // collapses two folders into a single series card on the next load.
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

        // Also union together any cached records that themselves share aliases.
        foreach (var record in cacheRecords)
        {
            var recordKey = record.NormalizedKey;
            unionFind.Add(recordKey);
            foreach (var alias in EnumerateAllRecordTitles(record))
            {
                UnionWithCacheKey(unionFind, recordKey, alias, aliasIndex);
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

            // Pick the cache record (if any) attached to this representative.
            var record = ResolveRecordForGroup(representative, cacheRecords, unionFind);

            var canonicalTitle = record is null || string.IsNullOrWhiteSpace(record.CanonicalTitle)
                ? groupingTitle
                : record.CanonicalTitle;

            if (!groups.TryGetValue(representative, out var accumulator))
            {
                accumulator = new SeriesAccumulator
                {
                    Id = representative,
                    DisplayTitle = canonicalTitle,
                    CanonicalTitle = canonicalTitle,
                    MetadataSource = record?.Source,
                    Aliases = new List<string>()
                };

                // Seed aliases from the cache record so we surface user-managed
                // alternative names even when none of the underlying files use them.
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

        var groupedSeries = groups.Values
            .Select(accumulator =>
            {
                accumulator.Issues = accumulator.Issues
                    .OrderBy(issue => ExtractIssueSortKey(issue.Issue), new NaturalStringComparer())
                    .ThenBy(issue => issue.FileName, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return new SeriesLibraryDto
                {
                    Id = accumulator.Id,
                    Title = accumulator.DisplayTitle,
                    CanonicalTitle = accumulator.CanonicalTitle,
                    Aliases = accumulator.Aliases
                        .Where(alias => !string.IsNullOrWhiteSpace(alias))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(alias => alias, StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                    MetadataSource = accumulator.MetadataSource,
                    IssueCount = accumulator.Issues.Count,
                    TotalSize = accumulator.TotalSize,
                    LatestModified = ToUnixTime(accumulator.LatestModified),
                    CoverFilePath = accumulator.Issues.FirstOrDefault()?.FilePath ?? string.Empty,
                    Issues = accumulator.Issues
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

        groupedSeries = (sort?.ToLowerInvariant(), direction?.ToLowerInvariant()) switch
        {
            ("date", "desc") => groupedSeries.OrderByDescending(series => series.LatestModified).ToList(),
            ("date", "asc") => groupedSeries.OrderBy(series => series.LatestModified).ToList(),
            ("size", "desc") => groupedSeries.OrderByDescending(series => series.TotalSize).ToList(),
            ("size", "asc") => groupedSeries.OrderBy(series => series.TotalSize).ToList(),
            ("name", "desc") => groupedSeries.OrderByDescending(series => series.Title, StringComparer.OrdinalIgnoreCase).ToList(),
            _ => groupedSeries.OrderBy(series => series.Title, StringComparer.OrdinalIgnoreCase).ToList()
        };

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

    private static bool Contains(string? value, string search)
        => !string.IsNullOrWhiteSpace(value) && value.Contains(search, StringComparison.OrdinalIgnoreCase);

    private static long ToUnixTime(DateTime value)
        => value.Kind == DateTimeKind.Utc
            ? new DateTimeOffset(value, TimeSpan.Zero).ToUnixTimeSeconds()
            : new DateTimeOffset(value).ToUnixTimeSeconds();

    private static string ExtractIssueSortKey(string? issue)
        => string.IsNullOrWhiteSpace(issue) ? "~" : issue;

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
    /// Maps every alias (canonical + provider aliases + user aliases) of every
    /// cache record to the record's normalized key.
    /// </summary>
    private static Dictionary<string, string> BuildAliasIndex(IReadOnlyList<SeriesMetadataCacheRecord> records)
    {
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records)
        {
            foreach (var title in EnumerateAllRecordTitles(record))
            {
                var key = NormalizeKey(title);
                // Last writer wins — that's fine because we union-find afterwards.
                index[key] = record.NormalizedKey;
            }

            index[record.NormalizedKey] = record.NormalizedKey;
        }
        return index;
    }

    private static IEnumerable<string> EnumerateAllRecordTitles(SeriesMetadataCacheRecord record)
    {
        if (!string.IsNullOrWhiteSpace(record.CanonicalTitle)) yield return record.CanonicalTitle;
        foreach (var alias in record.Aliases ?? Enumerable.Empty<string>())
        {
            if (!string.IsNullOrWhiteSpace(alias)) yield return alias;
        }
        foreach (var alias in record.UserAliases ?? Enumerable.Empty<string>())
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
        if (aliasIndex.TryGetValue(titleKey, out var recordKey))
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
        foreach (var record in records)
        {
            if (unionFind.Contains(record.NormalizedKey)
                && string.Equals(unionFind.Find(record.NormalizedKey), representative, StringComparison.OrdinalIgnoreCase))
            {
                return record;
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
        public List<string> Aliases { get; set; } = new();
        public List<SeriesIssueDto> Issues { get; set; } = new();
        public long TotalSize { get; set; }
        public DateTime LatestModified { get; set; } = DateTime.MinValue;
    }
}

/// <summary>
/// Minimal disjoint-set / union-find structure used to merge series-title nodes
/// across files based on shared aliases.
/// </summary>
internal sealed class UnionFind<T> where T : notnull
{
    private readonly Dictionary<T, T> _parent;

    public UnionFind(IEqualityComparer<T> comparer)
    {
        _parent = new Dictionary<T, T>(comparer);
    }

    public bool Contains(T value) => _parent.ContainsKey(value);

    public void Add(T value)
    {
        if (!_parent.ContainsKey(value))
        {
            _parent[value] = value;
        }
    }

    public T Find(T value)
    {
        if (!_parent.TryGetValue(value, out var parent))
        {
            _parent[value] = value;
            return value;
        }

        // Path compression (iterative to avoid stack overflows on pathological chains).
        var root = value;
        while (!_parent[root].Equals(root))
        {
            root = _parent[root];
        }

        var current = value;
        while (!_parent[current].Equals(root))
        {
            var next = _parent[current];
            _parent[current] = root;
            current = next;
        }
        return root;
    }

    public void Union(T a, T b)
    {
        Add(a);
        Add(b);
        var rootA = Find(a);
        var rootB = Find(b);
        if (!rootA.Equals(rootB))
        {
            _parent[rootA] = rootB;
        }
    }
}

