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
}
