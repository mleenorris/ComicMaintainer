namespace ComicMaintainer.Core.Models;

/// <summary>
/// A batch job's status and counters, without its file list and errors. Listing jobs only
/// needs these, and a job can track tens of thousands of files.
/// </summary>
public sealed class ProcessingJobSummary
{
    public Guid JobId { get; init; }
    public JobStatus Status { get; init; }
    public string OperationName { get; init; } = string.Empty;
    public int TotalFiles { get; init; }
    public int ProcessedFiles { get; init; }
    public int FailedFiles { get; init; }
    public DateTime StartTime { get; init; }
    public DateTime? EndTime { get; init; }
    public string? CurrentFile { get; init; }

    /// <summary>
    /// True once the job has reached a state it can never leave.
    /// </summary>
    public bool IsTerminal => Status.IsTerminal();
}
