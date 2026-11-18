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
        => IsLeftoverFileName(title, [series], issue);

    /// <summary>
    /// <see cref="IsLeftoverFileName(string?, string?, string?)"/> for a series known by several
    /// names: its own, the name normalize resolves it to, and its ComicVine and user aliases. A
    /// file name may start with any of them (<c>ASM 003</c> for <c>The Amazing Spider-Man</c>
    /// with that alias), or with a shortened one (see <see cref="IsFileName(string?, IReadOnlyCollection{string?}, string?)"/>).
    /// </summary>
    public static bool IsLeftoverFileName(string? title, IReadOnlyCollection<string?> seriesNames, string? issue)
        => Classify(title, seriesNames, issue) == FileNameKind.ThisIssue;

    /// <summary>
    /// True when <paramref name="title"/> is an old file name, of this issue (see
    /// <see cref="IsLeftoverFileName"/>) or of another: a title holding nothing a story title
    /// would, after any series name (<c>Vol. 2 003 (2023)</c> on issue #2, <c>v4 011 (2018)</c> on
    /// #10). Such a title is a placeholder either way. When it names another issue, the tags are
    /// trusted over it: its cover year belongs to that other issue.
    /// </summary>
    public static bool IsFileName(string? title, string? series, string? issue)
        => IsFileName(title, [series], issue);

    /// <summary>
    /// <see cref="IsFileName(string?, string?, string?)"/> for a series known by several names
    /// (see <see cref="IsLeftoverFileName(string?, IReadOnlyCollection{string?}, string?)"/>). A
    /// name may also be shortened: a title whose leading words all appear, in order, in one of the
    /// names, followed by nothing a story title would, is a file name (<c>Soulfire Vol 1 03 (of 10)</c>
    /// for <c>Michael Turner's Soulfire</c>, <c>Batman 003</c> for <c>Batman: The Dark Knight</c>).
    /// </summary>
    public static bool IsFileName(string? title, IReadOnlyCollection<string?> seriesNames, string? issue)
        => Classify(title, seriesNames, issue) != FileNameKind.None;

    private enum FileNameKind
    {
        None,
        ThisIssue,
        OtherIssue
    }

    private static FileNameKind Classify(string? title, IReadOnlyCollection<string?> seriesNames, string? issue)
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

        var names = seriesNames.Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name!).ToList();
        var holdsNothingElse = HoldsNothingElse(AfterSeries(title, null))
                               || names.Any(name => HoldsNothingElse(AfterSeries(title, name)))
                               || names.Any(name => StartsWithShortenedName(title, name));
        if (!SameNumber(parsed, expected))
        {
            // A kind word marks this issue ("#Annual 2021" on Annual #8): the title is a label to keep.
            return holdsNothingElse && !KindWordPattern().IsMatch(title) ? FileNameKind.OtherIssue : FileNameKind.None;
        }

        var titleKey = Key(title);
        var startsWithSeries = names
            .Select(name => Key(AnnualIssue.WithoutAnnual(ComicVolume.WithoutVolume(name))))
            .Any(key => key.Length > 0 && titleKey.StartsWith(key, StringComparison.Ordinal));
        return startsWithSeries || holdsNothingElse ? FileNameKind.ThisIssue : FileNameKind.None;
    }

    // True when `text` holds nothing a story title would: no letters once the volume, issue
    // words, numbers, bracketed parts and punctuation are taken out.
    private static bool HoldsNothingElse(string text)
        => !FileNamePartsPattern().Replace(text, " ").Any(char.IsLetter);

    // Words a shortened name may not consist of alone: they name nothing.
    private static readonly HashSet<string> NamelessWords = new(StringComparer.Ordinal) { "the", "a", "an", "of", "and" };

    // True when `title` starts with some of `name`'s words, in their order, and holds nothing else
    // after them (see HoldsNothingElse): "Soulfire Vol 1 03" for "Michael Turner's Soulfire".
    private static bool StartsWithShortenedName(string title, string name)
    {
        var nameWords = Words(AnnualIssue.WithoutAnnual(ComicVolume.WithoutVolume(name))).Select(word => word.Text).ToList();
        if (nameWords.Count == 0)
        {
            return false;
        }

        var leading = new List<string>();
        foreach (var word in Words(title))
        {
            if (!word.Text.Any(char.IsLetter))
            {
                return false;
            }

            leading.Add(word.Text);
            if (HoldsNothingElse(title[word.End..]))
            {
                return leading.Any(text => text.Length >= 3 && !NamelessWords.Contains(text))
                       && IsInOrder(leading, nameWords);
            }
        }

        return false;
    }

    // True when every one of `words` appears in `within`, in the same order.
    private static bool IsInOrder(List<string> words, List<string> within)
    {
        var next = 0;
        foreach (var word in words)
        {
            next = within.IndexOf(word, next);
            if (next < 0)
            {
                return false;
            }

            next++;
        }

        return true;
    }

    // The words of `text`, lower-cased, with their end; an apostrophe joins a word ("Turner's").
    private static IEnumerable<(string Text, int End)> Words(string text)
    {
        foreach (Match match in WordPattern().Matches(text))
        {
            yield return (match.Value.Replace("'", string.Empty, StringComparison.Ordinal)
                .Replace("\u2019", string.Empty, StringComparison.Ordinal)
                .ToLowerInvariant(), match.Index + match.Length);
        }
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+(?:['\u2019][\p{L}\p{N}]+)*", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex WordPattern();

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
