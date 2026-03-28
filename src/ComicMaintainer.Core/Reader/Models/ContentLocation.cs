namespace ComicMaintainer.Core.Reader.Models;

/// <summary>
/// Represents a normalized reading position within a piece of content.
/// Flexible enough for both paged and longstrip (scroll-position) content.
/// </summary>
public class ContentLocation
{
    /// <summary>Zero-based page index for paged content.</summary>
    public int PageIndex { get; set; }

    /// <summary>
    /// Scroll offset as a fraction of total content height (0.0–1.0).
    /// Used for longstrip/continuous-scroll modes; <c>null</c> for paged modes.
    /// </summary>
    public double? ScrollFraction { get; set; }
}
