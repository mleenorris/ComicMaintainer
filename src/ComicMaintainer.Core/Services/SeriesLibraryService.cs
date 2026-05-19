using System.Text.RegularExpressions;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Logging;

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
        var (fileFilter, providerFilter) = SplitFilter(filter);
        var groups = await BuildGroupsAsync(fileFilter, allowDiskRead: true, cancellationToken);

        var groupedSeries = groups.Values
            .Where(accumulator => MatchesProviderFilter(accumulator, providerFilter))
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
        var (fileFilter, providerFilter) = SplitFilter(filter);
        // Summary mode never opens an archive on disk, so even huge libraries
        // stay snappy. Per-issue details are loaded lazily by GetSeriesIssuesAsync.
        var groups = await BuildGroupsAsync(fileFilter, allowDiskRead: false, cancellationToken);

        var summaries = groups.Values
            .Where(accumulator => MatchesProviderFilter(accumulator, providerFilter))
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
                LastLookupUtc = item.Accumulator.LastLookupUtc
            }).ToList(),
            Page = page,
            TotalPages = totalPages,
            TotalSeries = totalSeries,
            Offset = effectiveOffset
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
            return null;
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

        // Upgrade missing titles by opening just the visible archives.
        for (var i = 0; i < pageIssues.Count; i++)
        {
            var issue = pageIssues[i];
            if (!string.IsNullOrWhiteSpace(issue.Title) && !string.IsNullOrWhiteSpace(issue.Issue))
            {
                continue;
            }

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

            if (!groups.TryGetValue(representative, out var accumulator))
            {
                accumulator = new SeriesAccumulator
                {
                    Id = representative,
                    DisplayTitle = canonicalTitle,
                    CanonicalTitle = canonicalTitle,
                    MetadataSource = record?.Source,
                    LookupStatus = record?.LookupStatus,
                    LastLookupUtc = record?.LastLookupUtc,
                    Aliases = new List<string>(),
                    HasExternalImage = record is not null && record.HasImage,
                    ImageNormalizedKey = record?.NormalizedKey
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

        return groups;
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
            .OrderBy(issue => ExtractIssueSortKey(issue.Issue), new NaturalStringComparer())
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

    /// <summary>
    /// Splits the inbound filter token into a file-level filter (passed to the
    /// file store) and an optional series-level provider-match filter
    /// (<c>matched</c> or <c>unmatched</c>) applied after grouping.
    /// </summary>
    private static (string? FileFilter, string? ProviderFilter) SplitFilter(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return (null, null);
        }

        if (string.Equals(filter, ProviderMatchedFilter, StringComparison.OrdinalIgnoreCase)
            || string.Equals(filter, ProviderUnmatchedFilter, StringComparison.OrdinalIgnoreCase))
        {
            return (null, filter.ToLowerInvariant());
        }

        return (filter, null);
    }

    /// <summary>
    /// Returns true when the accumulator passes the requested provider-match
    /// filter. A series is considered "matched" when it has a non-empty
    /// metadata source (i.e. an external provider lookup or a manual entry
    /// has produced data for the series). Unknown providerFilter values
    /// disable the filter.
    /// </summary>
    private static bool MatchesProviderFilter(SeriesAccumulator accumulator, string? providerFilter)
    {
        if (string.IsNullOrWhiteSpace(providerFilter))
        {
            return true;
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
        return providerFilter switch
        {
            ProviderMatchedFilter => hasMatch,
            ProviderUnmatchedFilter => !hasMatch,
            _ => true
        };
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



