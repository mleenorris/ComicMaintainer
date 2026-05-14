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
}
