namespace ComicMaintainer.Core.Models;

/// <summary>
/// Represents series-focused metadata used for grouping and library presentation.
/// </summary>
public class SeriesMetadata
{
    public string? Series { get; set; }
    public string? AlternateSeries { get; set; }
    public string? SeriesGroup { get; set; }
    public string? Title { get; set; }
    public string? Issue { get; set; }
    public string? Volume { get; set; }
    public string? Publisher { get; set; }
    public int? Year { get; set; }
}
