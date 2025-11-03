namespace ComicMaintainer.Core.Models;

/// <summary>
/// Represents a comic file in the system
/// </summary>
public class ComicFile
{
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Directory { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public DateTime LastModified { get; set; }
    public bool IsProcessed { get; set; }
    public bool IsRenamed { get; set; }
    public bool IsNormalized { get; set; }
    public bool IsDuplicate { get; set; }
    public ComicMetadata? Metadata { get; set; }
}

/// <summary>
/// Represents comic metadata
/// </summary>
public class ComicMetadata
{
    public string? Series { get; set; }
    public string? Title { get; set; }
    public string? Issue { get; set; }
    public string? Volume { get; set; }
    public string? Publisher { get; set; }
    public int? Year { get; set; }
    public string? Summary { get; set; }
    public List<string> Authors { get; set; } = new();
    public List<string> Tags { get; set; } = new();
}

/// <summary>
/// Represents processing history entry
/// </summary>
public class ProcessingHistoryEntry
{
    public Guid Id { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    
    // Before/After tracking for changes
    public string? BeforeFilename { get; set; }
    public string? AfterFilename { get; set; }
    public string? BeforeTitle { get; set; }
    public string? AfterTitle { get; set; }
    public string? BeforeSeries { get; set; }
    public string? AfterSeries { get; set; }
    public string? BeforeIssue { get; set; }
    public string? AfterIssue { get; set; }
    public string? BeforePublisher { get; set; }
    public string? AfterPublisher { get; set; }
    public int? BeforeYear { get; set; }
    public int? AfterYear { get; set; }
    public string? BeforeVolume { get; set; }
    public string? AfterVolume { get; set; }
}

/// <summary>
/// Job result status
/// </summary>
public enum JobStatus
{
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled
}

/// <summary>
/// Represents a batch processing job
/// </summary>
public class ProcessingJob
{
    public Guid JobId { get; set; }
    public JobStatus Status { get; set; }
    public List<string> Files { get; set; } = new();
    public int TotalFiles { get; set; }
    public int ProcessedFiles { get; set; }
    public int FailedFiles { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public string? CurrentFile { get; set; }
    public Dictionary<string, string> Errors { get; set; } = new();
}
