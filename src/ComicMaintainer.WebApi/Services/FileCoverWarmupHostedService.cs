using ComicMaintainer.Core.Services;

namespace ComicMaintainer.WebApi.Services;

/// <summary>
/// Runs <see cref="FileCoverWarmupQueue"/> in the background: the covers of new and changed files
/// are generated one at a time before the library first shows them, and the work stops on shutdown.
/// </summary>
public sealed class FileCoverWarmupHostedService : BackgroundService
{
    private readonly FileCoverWarmupQueue _queue;

    public FileCoverWarmupHostedService(FileCoverWarmupQueue queue)
    {
        _queue = queue;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => _queue.RunAsync(stoppingToken);
}
