using ComicMaintainer.Core.Models;

namespace ComicMaintainer.Core.Utilities;

/// <summary>
/// The number of a volume known only by its start year, as readers count volumes: Batman (2016)
/// is the third DC volume named "Batman", after those of 1940 and 2011, so it is Vol. 3. Counted
/// from ComicVine's volumes: the ones named exactly like the series, from the publisher of the
/// volume that started that year, that started earlier. One-shots and giveaways published under
/// the same name (a single issue) are not counted. It is a suggestion the user reviews, never
/// written on its own: ComicVine also lists, under the same name, volumes readers do not count
/// (DC's 2012 volumes of a few issues next to each New 52 series), so the user can leave any of
/// the earlier volumes out.
/// </summary>
public static class VolumeOrdinal
{
    /// <summary>
    /// A suggested volume number: <see cref="Match"/> is the volume that started in the year asked
    /// for, and <see cref="Earlier"/> the publisher's earlier volumes of that name, oldest first.
    /// <see cref="Number"/> counts those for which <see cref="CountsByDefault"/> holds, plus one.
    /// </summary>
    public sealed record Suggestion(
        int Number,
        string? Publisher,
        ExternalSeriesMetadata Match,
        IReadOnlyList<ExternalSeriesMetadata> Earlier);

    /// <summary>
    /// The number of the volume of <paramref name="title"/> that started in
    /// <paramref name="startYear"/>, counted among <paramref name="volumes"/>; null when none of
    /// them is named <paramref name="title"/> and started that year.
    /// </summary>
    public static Suggestion? Suggest(IEnumerable<ExternalSeriesMetadata> volumes, string title, int startYear)
    {
        var key = Key(title);
        if (key.Length == 0)
        {
            return null;
        }

        var named = volumes
            .Where(volume => volume.StartYear is not null && Key(volume.CanonicalTitle) == key)
            .ToList();

        // Several volumes can share the name and year (a reprint or a foreign edition): the one
        // with the most issues is the series itself.
        var match = named
            .Where(volume => volume.StartYear == startYear)
            .OrderByDescending(volume => volume.TotalChapters ?? 0)
            .FirstOrDefault();
        if (match is null)
        {
            return null;
        }

        var earlier = named
            .Where(volume => volume.StartYear < startYear
                             && string.Equals(volume.Publisher, match.Publisher, StringComparison.OrdinalIgnoreCase))
            .OrderBy(volume => volume.StartYear)
            .ToList();
        return new Suggestion(earlier.Count(CountsByDefault) + 1, match.Publisher, match, earlier);
    }

    /// <summary>
    /// True for an earlier volume counted unless the user leaves it out: any but a single issue
    /// (a one-shot or giveaway). A volume whose issue count is unknown is counted.
    /// </summary>
    public static bool CountsByDefault(ExternalSeriesMetadata volume) => (volume.TotalChapters ?? 2) > 1;

    /// <summary>
    /// The other way round from <see cref="Suggest"/>: the volume of <paramref name="title"/> that
    /// is number <paramref name="number"/>, counting the publisher's volumes of that name by start
    /// year, one-shots left out. The publisher is that of <paramref name="knownVolumeId"/> (the
    /// series' own ComicVine match) when it is among <paramref name="volumes"/>, otherwise the one
    /// with the most volumes of that name. Null when there are fewer volumes than the number.
    /// </summary>
    /// <remarks>
    /// ComicVine also lists, under the series' name, short volumes readers do not count, such as
    /// a run of collected editions: Marvel's "All-New X-Men" of 2014 has 4 issues, between the
    /// 2013 volume (41) and the 2016 one (19). A volume with fewer issues than
    /// <paramref name="highestIssueHeld"/>, the highest issue number the library has of this
    /// volume, cannot be it, so the next counted volume that can is taken instead.
    /// </remarks>
    public static ExternalSeriesMetadata? Numbered(
        IEnumerable<ExternalSeriesMetadata> volumes,
        string title,
        int number,
        string? knownVolumeId = null,
        int highestIssueHeld = 0)
    {
        var key = Key(title);
        if (number < 1 || key.Length == 0)
        {
            return null;
        }

        var named = volumes
            .Where(volume => volume.StartYear is not null && Key(volume.CanonicalTitle) == key)
            .ToList();
        var known = knownVolumeId is null ? null : named.FirstOrDefault(volume => volume.SourceId == knownVolumeId);
        var publisher = known is not null
            ? known.Publisher
            : named.Where(CountsByDefault)
                .GroupBy(volume => volume.Publisher ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Min(volume => volume.StartYear))
                .FirstOrDefault()?.Key;

        var counted = named
            .Where(volume => string.Equals(volume.Publisher ?? string.Empty, publisher ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                             && CountsByDefault(volume))
            .OrderBy(volume => volume.StartYear)
            .ThenByDescending(volume => volume.TotalChapters ?? 0)
            .ToList();
        return counted
            .Skip(number - 1)
            .FirstOrDefault(volume => volume.TotalChapters is not { } issues || issues >= highestIssueHeld);
    }

    /// <summary>Letters and digits only, lower case, so "All-New X-Men" and "All New X-Men" compare equal.</summary>
    private static string Key(string? text)
        => new((text ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
