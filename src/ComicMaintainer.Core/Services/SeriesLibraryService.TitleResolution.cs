using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.Core.Services;

public partial class SeriesLibraryService
{
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

    /// <summary>
    /// Indexes every image-bearing cache record by ALL of its known titles —
    /// canonical title, user-selected name, user aliases, provider aliases, and
    /// provider-supplied localized titles — so a file group can adopt an image
    /// even when the union-find grouping never bridged to that record (the
    /// grouping only follows canonical titles + aliases, so a record reachable
    /// only by a localized title or pinned name is otherwise missed). This
    /// mirrors <c>SeriesMetadataCacheService.GetByTitleAsync</c>, which the
    /// Manage Names modal uses to surface the image, keeping the library card
    /// and the modal in agreement. When several image records share a title
    /// key, a user-uploaded image wins over a downloaded one, with the most
    /// recently downloaded/uploaded image breaking ties. Ambiguous keys (e.g.
    /// digit-only) are skipped so they cannot bridge unrelated series.
    /// </summary>
    private static Dictionary<string, SeriesMetadataCacheRecord> BuildImageTitleIndex(
        IReadOnlyList<SeriesMetadataCacheRecord> records)
    {
        var index = new Dictionary<string, SeriesMetadataCacheRecord>(StringComparer.OrdinalIgnoreCase);

        foreach (var record in records)
        {
            if (!record.HasImage)
            {
                continue;
            }

            foreach (var title in EnumerateImageRecordTitles(record))
            {
                if (string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                var key = NormalizeKey(title);
                if (IsAmbiguousNormalizedKey(key))
                {
                    continue;
                }

                if (!index.TryGetValue(key, out var incumbent)
                    || IsBetterImageRecord(record, incumbent))
                {
                    index[key] = record;
                }
            }
        }

        return index;
    }

    /// <summary>
    /// Enumerates every title an image-bearing record can be addressed by. Kept
    /// in sync with <c>SeriesMetadataCacheService.EntityMatchesTitleKey</c> so
    /// the library card resolves the same image the Manage Names modal shows.
    /// </summary>
    private static IEnumerable<string> EnumerateImageRecordTitles(SeriesMetadataCacheRecord record)
    {
        yield return record.NormalizedKey;
        if (!string.IsNullOrWhiteSpace(record.CanonicalTitle)) yield return record.CanonicalTitle;
        if (!string.IsNullOrWhiteSpace(record.SeriesName)) yield return record.SeriesName!;
        foreach (var alias in record.UserAliases ?? Enumerable.Empty<string>())
        {
            if (!string.IsNullOrWhiteSpace(alias)) yield return alias;
        }
        foreach (var alias in record.Aliases ?? Enumerable.Empty<string>())
        {
            if (!string.IsNullOrWhiteSpace(alias)) yield return alias;
        }
        foreach (var localized in record.LocalizedTitles ?? Enumerable.Empty<LocalizedTitle>())
        {
            if (localized is not null && !string.IsNullOrWhiteSpace(localized.Title)) yield return localized.Title;
        }
    }

    /// <summary>
    /// Ranks one image-bearing record against another, mirroring
    /// <see cref="ResolveImageRecordForGroup"/>: a user-uploaded image wins over
    /// a downloaded one, with the most recently downloaded/uploaded image
    /// breaking ties.
    /// </summary>
    private static bool IsBetterImageRecord(SeriesMetadataCacheRecord candidate, SeriesMetadataCacheRecord incumbent)
    {
        if (candidate.IsUserImage != incumbent.IsUserImage)
        {
            return candidate.IsUserImage;
        }
        return IsMoreRecent(candidate.ImageDownloadedUtc, incumbent.ImageDownloadedUtc);
    }

    /// <summary>
    /// Resolves an image record for a group by matching any of the group's
    /// known titles against <paramref name="imageTitleIndex"/>. Used as a
    /// fallback when no image record sits inside the group's union-find
    /// component. Returns null when no title matches.
    /// </summary>
    private static SeriesMetadataCacheRecord? ResolveImageRecordByTitle(
        IReadOnlyDictionary<string, SeriesMetadataCacheRecord> imageTitleIndex,
        params string?[] titles)
    {
        foreach (var title in titles)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var key = NormalizeKey(title);
            if (IsAmbiguousNormalizedKey(key))
            {
                continue;
            }

            if (imageTitleIndex.TryGetValue(key, out var record))
            {
                return record;
            }
        }

        return null;
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
}
