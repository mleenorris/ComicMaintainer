using ComicMaintainer.Core.Interfaces;

namespace ComicMaintainer.WebApi.Services;

/// <summary>
/// Hosted service that starts and gracefully stops the background queue that
/// embeds series covers into the first comic archive.
/// </summary>
public class SeriesArchiveCoverWriteQueueHostedService : IHostedService
{
    private readonly ISeriesArchiveCoverWriteQueue _queue;
    private readonly ILogger<SeriesArchiveCoverWriteQueueHostedService> _logger;

    public SeriesArchiveCoverWriteQueueHostedService(
        ISeriesArchiveCoverWriteQueue queue,
        ILogger<SeriesArchiveCoverWriteQueueHostedService> logger)
    {
        _queue = queue;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting Series Archive Cover Write Queue");
        await _queue.StartAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping Series Archive Cover Write Queue");
        await _queue.StopAsync(cancellationToken);
    }
}
