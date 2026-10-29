using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SharpCompress.Archives;
using SharpCompress.Archives.Zip;
using SharpCompress.Common;
using SharpCompress.Writers;
using SharpCompress.Writers.Zip;

namespace ComicMaintainer.Core.Services;

public partial class ComicProcessorService
{
    private async Task BroadcastJobStatusAsync(ProcessingJob job)
    {
        var snapshot = CloneJob(job);

        if (_eventBroadcaster != null)
        {
            await _eventBroadcaster.BroadcastJobUpdateAsync(
                snapshot.JobId,
                snapshot.Status.ToString().ToLower(),
                snapshot.ProcessedFiles + snapshot.FailedFiles,
                snapshot.TotalFiles,
                snapshot.ProcessedFiles,
                snapshot.FailedFiles);
        }

        await PersistJobStateAsync(snapshot);
    }

    /// <summary>
    /// Writes a job snapshot to durable storage, throttling mid-run progress writes.
    /// </summary>
    /// <remarks>
    /// This runs after every file in a batch, so persisting unconditionally would add a
    /// database write per file on top of the real work. Status changes and terminal states are
    /// always written because they are what a restart needs to see; progress between them is
    /// written at most once per <see cref="JobPersistenceInterval"/> and is only an optimisation
    /// for how much progress an interrupted job appears to have made.
    /// </remarks>
    private async Task PersistJobStateAsync(ProcessingJob snapshot)
    {
        if (_jobStateStore == null)
        {
            return;
        }

        await WithJobPersistenceLockAsync(snapshot.JobId, async () =>
        {
            if (_deletedPersistedJobs.ContainsKey(snapshot.JobId))
            {
                return;
            }

            // Status broadcasts are fire-and-forget. Serialize per-job writes so a late queued/
            // running save cannot land after a terminal one.
            if (_persistedTerminalJobs.ContainsKey(snapshot.JobId))
            {
                return;
            }

            var isTransition = !_lastPersistedStatus.TryGetValue(snapshot.JobId, out var lastStatus)
                || lastStatus != snapshot.Status;

            if (!isTransition)
            {
                var lastWrite = _lastPersistedAt.TryGetValue(snapshot.JobId, out var at) ? at : DateTime.MinValue;
                if (DateTime.UtcNow - lastWrite < JobPersistenceInterval)
                {
                    return;
                }
            }

            if (snapshot.IsTerminal && !_persistedTerminalJobs.TryAdd(snapshot.JobId, true))
            {
                // Another thread is already writing the terminal state for this job.
                return;
            }

            try
            {
                await _jobStateStore.SaveAsync(snapshot);
                _lastPersistedStatus[snapshot.JobId] = snapshot.Status;
                _lastPersistedAt[snapshot.JobId] = DateTime.UtcNow;

                if (snapshot.IsTerminal)
                {
                    // The job will never change again, so stop tracking its throttle state.
                    _lastPersistedStatus.TryRemove(snapshot.JobId, out _);
                    _lastPersistedAt.TryRemove(snapshot.JobId, out _);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (snapshot.IsTerminal)
                {
                    // Let a retry write the final state rather than leaving it permanently blocked.
                    _persistedTerminalJobs.TryRemove(snapshot.JobId, out _);
                }

                // Never let a persistence problem abort the batch the user asked for.
                _logger.LogWarning(ex, "Failed to persist state for job {JobId}", snapshot.JobId);
            }
        });
    }

    /// <summary>
    /// Adds jobs recovered from durable storage so the API can still answer for them.
    /// </summary>
    /// <remarks>
    /// Existing in-memory jobs win: a job created since startup is authoritative over whatever
    /// the database recorded for it.
    /// </remarks>
    public void RestoreJobs(IEnumerable<ProcessingJob> jobs)
    {
        ArgumentNullException.ThrowIfNull(jobs);

        foreach (var job in jobs)
        {
            _jobs.TryAdd(job.JobId, job);
        }
    }

    public ProcessingJob? GetJob(Guid jobId)
    {
        return _jobs.TryGetValue(jobId, out var job) ? CloneJob(job) : null;
    }

    public ProcessingJob? GetActiveJob()
    {
        return _jobs.Values
            .Select(CloneJob)
            .FirstOrDefault(j => j.Status == JobStatus.Running || j.Status == JobStatus.Queued);
    }

    public IEnumerable<ProcessingJob> GetAllJobs()
    {
        return _jobs.Values
            .Select(CloneJob)
            .OrderByDescending(j => j.StartTime);
    }

    public async Task<bool> DeleteJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        if (!_jobs.TryGetValue(jobId, out var job))
        {
            return false;
        }

        var snapshot = CloneJob(job);
        if (!snapshot.IsTerminal)
        {
            _logger.LogWarning("Cannot delete active job: {JobId}", jobId);
            return false;
        }

        if (_jobStateStore != null)
        {
            // Deleting only from memory would resurrect the job on the next restart.
            await WithJobPersistenceLockAsync(jobId, async () =>
            {
                _deletedPersistedJobs[jobId] = true;
                try
                {
                    await _jobStateStore.DeleteAsync(jobId, cancellationToken);
                }
                catch
                {
                    _deletedPersistedJobs.TryRemove(jobId, out _);
                    throw;
                }

                _persistedTerminalJobs.TryRemove(jobId, out _);
                _lastPersistedStatus.TryRemove(jobId, out _);
                _lastPersistedAt.TryRemove(jobId, out _);
            });
        }

        var removed = _jobs.TryRemove(jobId, out _);
        _jobCancellationTokens.TryRemove(jobId, out _);
        _jobSyncLocks.TryRemove(jobId, out _);

        return removed;
    }

    public bool CancelJob(Guid jobId)
    {
        if (_jobCancellationTokens.TryGetValue(jobId, out var cts))
        {
            _logger.LogInformation("Cancelling job: {JobId}", jobId);
            if (TryCancel(cts))
            {
                return true;
            }
        }
        
        _logger.LogWarning("Cannot cancel job {JobId}: no active cancellation token found", jobId);
        return false;
    }

    /// <summary>
    /// Signals a job to stop. Returns false when the job has already finished and disposed its
    /// token source, which it can do between a caller's lookup and this call.
    /// </summary>
    private static bool TryCancel(CancellationTokenSource cts)
    {
        try
        {
            cts.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private ProcessingJob CloneJob(ProcessingJob job)
    {
        var syncLock = GetJobSyncLock(job.JobId);
        lock (syncLock)
        {
            return new ProcessingJob
            {
                JobId = job.JobId,
                Status = job.Status,
                OperationName = job.OperationName,
                Files = new List<string>(job.Files),
                TotalFiles = job.TotalFiles,
                ProcessedFiles = job.ProcessedFiles,
                FailedFiles = job.FailedFiles,
                StartTime = job.StartTime,
                EndTime = job.EndTime,
                CurrentFile = job.CurrentFile,
                Errors = new Dictionary<string, string>(job.Errors)
            };
        }
    }

    private object GetJobSyncLock(Guid jobId)
    {
        return _jobSyncLocks.GetOrAdd(jobId, static _ => new object());
    }

    private async Task WithJobPersistenceLockAsync(Guid jobId, Func<Task> action)
    {
        var gate = _jobPersistenceLocks.GetOrAdd(jobId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            await action();
        }
        finally
        {
            gate.Release();
        }
    }
}
