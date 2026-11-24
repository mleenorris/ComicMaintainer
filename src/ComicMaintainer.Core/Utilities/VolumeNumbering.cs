using System.Globalization;

namespace ComicMaintainer.Core.Utilities;

/// <summary>
/// Splits a series' issues into runs that are numbered on their own, so issue order and
/// missing-issue gaps are worked out per run:
/// <list type="bullet">
/// <item>Annuals, Specials and Giant-Size issues (see <see cref="IssueFormat"/>) are always runs of their own:
/// Annual #1 is not issue #1, nor Special #1 or Giant-Size #1.</item>
/// <item>Volumes are runs of their own when the numbering restarts with each volume, the way
/// American comics are relaunched (<c>Green Lantern</c> #1 of 1990 and #1 of 2005).</item>
/// </list>
/// </summary>
/// <remarks>
/// A series whose volumes continue one numbering (a manga's tankōbon volumes: chapters 1-10
/// in volume 1, 11-20 in volume 2) is left as one run: there, volume 2 is not missing
/// chapters 1-10. Numbering restarts when the same whole issue number appears in two volumes.
/// </remarks>
public static class VolumeNumbering
{
    /// <summary>
    /// One run's issues, in their original order. <see cref="Volume"/> is the run's volume, or
    /// null when the run is not split by volume or its issues' volume is unknown.
    /// <see cref="Kind"/> is the run's kind of issue (<see cref="IssueFormat.Annual"/>,
    /// <see cref="IssueFormat.Special"/>, <see cref="IssueFormat.GiantSize"/>), or null for the series' regular issues.
    /// </summary>
    public sealed record VolumeGroup<T>(ComicVolume? Volume, IReadOnlyList<T> Items, string? Kind = null)
    {
        /// <summary>True for a run of Annuals.</summary>
        public bool IsAnnual => Kind == IssueFormat.Annual;
    }

    /// <summary>
    /// The issues split into runs (see the class summary): the series' own issues first, then
    /// each kind of issue <paramref name="kindOf"/> names, in <see cref="IssueFormat.Kinds"/>
    /// order (Annuals, Specials, then Giant-Size issues); within each, by volume when the numbering restarts by volume (numbered
    /// volumes, then start years, each ascending; issues of no known volume last). Null when
    /// the series is one run.
    /// </summary>
    public static IReadOnlyList<VolumeGroup<T>>? SeparateRuns<T>(
        IEnumerable<T> items,
        Func<T, ComicVolume?> volumeOf,
        Func<T, string?> issueOf,
        Func<T, string?>? kindOf = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        var all = items.ToList();
        var byKind = all
            .GroupBy(item => kindOf?.Invoke(item))
            .ToDictionary(group => group.Key ?? string.Empty, group => group.ToList());
        if (byKind.Count == 1 && byKind.ContainsKey(string.Empty))
        {
            return ByRestartingVolume(all, volumeOf, issueOf, kind: null);
        }

        var runs = new List<VolumeGroup<T>>();
        var kinds = byKind.Keys
            .OrderBy(kind => kind.Length == 0 ? -1 : KindOrder(kind))
            .ThenBy(kind => kind, StringComparer.OrdinalIgnoreCase);
        foreach (var key in kinds)
        {
            var kind = key.Length == 0 ? null : key;
            var issues = byKind[key];
            runs.AddRange(ByRestartingVolume(issues, volumeOf, issueOf, kind)
                          ?? [new VolumeGroup<T>(null, issues, kind)]);
        }

        return runs;
    }

    private static int KindOrder(string kind)
    {
        for (var i = 0; i < IssueFormat.Kinds.Count; i++)
        {
            if (string.Equals(IssueFormat.Kinds[i], kind, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return IssueFormat.Kinds.Count;
    }

    /// <summary>
    /// The whole number of an issue ("12", "012", "12.5" give 12), or null for blank,
    /// negative or non-numeric issue labels ("Annual").
    /// </summary>
    public static long? WholeIssueNumber(string? issue)
    {
        if (string.IsNullOrWhiteSpace(issue)
            || !double.TryParse(issue.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || value < 0
            || double.IsNaN(value)
            || double.IsInfinity(value))
        {
            return null;
        }

        return (long)Math.Floor(value);
    }

    /// <summary>The issues by volume when their numbering restarts by volume; null for one run.</summary>
    private static List<VolumeGroup<T>>? ByRestartingVolume<T>(
        IEnumerable<T> items,
        Func<T, ComicVolume?> volumeOf,
        Func<T, string?> issueOf,
        string? kind)
    {
        var groups = items
            .GroupBy(volumeOf)
            .Select(group => new VolumeGroup<T>(group.Key, group.ToList(), kind))
            .ToList();

        var known = groups.Where(group => group.Volume is not null).ToList();
        if (known.Count < 2 || !SharesAnIssueNumber(known, issueOf))
        {
            return null;
        }

        return groups
            .OrderBy(group => !group.Volume.HasValue)
            .ThenBy(group => group.Volume.GetValueOrDefault().IsYear)
            .ThenBy(group => group.Volume.GetValueOrDefault().Value)
            .ToList();
    }

    private static bool SharesAnIssueNumber<T>(IEnumerable<VolumeGroup<T>> groups, Func<T, string?> issueOf)
    {
        var seen = new HashSet<long>();
        var numbersOfEachGroup = groups.Select(group => group.Items
            .Select(item => WholeIssueNumber(issueOf(item)))
            .OfType<long>()
            .ToHashSet());
        foreach (var numbers in numbersOfEachGroup)
        {
            if (numbers.Overlaps(seen))
            {
                return true;
            }

            seen.UnionWith(numbers);
        }

        return false;
    }
}
