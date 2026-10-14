namespace ComicMaintainer.Core.Models;

/// <summary>
/// Result of probing the configured Suwayomi server.
/// </summary>
public class SuwayomiConnectionStatus
{
    /// <summary>True when the Suwayomi integration is enabled in settings.</summary>
    public bool Enabled { get; set; }

    /// <summary>True when a base URL has been configured.</summary>
    public bool Configured { get; set; }

    /// <summary>True when the server answered the probe successfully.</summary>
    public bool Connected { get; set; }

    /// <summary>Server name reported by Suwayomi (e.g. "Suwayomi-Server").</summary>
    public string? ServerName { get; set; }

    /// <summary>Server version reported by Suwayomi.</summary>
    public string? Version { get; set; }

    /// <summary>Human readable failure reason when <see cref="Connected"/> is false.</summary>
    public string? Error { get; set; }
}

/// <summary>
/// A manga/series entry in the Suwayomi library.
/// </summary>
public class SuwayomiSeries
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public bool InLibrary { get; set; }
    public string? Source { get; set; }
    public string? ThumbnailUrl { get; set; }
}

/// <summary>
/// A chapter belonging to a <see cref="SuwayomiSeries"/>.
/// </summary>
public class SuwayomiChapter
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public double ChapterNumber { get; set; }
    public bool IsDownloaded { get; set; }
}

/// <summary>
/// Outcome of asking Suwayomi to download the issues a series is missing.
/// </summary>
public class SuwayomiDownloadResult
{
    /// <summary>The Suwayomi series the issues were matched against.</summary>
    public SuwayomiSeries? Series { get; set; }

    /// <summary>Chapters that were added to the Suwayomi download queue.</summary>
    public List<SuwayomiChapter> QueuedChapters { get; set; } = new();

    /// <summary>
    /// Requested issue numbers that Suwayomi has no (non-downloaded) chapter
    /// for, so nothing could be queued for them.
    /// </summary>
    public List<int> UnmatchedIssueNumbers { get; set; } = new();

    /// <summary>
    /// Requested issue numbers whose chapter is already downloaded on the
    /// Suwayomi side (so it was not queued again).
    /// </summary>
    public List<int> AlreadyDownloadedIssueNumbers { get; set; } = new();
}

/// <summary>
/// Raised when a Suwayomi request cannot be completed (not configured,
/// unreachable, or the server returned an error).
/// </summary>
public class SuwayomiException : Exception
{
    public SuwayomiException(string message) : base(message)
    {
    }

    public SuwayomiException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
