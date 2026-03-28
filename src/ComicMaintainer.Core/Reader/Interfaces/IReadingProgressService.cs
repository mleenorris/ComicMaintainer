using ComicMaintainer.Core.Reader.Models;

namespace ComicMaintainer.Core.Reader.Interfaces;

/// <summary>
/// Manages durable per-user reading progress.
/// </summary>
public interface IReadingProgressService
{
    /// <summary>
    /// Returns the reading progress for the given user and content,
    /// or <c>null</c> if no progress has been saved yet.
    /// </summary>
    Task<ReadingProgress?> GetProgressAsync(string userId, string contentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves (creates or updates) reading progress for the given user and content.
    /// </summary>
    Task SaveProgressAsync(ReadingProgress progress, CancellationToken cancellationToken = default);
}
