using ComicMaintainer.Core.Utilities;

namespace ComicMaintainer.Core.Models;

/// <summary>
/// The series-level ComicInfo values taken from a metadata provider. They only ever fill
/// elements that are empty (see <see cref="ComicInfoXml.FillEmptyFields"/>).
/// </summary>
/// <param name="Summary">Plain-text synopsis, bounded in length.</param>
/// <param name="Genre">Comma-separated genres.</param>
/// <param name="Web">The series' page at the provider.</param>
/// <param name="Count">Total issues; only for a series the provider reports as finished.</param>
/// <param name="Manga">
/// <see cref="MangaRightToLeft"/> for a series of Japanese origin, otherwise null: other origins
/// say nothing reliable about reading direction.
/// </param>
public sealed record ProviderComicInfoFields(
    string? Summary = null,
    string? Genre = null,
    string? Web = null,
    int? Count = null,
    string? Manga = null)
{
    /// <summary>The ComicInfo value for manga read from right to left.</summary>
    public const string MangaRightToLeft = "YesAndRightToLeft";

    private const string JapaneseOrigin = "ja";

    /// <summary>True when there is nothing to fill.</summary>
    public bool IsEmpty => Summary is null && Genre is null && Web is null && Count is null && Manga is null;

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
