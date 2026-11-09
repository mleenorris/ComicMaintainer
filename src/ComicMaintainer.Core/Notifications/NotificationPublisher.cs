using System.Threading.Channels;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.ErrorReporting;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.Core.Notifications;

/// <summary>
/// Raises notification events. Every method returns at once and never throws, so a
/// notification problem can neither fail nor slow the job that raised it.
/// </summary>
public interface INotificationPublisher
{
    /// <summary>A chapter is new to the library.</summary>
    /// <param name="seriesTitle">The series, which chapters are batched by.</param>
    /// <param name="chapterLabel">The chapter, as it appears in the message.</param>
    /// <param name="filePath">
    /// The chapter's file when it may not really be new (a file processed again after its
    /// status was cleared); null when the caller knows it is.
    /// </param>
    void NewChapter(string seriesTitle, string chapterLabel, string? filePath = null);

    /// <summary>Files in the library changed.</summary>
    void LibraryChanged();

    /// <summary>A Suwayomi import pass could not bring every chapter in.</summary>
    void SuwayomiImportFailed(string summary);

    /// <summary>A scheduled job failed.</summary>
    void ScheduledJobFailed(string jobKey, string jobName, string message);

    /// <summary>A scheduled job finished and reported something to look at.</summary>
    void ScheduledJobFindings(string jobKey, string jobName, string summary);
}

/// <summary>An event on its way to the delivery queue.</summary>
/// <param name="EventType">One of <see cref="NotificationEventType"/>.</param>
/// <param name="GroupKey">The series or job the event belongs to.</param>
/// <param name="Title">The series or job name.</param>
/// <param name="Body">The chapter label or message.</param>
/// <param name="FilePath">The file to check is new, for a new chapter; otherwise null.</param>
internal sealed record NotificationEvent(string EventType, string? GroupKey, string Title, string? Body, string? FilePath);

/// <summary>
/// Default <see cref="INotificationPublisher"/>: events go into a bounded in-memory channel
/// that <see cref="NotificationDispatcher"/> drains into the delivery table, so raising one
/// never waits for the database. Nothing is queued while no target wants the event.
/// </summary>
public sealed class NotificationPublisher : INotificationPublisher
{
    /// <summary>Events held for the dispatcher before new ones are dropped.</summary>
    public const int Capacity = 10_000;

    private static readonly TimeSpan DropWarningInterval = TimeSpan.FromMinutes(1);

    private readonly Channel<NotificationEvent> _events = Channel.CreateBounded<NotificationEvent>(
        new BoundedChannelOptions(Capacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

    private readonly IOptionsMonitor<AppSettings> _settings;
    private readonly ILogger<NotificationPublisher> _logger;
    private readonly TimeProvider _timeProvider;
    private long _lastDropWarningTicks;

    public NotificationPublisher(
        IOptionsMonitor<AppSettings> settings,
        ILogger<NotificationPublisher> logger,
        TimeProvider? timeProvider = null)
    {
        _settings = settings;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal ChannelReader<NotificationEvent> Events => _events.Reader;

    public void NewChapter(string seriesTitle, string chapterLabel, string? filePath = null) =>
        Publish(() => new NotificationEvent(
            NotificationEventType.NewChapters,
            SeriesKey.TryNormalize(seriesTitle) ?? seriesTitle.Trim().ToLowerInvariant(),
            seriesTitle,
            chapterLabel,
            filePath));

    public void LibraryChanged() =>
        Publish(() => new NotificationEvent(NotificationEventType.LibraryChanged, null, string.Empty, null, null));

    public void SuwayomiImportFailed(string summary) =>
        Publish(() => new NotificationEvent(
            NotificationEventType.SuwayomiImportFailed, "suwayomi-import", "Suwayomi import", ErrorReportRedactor.Redact(summary), null));

    public void ScheduledJobFailed(string jobKey, string jobName, string message) =>
        Publish(() => new NotificationEvent(
            NotificationEventType.JobFailed, jobKey, jobName, ErrorReportRedactor.Redact(message), null));

    public void ScheduledJobFindings(string jobKey, string jobName, string summary) =>
        Publish(() => new NotificationEvent(NotificationEventType.JobFindings, jobKey, jobName, summary, null));

    private void Publish(Func<NotificationEvent> create)
    {
        try
        {
            var notification = create();
            var settings = _settings.CurrentValue;
            if (!NotificationTarget.All.Any(target => NotificationTargetOptions.For(settings, target).Receives(notification.EventType)))
            {
                return;
            }

            if (!_events.Writer.TryWrite(notification))
            {
                WarnOfDrop();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Only ever from a bug here; the job that raised the event must carry on regardless.
            _logger.LogWarning(ex, "Could not queue a notification");
        }
    }

    /// <summary>Warns at most once a minute: a flood of drops must not become a flood of log lines.</summary>
    private void WarnOfDrop()
    {
        var now = _timeProvider.GetUtcNow().UtcTicks;
        var last = Interlocked.Read(ref _lastDropWarningTicks);
        if (now - last >= DropWarningInterval.Ticks
            && Interlocked.CompareExchange(ref _lastDropWarningTicks, now, last) == last)
        {
            _logger.LogWarning("The notification queue is full; new events are being dropped until it drains");
        }
    }
}
