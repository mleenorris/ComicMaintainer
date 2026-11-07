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

    /// <summary>
    /// Records that <paramref name="userId"/> is reading <paramref name="page"/> of
    /// the library file <paramref name="filePath"/>, the reader's per-page save.
    /// Both records of it are written in one transaction: the user's read-status
    /// row for the file (the page the reader resumes at) and, when
    /// <paramref name="totalPages"/> is known (greater than zero), the durable
    /// <see cref="ReadingProgress"/> behind "Continue Reading", whose
    /// <see cref="ReadingProgress.ContentId"/> is the file path. The page is
    /// clamped to <paramref name="totalPages"/> when that is known. Either row is
    /// created when missing; the read flag and the reader mode and direction of
    /// existing rows are left as they are.
    /// </summary>
    Task RecordPageAsync(string userId, string filePath, int page, int totalPages, CancellationToken cancellationToken = default);
}
