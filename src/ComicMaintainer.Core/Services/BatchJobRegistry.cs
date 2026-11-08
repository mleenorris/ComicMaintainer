using System.Collections.Concurrent;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Telemetry;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Default <see cref="IBatchJobRegistry"/>.
/// </summary>
/// <remarks>
/// Everything known about a job lives in one <see cref="JobEntry"/>, so forgetting a job (when
/// the user deletes it, or under <see cref="BatchJobRetention"/>) releases all of it at once.
/// </remarks>
public sealed class BatchJobRegistry : IBatchJobRegistry, IDisposable
{
    /// <summary>
    /// Minimum spacing between durable progress writes for a job that has not changed status.
    /// </summary>
    private static readonly TimeSpan JobPersistenceInterval = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<Guid, JobEntry> _entries = new();
    private readonly ILogger<BatchJobRegistry> _logger;
    private readonly IJobStateStore? _jobStateStore;
    private readonly IEventBroadcaster? _eventBroadcaster;
    private readonly ComicMaintainerMetrics? _metrics;
    private readonly int _maxWorkers;

    public BatchJobRegistry(
        IOptionsMonitor<AppSettings> settings,
        ILogger<BatchJobRegistry> logger,
        IJobStateStore? jobStateStore = null,
        IEventBroadcaster? eventBroadcaster = null,
        ComicMaintainerMetrics? metrics = null)
    {
        _logger = logger;
        _jobStateStore = jobStateStore;
        _eventBroadcaster = eventBroadcaster;
        _metrics = metrics;
        _maxWorkers = Math.Max(1, settings.CurrentValue.MaxWorkers);
    }

    /// <summary>
    /// A job and all of the registry's state about it.
    /// </summary>
    private sealed class JobEntry(ProcessingJob job)
    {
        public ProcessingJob Job { get; } = job;

        /// <summary>Guards the mutable fields of <see cref="Job"/>.</summary>
        public object Sync { get; } = new();

        /// <summary>Set while the job runs; a finished job has nothing left to cancel.</summary>
        public volatile CancellationTokenSource? Cancellation;

        /// <summary>Set once the job has finished and its final state has been published.</summary>
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Serializes durable writes for this job; the persistence fields below are only read
        /// or written while holding it.
        /// </summary>
        public SemaphoreSlim PersistenceGate { get; } = new(1, 1);

        public JobStatus? LastPersistedStatus;
        public DateTime LastPersistedAt;
        public bool TerminalPersisted;
        public bool Deleted;
    }

    /// <summary>
    /// The <see cref="IBatchJobReport"/> a post-loop hook writes to: problems go into the job's
    /// errors, under the job's lock like the per-item results, and are announced like a failed
    /// item.
    /// </summary>
    private sealed class BatchJobReport(BatchJobRegistry owner, JobEntry entry) : IBatchJobReport
    {
        public async Task ReportProblemAsync(string subject, string message)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(subject);
            lock (entry.Sync)
            {
                entry.Job.Errors[subject] = message;
            }

            if (owner._eventBroadcaster != null)
            {
                var name = Path.GetFileName(subject.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                await owner._eventBroadcaster.BroadcastFileProcessedAsync(
                    string.IsNullOrEmpty(name) ? subject : name,
                    false,
                    message);
            }
        }
    }

    /// <inheritdoc />
    public Guid StartJob(BatchJobDefinition definition, IEnumerable<string> items, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var jobId = Guid.NewGuid();
        var trackedItems = TrackableItems(items, definition.OperationName, jobId);

        _logger.LogDebug(
            "{OperationName}: Job {JobId} created and queued with {FileCount} files using up to {MaxWorkers} workers",
            definition.OperationName, jobId, trackedItems.Count, _maxWorkers);

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var entry = new JobEntry(new ProcessingJob
        {
            JobId = jobId,
            Status = JobStatus.Queued,
            OperationName = definition.OperationName,
            Files = trackedItems,
            TotalFiles = trackedItems.Count,
            StartTime = DateTime.UtcNow
        })
        {
            Cancellation = cancellation
        };
        _entries[jobId] = entry;

        // CancellationToken.None so the delegate always runs: if the token is already cancelled
        // (e.g. the request was aborted), it must end the job as Cancelled and release the token
        // source, rather than being skipped and leaving the job Queued forever.
        _ = Task.Run(() => RunAsync(entry, definition, cancellation), CancellationToken.None);

        return jobId;
    }

    /// <inheritdoc />
    public Task<Guid> RunCustomBatchJobAsync(
        string operationName,
        IEnumerable<string> trackedItems,
        Func<string, CancellationToken, Task<bool>> itemOperation,
        string failureMessage,
        Func<CancellationToken, Task>? postLoopAsync = null,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(StartJob(
            CustomJob(operationName, itemOperation, failureMessage, postLoopAsync is null ? null : (_, token) => postLoopAsync(token)),
            trackedItems,
            cancellationToken));
    }

    /// <inheritdoc />
    public Task<Guid> RunCustomBatchJobAsync(
        string operationName,
        IEnumerable<string> trackedItems,
        Func<string, CancellationToken, Task<bool>> itemOperation,
        string failureMessage,
        Func<IBatchJobReport, CancellationToken, Task> postLoopAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(postLoopAsync);
        return Task.FromResult(StartJob(
            CustomJob(operationName, itemOperation, failureMessage, postLoopAsync),
            trackedItems,
            cancellationToken));
    }

    private static BatchJobDefinition CustomJob(
        string operationName,
        Func<string, CancellationToken, Task<bool>> itemOperation,
        string failureMessage,
        Func<IBatchJobReport, CancellationToken, Task>? postLoopAsync)
    {
        return new BatchJobDefinition(
            operationName,
            operationName.ToLowerInvariant(),
            async (item, token) => await itemOperation(item, token),
            failureMessage,
            postLoopAsync);
    }

    /// <inheritdoc />
    public ProcessingJob? GetJob(Guid jobId)
    {
        return _entries.TryGetValue(jobId, out var entry) ? Copy(entry) : null;
    }

    /// <inheritdoc />
    public ProcessingJobSummary? GetActiveJob()
    {
        return _entries.Values
            .Select(Summarize)
            .FirstOrDefault(job => !job.IsTerminal);
    }

    /// <inheritdoc />
    public IReadOnlyList<ProcessingJobSummary> GetJobSummaries()
    {
        return _entries.Values
            .Select(Summarize)
            .OrderByDescending(job => job.StartTime)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<ProcessingJob?> WaitForCompletionAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        // Waiting on a task that is already complete ignores the token, and a caller that has
        // been cancelled must not carry on as if it had seen the job through.
        cancellationToken.ThrowIfCancellationRequested();

        if (!_entries.TryGetValue(jobId, out var entry))
        {
            return null;
        }

        await entry.Completion.Task.WaitAsync(cancellationToken);
        return Copy(entry);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        if (!_entries.TryGetValue(jobId, out var entry))
        {
            return false;
        }

        if (!Summarize(entry).IsTerminal)
        {
            _logger.LogWarning("Cannot delete active job: {JobId}", jobId);
            return false;
        }

        if (_jobStateStore != null)
        {
            // Deleting only from memory would resurrect the job on the next restart.
            await entry.PersistenceGate.WaitAsync(CancellationToken.None);
            try
            {
                entry.Deleted = true;
                try
                {
                    await _jobStateStore.DeleteAsync(jobId, cancellationToken);
                }
                catch
                {
                    entry.Deleted = false;
                    throw;
                }
            }
            finally
            {
                entry.PersistenceGate.Release();
            }
        }

        return _entries.TryRemove(jobId, out _);
    }

    /// <inheritdoc />
    public bool CancelJob(Guid jobId)
    {
        if (_entries.TryGetValue(jobId, out var entry) && entry.Cancellation is { } cancellation)
        {
            _logger.LogInformation("Cancelling job: {JobId}", jobId);
            if (TryCancel(cancellation))
            {
                return true;
            }
        }

        _logger.LogWarning("Cannot cancel job {JobId}: no active cancellation token found", jobId);
        return false;
    }

    /// <inheritdoc />
    public int CancelActiveJobs()
    {
        var cancelled = 0;
        foreach (var entry in _entries.Values)
        {
            if (entry.Cancellation is { } cancellation && TryCancel(cancellation))
            {
                cancelled++;
            }
        }

        return cancelled;
    }

    /// <inheritdoc />
    public void RestoreJobs(IEnumerable<ProcessingJob> jobs)
    {
        ArgumentNullException.ThrowIfNull(jobs);

        foreach (var job in jobs)
        {
            // Nothing will ever run a restored job, so nothing may wait for it.
            var entry = new JobEntry(job);
            entry.Completion.TrySetResult();

            // Existing in-memory jobs win: a job created since startup is authoritative over
            // whatever the database recorded for it.
            _entries.TryAdd(job.JobId, entry);
        }
    }

    public void Dispose() => CancelActiveJobs();

    /// <summary>
    /// Signals a job to stop. Returns false when the job has already finished and disposed its
    /// token source, which it can do between a caller's lookup and this call.
    /// </summary>
    private static bool TryCancel(CancellationTokenSource cancellation)
    {
        try
        {
            cancellation.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private List<string> TrackableItems(IEnumerable<string> items, string operationName, Guid jobId)
    {
        var all = items?.ToList() ?? new List<string>();
        var trackable = all.Where(item => !string.IsNullOrWhiteSpace(item)).ToList();

        if (trackable.Count != all.Count)
        {
            _logger.LogWarning(
                "{OperationName}: Skipping {SkippedCount} null or empty file path(s) in job {JobId}",
                operationName, all.Count - trackable.Count, jobId);
        }

        return trackable;
    }

    private async Task RunAsync(JobEntry entry, BatchJobDefinition definition, CancellationTokenSource cancellation)
    {
        using (cancellation)
        {
            // Also the outcome of an exception that the catches below let through (out of
            // memory): the job must not stay active forever after its runner has gone.
            var outcome = JobStatus.Failed;
            try
            {
                await ExecuteAsync(entry, definition, cancellation.Token);
                outcome = JobStatus.Completed;
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("{OperationName}: Job cancelled: {JobId}", definition.OperationName, entry.Job.JobId);
                outcome = JobStatus.Cancelled;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogError(ex, "{OperationName}: Error processing batch job: {JobId}", definition.OperationName, entry.Job.JobId);
            }
            finally
            {
                await FinishAsync(entry, definition.OperationName, outcome);
            }
        }

        await ForgetExpiredJobsAsync();
    }

    private async Task ExecuteAsync(JobEntry entry, BatchJobDefinition definition, CancellationToken cancellationToken)
    {
        var job = entry.Job;
        var operationName = definition.OperationName;

        await PublishAsync(entry);
        cancellationToken.ThrowIfCancellationRequested();

        _logger.LogDebug("{OperationName}: Job {JobId} starting execution", operationName, job.JobId);
        lock (entry.Sync)
        {
            job.Status = JobStatus.Running;
        }
        await PublishAsync(entry);

        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = _maxWorkers
        };

        await Parallel.ForEachAsync(
            job.Files.Select((file, index) => (File: file, Index: index)),
            parallelOptions,
            async (item, token) =>
            {
                _logger.LogDebug("{OperationName}: Job {JobId} {ActionDescription} file {FileIndex}/{TotalFiles}: {FilePath}",
                    operationName,
                    job.JobId,
                    definition.ActionDescription,
                    item.Index + 1,
                    job.TotalFiles,
                    LoggingHelper.SanitizePathForLog(item.File));

                var result = await definition.ItemOperation(item.File, token);
                var success = result.Success;
                var error = success ? null : result.Error ?? definition.FailureMessage;
                _metrics?.RecordFileProcessed(operationName, success);

                lock (entry.Sync)
                {
                    job.CurrentFile = item.File;
                    if (success)
                    {
                        job.ProcessedFiles++;
                    }
                    else
                    {
                        job.FailedFiles++;
                        job.Errors[item.File] = error!;
                    }
                }

                _logger.LogDebug("{OperationName}: Job {JobId} completed file {FileIndex}/{TotalFiles} with success={Success}: {FilePath}",
                    operationName,
                    job.JobId,
                    item.Index + 1,
                    job.TotalFiles,
                    success,
                    LoggingHelper.SanitizePathForLog(item.File));

                await PublishAsync(entry);

                if (_eventBroadcaster != null)
                {
                    await _eventBroadcaster.BroadcastFileProcessedAsync(Path.GetFileName(item.File), success, error);
                }
            });

        if (definition.PostLoopAsync is not null)
        {
            _logger.LogDebug("{OperationName}: Job {JobId} running post-loop hook", operationName, job.JobId);
            await definition.PostLoopAsync(new BatchJobReport(this, entry), cancellationToken);
        }
    }

    /// <summary>
    /// Records a job's outcome, publishes it, and only then releases whoever is waiting, so a
    /// waiter never sees a job finished that durable storage does not yet record as finished.
    /// </summary>
    private async Task FinishAsync(JobEntry entry, string operationName, JobStatus outcome)
    {
        entry.Cancellation = null;

        ProcessingJobSummary finished;
        lock (entry.Sync)
        {
            entry.Job.Status = outcome;
            entry.Job.EndTime = DateTime.UtcNow;
            finished = Summarize(entry);
        }

        _metrics?.RecordProcessingJob(operationName, outcome.ToString(), finished.EndTime!.Value - finished.StartTime);

        if (outcome == JobStatus.Completed)
        {
            _logger.LogInformation(
                "{OperationName}: Job {JobId} completed - Processed: {ProcessedFiles}, Failed: {FailedFiles}, Total: {TotalFiles}",
                operationName, finished.JobId, finished.ProcessedFiles, finished.FailedFiles, finished.TotalFiles);
        }

        try
        {
            await PublishAsync(entry);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The job is over either way; a failed announcement must not change how it ended.
            _logger.LogWarning(ex, "Failed to publish the final state of job {JobId}", finished.JobId);
        }
        finally
        {
            entry.Completion.TrySetResult();
        }
    }

    /// <summary>
    /// Announces a job's current state and writes it to durable storage.
    /// </summary>
    private async Task PublishAsync(JobEntry entry)
    {
        if (_eventBroadcaster != null)
        {
            var job = Summarize(entry);
            await _eventBroadcaster.BroadcastJobUpdateAsync(
                job.JobId,
                job.Status.ToString().ToLower(),
                job.ProcessedFiles + job.FailedFiles,
                job.TotalFiles,
                job.ProcessedFiles,
                job.FailedFiles);
        }

        await PersistAsync(entry);
    }

    /// <summary>
    /// Writes a job to durable storage, throttling mid-run progress writes.
    /// </summary>
    /// <remarks>
    /// This runs after every file in a batch, so persisting unconditionally would add a
    /// database write per file on top of the real work. Status changes and terminal states are
    /// always written because they are what a restart needs to see; progress between them is
    /// written at most once per <see cref="JobPersistenceInterval"/> and is only an optimisation
    /// for how much progress an interrupted job appears to have made. The state is read after
    /// the gate is taken, so a write can never put an older state over a newer one.
    /// </remarks>
    private async Task PersistAsync(JobEntry entry)
    {
        if (_jobStateStore == null)
        {
            return;
        }

        await entry.PersistenceGate.WaitAsync(CancellationToken.None);
        try
        {
            if (entry.Deleted || entry.TerminalPersisted)
            {
                return;
            }

            var status = Summarize(entry).Status;
            if (status == entry.LastPersistedStatus
                && DateTime.UtcNow - entry.LastPersistedAt < JobPersistenceInterval)
            {
                return;
            }

            var snapshot = SnapshotForPersistence(entry);
            try
            {
                await _jobStateStore.SaveAsync(snapshot);
                entry.LastPersistedStatus = snapshot.Status;
                entry.LastPersistedAt = DateTime.UtcNow;
                entry.TerminalPersisted = snapshot.IsTerminal;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Never let a persistence problem abort the batch the user asked for.
                _logger.LogWarning(ex, "Failed to persist state for job {JobId}", entry.Job.JobId);
            }
        }
        finally
        {
            entry.PersistenceGate.Release();
        }
    }

    /// <summary>
    /// Applies <see cref="BatchJobRetention"/> to the jobs in memory, and to the database when
    /// that forgot any, so a long-running process keeps to the limits a restart would apply.
    /// </summary>
    private async Task ForgetExpiredJobsAsync()
    {
        var cutoff = DateTime.UtcNow - BatchJobRetention.MaxAge;
        var expired = _entries.Values
            .Select(Summarize)
            .Where(job => job.IsTerminal)
            .OrderByDescending(job => job.StartTime)
            .Skip(BatchJobRetention.KeepMostRecent)
            .Where(job => job.StartTime < cutoff)
            .ToList();

        foreach (var job in expired)
        {
            _entries.TryRemove(job.JobId, out _);
        }

        if (expired.Count == 0 || _jobStateStore == null)
        {
            return;
        }

        try
        {
            await _jobStateStore.PruneAsync(cutoff, BatchJobRetention.KeepMostRecent);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The next restart prunes with the same rule.
            _logger.LogWarning(ex, "Failed to prune finished job records");
        }
    }

    private static ProcessingJobSummary Summarize(JobEntry entry)
    {
        var job = entry.Job;
        lock (entry.Sync)
        {
            return new ProcessingJobSummary
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
    }

    private static ProcessingJob Copy(JobEntry entry)
    {
        var job = entry.Job;
        lock (entry.Sync)
        {
            return CopyWithFiles(job, new List<string>(job.Files));
        }
    }

    /// <summary>
    /// A copy for the job state store, which only reads it. A job's file list never changes
    /// after creation, so unlike the errors it is shared rather than copied on every write.
    /// </summary>
    private static ProcessingJob SnapshotForPersistence(JobEntry entry)
    {
        var job = entry.Job;
        lock (entry.Sync)
        {
            return CopyWithFiles(job, job.Files);
        }
    }

    private static ProcessingJob CopyWithFiles(ProcessingJob job, List<string> files)
    {
        return new ProcessingJob
        {
            JobId = job.JobId,
            Status = job.Status,
            OperationName = job.OperationName,
            Files = files,
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
