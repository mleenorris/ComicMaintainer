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
    /// URL of a series-level cover image returned by the provider, if any.
    /// Used by the local image cache to download a poster for the series view.
    /// </summary>
    public string? ImageUrl { get; set; }

    /// <summary>
    /// Optional smaller thumbnail URL when the provider returns multiple sizes.
    /// </summary>
    public string? ThumbnailUrl { get; set; }

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
