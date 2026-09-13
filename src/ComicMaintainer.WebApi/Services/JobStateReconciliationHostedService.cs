using ComicMaintainer.Core.Interfaces;

namespace ComicMaintainer.WebApi.Services;

/// <summary>
/// Reconciles persisted batch job state with reality at startup.
/// </summary>
/// <remarks>
/// Any job the database still records as queued or running cannot actually be running: the
/// process that owned it is gone. Such jobs are marked interrupted and loaded back into the
/// processor so that a client still polling a job id from before the restart gets a real
/// answer instead of a 404 and an indefinitely stalled progress bar.
///
/// Interrupted jobs are deliberately not resumed. The batch operations are not transactional,
/// and re-running one could repeat side effects on files that were already handled; deciding
/// to run it again is left to the user, who can see what progress was made.
/// </remarks>
public class JobStateReconciliationHostedService : IHostedService
{
    /// <summary>Terminal jobs older than this are removed at startup.</summary>
    private static readonly TimeSpan JobRetention = TimeSpan.FromDays(7);

    /// <summary>Terminal jobs kept regardless of age, so recent history is never empty.</summary>
    private const int JobRetentionCount = 50;

    private readonly IJobStateStore _jobStateStore;
    private readonly IComicProcessorService _processor;
    private readonly ILogger<JobStateReconciliationHostedService> _logger;

    public JobStateReconciliationHostedService(
        IJobStateStore jobStateStore,
        IComicProcessorService processor,
        ILogger<JobStateReconciliationHostedService> logger)
    {
        _jobStateStore = jobStateStore;
        _processor = processor;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var interrupted = await _jobStateStore.MarkInterruptedAsync(cancellationToken);

            foreach (var job in interrupted)
            {
                _logger.LogWarning(
                    "Job {JobId} ({Operation}) was interrupted by a restart after {Processed}/{Total} file(s)",
                    job.JobId, job.OperationName, job.ProcessedFiles + job.FailedFiles, job.TotalFiles);
            }

            await _jobStateStore.PruneAsync(
                DateTime.UtcNow - JobRetention,
                JobRetentionCount,
                cancellationToken);

            var jobs = await _jobStateStore.GetAllAsync(cancellationToken);

            _processor.RestoreJobs(jobs);

            _logger.LogInformation("Restored {Count} persisted job record(s)", jobs.Count);
        }
        catch (Exception ex)
        {
            // Job history is not worth blocking startup over; the application is still fully
            // usable without it.
            _logger.LogError(ex, "Failed to reconcile persisted job state at startup");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
