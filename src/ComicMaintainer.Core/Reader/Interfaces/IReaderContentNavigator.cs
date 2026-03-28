using ComicMaintainer.Core.Reader.Models;

namespace ComicMaintainer.Core.Reader.Interfaces;

/// <summary>
/// Provides platform-neutral content navigation utilities for the reader.
/// Implementations may differ between web/PWA and MAUI contexts.
/// </summary>
public interface IReaderContentNavigator
{
    /// <summary>
    /// Returns the <see cref="ContentLocation"/> for the next logical position
    /// after <paramref name="current"/> given the supplied <paramref name="mode"/>
    /// and <paramref name="direction"/>.
    /// Returns <c>null</c> when already at the end of the content.
    /// </summary>
    ContentLocation? GetNextLocation(ContentLocation current, int totalPages, ReaderMode mode, ReadingDirection direction);

    /// <summary>
    /// Returns the <see cref="ContentLocation"/> for the previous logical position
    /// before <paramref name="current"/> given the supplied <paramref name="mode"/>
    /// and <paramref name="direction"/>.
    /// Returns <c>null</c> when already at the beginning of the content.
    /// </summary>
    ContentLocation? GetPreviousLocation(ContentLocation current, int totalPages, ReaderMode mode, ReadingDirection direction);
}
