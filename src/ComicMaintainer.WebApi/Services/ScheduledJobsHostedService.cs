using System.Collections.Concurrent;
using System.Diagnostics;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Core.Telemetry;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ComicMaintainer.WebApi.Services;

/// <summary>
/// Hosted service that drives the scheduled-jobs framework.
///
/// On <see cref="StartAsync"/> it seeds defaults for any unregistered jobs and
/// arms a per-job timer based on the persisted interval. Subscribes to
/// <see cref="IScheduledJobService.JobChanged"/> so updates from the API
/// (enable/disable, change interval, change options) re-arm the timers
/// without a process restart. Single-flight execution per job is enforced via
/// a <see cref="SemaphoreSlim"/> dictionary; an external <see cref="RunNowAsync"/>
/// invocation also flows through the same gate and reports whether it got
/// through.
/// </summary>
public sealed class ScheduledJobsHostedService : IHostedService, IDisposable
{
    private readonly ScheduledJobService _service;
    private readonly ILogger<ScheduledJobsHostedService> _logger;
    private readonly ComicMaintainerMetrics? _metrics;
    private readonly ConcurrentDictionary<string, JobSlot> _slots = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _shutdownCts;
    private bool _started;

    public ScheduledJobsHostedService(
        IScheduledJobService service,
        ILogger<ScheduledJobsHostedService> logger,
        ComicMaintainerMetrics? metrics = null)
    {
        _metrics = metrics;
        // The hosted service depends on the concrete ScheduledJobService for
        // the Handlers enumeration, but interacts with state through the
        // IScheduledJobService surface. The DI container resolves both to the
        // same singleton instance, so the cast here is safe and avoids
        // widening the public interface.
        _service = (ScheduledJobService)service;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _shutdownCts = new CancellationTokenSource();
        _started = true;

        await _service.SeedDefaultsAsync(cancellationToken);
        _service.JobChanged += OnJobChanged;

        var views = await _service.ListAsync(cancellationToken);
        foreach (var view in views)
        {
            ArmTimer(view);
        }

        _logger.LogInformation("ScheduledJobsHostedService started with {Count} job(s)", views.Count);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _started = false;
        _service.JobChanged -= OnJobChanged;

        try
        {
            _shutdownCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // already disposed
        }

        foreach (var slot in _slots.Values)
        {
            slot.Timer?.Dispose();
        }
        _slots.Clear();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        foreach (var slot in _slots.Values)
        {
            slot.Timer?.Dispose();
            slot.SingleFlight.Dispose();
        }
        _slots.Clear();
        _shutdownCts?.Dispose();
    }

    /// <summary>
    /// Trigger an immediate run of the named job in the background. The run
    /// honors single-flight: returns false, without starting anything, when the
    /// job is unknown or a run is already in progress for that key. Once
    /// started, the run is not tied to <paramref name="cancellationToken"/>.
    /// </summary>
    public Task<bool> RunNowAsync(string jobKey, CancellationToken cancellationToken = default)
    {
        var handler = _service.GetHandler(jobKey);
        if (handler is null)
        {
            return Task.FromResult(false);
        }
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<bool>(cancellationToken);
        }

        // Claim the single-flight slot here so the caller learns whether its
        // request actually started a run.
        var slot = _slots.GetOrAdd(jobKey, _ => new JobSlot());
        if (!slot.SingleFlight.Wait(0))
        {
            _logger.LogInformation(
                "Skipping {JobKey} run because previous run still in progress", LoggingHelper.SanitizeForLog(jobKey));
            return Task.FromResult(false);
        }

        // Fire-and-forget: the API surface returns 202 and the client polls the
        // job's last-status field. Failures are logged inside RunClaimedJobAsync,
        // which also releases the slot.
        _ = Task.Run(() => RunClaimedJobAsync(handler, jobKey, slot), CancellationToken.None);
        return Task.FromResult(true);
    }

    private void OnJobChanged(object? sender, ScheduledJobChangedEventArgs e)
    {
        if (!_started)
        {
            return;
        }
        // Reload the affected job and re-arm its timer.
        _ = Task.Run(async () =>
        {
            try
            {
                var view = await _service.GetAsync(e.JobKey);
                if (view is null)
                {
                    if (_slots.TryRemove(e.JobKey, out var removed))
                    {
                        removed.Timer?.Dispose();
                    }
                    return;
                }
                ArmTimer(view);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to re-arm timer for {JobKey}", LoggingHelper.SanitizeForLog(e.JobKey));
            }
        });
    }

    private void ArmTimer(ScheduledJobView view)
    {
        var slot = _slots.GetOrAdd(view.JobKey, _ => new JobSlot());

        slot.Timer?.Dispose();
        slot.Timer = null;

        if (!view.Enabled || view.IntervalMinutes <= 0)
        {
            _logger.LogDebug("Job {JobKey} disabled; timer not armed", LoggingHelper.SanitizeForLog(view.JobKey));
            return;
        }

        var interval = TimeSpan.FromMinutes(view.IntervalMinutes);
        var dueTime = view.NextRunUtc.HasValue
            ? view.NextRunUtc.Value - DateTime.UtcNow
            : interval;
        if (dueTime < TimeSpan.Zero)
        {
            dueTime = TimeSpan.FromSeconds(5);
        }

        var shutdownToken = _shutdownCts?.Token ?? CancellationToken.None;
        slot.Timer = new Timer(
            _ => _ = ExecuteJobAsync(view.JobKey, shutdownToken),
            null,
            dueTime,
            interval);

        _logger.LogInformation(
            "Scheduled job {JobKey} armed: first run in {DueSec:F0}s, interval {IntervalMin}m",
            LoggingHelper.SanitizeForLog(view.JobKey), dueTime.TotalSeconds, view.IntervalMinutes);
    }

    private async Task ExecuteJobAsync(string jobKey, CancellationToken cancellationToken)
    {
        var handler = _service.GetHandler(jobKey);
        if (handler is null)
        {
            return;
        }
        var slot = _slots.GetOrAdd(jobKey, _ => new JobSlot());

        // Single-flight: skip overlapping runs rather than queue them up. The
        // run in progress keeps its status and records its own result, so the
        // skipped run is only logged and counted, and the next run moves on.
        if (!await slot.SingleFlight.WaitAsync(0))
        {
            _logger.LogInformation(
                "Skipping {JobKey} run because previous run still in progress", LoggingHelper.SanitizeForLog(jobKey));
            _metrics?.RecordScheduledJobRun(handler.JobKey, "skipped", TimeSpan.Zero);
            await _service.AdvanceNextRunAsync(jobKey, cancellationToken);
            return;
        }

        await RunClaimedJobAsync(handler, jobKey, slot);
    }

    /// <summary>
    /// Runs the job whose single-flight slot the caller has already claimed and
    /// releases the slot when done.
    /// </summary>
    private async Task RunClaimedJobAsync(IScheduledJobHandler handler, string jobKey, JobSlot slot)
    {
        // jobKey has been validated against the registry, but it originates
        // from user input via the controller, so we still scrub it before
        // sending to the structured logger to avoid log forging.
        var safeKey = LoggingHelper.SanitizeForLog(jobKey);
        var token = _shutdownCts?.Token ?? CancellationToken.None;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await _service.MarkRunningAsync(jobKey, token);
            var view = await _service.GetAsync(jobKey, token);
            var optionsJson = view?.OptionsJson;

            _logger.LogInformation("Scheduled job {JobKey} starting", safeKey);
            var summary = await handler.ExecuteAsync(optionsJson, token);
            stopwatch.Stop();
            _metrics?.RecordScheduledJobRun(handler.JobKey, "success", stopwatch.Elapsed);
            await _service.RecordCompletionAsync(
                jobKey, ScheduledJobStatus.Success, summary, stopwatch.ElapsedMilliseconds, token);
            _logger.LogInformation("Scheduled job {JobKey} completed in {Ms}ms: {Summary}",
                safeKey, stopwatch.ElapsedMilliseconds, LoggingHelper.SanitizeForLog(summary));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            stopwatch.Stop();
            _metrics?.RecordScheduledJobRun(handler.JobKey, "cancelled", stopwatch.Elapsed);
            await _service.RecordCompletionAsync(
                jobKey, ScheduledJobStatus.Cancelled, "Run cancelled.", stopwatch.ElapsedMilliseconds);
            _logger.LogInformation("Scheduled job {JobKey} was cancelled", safeKey);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _metrics?.RecordScheduledJobRun(handler.JobKey, "failed", stopwatch.Elapsed);
            await _service.RecordCompletionAsync(
                jobKey, ScheduledJobStatus.Failed, ex.Message, stopwatch.ElapsedMilliseconds);
            _logger.LogError(ex, "Scheduled job {JobKey} failed", safeKey);
        }
        finally
        {
            slot.SingleFlight.Release();
        }
    }

    private sealed class JobSlot
    {
        public Timer? Timer;
        public readonly SemaphoreSlim SingleFlight = new(1, 1);
    }
}
