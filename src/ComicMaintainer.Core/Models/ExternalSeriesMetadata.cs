namespace ComicMaintainer.Core.Models;

/// <summary>
/// Represents canonical series metadata returned by an external metadata provider.
/// </summary>
public class ExternalSeriesMetadata
{
    public string CanonicalTitle { get; set; } = string.Empty;
    public List<string> Aliases { get; set; } = new();
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// The provider's own identifier of this entry (MangaDex manga id, AniList
    /// media id, ComicVine volume id), or null when the provider did not
    /// report one. Stored with a manual match so a later refresh re-fetches
    /// exactly the entry the user picked instead of searching by title again.
    /// </summary>
    public string? SourceId { get; set; }

    /// <summary>
    /// URL of a series-level cover image returned by the provider, if any.
    /// Used by the local image cache to download a poster for the series view.
    /// </summary>
    public string? ImageUrl { get; set; }

    /// <summary>
    /// Optional smaller thumbnail URL when the provider returns multiple sizes.
    /// </summary>
    public string? ThumbnailUrl { get; set; }

    /// <summary>
    /// Plain-text synopsis / summary / description of the series as reported by
    /// the provider, with any provider-specific HTML or BBCode stripped. Null
    /// when the provider returns no description.
    /// </summary>
    public string? Synopsis { get; set; }

    /// <summary>The series' page on the provider's website (http or https), or null when unknown.</summary>
    public string? SiteUrl { get; set; }

    /// <summary>Plain-text genre names as the provider lists them; empty when it lists none.</summary>
    public List<string> Genres { get; set; } = new();

    /// <summary>
    /// One of the <see cref="SeriesPublicationStatus"/> values, or null when the provider does
    /// not report a status.
    /// </summary>
    public string? PublicationStatus { get; set; }

    /// <summary>
    /// The number of chapters or issues the provider reports. For a series that is not finished
    /// this is at most a count so far, so only a finished series uses it as a total.
    /// </summary>
    public int? TotalChapters { get; set; }

    /// <summary>
    /// The language the series was first published in, as a lowercase primary language tag
    /// (<c>ja</c>, <c>ko</c>, <c>zh</c>, ...), or null when unknown. This describes the work,
    /// not the language of a particular file: a translation has the same origin.
    /// </summary>
    public string? Origin { get; set; }

    /// <summary>
    /// Optional confidence score (0–100) describing how well this candidate
    /// matches a user query. Populated by the search/scoring layer (not by
    /// providers themselves). 100 = exact normalized match on the canonical
    /// title; lower values reflect alias matches or fuzzy similarity.
    /// </summary>
    public double? MatchScore { get; set; }

    /// <summary>
    /// Provider-supplied titles tagged with their language (BCP-47, e.g.
    /// <c>en</c>, <c>ja</c>, <c>ja-Latn</c>, <c>ko</c>, <c>zh</c>). The list
    /// is ordered with the provider's canonical title first, followed by any
    /// remaining aliases. Untagged values (synonyms / unknown-language
    /// aliases) carry <c>Language = null</c>.
    /// </summary>
    public List<LocalizedTitle> LocalizedTitles { get; set; } = new();
}
