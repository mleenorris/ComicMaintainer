namespace ComicMaintainer.Core.Models;

/// <summary>
/// Runtime availability of the Suwayomi sidecar integration, surfaced to the UI
/// so the download actions can be shown/hidden and explained.
/// </summary>
public sealed class SuwayomiAvailability
{
    /// <summary>True when the integration is enabled in settings.</summary>
    public bool Enabled { get; init; }

    /// <summary>True when a base URL is configured.</summary>
    public bool Configured { get; init; }

    /// <summary>
    /// Null when not probed; otherwise whether the Suwayomi GraphQL endpoint
    /// responded successfully to a lightweight reachability probe.
    /// </summary>
    public bool? Reachable { get; init; }

    /// <summary>Human-readable status for the UI / logs.</summary>
    public string? StatusMessage { get; init; }
}

/// <summary>
/// A series tracked in Suwayomi's library that we matched to one of our series.
/// The bound <see cref="SourceName"/> is what determines which extension the
/// downloads come from.
/// </summary>
public sealed class SuwayomiSeriesMatch
{
    public int MangaId { get; init; }
    public string Title { get; init; } = string.Empty;
    public long SourceId { get; init; }
    public string? SourceName { get; init; }

    /// <summary>Confidence score (0-100) of the title match.</summary>
    public double MatchScore { get; init; }
}

/// <summary>A chapter as reported by Suwayomi for a given manga.</summary>
public sealed class SuwayomiChapter
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public double ChapterNumber { get; init; }
    public bool IsDownloaded { get; init; }
}

/// <summary>Per-issue outcome of a download request.</summary>
public sealed class SuwayomiIssueDownloadResult
{
    public string Issue { get; init; } = string.Empty;
    public bool Enqueued { get; init; }
    public string Status { get; init; } = string.Empty;
}

/// <summary>Aggregate result of a download request for one or more issues.</summary>
public sealed class SuwayomiDownloadResult
{
    /// <summary>True when the overall request succeeded (series matched, request sent).</summary>
    public bool Success { get; init; }

    /// <summary>Human-readable message describing the outcome.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>The Suwayomi series we matched, when one was found.</summary>
    public SuwayomiSeriesMatch? Match { get; init; }

    /// <summary>Per-issue results.</summary>
    public IReadOnlyList<SuwayomiIssueDownloadResult> Issues { get; init; }
        = Array.Empty<SuwayomiIssueDownloadResult>();

    public static SuwayomiDownloadResult Failed(string message) =>
        new() { Success = false, Message = message };
}
