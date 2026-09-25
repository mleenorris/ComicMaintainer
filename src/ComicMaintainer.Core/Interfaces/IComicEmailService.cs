using ComicMaintainer.Core.Models;

namespace ComicMaintainer.Core.Interfaces;

/// <summary>
/// Orchestrates emailing comics to saved ereader devices: queueing deliveries,
/// converting to EPUB when requested, sending, and recording the outcome.
/// </summary>
public interface IComicEmailService
{
    /// <summary>True when SMTP settings are complete enough to send.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Queues <paramref name="filePaths"/> for delivery to <paramref name="deviceId"/>.
    /// Files that do not exist, live outside the library, or (when
    /// <paramref name="skipAlreadyDelivered"/> is true) were already delivered to
    /// the device are reported in <see cref="EmailQueueResult.Skipped"/>.
    /// </summary>
    Task<EmailQueueResult> QueueFilesAsync(
        IEnumerable<string> filePaths,
        int deviceId,
        string? deliveryFormat,
        string source,
        bool skipAlreadyDelivered,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queues a newly processed issue for every enabled subscription of its
    /// series. Returns the number of deliveries queued.
    /// </summary>
    Task<int> QueueAutoSendAsync(string filePath, string? seriesTitle, CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs a queued delivery: converts if needed, sends, and records the
    /// result. Never throws for delivery failures; they are persisted on the
    /// delivery record instead.
    /// </summary>
    Task ProcessDeliveryAsync(int deliveryId, CancellationToken cancellationToken = default);

    /// <summary>Sends a short test email to a device so the user can verify SMTP settings.</summary>
    Task SendTestEmailAsync(int deviceId, CancellationToken cancellationToken = default);

    /// <summary>Most recent deliveries, newest first.</summary>
    Task<IReadOnlyList<ComicEmailDeliveryDto>> GetRecentDeliveriesAsync(int limit, CancellationToken cancellationToken = default);
}
