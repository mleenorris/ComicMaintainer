using System.Globalization;
using ComicMaintainer.Core.Models;

namespace ComicMaintainer.Core.Utilities;

/// <summary>
/// What the library holds of one series volume, the evidence its ComicVine volume is weighed
/// against (see <see cref="VolumeEvidence"/>).
/// </summary>
/// <param name="Publishers">The publishers its issues name.</param>
/// <param name="EarliestYear">The earliest cover year of its issues; null when none is known.</param>
/// <param name="HighestIssue">Its highest whole issue number; 0 when none is known.</param>
/// <param name="Issues">Its issues: number, cover year (null when unknown) and a real story title (null for a placeholder).</param>
public sealed record VolumeHoldings(
    IReadOnlyCollection<string> Publishers,
    int? EarliestYear,
    int HighestIssue,
    IReadOnlyList<HeldIssue> Issues)
{
    /// <summary>Nothing held, so nothing to weigh.</summary>
    public static VolumeHoldings None { get; } = new([], null, 0, []);
}

/// <summary>One issue the library holds: its number, cover year and real story title.</summary>
public sealed record HeldIssue(string Number, int? Year, string? Title);

/// <summary>A ComicVine issue as listed, to compare with the issues held.</summary>
public sealed record ListedIssue(string Number, string? CoverDate, string? Name);

/// <summary>
/// One ComicVine volume weighed as the series volume's: <see cref="Score"/> sums the evidence, for
/// and against; <see cref="Identity"/> only what names this volume (its start year, its place among
/// the publisher's volumes, the series' own match), which decides whether it fits at all;
/// <see cref="Reasons"/> says what counted, for the user to review.
/// </summary>
public sealed record VolumeCandidate(ExternalSeriesMetadata Volume, double Score, IReadOnlyList<string> Reasons, double Identity = 0);

/// <summary>
/// Weighs the ComicVine volumes named like a series volume against what the library holds of it,
/// so a volume is only taken when the evidence clearly points to one:
/// <list type="bullet">
/// <item>the start year the issues name (the same year, or one off), and among that year's volumes
/// of the name, being the longest (the series itself rather than a reprint or a one-shot);</item>
/// <item>being the series' own ComicVine match;</item>
/// <item>for a volume number, being the publisher's Nth volume of that name (see <see cref="VolumeOrdinal.Numbered"/>);</item>
/// <item>the publisher the issues name;</item>
/// <item>having at least as many issues as the highest one held;</item>
/// <item>having started no later than the earliest cover year held;</item>
/// <item>with the issues listed (<see cref="WithIssues"/>): the cover years and story titles of the issues held.</item>
/// </list>
/// A volume with fewer issues than the highest held, or a start year more than one off, is ruled out.
/// </summary>
public static class VolumeEvidence
{
    /// <summary>How far the best volume must score above the next for it to be taken without review.</summary>
    public const double Margin = 3d;

    /// <summary>
    /// The least <see cref="VolumeCandidate.Identity"/> a volume needs to fit at all: evidence that
    /// names it (its start year, its place among the publisher's volumes, the series' own match),
    /// not only issue counts and dates any volume of the name could have. Evidence against it lowers
    /// its rank, not whether it fits: an issue dated wrongly is caught when it is filled.
    /// </summary>
    public const double MinimumScore = 4d;

    /// <summary>The year some downloaders write when the date is unknown, which says nothing.</summary>
    private const int UnknownYear = 1970;

    /// <summary>
    /// The volumes of <paramref name="volumes"/> named exactly <paramref name="title"/>, weighed as
    /// the series volume of <paramref name="startYear"/> or <paramref name="volumeNumber"/> (one of
    /// them non-zero), best first; volumes ruled out are left out.
    /// </summary>
    public static IReadOnlyList<VolumeCandidate> Rank(
        IEnumerable<ExternalSeriesMetadata> volumes,
        string title,
        int startYear,
        int volumeNumber,
        string? knownVolumeId,
        VolumeHoldings holdings)
    {
        var key = Key(title);
        var named = volumes
            .Where(volume => volume.StartYear is not null && Key(volume.CanonicalTitle) == key)
            .GroupBy(volume => volume.SourceId ?? $"{volume.CanonicalTitle}|{volume.StartYear}")
            .Select(group => group.First())
            .ToList();
        var numbered = volumeNumber > 0
            ? VolumeOrdinal.Numbered(named, title, volumeNumber, knownVolumeId, holdings.HighestIssue)
            : null;
        var ofTheYear = startYear > 0
            ? named.Where(volume => volume.StartYear == startYear).OrderByDescending(volume => volume.TotalChapters ?? 0).ToList()
            : [];
        var longestOfTheYear = ofTheYear.FirstOrDefault();

        // The longest counts for much when the next is far shorter (the series beside its one-shots),
        // little when they are alike (a reprint or foreign edition of the whole run).
        var longestWeight = ofTheYear.Count > 1
                            && (longestOfTheYear!.TotalChapters ?? 0) >= 2 * Math.Max(1, ofTheYear[1].TotalChapters ?? 0)
            ? 3d
            : 1d;

        var ranked = new List<VolumeCandidate>();
        foreach (var volume in named)
        {
            var score = 0d;
            var identity = 0d;
            var reasons = new List<string>();
            var started = volume.StartYear!.Value;
            if (startYear > 0)
            {
                var off = Math.Abs(started - startYear);
                if (off > 1)
                {
                    continue;
                }

                identity += off == 0 ? 4 : 2;
                reasons.Add(off == 0 ? $"started in {started}" : $"started in {started}, a year off {startYear}");
                if (ReferenceEquals(volume, longestOfTheYear) && ofTheYear.Count > 1)
                {
                    identity += longestWeight;
                    reasons.Add($"the longest volume of that name started in {started}");
                }
            }

            if (knownVolumeId is not null && volume.SourceId == knownVolumeId)
            {
                identity += 3;
                reasons.Add("the series' own ComicVine match");
            }

            if (numbered is not null && ReferenceEquals(volume, numbered))
            {
                identity += 4;
                reasons.Add($"the publisher's volume {volumeNumber.ToString(CultureInfo.InvariantCulture)} of that name");
            }

            score += identity;

            if (holdings.Publishers.Count > 0 && !string.IsNullOrWhiteSpace(volume.Publisher))
            {
                if (holdings.Publishers.Any(publisher => SamePublisher(publisher, volume.Publisher)))
                {
                    score += 2;
                    reasons.Add($"published by {volume.Publisher}, as your issues say");
                }
                else
                {
                    score -= 3;
                    reasons.Add($"published by {volume.Publisher}, not {string.Join(" or ", holdings.Publishers)}");
                }
            }

            if (volume.TotalChapters == 1)
            {
                score -= 2;
                reasons.Add("a single issue: a one-shot or giveaway");
            }

            if (volume.TotalChapters is { } count && holdings.HighestIssue > 0)
            {
                if (count < holdings.HighestIssue)
                {
                    continue;
                }

                score += 1;
                reasons.Add($"{count} issues, enough for your #{holdings.HighestIssue}");
            }

            if (holdings.EarliestYear is { } earliest)
            {
                if (started > earliest + 1)
                {
                    score -= 3;
                    reasons.Add($"started in {started}, after your issues of {earliest}");
                }
                else
                {
                    score += 1;
                    reasons.Add($"started by {earliest}, when your issues begin");
                }
            }

            ranked.Add(new VolumeCandidate(volume, score, reasons, identity));
        }

        return Order(ranked);
    }

    /// <summary>
    /// The volume to take from <paramref name="ranked"/>: the best of those that fit (with an
    /// <see cref="VolumeCandidate.Identity"/> of at least <see cref="MinimumScore"/>), when its score
    /// is positive and at least <see cref="Margin"/> above every other volume's, fitting or not: a
    /// volume the evidence names only by its start year, which another outscores on everything
    /// else, is not taken. <c>Ambiguous</c> is true when some volume fits but none is clearly best,
    /// for the user to choose; both null and false when none fits.
    /// </summary>
    public static (VolumeCandidate? Choice, bool Ambiguous) Decide(IReadOnlyList<VolumeCandidate> ranked)
    {
        ArgumentNullException.ThrowIfNull(ranked);
        var best = ranked.Where(candidate => candidate.Identity >= MinimumScore).MaxBy(candidate => candidate.Score);
        if (best is null)
        {
            return (null, false);
        }

        var rival = ranked.Where(candidate => !ReferenceEquals(candidate, best)).Select(candidate => (double?)candidate.Score).Max();
        return best.Score > 0 && (rival is null || best.Score - rival >= Margin) ? (best, false) : (null, true);
    }

    /// <summary>
    /// How many of <paramref name="ranked"/> are about as good as the best fitting volume (within
    /// <see cref="Margin"/> of it), the best included: what a review is between.
    /// </summary>
    public static int CloseCalls(IReadOnlyList<VolumeCandidate> ranked)
    {
        ArgumentNullException.ThrowIfNull(ranked);
        var best = ranked.Where(candidate => candidate.Identity >= MinimumScore).MaxBy(candidate => candidate.Score);
        return best is null ? 0 : ranked.Count(candidate => best.Score - candidate.Score < Margin);
    }

    /// <summary>
    /// <paramref name="candidate"/> weighed again with its listed issues: the cover years of the
    /// issues held that ComicVine lists under the same number (agreeing within a year), and their
    /// story titles. Only issues with a known cover year count, at least two of them.
    /// </summary>
    public static VolumeCandidate WithIssues(VolumeCandidate candidate, IEnumerable<ListedIssue> listed, VolumeHoldings holdings)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var issues = listed.ToList();
        var score = candidate.Score;
        var reasons = candidate.Reasons.ToList();
        var compared = 0;
        var agreeing = 0;
        var titles = 0;
        foreach (var held in holdings.Issues)
        {
            var match = issues.FirstOrDefault(issue => IssueNumberParser.AreEquivalent(issue.Number, held.Number));
            if (match is null)
            {
                continue;
            }

            if (held.Year is { } year && CoverYear(match.CoverDate) is { } coverYear)
            {
                compared++;
                agreeing += Math.Abs(coverYear - year) <= 1 ? 1 : 0;
            }

            if (held.Title is { } title && match.Name is { } name && Key(title) == Key(name) && Key(title).Length > 0)
            {
                titles++;
            }
        }

        if (compared >= 2)
        {
            var share = (double)agreeing / compared;
            if (share >= 0.8)
            {
                score += 4;
                reasons.Add($"the cover years of {agreeing} of {compared} of your issues match");
            }
            else if (share <= 0.3)
            {
                score -= 4;
                reasons.Add($"the cover years of only {agreeing} of {compared} of your issues match");
            }
        }

        if (titles > 0)
        {
            score += Math.Min(titles, 3);
            reasons.Add(titles == 1 ? "a story title matches" : $"{titles} story titles match");
        }

        return candidate with { Score = score, Reasons = reasons };
    }

    /// <summary>Best first; on a tie the earlier volume, then the one with more issues.</summary>
    public static IReadOnlyList<VolumeCandidate> Order(IEnumerable<VolumeCandidate> candidates)
        => candidates
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Volume.StartYear)
            .ThenByDescending(candidate => candidate.Volume.TotalChapters ?? 0)
            .ToList();

    /// <summary>A cover year that says something: from 1900 on, and not the 1970 of an unknown date.</summary>
    public static int? KnownYear(int? year) => year is >= 1900 and < 2200 and not UnknownYear ? year : null;

    /// <summary>The year of a ComicVine cover date (<c>2016-08-01</c>); null when absent.</summary>
    public static int? CoverYear(string? coverDate)
        => coverDate is { Length: >= 4 } && int.TryParse(coverDate.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year)
            ? KnownYear(year)
            : null;

    private static bool SamePublisher(string held, string candidate)
    {
        var a = PublisherKey(held);
        var b = PublisherKey(candidate);
        return a.Length > 0 && b.Length > 0 && (a == b || a.StartsWith(b, StringComparison.Ordinal) || b.StartsWith(a, StringComparison.Ordinal));
    }

    private static readonly string[] PublisherSuffixes = ["comics", "comic", "publishing", "entertainment", "inc", "llc", "ltd"];

    // "DC Comics", "DC" and "D.C. Comics, Inc." compare equal.
    private static string PublisherKey(string publisher)
    {
        var key = Key(publisher);
        string before;
        do
        {
            before = key;
            foreach (var suffix in PublisherSuffixes)
            {
                if (key.EndsWith(suffix, StringComparison.Ordinal) && key.Length > suffix.Length)
                {
                    key = key[..^suffix.Length];
                }
            }
        }
        while (key != before);

        return key;
    }

    private static string Key(string? text)
        => new((text ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
