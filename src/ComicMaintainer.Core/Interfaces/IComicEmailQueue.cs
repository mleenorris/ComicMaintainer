namespace ComicMaintainer.Core.Interfaces;

/// <summary>
/// In-process queue of pending comic-email deliveries. Sending happens on a
/// background worker so API requests (and the file watcher) never block on SMTP.
/// </summary>
public interface IComicEmailQueue
{
    /// <summary>Enqueues a persisted delivery id for sending.</summary>
    void Enqueue(int deliveryId);

    /// <summary>Starts the background consumer.</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops the background consumer and waits for the in-flight send.</summary>
    Task StopAsync(CancellationToken cancellationToken = default);
}
