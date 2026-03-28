namespace ComicMaintainer.Core.Reader.Models;

/// <summary>
/// Represents durable per-user reading position and completion state for a piece of content.
/// </summary>
public class ReadingProgress
{
    /// <summary>Identity of the user this progress belongs to.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Identifier of the content (e.g. file path or comic ID).</summary>
    public string ContentId { get; set; } = string.Empty;

    /// <summary>1-based page number the user was last reading.</summary>
    public int CurrentPage { get; set; }

    /// <summary>Total number of pages in the content.</summary>
    public int TotalPages { get; set; }

    /// <summary>Percentage of the content read (0–100), derived from CurrentPage / TotalPages.</summary>
    public double PercentComplete { get; set; }

    /// <summary>UTC timestamp of the last read activity.</summary>
    public DateTime LastReadAt { get; set; }

    /// <summary>UTC timestamp when the content was fully completed; null if not yet completed.</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>The reader mode in use when progress was last saved.</summary>
    public ReaderMode LastReaderMode { get; set; }

    /// <summary>The reading direction in use when progress was last saved.</summary>
    public ReadingDirection ReadingDirection { get; set; }
}
