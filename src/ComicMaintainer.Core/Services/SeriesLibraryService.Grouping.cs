using System.Collections.Concurrent;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace ComicMaintainer.Core.Services;

public partial class SeriesLibraryService
{
    /// <summary>
    /// Loads the file store, resolves grouping titles and external cache info,
    /// and returns a dictionary of series accumulators keyed by their union-find
    /// representative (the series id surfaced to the API).
    /// </summary>
    /// <remarks>
    /// Cache-only builds (<paramref name="allowDiskRead"/> = false) are served
    /// from a <see cref="GroupsSnapshot"/> that is reused until the file store,
    /// read state, metadata cache or relevant settings change. The returned
    /// dictionary and its accumulators are shared between requests and MUST be
    /// treated as read-only by callers.
    /// </remarks>
    private async Task<Dictionary<string, SeriesAccumulator>> BuildGroupsAsync(
        string? filter,
        bool allowDiskRead,
        CancellationToken cancellationToken)
        => (await GetGroupsSnapshotAsync(filter, allowDiskRead, cancellationToken)).Groups;

    /// <summary>
    /// Returns the grouped library for <paramref name="filter"/>, reusing a
    /// cached snapshot when the inputs have not changed since it was built.
    /// </summary>
    private async Task<GroupsSnapshot> GetGroupsSnapshotAsync(
        string? filter,
        bool allowDiskRead,
        CancellationToken cancellationToken)
    {
        // Disk reads make the result depend on archive contents we don't
        // version, and sources that don't report change versions (e.g. test
        // doubles) can't be validated — build fresh in both cases. Snapshots
        // carry per-user read state, so without a user context to key on we
        // can't tell users apart and must not share them either.
        if (allowDiskRead
            || _userContext is null
            || _fileStore is not IChangeVersionSource fileVersions
            || _metadataCache is not IChangeVersionSource metadataVersions)
        {
            return await BuildGroupsSnapshotAsync(filter, allowDiskRead, default, cancellationToken);
        }

        var cacheKey = new GroupsCacheKey(
            string.IsNullOrWhiteSpace(filter) ? null : filter.Trim().ToLowerInvariant(),
            _userContext?.UserId);

        // Capture versions BEFORE reading any inputs: a write that lands while
        // we build bumps the version afterwards, so the stored snapshot is
        // detected as stale on the next request instead of being served forever.
        var versions = CaptureSnapshotVersions(fileVersions, metadataVersions);
        if (TryGetFreshSnapshot(cacheKey, versions, out var cached))
        {
            return cached;
        }

        await _snapshotBuildLock.WaitAsync(cancellationToken);
        try
        {
            // Another request may have rebuilt it while we waited.
            versions = CaptureSnapshotVersions(fileVersions, metadataVersions);
            if (TryGetFreshSnapshot(cacheKey, versions, out cached))
            {
                return cached;
            }

            var snapshot = await BuildGroupsSnapshotAsync(filter, allowDiskRead: false, versions, cancellationToken);

            // Drop snapshots that can never be served again so memory stays
            // bounded to (filters x active users) live entries.
            foreach (var entry in _snapshotCache)
            {
                if (!entry.Value.Versions.Equals(versions))
                {
                    _snapshotCache.TryRemove(entry);
                }
            }
            _snapshotCache[cacheKey] = snapshot;
            return snapshot;
        }
        finally
        {
            _snapshotBuildLock.Release();
        }
    }

    private SnapshotVersions CaptureSnapshotVersions(IChangeVersionSource fileVersions, IChangeVersionSource metadataVersions)
        => new(
            fileVersions.ChangeVersion,
            metadataVersions.ChangeVersion,
            _settings.CurrentValue?.DefaultPreferredLanguage);

    private bool TryGetFreshSnapshot(GroupsCacheKey key, SnapshotVersions versions, out GroupsSnapshot snapshot)
    {
        if (_snapshotCache.TryGetValue(key, out var existing)
            && existing.Versions.Equals(versions)
            && DateTime.UtcNow - existing.BuiltAtUtc < SnapshotMaxAge)
        {
            snapshot = existing;
            return true;
        }

        snapshot = null!;
        return false;
    }

    /// <summary>Drops every cached library snapshot (used by tests and diagnostics).</summary>
    internal void InvalidateSnapshots() => _snapshotCache.Clear();

    private async Task<GroupsSnapshot> BuildGroupsSnapshotAsync(
        string? filter,
        bool allowDiskRead,
        SnapshotVersions versions,
        CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var files = (await _fileStore.GetFilteredFilesAsync(filter, cancellationToken)).ToList();
        var loadedAt = stopwatch.Elapsed;
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
        var imageTitleIndex = BuildImageTitleIndex(cacheRecords);

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

        // Resolve the authoritative record (and image record) for every
        // union-find component once, instead of rescanning every cache record
        // for every file.
        var bestRecordByRepresentative = BuildBestRecordByRepresentative(cacheRecords, unionFind);
        var bestImageRecordByRepresentative = BuildBestImageRecordByRepresentative(cacheRecords, unionFind);
        var displayTitleByRecordKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var groups = new Dictionary<string, SeriesAccumulator>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in fileEntries)
        {
            var file = entry.File;
            var metadata = entry.Metadata;
            var groupingTitle = entry.GroupingTitle;
            var fileKey = groupingKeyByFile[file.FilePath];
            var representative = unionFind.Find(fileKey);

            bestRecordByRepresentative.TryGetValue(representative, out var record);
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
            string displayTitle;
            if (record is null)
            {
                displayTitle = canonicalTitle;
            }
            else if (!displayTitleByRecordKey.TryGetValue(record.NormalizedKey, out displayTitle!))
            {
                displayTitle = (_seriesNameResolver is not null
                    ? _seriesNameResolver.ResolveForRecord(record)
                    : SeriesDisplayTitleResolver.Resolve(record, _settings.CurrentValue.DefaultPreferredLanguage)) ?? string.Empty;
                displayTitleByRecordKey[record.NormalizedKey] = displayTitle;
            }
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
                var imageRecord = bestImageRecordByRepresentative.GetValueOrDefault(representative)
                    ?? (record is not null && record.HasImage ? record : null)
                    // Final fallback: the image may live on a record that the
                    // union-find never bridged to this group because it is only
                    // reachable by a localized title or pinned name (the grouping
                    // follows canonical titles + aliases). Match by the group's
                    // known titles so the card surfaces the same image the
                    // Manage Names modal resolves by title.
                    ?? ResolveImageRecordByTitle(imageTitleIndex, displayTitle, canonicalTitle, groupingTitle);

                accumulator = new SeriesAccumulator
                {
                    // The public series id (used in #/series/{id} deep links and
                    // every id-based API lookup) is derived from the pinned
                    // user-selected name when one exists, so the URL matches the
                    // displayed title instead of an arbitrary alias / localized
                    // (e.g. CJK/Bengali) folder title. Falls back to the
                    // union-find representative when the series is unpinned or
                    // the pinned name produces an ambiguous slug. Lookups still
                    // resolve the old representative-based ids (see
                    // TryResolveGroup) so existing links/bookmarks keep working.
                    Id = ComputePublicSeriesId(record?.SeriesName, representative),
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

            if (!string.Equals(groupingTitle, canonicalTitle, StringComparison.OrdinalIgnoreCase))
            {
                accumulator.TryAddAlias(groupingTitle);
            }

            foreach (var alias in entry.MetadataAliases)
            {
                if (!string.Equals(alias, canonicalTitle, StringComparison.OrdinalIgnoreCase))
                {
                    accumulator.TryAddAlias(alias);
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
        var collapsed = CollapseDuplicateAccumulators(groups);

        // Precompute the per-series values every list/overview request needs,
        // and an index from file path to owning series for reader navigation.
        var ownerByFilePath = new Dictionary<string, SeriesAccumulator>(StringComparer.Ordinal);
        foreach (var accumulator in collapsed.Values)
        {
            accumulator.SortedIssues = SortIssues(accumulator.Issues);
            foreach (var issue in accumulator.Issues)
            {
                if (!string.IsNullOrEmpty(issue.FilePath))
                {
                    ownerByFilePath.TryAdd(issue.FilePath, accumulator);
                }
            }
        }

        stopwatch.Stop();
        _logger.LogDebug(
            "Built series library snapshot: {FileCount} files, {RecordCount} cache records, {SeriesCount} series in {ElapsedMs} ms (file load {LoadMs} ms, filter {Filter})",
            files.Count, cacheRecords.Count, collapsed.Count, stopwatch.ElapsedMilliseconds,
            (long)loadedAt.TotalMilliseconds, LoggingHelper.SanitizeForLog(filter));

        return new GroupsSnapshot(collapsed, groupingKeyByFile, ownerByFilePath, versions, DateTime.UtcNow);
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

        var dropped = new HashSet<SeriesAccumulator>();
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
                var survivorPaths = new HashSet<string>(
                    survivor.Issues.Select(existing => existing.FilePath ?? string.Empty),
                    StringComparer.OrdinalIgnoreCase);
                foreach (var issue in other.Issues)
                {
                    if (string.IsNullOrEmpty(issue.FilePath) || survivorPaths.Add(issue.FilePath))
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

                dropped.Add(other);

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
            if (!dropped.Contains(kvp.Value))
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

    /// <summary>
    /// Resolves a series group by its public id. Tries the dictionary key first
    /// (the union-find representative, which preserves back-compat for existing
    /// alias/representative-derived links and bookmarks), then falls back to a
    /// scan matching the accumulator's <see cref="SeriesAccumulator.Id"/> (the
    /// pinned-name slug). The dictionary remains keyed by representative for
    /// internal grouping, so the pinned id is only discoverable via the scan.
    /// </summary>
    private static bool TryResolveGroup(
        Dictionary<string, SeriesAccumulator> groups,
        string seriesId,
        out SeriesAccumulator accumulator)
    {
        if (groups.TryGetValue(seriesId, out var direct))
        {
            accumulator = direct;
            return true;
        }

        foreach (var candidate in groups.Values)
        {
            if (string.Equals(candidate.Id, seriesId, StringComparison.OrdinalIgnoreCase))
            {
                accumulator = candidate;
                return true;
            }
        }

        accumulator = null!;
        return false;
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

    private readonly record struct GroupsCacheKey(string? Filter, string? UserId);

    private readonly record struct SnapshotVersions(long FileVersion, long MetadataVersion, string? DefaultPreferredLanguage);

    /// <summary>
    /// An immutable (by convention) grouped view of the library for one file
    /// filter and user. Shared across concurrent requests.
    /// </summary>
    private sealed class GroupsSnapshot
    {
        public GroupsSnapshot(
            Dictionary<string, SeriesAccumulator> groups,
            IReadOnlyDictionary<string, string> groupingKeyByFile,
            IReadOnlyDictionary<string, SeriesAccumulator> ownerByFilePath,
            SnapshotVersions versions,
            DateTime builtAtUtc)
        {
            Groups = groups;
            GroupingKeyByFile = groupingKeyByFile;
            OwnerByFilePath = ownerByFilePath;
            Versions = versions;
            BuiltAtUtc = builtAtUtc;
        }

        public Dictionary<string, SeriesAccumulator> Groups { get; }

        /// <summary>Per-file grouping key (normalized title before union-find merges).</summary>
        public IReadOnlyDictionary<string, string> GroupingKeyByFile { get; }

        /// <summary>File path → owning series, for O(1) reader navigation lookups.</summary>
        public IReadOnlyDictionary<string, SeriesAccumulator> OwnerByFilePath { get; }

        public SnapshotVersions Versions { get; }

        public DateTime BuiltAtUtc { get; }

        /// <summary>
        /// Sorted/filtered summary lists keyed by (series filter, search, sort,
        /// direction), so infinite-scroll pages over the same view are a cheap
        /// slice instead of a full re-sort. Lives and dies with the snapshot.
        /// </summary>
        public ConcurrentDictionary<string, IReadOnlyList<SeriesAccumulator>> SummaryViews { get; } = new(StringComparer.Ordinal);
    }

    private sealed class SeriesAccumulator
    {
        private HashSet<string>? _aliasSet;
        private string? _issueSearchText;
        // 0 = not computed, 1 = false, 2 = true. An int (not bool?) so the
        // lazily cached value is written atomically when requests race.
        private int _hasMissingIssues;

        /// <summary>
        /// Adds <paramref name="alias"/> unless an equal alias (case-insensitive)
        /// is already present. Backed by a set so large series don't pay a
        /// linear scan per file.
        /// </summary>
        public bool TryAddAlias(string alias)
        {
            _aliasSet ??= new HashSet<string>(Aliases, StringComparer.OrdinalIgnoreCase);
            if (!_aliasSet.Add(alias))
            {
                return false;
            }
            Aliases.Add(alias);
            return true;
        }

        /// <summary>Issues in display order; populated once the snapshot is built.</summary>
        public List<SeriesIssueDto>? SortedIssues { get; set; }

        /// <summary>Issues in display order, computing them if the snapshot didn't.</summary>
        public IReadOnlyList<SeriesIssueDto> GetSortedIssues() => SortedIssues ??= SortIssues(Issues);

        /// <summary>Concatenated issue titles/numbers/file names used by library search (lazy).</summary>
        public string IssueSearchText => _issueSearchText ??= string.Join(
            "\n", GetSortedIssues().Select(i => $"{i.Title}\n{i.Issue}\n{i.FileName}"));

        /// <summary>Cached result of the "missing" series filter (lazy).</summary>
        public bool HasMissingIssues(Func<SeriesAccumulator, bool> compute)
        {
            var cached = Volatile.Read(ref _hasMissingIssues);
            if (cached == 0)
            {
                cached = compute(this) ? 2 : 1;
                Volatile.Write(ref _hasMissingIssues, cached);
            }
            return cached == 2;
        }

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
}
