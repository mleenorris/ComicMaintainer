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

    private readonly ILogger<ComicEmailQueue> _logger;
    private readonly TimeProvider _timeProvider;

    private readonly object _lifecycleLock = new();

    /// <summary>
    /// Every delivery the queue holds — waiting in the channel, scheduled for
    /// later or being processed — so the same delivery is never queued twice
    /// and the pending sweep can tell which pending rows nobody is working on.
    /// Also guards replacing <see cref="_channel"/>, so a delivery is never
    /// written to a channel that has already been completed.
    /// </summary>
    private readonly Dictionary<int, Entry> _entries = new();

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
    /// <param name="logger">Logger.</param>
    /// <param name="metrics">Publishes the queue depth when set.</param>
    /// <param name="timeProvider">
    /// Clock for the pending sweep and for scheduled deliveries; the system
    /// clock when null.
    /// </param>
    public ComicEmailQueue(
        ILogger<ComicEmailQueue> logger,
        ComicMaintainerMetrics? metrics = null,
        TimeProvider? timeProvider = null)
    {
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        metrics?.SetEmailQueueDepthProvider(() => _channel.Reader.Count);
    }

    private enum EntryState
    {
        /// <summary>Waiting in the channel.</summary>
        Queued,

        /// <summary>Waiting for its timer to put it in the channel.</summary>
        Scheduled,

        /// <summary>Handed to the delivery service.</summary>
        Processing,
    }

    /// <summary>A delivery the queue holds. Only touched under the <see cref="_entries"/> lock.</summary>
    private sealed class Entry
    {
        public EntryState State;

        /// <summary>Timer of a scheduled delivery.</summary>
        public ITimer? Timer;

        /// <summary>
        /// Bumped whenever the timer is replaced, so a callback of a replaced
        /// timer that already started running cannot queue the delivery early.
        /// </summary>
        public int TimerGeneration;

        /// <summary>When a delivery scheduled while it was being processed is due.</summary>
        public DateTimeOffset? ScheduledAfterProcessing;
    }

    public void Enqueue(int deliveryId)
    {
        if (deliveryId <= 0)
        {
            return;
        }

        lock (_entries)
        {
            // Already waiting, scheduled or being processed: queueing it again
            // would only make the consumer load a delivery that is already
            // handled.
            if (_entries.ContainsKey(deliveryId))
            {
                return;
            }

            var entry = new Entry();
            _entries.Add(deliveryId, entry);
            QueueNow(deliveryId, entry);
        }
    }

    public void Schedule(int deliveryId, DateTimeOffset dueAt)
    {
        if (deliveryId <= 0)
        {
            return;
        }

        lock (_entries)
        {
            if (!_entries.TryGetValue(deliveryId, out var entry))
            {
                entry = new Entry();
                _entries.Add(deliveryId, entry);
                ScheduleEntry(deliveryId, entry, dueAt);
                return;
            }

            switch (entry.State)
            {
                case EntryState.Processing:
                    entry.ScheduledAfterProcessing = dueAt;
                    break;
                case EntryState.Scheduled:
                    ScheduleEntry(deliveryId, entry, dueAt);
                    break;
                default:
                    // Already waiting in the channel; the delivery service sees
                    // that it is not due yet and schedules it again.
                    break;
            }
        }
    }

    /// <summary>Puts the delivery in the channel. Called under the <see cref="_entries"/> lock.</summary>
    private void QueueNow(int deliveryId, Entry entry)
    {
        entry.State = EntryState.Queued;
        _channel.Writer.TryWrite(deliveryId);
    }

    /// <summary>
    /// (Re)starts the timer that queues the delivery at <paramref name="dueAt"/>,
    /// or queues it right away when that time has passed. Called under the
    /// <see cref="_entries"/> lock.
    /// </summary>
    private void ScheduleEntry(int deliveryId, Entry entry, DateTimeOffset dueAt)
    {
        entry.Timer?.Dispose();
        entry.Timer = null;
        var generation = ++entry.TimerGeneration;

        var delay = dueAt - _timeProvider.GetUtcNow();
        if (delay <= TimeSpan.Zero)
        {
            QueueNow(deliveryId, entry);
            return;
        }

        entry.State = EntryState.Scheduled;
        entry.Timer = _timeProvider.CreateTimer(
            _ => OnScheduledDeliveryDue(deliveryId, entry, generation),
            null,
            delay,
            Timeout.InfiniteTimeSpan);
    }

    private void OnScheduledDeliveryDue(int deliveryId, Entry entry, int generation)
    {
        lock (_entries)
        {
            if (!_entries.TryGetValue(deliveryId, out var current) ||
                !ReferenceEquals(current, entry) ||
                entry.State != EntryState.Scheduled ||
                entry.TimerGeneration != generation)
            {
                return;
            }

            entry.Timer?.Dispose();
            entry.Timer = null;
            QueueNow(deliveryId, entry);
        }
    }

    public bool IsQueued(int deliveryId)
    {
        lock (_entries)
        {
            return _entries.ContainsKey(deliveryId);
        }
    }

    public Task StartAsync(IComicEmailService deliveryService, CancellationToken cancellationToken = default)
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
            _consumer = Task.Run(() => ConsumeAsync(deliveryService, previous, reader, stoppingCts.Token), CancellationToken.None);
            _sweep = Task.Run(() => SweepPendingAsync(deliveryService, sweepCts.Token), CancellationToken.None);
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
                if (_entries.TryGetValue(leftover, out var entry) && entry.State == EntryState.Queued)
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

    private async Task ConsumeAsync(IComicEmailService deliveryService, Task previous, ChannelReader<int> reader, CancellationToken cancellationToken)
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
                    if (!_entries.TryGetValue(deliveryId, out var entry))
                    {
                        entry = new Entry();
                        _entries.Add(deliveryId, entry);
                    }

                    entry.State = EntryState.Processing;
                    entry.ScheduledAfterProcessing = null;
                }

                try
                {
                    await deliveryService.ProcessDeliveryAsync(deliveryId, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Out of memory included: there is one consumer, and a single
                    // book too large to build must not end all later deliveries.
                    _logger.LogError(ex, "Unhandled error while processing comic email delivery {DeliveryId}", deliveryId);
                }
                finally
                {
                    lock (_entries)
                    {
                        // A retry scheduled while the delivery was processed
                        // starts its wait now; otherwise the queue is done with it.
                        if (_entries.TryGetValue(deliveryId, out var entry) &&
                            entry.ScheduledAfterProcessing is DateTimeOffset dueAt)
                        {
                            entry.ScheduledAfterProcessing = null;
                            ScheduleEntry(deliveryId, entry, dueAt);
                        }
                        else
                        {
                            _entries.Remove(deliveryId);
                        }
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
    private async Task SweepPendingAsync(IComicEmailService deliveryService, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await deliveryService.ResumePendingDeliveriesAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Out of memory included: a sweep that ended here would strand
                // whatever is pending until the next restart.
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
