namespace ComicMaintainer.Core.Models;

public class MetadataRefreshJob
{
    public Guid JobId { get; set; }
    public JobStatus Status { get; set; }
    public List<string> Series { get; set; } = new();
    public int TotalSeries { get; set; }
    public int ProcessedSeries { get; set; }
    public int Successes { get; set; }
    public int NotFound { get; set; }
    public int Failures { get; set; }
    public string? CurrentSeries { get; set; }
    public DateTime StartTime { get; set; } = DateTime.UtcNow;
    public DateTime? EndTime { get; set; }

    /// <summary>
    /// Rolling list of the most recent per-series outcomes. Bounded to a small
    /// number of entries so the UI can render a "live" trail of refresh
    /// activity without unbounded memory growth.
    /// </summary>
    public List<MetadataRefreshOutcome> RecentResults { get; set; } = new();
}

public class MetadataRefreshOutcome
{
    public string SeriesTitle { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Source { get; set; }
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
}
