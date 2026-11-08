using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.WebApi.Services;

/// <summary>
/// Hosted service that periodically cleans up stale database entries, prunes the processing
/// history to its retention settings and refreshes the SQLite query-planner statistics.
/// </summary>
/// <remarks>
/// The stale-entry cleanup is guarded by the file store (see
/// <see cref="IFileStoreService.CleanupStaleEntriesAsync"/>): it never deletes anything
/// while the library looks unmounted, waits for a file to stay missing across two runs, and
/// refuses mass deletions. On top of that the first run waits
/// <see cref="DefaultStartupDelay"/> after start-up instead of running inside
/// <see cref="StartAsync"/>, so it neither delays the application's start nor races the
/// file watcher's initial scan of a share that is still being mounted.
/// </remarks>
public class DatabaseCleanupHostedService : IHostedService, IDisposable
{
    /// <summary>How long after start-up the first cleanup runs.</summary>
    public static readonly TimeSpan DefaultStartupDelay = TimeSpan.FromMinutes(2);

    private readonly IFileStoreService _fileStore;
    private readonly IOptionsMonitor<AppSettings> _appSettings;
    private readonly ILogger<DatabaseCleanupHostedService> _logger;
    private readonly IDbContextFactory<ComicMaintainerDbContext>? _dbContextFactory;
    private readonly IProcessingHistoryService? _historyService;
    private readonly TimeSpan _startupDelay;
    private Timer? _timer;
    private readonly SemaphoreSlim _cleanupLock = new(1, 1);
    private CancellationTokenSource? _cancellationTokenSource;
    private IDisposable? _settingsChangeSubscription;
    private int _activeIntervalHours = int.MinValue;
    private readonly object _timerLock = new();
    private bool _started;
    private Task _startupRun = Task.CompletedTask;

    public DatabaseCleanupHostedService(
        IFileStoreService fileStore,
        IOptionsMonitor<AppSettings> appSettings,
        ILogger<DatabaseCleanupHostedService> logger,
        IDbContextFactory<ComicMaintainerDbContext>? dbContextFactory = null,
        TimeSpan? startupDelay = null,
        IProcessingHistoryService? historyService = null)
    {
        _fileStore = fileStore;
        _appSettings = appSettings;
        _logger = logger;
        _dbContextFactory = dbContextFactory;
        _historyService = historyService;
        _startupDelay = startupDelay is { } delay
            ? (delay > TimeSpan.Zero ? delay : TimeSpan.Zero)
            : DefaultStartupDelay;
    }

    /// <summary>The start-up cleanup run (completed once it has finished or was cancelled).</summary>
    public Task StartupRun => _startupRun;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting Database Cleanup Hosted Service");

        _cancellationTokenSource = new CancellationTokenSource();
        _started = true;

        // The start-up cleanup runs in the background after a delay rather than here.
        var stopping = _cancellationTokenSource.Token;
        _startupRun = Task.Run(() => RunStartupCleanupAsync(stopping), CancellationToken.None);

        // Schedule periodic cleanup based on configuration and subscribe to changes so the
        // schedule is updated live without restarting the service.
        ConfigureTimer(_appSettings.CurrentValue.DatabaseCleanupIntervalHours);
        _settingsChangeSubscription = _appSettings.OnChange(settings =>
        {
            try
            {
                ConfigureTimer(settings.DatabaseCleanupIntervalHours);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogError(ex, "Failed to reconfigure database cleanup timer after settings change");
            }
        });

        return Task.CompletedTask;
    }

    private async Task RunStartupCleanupAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (_startupDelay > TimeSpan.Zero)
            {
                await Task.Delay(_startupDelay, stoppingToken);
            }

            await RunCleanupAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopped before the first run was due.
        }
    }

    private void ConfigureTimer(int intervalHours)
    {
        lock (_timerLock)
        {
            if (!_started)
            {
                return;
            }

            if (intervalHours == _activeIntervalHours)
            {
                return;
            }

            // Tear down any existing timer
            if (_timer != null)
            {
                _timer.Dispose();
                _timer = null;
            }

            _activeIntervalHours = intervalHours;

            if (intervalHours > 0)
            {
                var interval = TimeSpan.FromHours(intervalHours);
                _logger.LogInformation("Database cleanup will run every {Hours} hours", intervalHours);

                // _cancellationTokenSource is created in StartAsync before the timer can be
                // configured (either inline or via OnChange), and the _started guard above
                // ensures we don't reach here after StopAsync.
                var token = _cancellationTokenSource?.Token ?? CancellationToken.None;
                _timer = new Timer(
                    async _ => await RunCleanupAsync(token),
                    null,
                    interval,
                    interval);
            }
            else
            {
                _logger.LogInformation("Database cleanup is set to run only on startup (interval = 0)");
            }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping Database Cleanup Hosted Service");
        _started = false;
        _settingsChangeSubscription?.Dispose();
        _settingsChangeSubscription = null;
        lock (_timerLock)
        {
            _timer?.Change(Timeout.Infinite, 0);
        }
        
        // Cancel only if not already disposed
        if (_cancellationTokenSource != null && !_cancellationTokenSource.IsCancellationRequested)
        {
            try
            {
                _cancellationTokenSource.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already disposed, ignore
            }
        }

        // The start-up run observes the cancellation and finishes promptly; wait for it so
        // nothing is still using the service once it is disposed.
        try
        {
            await _startupRun.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The host stopped waiting.
        }
    }

    private async Task RunCleanupAsync(CancellationToken cancellationToken)
    {
        // Prevent concurrent cleanup operations
        bool acquired;
        try
        {
            acquired = await _cleanupLock.WaitAsync(0, cancellationToken);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            return;
        }

        if (!acquired)
        {
            _logger.LogDebug("Database cleanup already in progress, skipping this run");
            return;
        }

        try
        {
            _logger.LogInformation("Starting scheduled database cleanup");
            var result = await _fileStore.CleanupStaleEntriesAsync(StaleCleanupOptions.Scheduled, cancellationToken);
            switch (result.Status)
            {
                case StaleCleanupStatus.Completed:
                    _logger.LogInformation("Database cleanup completed, removed {Count} stale entries", result.RemovedCount);
                    break;
                case StaleCleanupStatus.Cancelled:
                    _logger.LogInformation("Database cleanup was cancelled");
                    return;
                default:
                    // The file store has already logged why; this ties it to the schedule.
                    _logger.LogWarning("Scheduled database cleanup removed nothing ({Status}): {Message}", result.Status, result.Message);
                    break;
            }

            await PruneProcessingHistoryAsync(cancellationToken);
            await RefreshPlannerStatisticsAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Database cleanup was cancelled");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Error during scheduled database cleanup");
        }
        finally
        {
            try
            {
                _cleanupLock.Release();
            }
            catch (ObjectDisposedException)
            {
                // Disposed while a timer run was finishing; nothing waits on the lock any more.
            }
        }
    }

    /// <summary>
    /// Prunes processing history to <see cref="AppSettings.ProcessingHistoryRetentionDays"/> and
    /// <see cref="AppSettings.ProcessingHistoryMaxEntries"/>. Failures are logged and never fail
    /// the cleanup run.
    /// </summary>
    private async Task PruneProcessingHistoryAsync(CancellationToken cancellationToken)
    {
        if (_historyService is null)
        {
            return;
        }

        var settings = _appSettings.CurrentValue;
        try
        {
            var pruned = await _historyService.PruneAsync(
                settings.ProcessingHistoryRetentionDays,
                settings.ProcessingHistoryMaxEntries,
                cancellationToken);
            if (pruned > 0)
            {
                _logger.LogInformation(
                    "Pruned {Count} processing history entries (keeping {Days} day(s), at most {MaxEntries} entries)",
                    pruned, settings.ProcessingHistoryRetentionDays, settings.ProcessingHistoryMaxEntries);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Failed to prune processing history");
        }
    }

    /// <summary>
    /// Runs <c>PRAGMA optimize</c> so the planner has current statistics for the library
    /// indexes. Failures are logged and never fail the cleanup run.
    /// </summary>
    private async Task RefreshPlannerStatisticsAsync(CancellationToken cancellationToken)
    {
        if (_dbContextFactory is null)
        {
            return;
        }

        try
        {
            if (await SqliteStatisticsMaintenance.OptimizeAsync(_dbContextFactory, cancellationToken))
            {
                _logger.LogDebug("Refreshed SQLite query-planner statistics");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Failed to refresh SQLite query-planner statistics");
        }
    }

    public void Dispose()
    {
        _settingsChangeSubscription?.Dispose();
        _settingsChangeSubscription = null;
        lock (_timerLock)
        {
            _timer?.Dispose();
            _timer = null;
        }
        _cleanupLock.Dispose();
        _cancellationTokenSource?.Dispose();
    }
}
