namespace ComicMaintainer.Core.Notifications;

/// <summary>Sends the Settings page's test notifications.</summary>
public interface INotificationTester
{
    /// <summary>
    /// Sends a real test message to <paramref name="target"/>, or asks a media server to scan,
    /// bypassing the queue so the administrator sees the answer. Works on a target that is
    /// not enabled yet.
    /// </summary>
    /// <returns>False when <paramref name="target"/> is not a known target.</returns>
    /// <exception cref="NotificationDeliveryException">The target did not accept it.</exception>
    /// <exception cref="NotificationNotConfiguredException">The target's settings are incomplete.</exception>
    /// <exception cref="NotificationLibraryBusyException">A rescan was asked for while the library is being changed.</exception>
    Task<bool> SendTestAsync(string target, CancellationToken cancellationToken);
}

/// <summary>A rescan test was refused because files are being changed. The message is safe to show.</summary>
public sealed class NotificationLibraryBusyException : Exception
{
    public NotificationLibraryBusyException(string reason)
        : base(reason)
    {
    }
}

/// <summary>Default <see cref="INotificationTester"/>.</summary>
public sealed class NotificationTester : INotificationTester
{
    private readonly IReadOnlyDictionary<string, INotificationChannel> _channels;
    private readonly ILibraryActivityMonitor _activity;
    private readonly INotificationPublisher _publisher;
    private readonly TimeProvider _timeProvider;

    public NotificationTester(
        IEnumerable<INotificationChannel> channels,
        ILibraryActivityMonitor activity,
        INotificationPublisher publisher,
        TimeProvider? timeProvider = null)
    {
        _channels = channels.ToDictionary(channel => channel.Target, StringComparer.Ordinal);
        _activity = activity;
        _publisher = publisher;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<bool> SendTestAsync(string target, CancellationToken cancellationToken)
    {
        if (!_channels.TryGetValue(target, out var channel))
        {
            return false;
        }

        // The Test button asks for a real scan, so it follows the same rule as a queued one.
        if (NotificationTarget.IsRescan(target) && await _activity.IsBusyAsync(cancellationToken))
        {
            throw new NotificationLibraryBusyException(
                "The library is being changed right now, so no scan was requested. Try again when it is idle.");
        }

        var message = new NotificationMessage(
            NotificationMessage.Test,
            "ComicMaintainer test notification",
            NotificationTarget.IsRescan(target)
                ? "This is a test: ComicMaintainer asked for a library scan."
                : "This is a test message from ComicMaintainer. Your notification settings are working.",
            false,
            null,
            0,
            [],
            _timeProvider.GetUtcNow().UtcDateTime);
        await channel.SendAsync(message, cancellationToken);

        // Proof the target works again, so the queue need not sit out a long pause.
        _publisher.TargetVerified(target);
        return true;
    }
}
