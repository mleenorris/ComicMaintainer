using ComicMaintainer.Core.Services;

namespace ComicMaintainer.WebApi.Services;

/// <summary>
/// Runs <see cref="ArchiveWriteRecoveryService"/> once at startup, in the background so a large
/// library does not delay the web server. The sweep only touches files written before it started,
/// so it is safe while the file watcher already processes new files.
/// </summary>
public sealed class ArchiveWriteRecoveryHostedService : BackgroundService
{
    private readonly ArchiveWriteRecoveryService _recovery;
    private readonly ILogger<ArchiveWriteRecoveryHostedService> _logger;

    public ArchiveWriteRecoveryHostedService(
        ArchiveWriteRecoveryService recovery,
        ILogger<ArchiveWriteRecoveryHostedService> logger)
    {
        _recovery = recovery;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Run(() => _recovery.RecoverAsync(stoppingToken), stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down; the next startup sweeps again.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Recovery is best effort and must never stop the application from starting.
            _logger.LogError(ex, "Failed to recover interrupted archive writes at startup");
        }
    }
}
