using ComicMaintainer.Core.Interfaces;

namespace ComicMaintainer.Core.Models;

/// <summary>
/// Outcome of one item of a batch job. A failed item is counted in
/// <see cref="ProcessingJob.FailedFiles"/> and its <see cref="Error"/> (or the job's generic
/// failure message) is recorded in <see cref="ProcessingJob.Errors"/>.
/// </summary>
public readonly record struct BatchItemResult(bool Success, string? Error = null)
{
    public static implicit operator BatchItemResult(bool success) => new(success);
}

/// <summary>
/// What a batch job does: the per-item work <see cref="IBatchJobRegistry"/> runs for each
/// tracked item, and how the job is described.
/// </summary>
/// <param name="OperationName">Stable identifier used in logs, metrics and the job record.</param>
/// <param name="ActionDescription">Lower-case verb phrase used in log messages.</param>
/// <param name="ItemOperation">Runs for each tracked item, up to MaxWorkers at a time.</param>
/// <param name="FailureMessage">Recorded for an item that fails without its own error.</param>
/// <param name="PostLoopAsync">
/// Optional follow-up work after the per-item loop, before the job is marked
/// <see cref="JobStatus.Completed"/>. An exception from it fails the job.
/// </param>
public sealed record BatchJobDefinition(
    string OperationName,
    string ActionDescription,
    Func<string, CancellationToken, Task<BatchItemResult>> ItemOperation,
    string FailureMessage,
    Func<IBatchJobReport, CancellationToken, Task>? PostLoopAsync = null);
