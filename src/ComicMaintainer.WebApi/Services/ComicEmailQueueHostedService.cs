using ComicMaintainer.Core.Interfaces;

namespace ComicMaintainer.WebApi.Services;

/// <summary>
/// Hosted service that starts and gracefully stops the background queue that
/// emails comics to saved ereader devices.
/// </summary>
public class ComicEmailQueueHostedService : IHostedService
{
    private readonly IComicEmailQueue _queue;
    private readonly IComicEmailService _deliveryService;
    private readonly ILogger<ComicEmailQueueHostedService> _logger;

    public ComicEmailQueueHostedService(
        IComicEmailQueue queue,
        IComicEmailService deliveryService,
        ILogger<ComicEmailQueueHostedService> logger)
    {
        _queue = queue;
        _deliveryService = deliveryService;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting Comic Email delivery queue");
        await _queue.StartAsync(_deliveryService, cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping Comic Email delivery queue");
        await _queue.StopAsync(cancellationToken);
    }
}
