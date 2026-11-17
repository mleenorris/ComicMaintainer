using System.Globalization;
using System.Text.RegularExpressions;

namespace ComicMaintainer.Core.Utilities;

/// <summary>
/// Annuals: yearly extra issues published alongside a series (<c>Green Lantern Annual</c> #1).
/// They have their own numbering, so Annual #1 is not issue #1 of the series. ComicVine lists
/// them as a volume of their own named "&lt;series&gt; Annual", and taggers write that name into
/// <c>&lt;Series&gt;</c>; this app keeps them in their series, in a section of their own.
/// </summary>
public static partial class AnnualIssue
{
    private const string Word = "Annual";

    /// <summary>
    /// True when an issue is an Annual: its series name ends in "Annual" (before any volume:
    /// <c>Green Lantern Annual (1992)</c>), its issue number is written as one
    /// (<c>Annual 1</c>), or its file name names one (see <see cref="FromFileName"/>).
    /// </summary>
    public static bool IsAnnual(string? series, string? issue, string? fileName)
        => IsAnnualSeries(series) || IsAnnualIssueText(issue) || FromFileName(fileName);

    /// <summary>True when <paramref name="series"/> ends in the word "Annual", before any trailing volume.</summary>
    public static bool IsAnnualSeries(string? series)
        => !string.IsNullOrWhiteSpace(series) && TrailingAnnualPattern().IsMatch(series.Trim());

    /// <summary>True for an issue number written as an Annual: <c>Annual</c>, <c>Annual 1</c>, <c>Annual #1</c>.</summary>
    public static bool IsAnnualIssueText(string? issue)
        => !string.IsNullOrWhiteSpace(issue) && AnnualIssueTextPattern().IsMatch(issue.Trim());

    /// <summary>
    /// True when a file name names an Annual: the word "Annual" in the series part, before the
    /// issue number (<c>Green Lantern Annual 001 (1992)</c>, <c>Green Lantern (1990) Annual 001</c>,
    /// <c>Green Lantern Annual (1990) - Chapter 0001</c>).
    /// </summary>
    public static bool FromFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        var series = SeriesFromPath.FromFileName(fileName);
        return series is not null && TrailingAnnualPattern().IsMatch(series);
    }

    /// <summary>
    /// True when <paramref name="title"/> names the issue as an Annual: the word "Annual" right
    /// before the issue number (<c>All New X-Men Annual 001 (2015)</c>, <c>v2 .Annual 001</c>,
    /// <c>#Annual 2021</c>, <c>Annual_1</c> for issue 1 or 2021). A title is often all that is
    /// left of the original file name once a file has been renamed, so it is the only place that
    /// still marks the issue as an Annual. "Annual" elsewhere in a story title does not count.
    /// </summary>
    public static bool FromTitle(string? title, string? issue)
    {
        if (string.IsNullOrWhiteSpace(title) || IssueNumber(issue) is not { } expected ||
            !decimal.TryParse(expected, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number))
        {
            return false;
        }

        foreach (Match match in TitleAnnualNumberPattern().Matches(title))
        {
            if (decimal.TryParse(match.Groups["number"].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var named) &&
                named == number)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The number of an issue written as an Annual (<c>Annual 1</c> gives <c>1</c>), or the
    /// issue unchanged when it is not written that way. Null for a bare <c>Annual</c>.
    /// </summary>
    public static string? IssueNumber(string? issue)
    {
        if (string.IsNullOrWhiteSpace(issue))
        {
            return issue;
        }

        var match = AnnualIssueTextPattern().Match(issue.Trim());
        if (!match.Success)
        {
            return issue;
        }

        return match.Groups["number"].Success ? match.Groups["number"].Value : null;
    }

    /// <summary>
    /// <paramref name="series"/> as an Annual's series: "Annual" after the title, before any
    /// trailing volume (<c>Green Lantern (1990)</c> gives <c>Green Lantern Annual (1990)</c>).
    /// Unchanged when it already names an Annual.
    /// </summary>
    public static string WithAnnual(string series)
    {
        if (string.IsNullOrWhiteSpace(series) || IsAnnualSeries(series))
        {
            return series;
        }

        var (title, volume) = SplitTrailingVolume(series.Trim());
        return volume.Length == 0 ? $"{title} {Word}" : $"{title} {Word} {volume}";
    }

    /// <summary>
    /// The series an Annual belongs to: <paramref name="series"/> without its "Annual"
    /// (<c>Green Lantern Annual (1990)</c> gives <c>Green Lantern (1990)</c>). Unchanged when it
    /// does not name an Annual, or when "Annual" is all it holds.
    /// </summary>
    public static string WithoutAnnual(string series)
    {
        if (!IsAnnualSeries(series))
        {
            return series;
        }

        var (title, volume) = SplitTrailingVolume(series.Trim());
        var baseTitle = TrailingAnnualWordPattern().Replace(title, string.Empty).TrimEnd(' ', '-', ':', ',');
        if (!baseTitle.Any(char.IsLetterOrDigit))
        {
            return series;
        }

        return volume.Length == 0 ? baseTitle : $"{baseTitle} {volume}";
    }

    private static (string Title, string Volume) SplitTrailingVolume(string series)
    {
        var volume = TrailingVolumePattern().Match(series);
        return volume.Success && volume.Index > 0
            ? (series[..volume.Index].TrimEnd(), volume.Value.Trim())
            : (series, string.Empty);
    }

    // "... Annual" at the end of a name, before an optional "(1990)", "[1990]", "Vol. 2" or "v2".
    [GeneratedRegex(@"(?<![\p{L}\p{N}])annual\s*(?:[\(\[]\s*\d{4}\s*(?:-\s*(?:\d{4})?\s*)?[\)\]]|(?:vol(?:ume)?\.?\s*|v)\d{1,3})?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex TrailingAnnualPattern();

    // The word "Annual" ending a title.
    [GeneratedRegex(@"(?<![\p{L}\p{N}])annual$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex TrailingAnnualWordPattern();

    // A trailing volume: " (1990)", " [1990]", " (1990-1995)", " Vol. 2", " v2".
    [GeneratedRegex(@"\s*(?:[\(\[]\s*\d{4}\s*(?:-\s*(?:\d{4})?\s*)?[\)\]]|(?<![\p{L}\p{N}])(?:vol(?:ume)?\.?\s*|v)\d{1,3})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex TrailingVolumePattern();

    // "Annual" then the issue number, with spaces, "_", ":", ".", "#" or "-" between: "Annual 001", "Annual_1", "Annual: 01".
    [GeneratedRegex(@"(?<![\p{L}\p{N}])annual[\s_:.#-]*(?<number>\d+(?:\.\d+)?)(?![\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex TitleAnnualNumberPattern();

    // "Annual", "Annual 1", "Annual #1", "Annual 1.5".
    [GeneratedRegex(@"^annual(?:\s*#?\s*(?<number>\d+(?:\.\d+)?))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex AnnualIssueTextPattern();
}
