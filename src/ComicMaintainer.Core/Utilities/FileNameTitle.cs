using System.Globalization;
using System.Text.RegularExpressions;

namespace ComicMaintainer.Core.Utilities;

/// <summary>
/// A file name left in a comic's <c>&lt;Title&gt;</c>. Some taggers fill an empty title with the
/// file's name (<c>All New X-Men v2 008 (2016)</c>). Once the file has been renamed, that title
/// is the only place left that names the issue's volume and cover year.
/// </summary>
public static partial class FileNameTitle
{
    private const int MinYear = 1900;
    private const int MaxYear = 2199;

    /// <summary>
    /// True when <paramref name="title"/> is a file name rather than a story title: it holds the
    /// issue number (<c>008</c> for issue <c>8</c>), and either starts with the series name
    /// (ignoring punctuation, case and the series' own volume or "Annual", so <c>All New X-Men</c>
    /// matches <c>All-New X-Men</c>) or holds nothing else a story title would: only a volume
    /// token, issue words such as <c>Annual</c> or <c>#</c>, bracketed parts and punctuation.
    /// Taggers often left the name of a file inside a series folder, without the series:
    /// <c>v4 010 (2018)</c>, <c>v1 #1</c>, <c>001 (2015)</c>, <c>z-issue 001 (2016)</c>.
    /// </summary>
    public static bool IsLeftoverFileName(string? title, string? series, string? issue)
        => Classify(title, series, issue) == FileNameKind.ThisIssue;

    /// <summary>
    /// True when <paramref name="title"/> is an old file name, of this issue (see
    /// <see cref="IsLeftoverFileName"/>) or of another: a title holding nothing a story title
    /// would, after any series name (<c>Vol. 2 003 (2023)</c> on issue #2, <c>v4 011 (2018)</c> on
    /// #10). Such a title is a placeholder either way. When it names another issue, the tags are
    /// trusted over it: its cover year belongs to that other issue.
    /// </summary>
    public static bool IsFileName(string? title, string? series, string? issue)
        => Classify(title, series, issue) != FileNameKind.None;

    private enum FileNameKind
    {
        None,
        ThisIssue,
        OtherIssue
    }

    private static FileNameKind Classify(string? title, string? series, string? issue)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(issue))
        {
            return FileNameKind.None;
        }

        // A tie-in's suffix ("001.MU") is compared by its number: the title is still the file's
        // old name when <Number> holds the number alone.
        var parsed = IssueNumberParser.SplitSuffix(IssueNumberParser.Parse(title)) is { } suffixed
            ? suffixed.Number
            : IssueNumberParser.Parse(title);
        var expected = AnnualIssue.IssueNumber(issue);
        if (parsed is null || expected is null)
        {
            return FileNameKind.None;
        }

        var holdsNothingElse = !FileNamePartsPattern().Replace(AfterSeries(title, series), " ").Any(char.IsLetter);
        if (!SameNumber(parsed, expected))
        {
            // A kind word marks this issue ("#Annual 2021" on Annual #8): the title is a label to keep.
            // A bare number ("1602", "2099") is a story title: another issue's file name carries a marker.
            return holdsNothingElse && FileNameMarkerPattern().IsMatch(title) && !KindWordPattern().IsMatch(title)
                ? FileNameKind.OtherIssue
                : FileNameKind.None;
        }

        var seriesKey = string.IsNullOrWhiteSpace(series)
            ? string.Empty
            : Key(AnnualIssue.WithoutAnnual(ComicVolume.WithoutVolume(series)));
        return (seriesKey.Length > 0 && Key(title).StartsWith(seriesKey, StringComparison.Ordinal)) || holdsNothingElse
            ? FileNameKind.ThisIssue
            : FileNameKind.None;
    }

    /// <summary>
    /// What follows the series name and any volume token at the start of
    /// <paramref name="title"/>, without the punctuation in between: <c>All New X-Men .Special – One Down</c>
    /// for series <c>All-New X-Men</c> gives <c>Special – One Down</c>. The series is matched
    /// ignoring punctuation and case, as in <see cref="IsLeftoverFileName"/>. A title that does
    /// not start with the series loses only its leading punctuation (<c>.Special</c> gives
    /// <c>Special</c>).
    /// </summary>
    public static string AfterSeries(string title, string? series)
    {
        ArgumentNullException.ThrowIfNull(title);
        var rest = title;
        var seriesKey = string.IsNullOrWhiteSpace(series)
            ? string.Empty
            : Key(AnnualIssue.WithoutAnnual(ComicVolume.WithoutVolume(series)));
        if (seriesKey.Length > 0 && EndOfPrefix(title, seriesKey) is { } end)
        {
            rest = LeadingVolumePattern().Replace(title[end..], string.Empty);
        }

        return rest.TrimStart(LeadingPunctuation);
    }

    private static readonly char[] LeadingPunctuation = [' ', '\t', '.', '#', '_', ':', '-', '–', '—', '~', ','];

    // Where the letters and digits of `key` end at the start of `text`, punctuation aside; null
    // when `text` does not start with them, or they run on into a longer word ("X-Mens").
    private static int? EndOfPrefix(string text, string key)
    {
        var matched = 0;
        var end = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (!char.IsLetterOrDigit(text[i]))
            {
                continue;
            }

            if (matched == key.Length)
            {
                return i > end ? end : null;
            }

            if (char.ToLowerInvariant(text[i]) != key[matched])
            {
                return null;
            }

            matched++;
            end = i + 1;
        }

        return matched == key.Length ? end : null;
    }

    /// <summary>
    /// The cover year in a file name (without its extension): a bracketed year after the issue
    /// number, with no number after it (<c>Saga 001 (2012)</c>,
    /// <c>All New X-Men v2 008 (2016) (Digital)</c>). A year before the issue number
    /// (<c>Green Lantern (1990) 001</c>) is the volume's start year, not the cover date (see
    /// <see cref="ComicVolume.FromFileName"/>). Null when there is none.
    /// </summary>
    public static int? CoverYear(string? fileNameStem)
    {
        if (string.IsNullOrWhiteSpace(fileNameStem))
        {
            return null;
        }

        var years = BracketedYearPattern().Matches(fileNameStem);
        if (years.Count == 0)
        {
            return null;
        }

        var last = years[^1];
        var after = BracketedPattern().Replace(fileNameStem[(last.Index + last.Length)..], " ");
        if (after.Any(char.IsAsciiDigit) || IssueNumberParser.Parse(fileNameStem[..last.Index]) is null)
        {
            return null;
        }

        return int.TryParse(last.Groups["year"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var year)
               && year is >= MinYear and <= MaxYear
            ? year
            : null;
    }

    // Everything a file name holds besides the series: bracketed parts ("(2018)", "(of 10)"),
    // a volume token ("v4", "Vol. 2"), issue words ("#", "issue", "z-issue", "Annual",
    // "Special", "Giant-Size"), numbers (a tie-in's "1.MU" too) and punctuation.
    [GeneratedRegex(
        @"[\(\[\{][^\)\]\}]*[\)\]\}]" +
        @"|(?<!\p{L})v(?:ol(?:ume)?)?\.?\s*\d+" +
        @"|(?<!\p{L})(?:z-?)?(?:issue|chapter|ch|annual|special|giant(?:[\s-]?size)?)(?!\p{L})" +
        @"|\d+(?:\.\p{L}{1,4}(?![\p{L}\p{N}]))?" +
        @"|[\p{P}\p{S}\s_]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        1000)]
    private static partial Regex FileNamePartsPattern();

    // What marks a number as part of a file name: "#", a bracketed part, a volume token, an issue
    // word, or a leading zero ("003").
    [GeneratedRegex(
        @"#|[\(\[\{]|(?<!\p{L})v(?:ol(?:ume)?)?\.?\s*\d|(?<!\p{L})(?:z-?)?(?:issue|chapter|ch)(?!\p{L})|(?<![\p{L}\p{N}])0\d",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        1000)]
    private static partial Regex FileNameMarkerPattern();

    // A word naming a kind of issue (see IssueFormat): "Annual", "Special", "Giant".
    [GeneratedRegex(@"(?<!\p{L})(?:annual|special|giant)(?!\p{L})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex KindWordPattern();

    private static string Key(string text)
        => new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static bool SameNumber(string left, string right)
        => decimal.TryParse(left, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var a)
           && decimal.TryParse(right, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var b)
            ? a == b
            : string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    // "(2016)", "[2016]", "(2016-2017)".
    [GeneratedRegex(@"[\(\[]\s*(?<year>\d{4})\s*(?:-\s*(?:\d{4})?\s*)?[\)\]]", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex BracketedYearPattern();

    // A volume token right after the series: " v2", " Vol. 2", " (2015)".
    [GeneratedRegex(@"^[\s._-]*(?:v\d{1,3}|vol(?:ume)?\.?\s*\d{1,3}|[\(\[]\d{4}[\)\]])(?![\p{L}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex LeadingVolumePattern();

    // "(...)", "[...]", "{...}".
    [GeneratedRegex(@"\([^)]*\)|\[[^\]]*\]|\{[^}]*\}", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex BracketedPattern();
}
