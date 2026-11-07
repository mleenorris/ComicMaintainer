using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.WebApi.Services;

/// <summary>
/// Hosted service that periodically cleans up stale database entries and refreshes the
/// SQLite query-planner statistics.
/// </summary>
public class DatabaseCleanupHostedService : IHostedService, IDisposable
{
    private readonly IFileStoreService _fileStore;
    private readonly IOptionsMonitor<AppSettings> _appSettings;
    private readonly ILogger<DatabaseCleanupHostedService> _logger;
    private readonly IDbContextFactory<ComicMaintainerDbContext>? _dbContextFactory;
    private Timer? _timer;
    private readonly SemaphoreSlim _cleanupLock = new(1, 1);
    private CancellationTokenSource? _cancellationTokenSource;
    private IDisposable? _settingsChangeSubscription;
    private int _activeIntervalHours = int.MinValue;
    private readonly object _timerLock = new();
    private bool _started;

    public DatabaseCleanupHostedService(
        IFileStoreService fileStore,
        IOptionsMonitor<AppSettings> appSettings,
        ILogger<DatabaseCleanupHostedService> logger,
        IDbContextFactory<ComicMaintainerDbContext>? dbContextFactory = null)
    {
        _fileStore = fileStore;
        _appSettings = appSettings;
        _logger = logger;
        _dbContextFactory = dbContextFactory;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting Database Cleanup Hosted Service");
        
        _cancellationTokenSource = new CancellationTokenSource();
        _started = true;
        
        // Run cleanup on startup
        await RunCleanupAsync(cancellationToken);
        
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

    public Task StopAsync(CancellationToken cancellationToken)
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
        
        return Task.CompletedTask;
    }

    private async Task RunCleanupAsync(CancellationToken cancellationToken)
    {
        // Prevent concurrent cleanup operations
        if (!await _cleanupLock.WaitAsync(0, cancellationToken))
        {
            _logger.LogDebug("Database cleanup already in progress, skipping this run");
            return;
        }

        try
        {
            _logger.LogInformation("Starting scheduled database cleanup");
            var removedCount = await _fileStore.CleanupStaleEntriesAsync(cancellationToken);
            _logger.LogInformation("Database cleanup completed, removed {Count} stale entries", removedCount);
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
            _cleanupLock.Release();
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
