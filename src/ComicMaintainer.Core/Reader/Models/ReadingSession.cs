namespace ComicMaintainer.Core.Reader.Models;

/// <summary>
/// Represents a single reading session. Sessions can be incognito,
/// in which case they must not overwrite durable reading progress.
/// </summary>
public class ReadingSession
{
    /// <summary>Unique session identifier.</summary>
    public Guid SessionId { get; set; } = Guid.NewGuid();

    /// <summary>Identity of the user this session belongs to.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Identifier of the content being read.</summary>
    public string ContentId { get; set; } = string.Empty;

    /// <summary>UTC timestamp when the session started.</summary>
    public DateTime StartedAt { get; set; }

    /// <summary>UTC timestamp when the session ended; null if the session is still active.</summary>
    public DateTime? EndedAt { get; set; }

    /// <summary>1-based page number where the session started.</summary>
    public int StartPage { get; set; }

    /// <summary>1-based page number where the session ended; null if still active.</summary>
    public int? EndPage { get; set; }

    /// <summary>
    /// When true the session is incognito: activity is tracked for analytics
    /// but must not overwrite the user's durable <see cref="ReadingProgress"/>.
    /// </summary>
    public bool Incognito { get; set; }
}
