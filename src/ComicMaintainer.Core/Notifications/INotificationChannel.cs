namespace ComicMaintainer.Core.Notifications;

/// <summary>
/// One target notifications are delivered to. An implementation reads its settings when it
/// sends, so a change takes effect at the next delivery.
/// </summary>
public interface INotificationChannel
{
    /// <summary>The target this channel delivers to, one of <see cref="NotificationTarget"/>.</summary>
    string Target { get; }

    /// <summary>Delivers <paramref name="message"/>, or asks the media server to scan.</summary>
    /// <exception cref="NotificationDeliveryException">The target did not accept it.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task SendAsync(NotificationMessage message, CancellationToken cancellationToken);
}

/// <summary>What a channel delivers.</summary>
/// <param name="EventType">The event, one of <see cref="NotificationEventType"/>, or <see cref="Test"/>.</param>
/// <param name="Title">One line saying what happened.</param>
/// <param name="Body">The detail.</param>
/// <param name="IsFailure">Whether something went wrong, which raises the message's priority.</param>
/// <param name="Series">The series of a new-chapter batch; null otherwise.</param>
/// <param name="ItemCount">How many chapters a new-chapter batch holds; 0 otherwise.</param>
/// <param name="Items">The chapters (or, in a digest, the series) of a batch.</param>
/// <param name="OccurredAtUtc">When the delivery was created.</param>
public sealed record NotificationMessage(
    string EventType,
    string Title,
    string Body,
    bool IsFailure,
    string? Series,
    int ItemCount,
    IReadOnlyList<string> Items,
    DateTime OccurredAtUtc)
{
    /// <summary>The event type of a message sent by a Test button.</summary>
    public const string Test = "test";
}

/// <summary>A target did not accept a delivery. The message is safe to log and show.</summary>
public sealed class NotificationDeliveryException : Exception
{
    public NotificationDeliveryException(string reason, bool isTransient, TimeSpan? retryAfter = null)
        : base(reason)
    {
        IsTransient = isTransient;
        RetryAfter = retryAfter;
    }

    /// <summary>True when trying again later may work; false for a refusal that will not change on its own.</summary>
    public bool IsTransient { get; }

    /// <summary>How long the target asked to be left alone, when it said.</summary>
    public TimeSpan? RetryAfter { get; }
}

/// <summary>A target cannot be used because its settings are incomplete. The message is safe to show.</summary>
public sealed class NotificationNotConfiguredException : Exception
{
    public NotificationNotConfiguredException(string reason)
        : base(reason)
    {
    }
}
