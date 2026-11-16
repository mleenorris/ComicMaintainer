using System.Globalization;

namespace ComicMaintainer.Core.Utilities;

/// <summary>
/// Splits a series' issues into volumes when its numbering restarts with each volume, the way
/// American comics are relaunched (<c>Green Lantern</c> #1 of 1990 and #1 of 2005). Issue
/// order and missing-issue gaps are then worked out per volume.
/// </summary>
/// <remarks>
/// A series whose volumes continue one numbering (a manga's tankōbon volumes: chapters 1-10
/// in volume 1, 11-20 in volume 2) is left as one run: there, volume 2 is not missing
/// chapters 1-10. Numbering restarts when the same whole issue number appears in two volumes.
/// </remarks>
public static class VolumeNumbering
{
    /// <summary>One volume's issues, in their original order; <see cref="Volume"/> is null for issues of no known volume.</summary>
    public sealed record VolumeGroup<T>(ComicVolume? Volume, IReadOnlyList<T> Items);

    /// <summary>
    /// The issues grouped by volume when the series' numbering restarts by volume (see the
    /// remarks), ordered by volume (numbered volumes, then start years, each ascending; issues
    /// of no known volume last). Null when the series is one run.
    /// </summary>
    public static IReadOnlyList<VolumeGroup<T>>? RestartingGroups<T>(
        IEnumerable<T> items,
        Func<T, ComicVolume?> volumeOf,
        Func<T, string?> issueOf)
    {
        ArgumentNullException.ThrowIfNull(items);
        var groups = items
            .GroupBy(volumeOf)
            .Select(group => new VolumeGroup<T>(group.Key, group.ToList()))
            .ToList();

        var known = groups.Where(group => group.Volume is not null).ToList();
        if (known.Count < 2 || !SharesAnIssueNumber(known, issueOf))
        {
            return null;
        }

        return groups
            .OrderBy(group => group.Volume is null)
            .ThenBy(group => group.Volume?.IsYear)
            .ThenBy(group => group.Volume?.Value)
            .ToList();
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

    private static bool SharesAnIssueNumber<T>(IEnumerable<VolumeGroup<T>> groups, Func<T, string?> issueOf)
    {
        var seen = new HashSet<long>();
        foreach (var group in groups)
        {
            var numbers = group.Items
                .Select(item => WholeIssueNumber(issueOf(item)))
                .OfType<long>()
                .ToHashSet();
            if (numbers.Overlaps(seen))
            {
                return true;
            }

            seen.UnionWith(numbers);
        }

        return false;
    }
}
