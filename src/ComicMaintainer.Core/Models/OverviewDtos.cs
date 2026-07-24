using System.Text.Json.Serialization;

namespace ComicMaintainer.Core.Models;

/// <summary>
/// Internal per-series projection used to build the overview page. Carries the
/// series summary card plus the timestamps and file paths needed to bucket a
/// series into the Continue Reading / Series Updates / Newly Added rows.
/// </summary>
public class SeriesOverviewEntry
{
    public SeriesSummaryDto Summary { get; set; } = new();

    /// <summary>Earliest file CreatedAt for the series (when it first appeared).</summary>
    public DateTime EarliestCreatedAt { get; set; }

    /// <summary>Latest file CreatedAt for the series (most recent file added).</summary>
    public DateTime LatestCreatedAt { get; set; }

    /// <summary>Every tracked file path belonging to the series.</summary>
    public List<string> FilePaths { get; set; } = new();

    /// <summary>
    /// File paths within the series that are marked read via the file read flag
    /// (<see cref="ComicFile.IsRead"/>). This is independent of per-user reading
    /// progress and lets Continue Reading treat issues marked read outside the
    /// reader as read so a fully-read series is not kept on the row.
    /// </summary>
    public List<string> ReadFilePaths { get; set; } = new();
}

/// <summary>
/// A series card shown on the overview page. Extends the standard summary card
/// with optional "resume reading" hints used by the Continue Reading row.
/// </summary>
public class OverviewSeriesCard : SeriesSummaryDto
{
    /// <summary>File path of the issue to resume (Continue Reading row only).</summary>
    [JsonPropertyName("resume_file_path")]
    public string? ResumeFilePath { get; set; }

    /// <summary>Page to resume on within <see cref="ResumeFilePath"/>.</summary>
    [JsonPropertyName("resume_page")]
    public int? ResumePage { get; set; }

    /// <summary>UTC timestamp the user last read an issue in this series.</summary>
    [JsonPropertyName("last_read_utc")]
    public DateTime? LastReadUtc { get; set; }
}

/// <summary>
/// Payload for the overview/home page: three independently-bucketed series rows.
/// </summary>
public class OverviewResult
{
    /// <summary>
    /// Series the current user is still working through: an issue is partially
    /// read, or an issue was completed while later issues remain unread.
    /// </summary>
    [JsonPropertyName("continue_reading")]
    public List<OverviewSeriesCard> ContinueReading { get; set; } = new();

    /// <summary>Existing series that gained a new file within the recency window.</summary>
    [JsonPropertyName("series_updates")]
    public List<OverviewSeriesCard> SeriesUpdates { get; set; } = new();

    /// <summary>Series that first appeared within the recency window.</summary>
    [JsonPropertyName("newly_added_series")]
    public List<OverviewSeriesCard> NewlyAddedSeries { get; set; } = new();
}
