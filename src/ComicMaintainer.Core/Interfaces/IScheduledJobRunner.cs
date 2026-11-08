namespace ComicMaintainer.Core.Interfaces;

/// <summary>
/// Starts scheduled jobs on demand, through the same single-flight gate as their timers.
/// </summary>
public interface IScheduledJobRunner
{
    /// <summary>
    /// Starts the job in the background unless it is unknown or already running. Returns
    /// whether a run was started; once started, the run is not tied to
    /// <paramref name="cancellationToken"/>.
    /// </summary>
    Task<bool> RunNowAsync(string jobKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Like <see cref="RunNowAsync"/>, but a request that finds a run in progress is not
    /// dropped: the job runs once more when that run finishes, so work added after the
    /// current run read its input is still picked up. All requests made during one run share
    /// that follow-up run. Returns true when this request started a run.
    /// </summary>
    Task<bool> RunNowOrAfterCurrentRunAsync(string jobKey, CancellationToken cancellationToken = default);
}
