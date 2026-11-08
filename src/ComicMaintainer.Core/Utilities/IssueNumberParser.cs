using System.Globalization;
using System.Text.RegularExpressions;

namespace ComicMaintainer.Core.Utilities;

/// <summary>
/// Reads issue/chapter numbers out of comic file names. This is the only file-name issue parser
/// in the application: metadata fallbacks, normalize, rename and condensed e-reader book names
/// all go through it, so a file name yields the same number everywhere.
/// </summary>
/// <remarks>
/// <para>Bracketed qualifiers such as "(2016)", "[Digital]", "(c2c)", "{HQ}" or a "[5C2A9E01]"
/// checksum are ignored by every rule. The rules, in order:</para>
/// <list type="number">
/// <item>An explicitly marked number wins: "Ch 12", "Ch.12", "Chapter 012.5", "c012", "Issue 3"
/// or "#007". A marker must start a word, so "Punch 5" is not read as chapter 5. A bare "c"
/// marker must be followed by the number alone, so the scene tag "c2c" is not chapter 2.</item>
/// <item>Otherwise volume tokens such as "Vol.1", "Vol 2", "Volume 3" or "v01" are ignored too,
/// and the name is split into parts at " - " title separators. The last part that is only a
/// number is the issue: "Mob Psycho 100 - 005" gives 5 and "Series - 012 - Title 2" gives 12.
/// When no part is only a number, the last standalone number of the first part that has one is
/// used. A number in the series name or a year then gives way to the issue ("Batman (2016) 005"
/// gives 5, "Spider-Man 2099 012" gives 12), and a number in a chapter title after the separator
/// does not win ("Series 012 - Part 2" gives 12). A number glued to letters ("20th", "x2") is
/// never a candidate.</item>
/// <item>A name with no such number ("Series (2023)", "Series Vol 2") has no issue number.</item>
/// </list>
/// <para>Returned numbers have the zero padding of their integer part removed ("0012" becomes
/// "12", "0004.5" becomes "4.5"); only generated file names are zero padded.</para>
/// </remarks>
public static class IssueNumberParser
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// An explicitly marked chapter or issue number. The marker must not follow a letter, so it
    /// starts a word. A bare "c" only counts directly before a number that no letter, digit or
    /// further decimal follows ("c012", but not the "c2c" scene tag or a "5C2A9E01" checksum).
    /// </summary>
    private static readonly Regex MarkedNumberPattern = new(
        @"(?<!\p{L})(?:ch(?:ap(?:ter)?)?|issue)[\s._#-]*(?<number>\d+(?:\.\d+)?)" +
        @"|(?<!\p{L})c(?<number>\d+(?:\.\d+)?)(?![\p{L}\p{N}]|\.\d)" +
        @"|#\s*(?<number>\d+(?:\.\d+)?)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        MatchTimeout);

    /// <summary>Bracketed qualifiers such as "(2016)", "[Digital]" or "{HQ}".</summary>
    private static readonly Regex BracketedQualifierPattern = new(
        @"[\(\[\{][^\)\]\}]*[\)\]\}]",
        RegexOptions.CultureInvariant,
        MatchTimeout);

    /// <summary>Volume tokens such as "Vol.1", "Vol 2", "Volume 3" or "v01".</summary>
    private static readonly Regex VolumePattern = new(
        @"(?<!\p{L})v(?:ol(?:ume)?)?\.?[\s._-]*\d+(?:\.\d+)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        MatchTimeout);

    /// <summary>
    /// A " - " title separator (also with an en or em dash, or underscores instead of spaces).
    /// A hyphen inside a word, as in "Spider-Man", is not a separator.
    /// </summary>
    private static readonly Regex TitleSeparatorPattern = new(
        @"[\s_]+[-–—]+[\s_]+",
        RegexOptions.CultureInvariant,
        MatchTimeout);

    /// <summary>A name part that holds nothing but a number.</summary>
    private static readonly Regex NumberOnlyPattern = new(
        @"^\d+(?:\.\d+)?$",
        RegexOptions.CultureInvariant,
        MatchTimeout);

    /// <summary>A number that is not glued to letters or other digits.</summary>
    private static readonly Regex StandaloneNumberPattern = new(
        @"(?<![\p{L}\p{N}])\d+(?:\.\d+)?(?![\p{L}\p{N}])",
        RegexOptions.CultureInvariant,
        MatchTimeout);

    /// <summary>Characters trimmed from a name part before checking whether it is only a number.</summary>
    private static readonly char[] PartTrimCharacters = [' ', '\t', '_', '.', '-', '–', '—'];

    /// <summary>
    /// Returns the issue number of a file name (without its extension), or null when the name
    /// holds none. See the class remarks for the rules.
    /// </summary>
    public static string? Parse(string? fileNameStem)
    {
        if (string.IsNullOrWhiteSpace(fileNameStem))
        {
            return null;
        }

        var withoutBrackets = RemoveBracketedQualifiers(fileNameStem);
        var marked = MarkedNumberPattern.Match(withoutBrackets);
        if (marked.Success)
        {
            return StripLeadingZeros(marked.Groups["number"].Value);
        }

        string? numberOnlyPart = null;
        string? lastNumberOfFirstNumberedPart = null;
        foreach (var part in TitleSeparatorPattern.Split(VolumePattern.Replace(withoutBrackets, " ")))
        {
            var trimmed = part.Trim(PartTrimCharacters);
            if (NumberOnlyPattern.IsMatch(trimmed))
            {
                numberOnlyPart = trimmed;
                continue;
            }

            if (lastNumberOfFirstNumberedPart is null)
            {
                var numbers = StandaloneNumberPattern.Matches(part);
                if (numbers.Count > 0)
                {
                    lastNumberOfFirstNumberedPart = numbers[^1].Value;
                }
            }
        }

        var issue = numberOnlyPart ?? lastNumberOfFirstNumberedPart;
        return issue is null ? null : StripLeadingZeros(issue);
    }

    /// <summary>
    /// Returns the explicitly marked chapter/issue number of a file name ("Ch 12", "#007",
    /// "Issue 3", "c012"), or null when there is no marker outside brackets. Unlike
    /// <see cref="Parse"/> this never guesses from an unmarked number, so it cannot mistake a
    /// volume, year or title number for the chapter.
    /// </summary>
    public static string? ParseMarked(string? fileNameStem)
    {
        if (string.IsNullOrWhiteSpace(fileNameStem))
        {
            return null;
        }

        var match = MarkedNumberPattern.Match(RemoveBracketedQualifiers(fileNameStem));
        return match.Success ? StripLeadingZeros(match.Groups["number"].Value) : null;
    }

    /// <summary>
    /// True when <paramref name="fileNameStem"/> already holds <paramref name="issue"/> as a
    /// marked number or as a standalone number outside brackets and volume tokens, compared
    /// numerically ("0005" holds "5"). Used to check that a generated file name kept the chapter.
    /// </summary>
    public static bool ContainsIssueNumber(string? fileNameStem, string? issue)
    {
        if (string.IsNullOrWhiteSpace(fileNameStem) || string.IsNullOrWhiteSpace(issue))
        {
            return false;
        }

        var withoutBrackets = RemoveBracketedQualifiers(fileNameStem);
        foreach (Match match in MarkedNumberPattern.Matches(withoutBrackets))
        {
            if (AreEquivalent(match.Groups["number"].Value, issue))
            {
                return true;
            }
        }

        foreach (Match match in StandaloneNumberPattern.Matches(VolumePattern.Replace(withoutBrackets, " ")))
        {
            if (AreEquivalent(match.Value, issue))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns true when two chapter/issue numbers have the same numeric value ("1", "0001" and
    /// "0001.0" are all equivalent). Non-numeric values are compared ordinally; two missing
    /// values are equivalent, a missing and a present one are not.
    /// </summary>
    public static bool AreEquivalent(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
        {
            return string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b);
        }

        if (string.Equals(a, b, StringComparison.Ordinal))
        {
            return true;
        }

        return decimal.TryParse(a, NumberStyles.Number, CultureInfo.InvariantCulture, out var da) &&
            decimal.TryParse(b, NumberStyles.Number, CultureInfo.InvariantCulture, out var db) &&
            da == db;
    }

    /// <summary>
    /// Strips the zero padding of the integer part of a numeric chapter/issue string while keeping
    /// any decimal portion ("0012" becomes "12", "0004.5" becomes "4.5", "0000" becomes "0").
    /// Values without leading zeros, such as "Extra", are returned unchanged.
    /// </summary>
    public static string StripLeadingZeros(string number)
    {
        if (string.IsNullOrEmpty(number))
        {
            return number;
        }

        var dotIndex = number.IndexOf('.');
        var integerPart = dotIndex >= 0 ? number[..dotIndex] : number;
        var fractionalPart = dotIndex >= 0 ? number[dotIndex..] : string.Empty;

        var trimmed = integerPart.TrimStart('0');
        if (trimmed.Length == 0)
        {
            trimmed = "0";
        }

        return trimmed + fractionalPart;
    }

    /// <summary>
    /// Replaces bracketed qualifiers with a space, so neither a marker nor a number inside them
    /// ("(c2c)", "(2016)", "[5C2A9E01]") is read and the words around them stay apart.
    /// </summary>
    private static string RemoveBracketedQualifiers(string fileNameStem) =>
        BracketedQualifierPattern.Replace(fileNameStem, " ");
}
