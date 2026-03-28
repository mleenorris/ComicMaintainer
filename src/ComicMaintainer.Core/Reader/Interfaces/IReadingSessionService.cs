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
    /// Ends an existing session identified by <paramref name="sessionId"/>,
    /// recording the final page reached.
    /// </summary>
    Task<ReadingSession?> EndSessionAsync(Guid sessionId, int endPage, CancellationToken cancellationToken = default);
}
