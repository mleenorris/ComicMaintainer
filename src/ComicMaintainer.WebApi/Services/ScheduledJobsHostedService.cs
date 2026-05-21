using System.Collections.Concurrent;
using System.Diagnostics;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
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
/// invocation also flows through the same gate.
/// </summary>
public sealed class ScheduledJobsHostedService : IHostedService, IDisposable
{
    private readonly ScheduledJobService _service;
    private readonly ILogger<ScheduledJobsHostedService> _logger;
    private readonly ConcurrentDictionary<string, JobSlot> _slots = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _shutdownCts;
    private bool _started;

    public ScheduledJobsHostedService(
        IScheduledJobService service,
        ILogger<ScheduledJobsHostedService> logger)
    {
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
    /// Trigger an immediate run of the named job. The run honors single-flight
    /// (returns false when a run is already in progress for that key).
    /// </summary>
    public Task<bool> RunNowAsync(string jobKey, CancellationToken cancellationToken = default)
    {
        var handler = _service.GetHandler(jobKey);
        if (handler is null)
        {
            return Task.FromResult(false);
        }
        // Fire-and-forget: the API surface returns 202 and the client polls the
        // job's last-status field. Failures are logged inside ExecuteJobAsync.
        _ = Task.Run(() => ExecuteJobAsync(jobKey, fromTimer: false), cancellationToken);
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
                _logger.LogError(ex, "Failed to re-arm timer for {JobKey}", e.JobKey);
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
            _logger.LogDebug("Job {JobKey} disabled; timer not armed", view.JobKey);
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

        slot.Timer = new Timer(
            _ => _ = ExecuteJobAsync(view.JobKey, fromTimer: true),
            null,
            dueTime,
            interval);

        _logger.LogInformation(
            "Scheduled job {JobKey} armed: first run in {DueSec:F0}s, interval {IntervalMin}m",
            view.JobKey, dueTime.TotalSeconds, view.IntervalMinutes);
    }

    private async Task ExecuteJobAsync(string jobKey, bool fromTimer)
    {
        var handler = _service.GetHandler(jobKey);
        if (handler is null)
        {
            return;
        }
        var slot = _slots.GetOrAdd(jobKey, _ => new JobSlot());

        // Single-flight: skip overlapping runs rather than queue them up.
        if (!await slot.SingleFlight.WaitAsync(0))
        {
            _logger.LogInformation("Skipping {JobKey} run because previous run still in progress", jobKey);
            if (fromTimer)
            {
                await _service.RecordCompletionAsync(jobKey, ScheduledJobStatus.Skipped,
                    "Skipped: previous run still in progress.", durationMs: 0);
            }
            return;
        }

        var token = _shutdownCts?.Token ?? CancellationToken.None;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await _service.MarkRunningAsync(jobKey, token);
            var view = await _service.GetAsync(jobKey, token);
            var optionsJson = view?.OptionsJson;

            _logger.LogInformation("Scheduled job {JobKey} starting", jobKey);
            var summary = await handler.ExecuteAsync(optionsJson, token);
            stopwatch.Stop();
            await _service.RecordCompletionAsync(
                jobKey, ScheduledJobStatus.Success, summary, stopwatch.ElapsedMilliseconds, token);
            _logger.LogInformation("Scheduled job {JobKey} completed in {Ms}ms: {Summary}",
                jobKey, stopwatch.ElapsedMilliseconds, summary);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            stopwatch.Stop();
            await _service.RecordCompletionAsync(
                jobKey, ScheduledJobStatus.Cancelled, "Run cancelled.", stopwatch.ElapsedMilliseconds);
            _logger.LogInformation("Scheduled job {JobKey} was cancelled", jobKey);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            await _service.RecordCompletionAsync(
                jobKey, ScheduledJobStatus.Failed, ex.Message, stopwatch.ElapsedMilliseconds);
            _logger.LogError(ex, "Scheduled job {JobKey} failed", jobKey);
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
