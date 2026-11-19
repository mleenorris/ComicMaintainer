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
/// the series' synopsis, missing writers, and empty Story Arc, Month and Web elements. It also
/// records the issue's id (<c>[CVDB12345]</c> in Notes, its page in Web), so the file names its
/// ComicVine issue from then on (see <see cref="ComicVineTagId"/>). A field the user has locked
/// by editing it is never changed.
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
    /// <remarks>
    /// <paramref name="previous"/> is the ComicVine issue the file was filled from before, when
    /// that was a different one (it was matched to the wrong volume): what it wrote counts as not
    /// filled, so the right issue's details replace it. <paramref name="replacesTaggedIssue"/> is
    /// true when the issue the tags name is shown to be wrong (another number, or this
    /// application's own earlier match): its id and page are replaced by this issue's.
    /// </remarks>
    public static Plan For(
        ComicMetadata current,
        ComicVineIssueEntity issue,
        string? seriesSynopsis,
        ComicVineIssueEntity? previous = null,
        bool replacesTaggedIssue = false)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(issue);
        var (previousYear, _) = CoverDate(previous?.CoverDate);

        var locked = (ComicMetadataFieldFlags)current.UserLockedFieldsMask;
        var update = new ComicMetadata();
        var fields = ComicMetadataFieldFlags.None;

        if (!locked.HasFlag(ComicMetadataFieldFlags.Title)
            && !string.IsNullOrWhiteSpace(issue.Name)
            && (IsPlaceholderTitle(current.Title, current.Series, current.Issue) || WrittenBy(previous?.Name, current.Title))
            && !string.Equals(current.Title, issue.Name, StringComparison.Ordinal))
        {
            update.Title = issue.Name.Trim();
            fields |= ComicMetadataFieldFlags.Title;
        }

        var (year, month) = CoverDate(issue.CoverDate);
        if (!locked.HasFlag(ComicMetadataFieldFlags.Year)
            && year is { } coverYear
            && (current.Year is null or <= 0 or UnknownYear || (previousYear is not null && current.Year == previousYear))
            && current.Year != coverYear)
        {
            update.Year = coverYear;
            fields |= ComicMetadataFieldFlags.Year;
        }

        var summary = SynopsisTextNormalizer.ToComicInfoSummary(issue.Summary);
        if (!locked.HasFlag(ComicMetadataFieldFlags.Summary)
            && !string.IsNullOrWhiteSpace(summary)
            && (string.IsNullOrWhiteSpace(current.Summary) || IsSeriesSynopsis(current.Summary, seriesSynopsis)
                || WrittenBy(SynopsisTextNormalizer.ToComicInfoSummary(previous?.Summary), current.Summary))
            && !string.Equals(current.Summary?.Trim(), summary.Trim(), StringComparison.Ordinal))
        {
            update.Summary = summary;
            fields |= ComicMetadataFieldFlags.Summary;
        }

        var writers = SplitList(issue.Writers);
        var previousWriters = SplitList(previous?.Writers);
        var authorsFromPrevious = previousWriters.Count > 0 && current.Authors.SequenceEqual(previousWriters, StringComparer.Ordinal);
        if (!locked.HasFlag(ComicMetadataFieldFlags.Authors) && (current.Authors.Count == 0 || authorsFromPrevious) && writers.Count > 0
            && !current.Authors.SequenceEqual(writers, StringComparer.Ordinal))
        {
            update.Authors = writers;
            fields |= ComicMetadataFieldFlags.Authors;
        }

        var arcs = StoryArcs(issue.StoryArcsJson);
        var fill = new ProviderComicInfoFields(
            Web: ProviderMetadataText.HttpUrl(issue.SiteUrl),
            StoryArc: arcs.Count == 0 ? null : string.Join(", ", arcs.Select(arc => arc.Name)),
            Month: month,
            ComicVineIssueId: issue.Id,
            ReplacesComicVineIssueId: replacesTaggedIssue);
        return new Plan(update, fields, fill);
    }

    /// <summary>
    /// True for a title that names nothing but the issue: none at all, a generated one
    /// (<c>Chapter 8</c>, <c>#8</c>, <c>Issue #8</c>, <c>v2 008 (2016)</c>), or an old file name
    /// left by a tagger, of this issue or another (see <see cref="FileNameTitle.IsFileName"/>).
    /// </summary>
    public static bool IsPlaceholderTitle(string? title, string? series, string? issue)
    {
        if (string.IsNullOrWhiteSpace(title) || FileNameTitle.IsFileName(title, series, issue))
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

    /// <summary>True when <paramref name="current"/> is exactly what an earlier issue wrote.</summary>
    private static bool WrittenBy(string? written, string? current)
        => !string.IsNullOrWhiteSpace(written) && string.Equals(written.Trim(), current?.Trim(), StringComparison.Ordinal);

    private static bool IsSeriesSynopsis(string summary, string? seriesSynopsis)
        => SynopsisTextNormalizer.ToComicInfoSummary(seriesSynopsis) is { Length: > 0 } synopsis
           && string.Equals(summary.Trim(), synopsis.Trim(), StringComparison.Ordinal);

    private static List<string> SplitList(string? text)
        => (text ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

    // Everything a placeholder title is made of: issue words, a volume token, numbers, a year in
    // brackets and punctuation.
    // A tie-in's number ("Chapter 1.MU") goes whole, suffix and all.
    [GeneratedRegex(@"\b(?:chapter|ch|issue|no|number|vol|volume|v\d+)\b|\d+\.\p{L}{1,4}(?![\p{L}\p{N}])|\d+|[\p{P}\p{S}_]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex IssueWordsPattern();
}
