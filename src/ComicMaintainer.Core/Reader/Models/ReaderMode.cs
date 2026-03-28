namespace ComicMaintainer.Core.Reader.Models;

/// <summary>
/// Defines how content is presented to the reader.
/// </summary>
public enum ReaderMode
{
    /// <summary>One page displayed at a time.</summary>
    SinglePage,

    /// <summary>Vertical continuous scroll (webtoon/longstrip style).</summary>
    Longstrip,

    /// <summary>Two pages side-by-side, left-to-right order.</summary>
    DoublePage,

    /// <summary>Two pages side-by-side, right-to-left order (manga).</summary>
    DoublePageManga,

    /// <summary>Scale page to fit the viewport width.</summary>
    FitWidth,

    /// <summary>Scale page to fit the viewport height.</summary>
    FitHeight
}
