using System.Text.RegularExpressions;

namespace ComicMaintainer.Core.Utilities;

/// <summary>
/// Splits the volume year off a series name written the way comic libraries
/// name volume folders: <c>Green Lantern (2023)</c>, <c>Green Lantern [2023]</c>,
/// <c>Green Lantern (2023-2024)</c> or <c>Green Lantern (2023-)</c>. The year is
/// the volume's start year, which tells apart volumes that share a name
/// (ComicVine lists dozens of volumes called just "Green Lantern").
/// </summary>
public static partial class SeriesTitleYear
{
    /// <summary>Earliest year accepted as a volume year.</summary>
    private const int MinYear = 1900;

    /// <summary>Latest year accepted as a volume year.</summary>
    private const int MaxYear = 2199;

    /// <summary>
    /// The title and volume year of <paramref name="seriesName"/>. A name
    /// without a trailing bracketed year comes back unchanged with a null year,
    /// and so does one that would be left without a title (<c>(2023)</c>).
    /// </summary>
    public static (string Title, int? Year) Parse(string? seriesName)
    {
        if (string.IsNullOrWhiteSpace(seriesName))
        {
            return (seriesName?.Trim() ?? string.Empty, null);
        }

        var trimmed = seriesName.Trim();
        var match = TrailingYearPattern().Match(trimmed);
        if (!match.Success
            || !int.TryParse(match.Groups["year"].Value, out var year)
            || year is < MinYear or > MaxYear)
        {
            return (trimmed, null);
        }

        var title = match.Groups["title"].Value.Trim();
        return title.Any(char.IsLetterOrDigit) ? (title, year) : (trimmed, null);
    }

    /// <summary>The volume year of <paramref name="seriesName"/>, or null when it names none.</summary>
    public static int? YearOf(string? seriesName) => Parse(seriesName).Year;

    /// <summary>
    /// <paramref name="seriesName"/> qualified with <paramref name="year"/>
    /// (<c>Green Lantern (2023)</c>), unless it already names a year of its own.
    /// </summary>
    public static string WithYear(string seriesName, int year)
        => YearOf(seriesName) is null ? $"{seriesName.Trim()} ({year})" : seriesName.Trim();

    // "<title> (YYYY)", "<title> [YYYY]", "<title> (YYYY-YYYY)" or "<title> (YYYY-)".
    [GeneratedRegex(@"^(?<title>.*?)\s*[\(\[]\s*(?<year>\d{4})\s*(?:-\s*(?:\d{4})?\s*)?[\)\]]$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingYearPattern();
}
