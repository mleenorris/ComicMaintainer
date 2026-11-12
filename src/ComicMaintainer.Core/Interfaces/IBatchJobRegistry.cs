using ComicMaintainer.Core.Models;

namespace ComicMaintainer.Core.Interfaces;

/// <summary>
/// Runs batch jobs and owns everything about them: state, cancellation, persistence and how
/// long a finished job is remembered. What a job does to each item is supplied by the caller.
/// </summary>
public interface IBatchJobRegistry
{
    /// <summary>
    /// Registers a job over <paramref name="items"/> and starts it in the background. Null and
    /// blank items are skipped. Returns immediately with the job's id.
    /// </summary>
    /// <param name="definition">What the job does.</param>
    /// <param name="items">The items to run it for, usually file paths.</param>
    /// <param name="cancellationToken">Cancels the job; a request token would end the job with the request.</param>
    Guid StartJob(BatchJobDefinition definition, IEnumerable<string> items, CancellationToken cancellationToken = default);

    /// <summary>
    /// Generic background job entry point used by callers that need to run a
    /// custom per-item operation through the standard job pipeline (i.e.
    /// surface progress + completion via <c>job_updated</c> / <c>file_processed</c>
    /// SSE events and the existing UI progress modal).
    /// </summary>
    /// <param name="operationName">Stable identifier used in log messages.</param>
    /// <param name="trackedItems">
    ///   The list of items the job will iterate over. Typically file paths,
    ///   but can be any string identifier — the job's <see cref="ProcessingJob.Files"/>
    ///   list mirrors this collection so the UI can render per-item progress.
    /// </param>
    /// <param name="itemOperation">
    ///   Per-item operation invoked once per <paramref name="trackedItems"/>
    ///   entry. Returning <c>false</c> increments the job's failure count and
    ///   records <paramref name="failureMessage"/> as the error for that item.
    ///   It runs without the per-file <see cref="IFileOperationLock"/>: an item
    ///   that moves, rewrites or deletes an archive itself must hold that file's
    ///   lock while it does, and must not call a public file operation of
    ///   <see cref="IComicProcessorService"/> on the same file meanwhile (the
    ///   lock is not reentrant).
    /// </param>
    /// <param name="failureMessage">Error string recorded when <paramref name="itemOperation"/> returns false.</param>
    /// <param name="postLoopAsync">
    ///   Optional cleanup / follow-up work that runs after the per-item loop
    ///   completes but before the job is marked <c>Completed</c>. Exceptions
    ///   from this hook flip the job to <c>Failed</c>.
    /// </param>
    /// <param name="cancellationToken">Cancels the job.</param>
    Task<Guid> RunCustomBatchJobAsync(
        string operationName,
        IEnumerable<string> trackedItems,
        Func<string, CancellationToken, Task<bool>> itemOperation,
        string failureMessage,
        Func<CancellationToken, Task>? postLoopAsync = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Same as the other <c>RunCustomBatchJobAsync</c> overload, but the
    /// post-loop hook also receives an <see cref="IBatchJobReport"/> to report
    /// problems with things that were not tracked items (for example a source
    /// folder that had to be kept).
    /// </summary>
    Task<Guid> RunCustomBatchJobAsync(
        string operationName,
        IEnumerable<string> trackedItems,
        Func<string, CancellationToken, Task<bool>> itemOperation,
        string failureMessage,
        Func<IBatchJobReport, CancellationToken, Task> postLoopAsync,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// A full copy of the job, including its file list and errors, or null when no such job
    /// is remembered.
    /// </summary>
    ProcessingJob? GetJob(Guid jobId);

    /// <summary>
    /// The job currently queued or running, if any.
    /// </summary>
    ProcessingJobSummary? GetActiveJob();

    /// <summary>
    /// Every remembered job, most recently started first.
    /// </summary>
    IReadOnlyList<ProcessingJobSummary> GetJobSummaries();

    /// <summary>
    /// Waits until the job reaches a terminal state and returns its final state. Returns null
    /// when no such job is remembered. A job that has already finished returns at once.
    /// </summary>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled first. The job itself keeps running.
    /// </exception>
    Task<ProcessingJob?> WaitForCompletionAsync(Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Forgets a finished job, in memory and in durable storage. Returns false when the job
    /// is unknown or has not finished.
    /// </summary>
    Task<bool> DeleteJobAsync(Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks a running job to stop at its next cancellation check. Returns false when the job
    /// is unknown or has already finished.
    /// </summary>
    bool CancelJob(Guid jobId);

    /// <summary>
    /// Asks every job still running to stop, and returns how many were asked.
    /// </summary>
    int CancelActiveJobs();

    /// <summary>
    /// Add jobs recovered from durable storage to the in-memory job list, so the API can still
    /// answer for jobs that were created before the process restarted. Jobs already present in
    /// memory are left untouched.
    /// </summary>
    void RestoreJobs(IEnumerable<ProcessingJob> jobs);
}
