namespace ComicMaintainer.Core.Reader.Models;

/// <summary>
/// Defines the reading direction for paginated content.
/// </summary>
public enum ReadingDirection
{
    /// <summary>Pages progress left to right (Western comics).</summary>
    LeftToRight,

    /// <summary>Pages progress right to left (manga).</summary>
    RightToLeft
}
