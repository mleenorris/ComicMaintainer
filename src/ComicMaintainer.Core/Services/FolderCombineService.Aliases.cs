using System.Text.RegularExpressions;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace ComicMaintainer.Core.Services;

public sealed partial class FolderCombineService
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

    private sealed record FolderCombineAliasEntry(string CanonicalKey, string DisplayTitle);

    private static string? BuildFolderCombineSeriesDisplayName(ComicFile file, IReadOnlyDictionary<string, FolderCombineAliasEntry>? aliasIndex = null)
    {
        // When the file's series name maps (via alias) to a known cache record,
        // prefer that record's canonical title so every folder in the group is
        // labelled consistently.
        if (aliasIndex is not null)
        {
            var displayTitle = EnumerateSeriesNameCandidates(file)
                .Select(candidate => FindAliasEntry(aliasIndex, NormalizeFolderCombineKey(candidate))?.DisplayTitle)
                .FirstOrDefault(title => !string.IsNullOrWhiteSpace(title));
            if (displayTitle is not null)
            {
                return displayTitle;
            }
        }

        return FirstNonEmpty(
            file.Metadata?.Series,
            ExtractSeriesNameFromFileName(file),
            Path.GetFileName(file.Directory),
            Path.GetFileName(Path.GetDirectoryName(file.FilePath) ?? string.Empty));
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
            // Skip degenerate keys (e.g. CJK-only titles that collapse to a
            // bare number) so we don't accidentally cross-resolve to an
            // unrelated record whose canonical key happens to share that digit.
            var aliasEntry = EnumerateSeriesNameCandidates(file)
                .Select(NormalizeFolderCombineKey)
                .Where(key => key is not null && !IsAmbiguousNormalizedKey(key))
                .Select(key => FindAliasEntry(aliasIndex, key))
                .FirstOrDefault(entry => entry is not null);
            if (aliasEntry is not null)
            {
                normalizedSeries = aliasEntry.CanonicalKey;
            }
        }

        var volume = file.Metadata?.Volume?.Trim();
        return string.IsNullOrWhiteSpace(volume)
            ? normalizedSeries
            : $"{normalizedSeries}|{volume.ToLowerInvariant()}";
    }

    private static FolderCombineAliasEntry? FindAliasEntry(
        IReadOnlyDictionary<string, FolderCombineAliasEntry> aliasIndex,
        string? key) =>
        key is not null && aliasIndex.TryGetValue(key, out var entry) ? entry : null;

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
    /// preferred display title.
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
        var aliases = (record.Aliases ?? Enumerable.Empty<string>())
            .Concat(record.UserAliases ?? Enumerable.Empty<string>())
            .Where(alias => !string.IsNullOrWhiteSpace(alias));
        foreach (var alias in aliases)
        {
            yield return alias;
        }
    }

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

    /// <summary>
    /// Persists the source-folder series names (and any distinct file-level
    /// series names) as user aliases on the destination's series cache record,
    /// with the destination folder name set as the user-canonical title. This
    /// is best-effort: a cache failure is logged and swallowed so it never
    /// breaks the combine job.
    /// </summary>
    private async Task PersistFolderCombineAliasesAsync(
        FolderCombinePlan plan,
        IReadOnlyDictionary<string, string> preMoveSeriesBySourcePath,
        CancellationToken cancellationToken)
    {
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
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogDebug(ex, "Failed to consult existing canonical title for destination {Destination}",
                    LoggingHelper.SanitizePathForLog(plan.Destination));
            }

            // Build the set of alias candidates from:
            //   1. Each source folder's folder-name-derived series.
            //   2. Any distinct Series values captured from the in-memory
            //      metadata of files that were just moved.
            var candidates = plan.SourceDirectories
                .Where(sourceDir => !string.IsNullOrWhiteSpace(sourceDir))
                .Select(sourceDir => Path.GetFileName(sourceDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))
                .Where(folderName => !string.IsNullOrWhiteSpace(folderName))
                .Select(folderName => ComicFileProcessor.NormalizeSeriesName(folderName, forComparison: false))
                .Where(derived => !string.IsNullOrWhiteSpace(derived))
                .ToList();

            candidates.AddRange(preMoveSeriesBySourcePath.Values
                .Where(series => !string.IsNullOrWhiteSpace(series))
                .Select(series => series.Trim()));

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
                        merged.UnionWith(existingAliases
                            .Where(existing => !string.IsNullOrWhiteSpace(existing)
                                && !string.Equals(existing, canonicalOverride, StringComparison.OrdinalIgnoreCase))
                            .Select(existing => existing.Trim()));
                        filtered = merged.ToList();
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
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
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Failed to persist folder-combine aliases for destination {Destination}",
                LoggingHelper.SanitizePathForLog(plan.Destination));
        }
    }
}
