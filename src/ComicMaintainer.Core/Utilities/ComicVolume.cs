using System.Globalization;
using System.Text.RegularExpressions;

namespace ComicMaintainer.Core.Utilities;

/// <summary>
/// The volume a comic issue belongs to. American comics restart their numbering with each
/// volume (a "relaunch"), so <c>Green Lantern</c> #1 of 1990 and #1 of 2005 are different
/// issues. A volume is known either by its start year (<c>Green Lantern (1990)</c>, as
/// ComicVine and ComicTagger write it) or by its number (<c>Green Lantern Vol. 2</c>).
/// </summary>
/// <param name="Value">The start year or the volume number.</param>
/// <param name="IsYear">True when <paramref name="Value"/> is a start year.</param>
public readonly partial record struct ComicVolume(int Value, bool IsYear)
{
    private const int MinYear = 1900;
    private const int MaxYear = 2199;
    private const int MaxVolumeNumber = 999;

    /// <summary>
    /// Stable identity of the volume, distinct for a year and a number with the same value:
    /// <c>y1990</c> or <c>v2</c>.
    /// </summary>
    public string Key => IsYear
        ? $"y{Value.ToString(CultureInfo.InvariantCulture)}"
        : $"v{Value.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>How the volume is written after a series name: <c>(1990)</c> or <c>Vol. 2</c>.</summary>
    public string Label => IsYear
        ? $"({Value.ToString(CultureInfo.InvariantCulture)})"
        : $"Vol. {Value.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>How the volume is named on its own: <c>1990</c> or <c>Vol. 2</c>.</summary>
    public string DisplayName => IsYear ? Value.ToString(CultureInfo.InvariantCulture) : Label;

    /// <summary>
    /// The volume of an issue, from the first source that names one: the ComicInfo
    /// <c>&lt;Volume&gt;</c> tag, then a year or volume token at the end of the series name,
    /// then the file name (see <see cref="FromFileName"/>). Null when none does.
    /// </summary>
    public static ComicVolume? Resolve(string? volumeTag, string? seriesName, string? fileName)
        => FromTag(volumeTag) ?? FromSeriesName(seriesName) ?? FromFileName(fileName);

    /// <summary>
    /// Reads a ComicInfo <c>&lt;Volume&gt;</c> value: <c>2</c>, <c>02</c>, <c>v2</c>,
    /// <c>Vol. 2</c> or a start year such as <c>1990</c>. Null for blank, negative (ComicInfo's
    /// "unset" is -1), zero or other values.
    /// </summary>
    public static ComicVolume? FromTag(string? volumeTag)
    {
        if (string.IsNullOrWhiteSpace(volumeTag))
        {
            return null;
        }

        var match = TagPattern().Match(volumeTag.Trim());
        return match.Success ? FromNumber(match.Groups["value"].Value) : null;
    }

    /// <summary>
    /// Reads a volume written at the end of a series name: a start year in brackets
    /// (<c>Green Lantern (1990)</c>, <c>[1990]</c>, <c>(1990-1995)</c>) or a volume token
    /// (<c>Saga Vol. 2</c>, <c>Saga v2</c>, <c>Saga Volume 2</c>). Null otherwise.
    /// </summary>
    public static ComicVolume? FromSeriesName(string? seriesName)
    {
        if (string.IsNullOrWhiteSpace(seriesName))
        {
            return null;
        }

        var trimmed = seriesName.Trim();
        var year = TrailingYearPattern().Match(trimmed);
        if (year.Success)
        {
            return FromYear(year.Groups["year"].Value);
        }

        var volume = TrailingVolumePattern().Match(trimmed);
        return volume.Success ? FromNumber(volume.Groups["value"].Value) : null;
    }

    /// <summary>
    /// <paramref name="seriesName"/> without the volume it ends in (see
    /// <see cref="FromSeriesName"/>): <c>Green Lantern (1990)</c> and <c>Saga Vol. 2</c> give
    /// <c>Green Lantern</c> and <c>Saga</c>. Unchanged when it ends in no volume, or when the
    /// volume is all it holds.
    /// </summary>
    public static string WithoutVolume(string seriesName)
    {
        if (string.IsNullOrWhiteSpace(seriesName))
        {
            return seriesName;
        }

        var trimmed = seriesName.Trim();
        var match = TrailingYearPattern().Match(trimmed);
        if (!match.Success || FromYear(match.Groups["year"].Value) is null)
        {
            match = TrailingVolumePattern().Match(trimmed);
            if (!match.Success || FromNumber(match.Groups["value"].Value) is null)
            {
                return trimmed;
            }
        }

        var title = trimmed[..match.Index].TrimEnd(' ', '-', ',', ':', '–', '—');
        return title.Any(char.IsLetterOrDigit) ? title : trimmed;
    }

    /// <summary>
    /// Reads the volume from a file name: a volume token anywhere in it (<c>Saga v02 001</c>,
    /// <c>Saga Vol. 2 - Chapter 001</c>), or else a start year in brackets that comes before the
    /// issue number (<c>Green Lantern (1990) 001</c>, <c>Green Lantern (1990) - Chapter 001</c>).
    /// A year after the issue number (<c>Saga 001 (2012)</c>) is the issue's cover date, not
    /// the volume, so it is ignored. Null when the name names no volume.
    /// </summary>
    public static ComicVolume? FromFileName(string? fileName)
    {
        return string.IsNullOrWhiteSpace(fileName)
            ? null
            : FromFileNameStem(Path.GetFileNameWithoutExtension(fileName));
    }

    /// <summary>
    /// <see cref="FromFileName"/> for a name that has no extension, such as a file name left in
    /// a <c>&lt;Title&gt;</c> (see <see cref="FileNameTitle"/>), where a decimal issue
    /// (<c>012.5</c>) must not be taken for an extension.
    /// </summary>
    public static ComicVolume? FromFileNameStem(string? stem)
    {
        if (string.IsNullOrWhiteSpace(stem))
        {
            return null;
        }

        var volume = VolumeTokenPattern().Match(stem);
        if (volume.Success)
        {
            return FromNumber(volume.Groups["value"].Value);
        }

        var year = BracketedYearPattern().Match(stem);
        if (!year.Success)
        {
            return null;
        }

        // Only a year followed by the issue number names the volume.
        var rest = BracketedPattern().Replace(stem[(year.Index + year.Length)..], " ");
        return rest.Any(char.IsAsciiDigit) ? FromYear(year.Groups["year"].Value) : null;
    }

    /// <summary>
    /// A number that is a plausible start year becomes a year volume; one from 1 to
    /// <see cref="MaxVolumeNumber"/> a numbered volume.
    /// </summary>
    private static ComicVolume? FromNumber(string digits)
    {
        if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        if (value is >= MinYear and <= MaxYear)
        {
            return new ComicVolume(value, IsYear: true);
        }

        return value is >= 1 and <= MaxVolumeNumber ? new ComicVolume(value, IsYear: false) : null;
    }

    /// <summary>A start-year volume for <paramref name="year"/>; null outside 1900-2199.</summary>
    public static ComicVolume? FromYear(int year)
        => year is >= MinYear and <= MaxYear ? new ComicVolume(year, IsYear: true) : null;

    private static ComicVolume? FromYear(string digits)
        => int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var year)
           && year is >= MinYear and <= MaxYear
            ? new ComicVolume(year, IsYear: true)
            : null;

    // "2", "02", "v2", "Vol 2", "Vol. 2", "Volume 2", "1990".
    [GeneratedRegex(@"^(?:v(?:ol(?:ume)?)?\.?\s*)?(?<value>\d{1,4})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex TagPattern();

    // "... (1990)", "... [1990]", "... (1990-1995)", "... (1990-)" at the end of a name.
    [GeneratedRegex(@"[\(\[]\s*(?<year>\d{4})\s*(?:-\s*(?:\d{4})?\s*)?[\)\]]$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex TrailingYearPattern();

    // "... Vol. 2", "... Vol 2", "... Volume 2", "... v2" at the end of a name.
    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?:vol(?:ume)?\.?\s*|v)(?<value>\d{1,3})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex TrailingVolumePattern();

    // "v02", "Vol. 2", "Vol 2", "Volume 2", "Vol.2" as a word of a file name.
    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?:vol(?:ume)?\.?[\s._-]*|v)(?<value>\d{1,3})(?![\p{L}\p{N}]|\.\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex VolumeTokenPattern();

    // The first bracketed year: "(1990)", "[1990]", "(1990-1995)".
    [GeneratedRegex(@"[\(\[]\s*(?<year>\d{4})\s*(?:-\s*(?:\d{4})?\s*)?[\)\]]", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex BracketedYearPattern();

    // "(...)", "[...]", "{...}".
    [GeneratedRegex(@"\([^)]*\)|\[[^\]]*\]|\{[^}]*\}", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex BracketedPattern();
}
