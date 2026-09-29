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
    /// <paramref name="subscriptionId"/> records the series subscription that
    /// triggered an automatic send so its last-sent timestamp can be stamped
    /// once the message is actually delivered.
    /// </summary>
    Task<EmailQueueResult> QueueFilesAsync(
        IEnumerable<string> filePaths,
        int deviceId,
        string? deliveryFormat,
        string source,
        bool skipAlreadyDelivered,
        int? subscriptionId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queues <paramref name="filePaths"/> as condensed EPUB books: the issues
    /// are put in reading order and grouped into books of
    /// <paramref name="issuesPerBook"/> issues (or one book for every issue when
    /// the mode is <see cref="EmailCondenseMode.All"/>). Condensed delivery is
    /// always EPUB; the original archives cannot be merged.
    /// A book never mixes series. Set <paramref name="preserveIssueOrder"/> when
    /// the caller already supplies a single series in reading order (a
    /// send-series request); otherwise the issues are grouped by series folder
    /// and read in natural order within each group.
    /// </summary>
    Task<EmailQueueResult> QueueCondensedFilesAsync(
        IEnumerable<string> filePaths,
        int deviceId,
        string? condenseMode,
        int? issuesPerBook,
        string source,
        bool skipAlreadyDelivered,
        bool preserveIssueOrder = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Describes the books a condensed send would produce, including the
    /// estimated size of each one, so the caller can warn that an oversized
    /// book has to be downloaded instead of emailed. Nothing is queued.
    /// Pass the same <paramref name="deviceId"/> and
    /// <paramref name="skipAlreadyDelivered"/> the send will use so the plan
    /// describes exactly the books that would be queued.
    /// </summary>
    Task<CondensePlanDto> PlanCondensedDeliveryAsync(
        IEnumerable<string> filePaths,
        string? condenseMode,
        int? issuesPerBook,
        bool preserveIssueOrder = false,
        int? deviceId = null,
        bool skipAlreadyDelivered = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Builds one condensed book (the <paramref name="bookIndex"/>-th book of the
    /// plan for the same inputs) as a temporary file for the caller to stream
    /// back as a download. The caller must delete the file when finished.
    /// </summary>
    Task<CondensedBookFile> CreateCondensedBookAsync(
        IEnumerable<string> filePaths,
        string? condenseMode,
        int? issuesPerBook,
        int bookIndex,
        bool preserveIssueOrder = false,
        int? deviceId = null,
        bool skipAlreadyDelivered = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queues a newly processed issue for every enabled subscription of its
    /// series. Matches subscriptions created from either the metadata series
    /// name or the containing folder name. Returns the number of deliveries queued.
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
