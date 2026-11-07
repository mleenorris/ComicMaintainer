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
/// <remarks>
/// The queue itself lives in memory, but the delivery rows are durable: while it
/// runs, the queue periodically asks the delivery service to re-queue every
/// pending row it does not hold (<see cref="IComicEmailService.ResumePendingDeliveriesAsync"/>),
/// starting as soon as it starts. Deliveries left pending by a restart, a crash
/// or a failure before they were claimed are therefore picked up again instead
/// of staying pending — and blocking the already-delivered check — for good.
/// </remarks>
public class ComicEmailQueue : IComicEmailQueue
{
    /// <summary>
    /// How often the pending deliveries in the database are compared with what
    /// the queue holds, after the comparison made when the queue starts.
    /// </summary>
    public static readonly TimeSpan PendingSweepInterval = TimeSpan.FromMinutes(15);

    private readonly Func<IComicEmailService> _serviceFactory;
    private readonly ILogger<ComicEmailQueue> _logger;
    private readonly TimeProvider _timeProvider;

    private readonly object _lifecycleLock = new();

    /// <summary>
    /// Every delivery the queue holds — waiting in the channel or being
    /// processed — so the same delivery is never queued twice and the pending
    /// sweep can tell which pending rows nobody is working on. Also guards
    /// replacing <see cref="_channel"/>, so a delivery is never written to a
    /// channel that has already been completed.
    /// </summary>
    private readonly Dictionary<int, EntryState> _entries = new();

    // Replaced on every stop, so deliveries queued while stopped wait for the next start.
    private volatile Channel<int> _channel = CreateChannel();

    // Set while the queue runs.
    private CancellationTokenSource? _stoppingCts;

    // Cancelled as soon as the run is asked to stop; ends its pending sweep.
    private CancellationTokenSource? _sweepCts;

    // The latest consumer, which may still be draining after a stop.
    private Task _consumer = Task.CompletedTask;

    // The latest run's pending sweep.
    private Task _sweep = Task.CompletedTask;

    // Completes once every consumer started so far has finished. A run whose wait
    // for its predecessor was cancelled ends early, so the next run must not rely
    // on that run alone to know the earlier ones are done.
    private Task _allConsumersDone = Task.CompletedTask;

    /// <summary>Initializes a new instance of the <see cref="ComicEmailQueue"/> class.</summary>
    /// <param name="serviceFactory">
    /// Resolves the delivery service lazily; the service itself enqueues work,
    /// so constructor injection would form a dependency cycle.
    /// </param>
    /// <param name="logger">Logger.</param>
    /// <param name="metrics">Publishes the queue depth when set.</param>
    /// <param name="timeProvider">Clock for the pending sweep; the system clock when null.</param>
    public ComicEmailQueue(
        Func<IComicEmailService> serviceFactory,
        ILogger<ComicEmailQueue> logger,
        ComicMaintainerMetrics? metrics = null,
        TimeProvider? timeProvider = null)
    {
        _serviceFactory = serviceFactory;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        metrics?.SetEmailQueueDepthProvider(() => _channel.Reader.Count);
    }

    private enum EntryState
    {
        /// <summary>Waiting in the channel.</summary>
        Queued,

        /// <summary>Handed to the delivery service.</summary>
        Processing,
    }

    public void Enqueue(int deliveryId)
    {
        if (deliveryId <= 0)
        {
            return;
        }

        lock (_entries)
        {
            // Already waiting or being processed: queueing it again would only
            // make the consumer load a delivery that is already handled.
            if (!_entries.TryAdd(deliveryId, EntryState.Queued))
            {
                return;
            }

            _channel.Writer.TryWrite(deliveryId);
        }
    }

    public bool IsQueued(int deliveryId)
    {
        lock (_entries)
        {
            return _entries.ContainsKey(deliveryId);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_lifecycleLock)
        {
            if (_stoppingCts is not null)
            {
                return Task.CompletedTask;
            }

            // Each run works on its own token sources and channel, so a stop or
            // restart replacing the fields cannot affect a run already going.
            var stoppingCts = new CancellationTokenSource();
            var sweepCts = new CancellationTokenSource();
            var reader = _channel.Reader;
            var previous = _allConsumersDone;
            _stoppingCts = stoppingCts;
            _sweepCts = sweepCts;
            _consumer = Task.Run(() => ConsumeAsync(previous, reader, stoppingCts.Token), CancellationToken.None);
            _sweep = Task.Run(() => SweepPendingAsync(sweepCts.Token), CancellationToken.None);
            _allConsumersDone = Task.WhenAll(previous, _consumer);
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task consumer;
        Task sweep;
        CancellationTokenSource stoppingCts;
        CancellationTokenSource sweepCts;
        Channel<int> channel;
        lock (_lifecycleLock)
        {
            if (_stoppingCts is null || _sweepCts is null)
            {
                return;
            }

            consumer = _consumer;
            sweep = _sweep;
            stoppingCts = _stoppingCts;
            sweepCts = _sweepCts;
            channel = _channel;
            _stoppingCts = null;
            _sweepCts = null;
            lock (_entries)
            {
                _channel = CreateChannel();
            }
        }

        // Stop accepting new work for this run but let the consumer finish what is
        // already queued.
        channel.Writer.TryComplete();

        using (sweepCts)
        {
            await sweepCts.CancelAsync();
            await sweep;
        }

        using (stoppingCts)
        {
            // If the shutdown deadline fires before the queue drained, cancel the
            // consumer; the remaining rows stay 'pending' in the database.
            using (cancellationToken.Register(() => stoppingCts.Cancel()))
            {
                await consumer;
            }
        }

        // Whatever the run did not get to is no longer held by the queue, so the
        // pending sweep of the next run picks it up from the database again.
        lock (_entries)
        {
            while (channel.Reader.TryRead(out var leftover))
            {
                if (_entries.TryGetValue(leftover, out var state) && state == EntryState.Queued)
                {
                    _entries.Remove(leftover);
                }
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
                lock (_entries)
                {
                    _entries[deliveryId] = EntryState.Processing;
                }

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
                finally
                {
                    lock (_entries)
                    {
                        _entries.Remove(deliveryId);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    /// <summary>
    /// Re-queues pending deliveries the queue does not hold: right away, which
    /// resumes whatever an earlier run left behind, and then every
    /// <see cref="PendingSweepInterval"/> so a delivery that failed before it
    /// was claimed is retried rather than left pending.
    /// </summary>
    private async Task SweepPendingAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _serviceFactory().ResumePendingDeliveriesAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogError(ex, "Failed to re-queue pending comic email deliveries");
            }

            try
            {
                await Task.Delay(PendingSweepInterval, _timeProvider, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
