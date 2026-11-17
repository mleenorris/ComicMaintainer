using ComicMaintainer.Core.Utilities;

namespace ComicMaintainer.Core.Models;

/// <summary>
/// The ComicInfo values taken from a metadata provider: the series' own, and an issue's from
/// ComicVine (<see cref="StoryArc"/>, <see cref="Month"/>, and its page as <see cref="Web"/>).
/// They only ever fill elements that are empty (see <see cref="ComicInfoXml.FillEmptyFields"/>).
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
public sealed record ProviderComicInfoFields(
    string? Summary = null,
    string? Genre = null,
    string? Web = null,
    int? Count = null,
    string? Manga = null,
    string? StoryArc = null,
    int? Month = null)
{
    /// <summary>The ComicInfo value for manga read from right to left.</summary>
    public const string MangaRightToLeft = "YesAndRightToLeft";

    private const string JapaneseOrigin = "ja";

    /// <summary>True when there is nothing to fill.</summary>
    public bool IsEmpty => Summary is null && Genre is null && Web is null && Count is null && Manga is null
                           && StoryArc is null && Month is null;

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
