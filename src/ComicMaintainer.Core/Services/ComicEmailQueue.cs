using System.Threading.Channels;
using ComicMaintainer.Core.Interfaces;
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

    private readonly Channel<int> _channel = Channel.CreateUnbounded<int>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });

    private readonly object _lifecycleLock = new();
    private CancellationTokenSource? _stoppingCts;
    private Task? _consumer;

    /// <param name="serviceFactory">
    /// Resolves the delivery service lazily; the service itself enqueues work,
    /// so constructor injection would form a dependency cycle.
    /// </param>
    public ComicEmailQueue(Func<IComicEmailService> serviceFactory, ILogger<ComicEmailQueue> logger)
    {
        _serviceFactory = serviceFactory;
        _logger = logger;
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
            if (_consumer is not null)
            {
                return Task.CompletedTask;
            }

            _stoppingCts = new CancellationTokenSource();
            _consumer = Task.Run(() => ConsumeAsync(_stoppingCts.Token), CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task? consumer;
        CancellationTokenSource? stoppingCts;
        lock (_lifecycleLock)
        {
            if (_consumer is null)
            {
                return;
            }

            consumer = _consumer;
            stoppingCts = _stoppingCts;
            _consumer = null;
        }

        // Stop accepting new work but let the consumer finish what is already queued.
        _channel.Writer.TryComplete();

        using var registration = cancellationToken.Register(() => stoppingCts?.Cancel());

        try
        {
            await consumer;
        }
        catch (OperationCanceledException)
        {
            // Shutdown deadline fired before the queue drained; pending rows stay
            // 'pending' in the database and are re-queued on the next send.
        }
        finally
        {
            stoppingCts?.Dispose();
            lock (_lifecycleLock)
            {
                _stoppingCts = null;
            }
        }
    }

    private async Task ConsumeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var deliveryId in _channel.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    await _serviceFactory().ProcessDeliveryAsync(deliveryId, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
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
