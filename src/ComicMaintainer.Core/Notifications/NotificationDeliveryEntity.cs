namespace ComicMaintainer.Core.Notifications;

/// <summary>States of a <see cref="NotificationDeliveryEntity"/>.</summary>
public static class NotificationDeliveryStatus
{
    /// <summary>Waiting for its due time, a free rate-limit slot or the library to go quiet.</summary>
    public const string Pending = "Pending";

    /// <summary>Delivered.</summary>
    public const string Sent = "Sent";

    /// <summary>Given up on.</summary>
    public const string Failed = "Failed";

    /// <summary>Not sent because the target was switched off or stopped receiving this event.</summary>
    public const string Cancelled = "Cancelled";
}

/// <summary>
/// One message or rescan request waiting for, or already through, delivery to one target. The
/// rows are the queue: <c>NotificationDispatcher</c> works from them alone, so a
/// restart picks up where the last run stopped.
/// </summary>
public class NotificationDeliveryEntity
{
    public int Id { get; set; }

    /// <summary>One of <see cref="NotificationTarget"/>.</summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>One of <see cref="NotificationEventType"/>.</summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>
    /// What later events are merged or suppressed by: the series of a new-chapter batch, the
    /// job of a job event, null for a rescan.
    /// </summary>
    public string? GroupKey { get; set; }

    /// <summary>The series, job or digest the delivery is about.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>The text of a job or import event.</summary>
    public string? Body { get; set; }

    /// <summary>Chapters in a new-chapter batch, including those not listed in <see cref="ItemsJson"/>.</summary>
    public int ItemCount { get; set; }

    /// <summary>A JSON array of the first chapters of a batch (a digest lists its series instead).</summary>
    public string? ItemsJson { get; set; }

    /// <summary>One of <see cref="NotificationDeliveryStatus"/>.</summary>
    public string Status { get; set; } = NotificationDeliveryStatus.Pending;

    /// <summary>Attempts started so far, saved before each attempt so a crash still counts.</summary>
    public int AttemptCount { get; set; }

    /// <summary>When the delivery may next be attempted (UTC).</summary>
    public DateTime DueAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    /// <summary>Why the last attempt failed, in words safe to show and log.</summary>
    public string? LastError { get; set; }
}
