using ComicMaintainer.Core.Interfaces;

namespace ComicMaintainer.WebApi.Services;

/// <summary>
/// Hosted service that starts and gracefully stops the background queue of series
/// cover artifact changes, with the writer that consumes it.
/// </summary>
public class SeriesCoverArtifactQueueHostedService : IHostedService
{
    private readonly ISeriesCoverArtifactQueue _queue;
    private readonly ISeriesCoverArtifactWriter _writer;
    private readonly ILogger<SeriesCoverArtifactQueueHostedService> _logger;

    public SeriesCoverArtifactQueueHostedService(
        ISeriesCoverArtifactQueue queue,
        ISeriesCoverArtifactWriter writer,
        ILogger<SeriesCoverArtifactQueueHostedService> logger)
    {
        _queue = queue;
        _writer = writer;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting series cover artifact queue");
        await _queue.StartAsync(_writer, cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping series cover artifact queue");
        await _queue.StopAsync(cancellationToken);
    }
}
