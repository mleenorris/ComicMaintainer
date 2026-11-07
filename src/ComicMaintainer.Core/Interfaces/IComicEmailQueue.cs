namespace ComicMaintainer.Core.Interfaces;

/// <summary>
/// In-process queue of pending comic-email deliveries. Sending happens on a
/// background worker so API requests (and the file watcher) never block on SMTP.
/// </summary>
public interface IComicEmailQueue
{
    /// <summary>
    /// Enqueues a persisted delivery id for sending. A delivery the queue
    /// already holds (waiting or being processed) is not queued a second time.
    /// </summary>
    void Enqueue(int deliveryId);

    /// <summary>
    /// True while the queue holds the delivery: it is waiting to be processed
    /// or being processed.
    /// </summary>
    bool IsQueued(int deliveryId);

    /// <summary>
    /// Starts the background consumer, and the sweep that re-queues pending
    /// deliveries the queue does not hold (see
    /// <see cref="IComicEmailService.ResumePendingDeliveriesAsync"/>): once right
    /// away, so deliveries an earlier run left pending are resumed, and then
    /// periodically.
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the background consumer and waits for the in-flight send. Deliveries
    /// queued after the stop are kept for the next <see cref="StartAsync"/>.
    /// </summary>
    Task StopAsync(CancellationToken cancellationToken = default);
}
