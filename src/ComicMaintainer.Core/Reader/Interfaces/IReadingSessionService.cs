using ComicMaintainer.Core.Reader.Models;

namespace ComicMaintainer.Core.Reader.Interfaces;

/// <summary>
/// Manages reading session lifecycle.
/// </summary>
public interface IReadingSessionService
{
    /// <summary>
    /// Starts a new reading session and returns it.
    /// If <paramref name="incognito"/> is <c>true</c> the session must not
    /// overwrite durable reading progress when it ends.
    /// </summary>
    Task<ReadingSession> StartSessionAsync(string userId, string contentId, int startPage, bool incognito = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ends the session identified by <paramref name="sessionId"/> that belongs
    /// to <paramref name="userId"/>, recording the final page reached.
    /// Returns <c>null</c>, and changes nothing, when no such session exists or
    /// it belongs to another user: a session id alone never lets a caller end
    /// someone else's session or move their reading progress.
    /// </summary>
    Task<ReadingSession?> EndSessionAsync(string userId, Guid sessionId, int endPage, CancellationToken cancellationToken = default);
}
