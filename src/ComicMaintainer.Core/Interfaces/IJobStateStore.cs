using ComicMaintainer.Core.Models;

namespace ComicMaintainer.Core.Interfaces;

/// <summary>
/// Durable storage for batch processing jobs, so that job state survives a process restart.
/// </summary>
/// <remarks>
/// Batch jobs live in memory while they run. Without persistence a restart — including the
/// application's own <c>POST /api/settings/restart</c> — silently discards them, leaving the
/// UI polling a job id the server no longer recognises. This store lets the server record
/// what was in flight and report a definite <see cref="JobStatus.Interrupted"/> outcome
/// afterwards.
/// </remarks>
public interface IJobStateStore
{
    /// <summary>
    /// Inserts or updates the durable record for <paramref name="job"/>.
    /// </summary>
    Task SaveAsync(ProcessingJob job, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns persisted jobs, most recently started first.
    /// </summary>
    Task<IReadOnlyList<ProcessingJob>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the durable record for a job. Returns false when no record existed.
    /// </summary>
    Task<bool> DeleteAsync(Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks every job still recorded as queued or running as
    /// <see cref="JobStatus.Interrupted"/>, and returns those jobs in their updated form.
    /// </summary>
    /// <remarks>
    /// Called once during startup. Any job that is still non-terminal in the database at that
    /// point cannot be running, because the process that owned it no longer exists.
    /// </remarks>
    Task<IReadOnlyList<ProcessingJob>> MarkInterruptedAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes terminal jobs that finished before <paramref name="cutoff"/>, keeping at most
    /// <paramref name="keepMostRecent"/> of them regardless of age. Returns the number deleted.
    /// </summary>
    Task<int> PruneAsync(DateTime cutoff, int keepMostRecent, CancellationToken cancellationToken = default);
}
