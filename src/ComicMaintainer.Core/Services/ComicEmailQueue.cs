using System.Threading.Channels;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Telemetry;
using Microsoft.Extensions.Logging;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Default <see cref="IComicEmailQueue"/> implementation. Deliveries are drained
/// one at a time by a single background consumer so SMTP work never blocks an API
/// request or the file watcher, and so the mail server is not hit concurrently.
/// </summary>
public class ComicEmailQueue : IComicEmailQueue
{
    private readonly Func<IComicEmailService> _serviceFactory;
    private readonly ILogger<ComicEmailQueue> _logger;

    private readonly object _lifecycleLock = new();

    // Replaced on every stop, so deliveries queued while stopped wait for the next start.
    private volatile Channel<int> _channel = CreateChannel();

    // Set while the queue runs.
    private CancellationTokenSource? _stoppingCts;

    // The latest consumer, which may still be draining after a stop.
    private Task _consumer = Task.CompletedTask;

    // Completes once every consumer started so far has finished. A run whose wait
    // for its predecessor was cancelled ends early, so the next run must not rely
    // on that run alone to know the earlier ones are done.
    private Task _allConsumersDone = Task.CompletedTask;

    /// <summary>Initializes a new instance of the <see cref="ComicEmailQueue"/> class.</summary>
    /// <param name="serviceFactory">
    /// Resolves the delivery service lazily; the service itself enqueues work,
    /// so constructor injection would form a dependency cycle.
    /// </param>
    public ComicEmailQueue(
        Func<IComicEmailService> serviceFactory,
        ILogger<ComicEmailQueue> logger,
        ComicMaintainerMetrics? metrics = null)
    {
        _serviceFactory = serviceFactory;
        _logger = logger;
        metrics?.SetEmailQueueDepthProvider(() => _channel.Reader.Count);
    }

    public void Enqueue(int deliveryId)
    {
        if (deliveryId <= 0)
        {
            return;
        }

        _channel.Writer.TryWrite(deliveryId);
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_lifecycleLock)
        {
            if (_stoppingCts is not null)
            {
                return Task.CompletedTask;
            }

            // Each run works on its own token source and channel, so a stop or
            // restart replacing the fields cannot affect a run already going.
            var stoppingCts = new CancellationTokenSource();
            var reader = _channel.Reader;
            var previous = _allConsumersDone;
            _stoppingCts = stoppingCts;
            _consumer = Task.Run(() => ConsumeAsync(previous, reader, stoppingCts.Token), CancellationToken.None);
            _allConsumersDone = Task.WhenAll(previous, _consumer);
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task consumer;
        CancellationTokenSource stoppingCts;
        Channel<int> channel;
        lock (_lifecycleLock)
        {
            if (_stoppingCts is null)
            {
                return;
            }

            consumer = _consumer;
            stoppingCts = _stoppingCts;
            channel = _channel;
            _stoppingCts = null;
            _channel = CreateChannel();
        }

        // Stop accepting new work for this run but let the consumer finish what is
        // already queued.
        channel.Writer.TryComplete();

        using (stoppingCts)
        {
            // If the shutdown deadline fires before the queue drained, cancel the
            // consumer; the remaining rows stay 'pending' in the database.
            using (cancellationToken.Register(() => stoppingCts.Cancel()))
            {
                await consumer;
            }
        }
    }

    private static Channel<int> CreateChannel() => Channel.CreateUnbounded<int>(new UnboundedChannelOptions
    {
        SingleReader = false,
        SingleWriter = false
    });

    private async Task ConsumeAsync(Task previous, ChannelReader<int> reader, CancellationToken cancellationToken)
    {
        // A restart can overlap the previous run still draining; wait for it so the
        // mail server is never hit concurrently. If this run is stopped meanwhile,
        // the read below ends it.
        await previous.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        try
        {
            await foreach (var deliveryId in reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    await _serviceFactory().ProcessDeliveryAsync(deliveryId, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _logger.LogError(ex, "Unhandled error while processing comic email delivery {DeliveryId}", deliveryId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }
}
