using ComicMaintainer.Core.Models;

namespace ComicMaintainer.Core.Interfaces;

/// <summary>
/// CRUD + run-coordination service for the scheduled-jobs framework.
/// Persists <see cref="ScheduledJobEntity"/> rows and merges them with the
/// metadata supplied by the registered <see cref="IScheduledJobHandler"/>s.
/// </summary>
public interface IScheduledJobService
{
    /// <summary>
    /// Ensures every registered handler has a row in the database, applying
    /// the handler's <see cref="IScheduledJobHandler.Defaults"/> for newly
    /// inserted rows. Safe to call repeatedly (idempotent).
    /// </summary>
    Task SeedDefaultsAsync(CancellationToken cancellationToken = default);

    /// <summary>List every job (combining persisted state with handler metadata).</summary>
    Task<IReadOnlyList<ScheduledJobView>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Fetch a single job, or null if no handler with that key is registered.</summary>
    Task<ScheduledJobView?> GetAsync(string jobKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Update the user-editable schedule fields. <paramref name="intervalMinutes"/>
    /// must be positive when <paramref name="enabled"/> is true.
    /// </summary>
    Task<ScheduledJobView?> UpdateAsync(
        string jobKey,
        bool enabled,
        int intervalMinutes,
        string? optionsJson,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Mark a job as running and return the timestamp captured. Used by the
    /// hosted service to broadcast accurate status before invoking the handler.
    /// </summary>
    Task MarkRunningAsync(string jobKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persist the result of a run (success or failure) and recompute the
    /// next scheduled occurrence.
    /// </summary>
    Task RecordCompletionAsync(
        string jobKey,
        ScheduledJobStatus status,
        string? message,
        long durationMs,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Notification raised when a job's enabled flag, interval, or options
    /// change, so the hosted service can re-arm timers without restart.
    /// </summary>
    event EventHandler<ScheduledJobChangedEventArgs>? JobChanged;
}

public sealed class ScheduledJobChangedEventArgs : EventArgs
{
    public ScheduledJobChangedEventArgs(string jobKey)
    {
        JobKey = jobKey;
    }

    public string JobKey { get; }
}
