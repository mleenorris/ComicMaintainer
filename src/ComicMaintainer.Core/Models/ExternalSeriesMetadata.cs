namespace ComicMaintainer.Core.Models;

/// <summary>
/// Represents canonical series metadata returned by an external metadata provider.
/// </summary>
public class ExternalSeriesMetadata
{
    public string CanonicalTitle { get; set; } = string.Empty;
    public List<string> Aliases { get; set; } = new();
    public string Source { get; set; } = string.Empty;
}
