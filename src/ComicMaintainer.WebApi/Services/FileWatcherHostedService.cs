using ComicMaintainer.Core.Interfaces;

namespace ComicMaintainer.WebApi.Services;

/// <summary>
/// Hosted service that starts the file watcher when the application starts
/// </summary>
public class FileWatcherHostedService : IHostedService
{
    private readonly IFileWatcherService _fileWatcher;
    private readonly ILogger<FileWatcherHostedService> _logger;

    public FileWatcherHostedService(
        IFileWatcherService fileWatcher,
        ILogger<FileWatcherHostedService> logger)
    {
        _fileWatcher = fileWatcher;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting File Watcher Hosted Service");
        await _fileWatcher.StartAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping File Watcher Hosted Service");
        await _fileWatcher.StopAsync(cancellationToken);
    }
}
