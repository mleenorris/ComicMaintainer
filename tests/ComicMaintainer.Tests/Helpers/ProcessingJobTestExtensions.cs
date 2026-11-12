using ComicMaintainer.Core.Models;

namespace ComicMaintainer.Tests.Helpers;

/// <summary>
/// Test helpers for batch job models.
/// </summary>
internal static class ProcessingJobTestExtensions
{
    /// <summary>
    /// The summary of <paramref name="job"/> that a job registry would report.
    /// </summary>
    public static ProcessingJobSummary ToSummary(this ProcessingJob job) => new()
    {
        JobId = job.JobId,
        Status = job.Status,
        OperationName = job.OperationName,
        TotalFiles = job.TotalFiles,
        ProcessedFiles = job.ProcessedFiles,
        FailedFiles = job.FailedFiles,
        StartTime = job.StartTime,
        EndTime = job.EndTime,
        CurrentFile = job.CurrentFile
    };
}
