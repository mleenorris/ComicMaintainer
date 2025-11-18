using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.WebApi.Services;

/// <summary>
/// Hosted service that periodically cleans up stale database entries
/// </summary>
public class DatabaseCleanupHostedService : IHostedService, IDisposable
{
    private readonly IFileStoreService _fileStore;
    private readonly IOptionsMonitor<AppSettings> _appSettings;
    private readonly ILogger<DatabaseCleanupHostedService> _logger;
    private Timer? _timer;
    private readonly SemaphoreSlim _cleanupLock = new(1, 1);
    private CancellationTokenSource? _cancellationTokenSource;

    public DatabaseCleanupHostedService(
        IFileStoreService fileStore,
        IOptionsMonitor<AppSettings> appSettings,
        ILogger<DatabaseCleanupHostedService> logger)
    {
        _fileStore = fileStore;
        _appSettings = appSettings;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting Database Cleanup Hosted Service");
        
        _cancellationTokenSource = new CancellationTokenSource();
        
        // Run cleanup on startup
        await RunCleanupAsync(cancellationToken);
        
        // Schedule periodic cleanup based on configuration
        var intervalHours = _appSettings.CurrentValue.DatabaseCleanupIntervalHours;
        
        if (intervalHours > 0)
        {
            var interval = TimeSpan.FromHours(intervalHours);
            _logger.LogInformation("Database cleanup will run every {Hours} hours", intervalHours);
            
            _timer = new Timer(
                async _ => await RunCleanupAsync(_cancellationTokenSource.Token),
                null,
                interval,
                interval);
        }
        else
        {
            _logger.LogInformation("Database cleanup is set to run only on startup (interval = 0)");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping Database Cleanup Hosted Service");
        _timer?.Change(Timeout.Infinite, 0);
        
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
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Database cleanup was cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during scheduled database cleanup");
        }
        finally
        {
            _cleanupLock.Release();
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _cleanupLock.Dispose();
        _cancellationTokenSource?.Dispose();
    }
}
