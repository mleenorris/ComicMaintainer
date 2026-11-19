using ComicMaintainer.Core.Utilities;

namespace ComicMaintainer.Core.Models;

/// <summary>
/// The ComicInfo values taken from a metadata provider: the series' own, and an issue's from
/// ComicVine (<see cref="StoryArc"/>, <see cref="Month"/>, its page as <see cref="Web"/> and its id
/// as <see cref="ComicVineIssueId"/>). They only ever fill elements that are empty (see
/// <see cref="ComicInfoXml.FillEmptyFields"/>), except that an issue's page replaces the series'
/// ComicVine page in Web, and its id is recorded in Notes.
/// </summary>
/// <param name="Summary">Plain-text synopsis, bounded in length.</param>
/// <param name="Genre">Comma-separated genres.</param>
/// <param name="Web">The series' page at the provider.</param>
/// <param name="Count">Total issues; only for a series the provider reports as finished.</param>
/// <param name="Manga">
/// <see cref="MangaRightToLeft"/> for a series of Japanese origin, otherwise null: other origins
/// say nothing reliable about reading direction.
/// </param>
/// <param name="StoryArc">The issue's story arcs, comma-separated.</param>
/// <param name="Month">The month of the issue's cover date, 1 to 12.</param>
/// <param name="ComicVineIssueId">
/// The ComicVine issue the file was filled from, recorded as <c>[CVDB12345]</c> in Notes (see
/// <see cref="ComicVineTagId"/>); <see cref="Web"/> is then that issue's page.
/// </param>
/// <param name="ReplacesComicVineIssueId">
/// True when the tags named another ComicVine issue, which the file was not filled from: its id
/// in Notes, and its page in Web, are replaced by this issue's.
/// </param>
public sealed record ProviderComicInfoFields(
    string? Summary = null,
    string? Genre = null,
    string? Web = null,
    int? Count = null,
    string? Manga = null,
    string? StoryArc = null,
    int? Month = null,
    int? ComicVineIssueId = null,
    bool ReplacesComicVineIssueId = false)
{
    /// <summary>The ComicInfo value for manga read from right to left.</summary>
    public const string MangaRightToLeft = "YesAndRightToLeft";

    private const string JapaneseOrigin = "ja";

    /// <summary>True when there is nothing to fill.</summary>
    public bool IsEmpty => Summary is null && Genre is null && Web is null && Count is null && Manga is null
                           && StoryArc is null && Month is null && ComicVineIssueId is null;

    /// <summary>The values a cached provider match supplies.</summary>
    public static ProviderComicInfoFields From(SeriesMetadataCacheRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var genres = ProviderMetadataText.Genres(record.Genres);
        var finished = record.PublicationStatus == SeriesPublicationStatus.Completed;
        return new ProviderComicInfoFields(
            SynopsisTextNormalizer.ToComicInfoSummary(record.Synopsis),
            genres.Count == 0 ? null : string.Join(", ", genres),
            ProviderMetadataText.HttpUrl(record.SiteUrl),
            finished && record.TotalChapters > 0 ? record.TotalChapters : null,
            string.Equals(record.Origin, JapaneseOrigin, StringComparison.OrdinalIgnoreCase) ? MangaRightToLeft : null);
    }
}
