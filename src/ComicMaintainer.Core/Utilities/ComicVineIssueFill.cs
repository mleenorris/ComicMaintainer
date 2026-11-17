using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Models;

namespace ComicMaintainer.Core.Utilities;

/// <summary>
/// What a ComicVine issue adds to a file's tags. It only ever fills in: a title that is a
/// placeholder (<c>Chapter 8</c>, <c>#8</c>, an old file name), a missing cover year (or the
/// 1970 some downloaders write when they do not know it), a missing summary or one that is only
/// the series' synopsis, missing writers, and empty Story Arc, Month and Web elements. A field
/// the user has locked by editing it is never changed.
/// </summary>
public static partial class ComicVineIssueFill
{
    /// <summary>The year some downloaders and taggers write when the date is unknown (the Unix epoch).</summary>
    private const int UnknownYear = 1970;

    /// <summary>
    /// The write for one file: <see cref="Fields"/> of <see cref="Metadata"/> replace their
    /// values, and <see cref="Fill"/> goes into elements that are still empty.
    /// </summary>
    public sealed record Plan(ComicMetadata Metadata, ComicMetadataFieldFlags Fields, ProviderComicInfoFields Fill)
    {
        /// <summary>True when the issue adds nothing.</summary>
        public bool IsEmpty => Fields == ComicMetadataFieldFlags.None && Fill.IsEmpty;
    }

    /// <summary>
    /// The plan for a file whose tags are <paramref name="current"/>, from
    /// <paramref name="issue"/>. <paramref name="seriesSynopsis"/> is the synopsis the series'
    /// provider match gave, which may already sit in the file's Summary.
    /// </summary>
    public static Plan For(ComicMetadata current, ComicVineIssueEntity issue, string? seriesSynopsis)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(issue);

        var locked = (ComicMetadataFieldFlags)current.UserLockedFieldsMask;
        var update = new ComicMetadata();
        var fields = ComicMetadataFieldFlags.None;

        if (!locked.HasFlag(ComicMetadataFieldFlags.Title)
            && !string.IsNullOrWhiteSpace(issue.Name)
            && IsPlaceholderTitle(current.Title, current.Series, current.Issue)
            && !string.Equals(current.Title, issue.Name, StringComparison.Ordinal))
        {
            update.Title = issue.Name.Trim();
            fields |= ComicMetadataFieldFlags.Title;
        }

        var (year, month) = CoverDate(issue.CoverDate);
        if (!locked.HasFlag(ComicMetadataFieldFlags.Year)
            && year is { } coverYear
            && current.Year is null or <= 0 or UnknownYear
            && current.Year != coverYear)
        {
            update.Year = coverYear;
            fields |= ComicMetadataFieldFlags.Year;
        }

        var summary = SynopsisTextNormalizer.ToComicInfoSummary(issue.Summary);
        if (!locked.HasFlag(ComicMetadataFieldFlags.Summary)
            && !string.IsNullOrWhiteSpace(summary)
            && (string.IsNullOrWhiteSpace(current.Summary) || IsSeriesSynopsis(current.Summary, seriesSynopsis))
            && !string.Equals(current.Summary?.Trim(), summary.Trim(), StringComparison.Ordinal))
        {
            update.Summary = summary;
            fields |= ComicMetadataFieldFlags.Summary;
        }

        var writers = SplitList(issue.Writers);
        if (!locked.HasFlag(ComicMetadataFieldFlags.Authors) && current.Authors.Count == 0 && writers.Count > 0)
        {
            update.Authors = writers;
            fields |= ComicMetadataFieldFlags.Authors;
        }

        var arcs = StoryArcs(issue.StoryArcsJson);
        var fill = new ProviderComicInfoFields(
            Web: ProviderMetadataText.HttpUrl(issue.SiteUrl),
            StoryArc: arcs.Count == 0 ? null : string.Join(", ", arcs.Select(arc => arc.Name)),
            Month: month);
        return new Plan(update, fields, fill);
    }

    /// <summary>
    /// True for a title that names nothing but the issue: none at all, a generated one
    /// (<c>Chapter 8</c>, <c>#8</c>, <c>Issue #8</c>, <c>v2 008 (2016)</c>), or an old file name
    /// left by a tagger (see <see cref="FileNameTitle.IsLeftoverFileName"/>).
    /// </summary>
    public static bool IsPlaceholderTitle(string? title, string? series, string? issue)
    {
        if (string.IsNullOrWhiteSpace(title) || FileNameTitle.IsLeftoverFileName(title, series, issue))
        {
            return true;
        }

        var words = IssueWordsPattern().Replace(title, " ");
        return !words.Any(char.IsLetter);
    }

    /// <summary>The year and month of a ComicVine cover date (<c>2016-08-01</c>), each null when absent.</summary>
    public static (int? Year, int? Month) CoverDate(string? coverDate)
    {
        if (string.IsNullOrWhiteSpace(coverDate))
        {
            return (null, null);
        }

        var parts = coverDate.Trim().Split('-');
        int? year = int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var y) && y is >= 1900 and <= 2199 ? y : null;
        int? month = year is not null && parts.Length > 1
                     && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var m) && m is >= 1 and <= 12
            ? m
            : null;
        return (year, month);
    }

    /// <summary>The story arcs of <see cref="ComicVineIssueEntity.StoryArcsJson"/>; empty when there are none or it cannot be read.</summary>
    public static IReadOnlyList<ComicVineArcReference> StoryArcs(string? storyArcsJson)
    {
        if (string.IsNullOrWhiteSpace(storyArcsJson))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<ComicVineArcReference>>(storyArcsJson, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The JSON stored in <see cref="ComicVineIssueEntity.StoryArcsJson"/>.</summary>
    public static string StoryArcsJson(IReadOnlyList<ComicVineArcReference> arcs)
        => JsonSerializer.Serialize(arcs, JsonOptions);

    /// <summary>
    /// The JSON for <see cref="StoryArcsJson(IReadOnlyList{ComicVineArcReference})"/> that fits a column of
    /// <paramref name="maxLength"/> characters: whole arcs are dropped from the end, so the text stays valid JSON
    /// (cutting the string would leave it unreadable).
    /// </summary>
    public static string StoryArcsJson(IReadOnlyList<ComicVineArcReference> arcs, int maxLength)
    {
        var kept = arcs.ToList();
        var json = StoryArcsJson(kept);
        while (json.Length > maxLength && kept.Count > 0)
        {
            kept.RemoveAt(kept.Count - 1);
            json = StoryArcsJson(kept);
        }

        return json;
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static bool IsSeriesSynopsis(string summary, string? seriesSynopsis)
        => SynopsisTextNormalizer.ToComicInfoSummary(seriesSynopsis) is { Length: > 0 } synopsis
           && string.Equals(summary.Trim(), synopsis.Trim(), StringComparison.Ordinal);

    private static List<string> SplitList(string? text)
        => (text ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

    // Everything a placeholder title is made of: issue words, a volume token, numbers, a year in
    // brackets and punctuation.
    [GeneratedRegex(@"\b(?:chapter|ch|issue|no|number|vol|volume|v\d+)\b|\d+|[\p{P}\p{S}_]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex IssueWordsPattern();
}
