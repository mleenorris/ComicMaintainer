using System.Text.Json;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.Core.Notifications;

/// <summary>
/// The durable notification queue. It turns raised events into rows of
/// <see cref="NotificationDeliveryEntity"/> and works through the rows that are due, one at a
/// time, on a single background loop. Everything it needs to carry on is in the rows, so a
/// restart resumes where the last run stopped.
/// </summary>
/// <remarks>
/// <para>New chapters are collected per series until the series has been quiet for a while, so
/// a batch of files is one message; when many series come due together they become one digest.
/// A job that keeps failing is reported once per <see cref="RepeatSuppression"/>.</para>
/// <para>A rescan target has at most one pending row, which each change pushes back
/// (<see cref="RescanDebounce"/>), and it is only sent once
/// <see cref="ILibraryActivityMonitor"/> reports the library idle.</para>
/// <para>Each target is spaced out (<see cref="MinTimeBetweenSends"/>) and left alone for a
/// growing time while it keeps failing, so a target that is down costs little and cannot hold
/// up the others.</para>
/// </remarks>
public sealed class NotificationDispatcher : BackgroundService
{
    /// <summary>How long a series stays quiet before its batch goes out.</summary>
    public static readonly TimeSpan BatchQuietPeriod = TimeSpan.FromMinutes(2);

    /// <summary>Longest a batch is held while chapters keep arriving.</summary>
    public static readonly TimeSpan BatchMaxHold = TimeSpan.FromMinutes(15);

    /// <summary>How long the library stays quiet before a rescan is requested.</summary>
    public static readonly TimeSpan RescanDebounce = TimeSpan.FromMinutes(2);

    /// <summary>Longest a rescan is pushed back by further changes; it still waits for the library to be idle.</summary>
    public static readonly TimeSpan RescanMaxHold = TimeSpan.FromMinutes(30);

    /// <summary>How soon the library is checked again when a rescan found it busy.</summary>
    public static readonly TimeSpan BusyRecheckDelay = TimeSpan.FromSeconds(30);

    /// <summary>Time kept between two deliveries to the same target.</summary>
    public static readonly TimeSpan MinTimeBetweenSends = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Waits between attempts after a temporary failure: the first retry waits for the
    /// first entry, and so on. A delivery gets <see cref="MaxAttempts"/> attempts in all.
    /// </summary>
    public static readonly IReadOnlyList<TimeSpan> RetryDelays = new[]
    {
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(2),
        TimeSpan.FromHours(6),
    };

    /// <summary>Most attempts a delivery gets when its failures are temporary.</summary>
    public static int MaxAttempts => RetryDelays.Count + 1;

    /// <summary>How long the same job or import event is not sent to a target again.</summary>
    public static readonly TimeSpan RepeatSuppression = TimeSpan.FromHours(6);

    /// <summary>How many series coming due together are folded into one digest.</summary>
    public const int DigestThreshold = 10;

    /// <summary>Chapters (or digest series) listed in a batch; further ones are only counted.</summary>
    public const int MaxListedItems = 50;

    /// <summary>Pending deliveries kept per target; the oldest make way when this is reached.</summary>
    public const int MaxPendingPerTarget = 500;

    /// <summary>A delivery still pending after this long is given up on.</summary>
    public static readonly TimeSpan PendingExpiry = TimeSpan.FromDays(3);

    /// <summary>How long a finished delivery is kept.</summary>
    public static readonly TimeSpan FinishedRetention = TimeSpan.FromDays(7);

    /// <summary>Finished deliveries kept per target, however recent.</summary>
    public const int MaxFinishedPerTarget = 200;

    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan PruneInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan ErrorRetryDelay = TimeSpan.FromSeconds(30);

    /// <summary>The group key of a digest; a series key always has a letter or digit first.</summary>
    internal const string DigestGroupKey = "#digest";

    private readonly IDbContextFactory<ComicMaintainerDbContext> _dbFactory;
    private readonly NotificationPublisher _publisher;
    private readonly IReadOnlyDictionary<string, INotificationChannel> _channels;
    private readonly IOptionsMonitor<AppSettings> _settings;
    private readonly ILibraryActivityMonitor _activity;
    private readonly ILogger<NotificationDispatcher> _logger;
    private readonly TimeProvider _timeProvider;

    /// <summary>Events read from the publisher that are not in the database yet.</summary>
    private readonly Queue<NotificationEvent> _unsavedEvents = new();

    /// <summary>When each target may next be sent to.</summary>
    private readonly Dictionary<string, DateTime> _nextSendAllowed = new(StringComparer.Ordinal);

    /// <summary>Consecutive temporary failures per target, for how long to leave it alone.</summary>
    private readonly Dictionary<string, int> _consecutiveFailures = new(StringComparer.Ordinal);

    /// <summary>Each target's settings when last looked at, to notice a change.</summary>
    private readonly Dictionary<string, NotificationTargetOptions> _lastSeenOptions = new(StringComparer.Ordinal);

    private DateTime _lastPruneAt = DateTime.MinValue;

    public NotificationDispatcher(
        IDbContextFactory<ComicMaintainerDbContext> dbFactory,
        NotificationPublisher publisher,
        IEnumerable<INotificationChannel> channels,
        IOptionsMonitor<AppSettings> settings,
        ILibraryActivityMonitor activity,
        ILogger<NotificationDispatcher> logger,
        TimeProvider? timeProvider = null)
    {
        _dbFactory = dbFactory;
        _publisher = publisher;
        _channels = channels.ToDictionary(channel => channel.Target, StringComparer.Ordinal);
        _settings = settings;
        _activity = activity;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    private DateTime Now => _timeProvider.GetUtcNow().UtcDateTime;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var wait = await RunOnceAsync(stoppingToken);
                await WaitForWorkAsync(wait, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Whatever went wrong (a locked database, say), the queue must keep going;
                // anything not yet delivered is still in the table.
                _logger.LogWarning(ex, "The notification queue could not run; trying again shortly");
                await Task.Delay(ErrorRetryDelay, _timeProvider, stoppingToken)
                    .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }

        await SaveRemainingEventsAsync();
    }

    /// <summary>
    /// Saves what was raised, delivers everything that is due, and prunes when an hour has
    /// passed.
    /// </summary>
    /// <returns>How long to wait before running again if nothing else is raised.</returns>
    public async Task<TimeSpan> RunOnceAsync(CancellationToken cancellationToken)
    {
        await SaveRaisedEventsAsync(cancellationToken);
        await ReleaseReconfiguredTargetsAsync(cancellationToken);
        await PruneIfDueAsync(cancellationToken);

        while (await DeliverNextDueAsync(cancellationToken))
        {
            // Anything raised during a slow delivery joins its batch.
            await SaveRaisedEventsAsync(cancellationToken);
        }

        return await TimeUntilNextDeliveryAsync(cancellationToken);
    }

    private async Task WaitForWorkAsync(TimeSpan wait, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delay = Task.Delay(wait, _timeProvider, waiting.Token);
        var raised = _publisher.Events.WaitToReadAsync(waiting.Token).AsTask();
        await Task.WhenAny(delay, raised);
        await waiting.CancelAsync();
        await delay.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        await ((Task)raised).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    /// <summary>
    /// Last chance on shutdown for events still in memory. Raised events are saved within
    /// moments of being raised, so a failure here costs only the few that arrived in the
    /// last instants.
    /// </summary>
    private async Task SaveRemainingEventsAsync()
    {
        try
        {
            await SaveRaisedEventsAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Could not save {Count} notification event(s) during shutdown", _unsavedEvents.Count);
        }
    }

    // ---------------------------------------------------------------------
    // From events to rows
    // ---------------------------------------------------------------------

    private async Task SaveRaisedEventsAsync(CancellationToken cancellationToken)
    {
        // Bounded while the database is unavailable; the publisher's channel then fills
        // and it drops, and warns about, what no longer fits.
        while (_unsavedEvents.Count < NotificationPublisher.Capacity && _publisher.Events.TryRead(out var notification))
        {
            _unsavedEvents.Enqueue(notification);
        }

        if (_unsavedEvents.Count == 0)
        {
            return;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        while (_unsavedEvents.TryPeek(out var notification))
        {
            await ApplyEventAsync(db, notification, cancellationToken);

            // Only after it is committed, so a database failure retries this event.
            _unsavedEvents.Dequeue();
        }
    }

    private async Task ApplyEventAsync(ComicMaintainerDbContext db, NotificationEvent notification, CancellationToken cancellationToken)
    {
        if (notification.EventType == NotificationEventType.TargetVerified)
        {
            await ReleaseTargetAsync(notification.GroupKey!, cancellationToken);
            return;
        }

        var now = Now;
        if (notification.FilePath is not null && !await IsRecentlyAddedAsync(db, notification.FilePath, now, cancellationToken))
        {
            return;
        }

        var settings = _settings.CurrentValue;
        foreach (var target in NotificationTarget.All)
        {
            if (!_channels.ContainsKey(target) || !NotificationTargetOptions.For(settings, target).Receives(notification.EventType))
            {
                continue;
            }

            if (NotificationTarget.IsRescan(target))
            {
                await RequestRescanAsync(db, target, now, cancellationToken);
            }
            else if (notification.EventType == NotificationEventType.NewChapters)
            {
                await AddChapterToBatchAsync(db, target, notification, now, cancellationToken);
            }
            else
            {
                await AddJobEventAsync(db, target, notification, now, cancellationToken);
            }

            await db.SaveChangesAsync(cancellationToken);
            await EnforcePendingLimitAsync(db, target, cancellationToken);
        }
    }

    /// <summary>
    /// Whether a file was first seen recently, so one that is processed again long after it
    /// arrived, because someone cleared its status for example, is not announced as new.
    /// </summary>
    private static async Task<bool> IsRecentlyAddedAsync(ComicMaintainerDbContext db, string filePath, DateTime now, CancellationToken cancellationToken)
    {
        var created = await db.ComicFiles
            .AsNoTracking()
            .Where(file => file.FilePath == filePath)
            .Select(file => (DateTime?)file.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        return created is { } createdAt && now - DateTime.SpecifyKind(createdAt, DateTimeKind.Utc) <= NewFileWindow;
    }

    /// <summary>How long after a file is first seen its first processing counts as a new chapter.</summary>
    public static readonly TimeSpan NewFileWindow = TimeSpan.FromDays(7);

    private static async Task RequestRescanAsync(ComicMaintainerDbContext db, string target, DateTime now, CancellationToken cancellationToken)
    {
        var pending = await db.NotificationDeliveries.FirstOrDefaultAsync(
            delivery => delivery.Target == target
                && delivery.EventType == NotificationEventType.LibraryChanged
                && delivery.Status == NotificationDeliveryStatus.Pending,
            cancellationToken);

        if (pending is null)
        {
            db.NotificationDeliveries.Add(new NotificationDeliveryEntity
            {
                Target = target,
                EventType = NotificationEventType.LibraryChanged,
                Title = "Library",
                DueAt = now + RescanDebounce,
                CreatedAt = now,
            });
        }
        else if (pending.AttemptCount == 0)
        {
            // Only ever later: a rescan past its limit is already due, and pulling it back to
            // then would cut short the wait of one that just found the library busy.
            var pushedBack = Earliest(now + RescanDebounce, AsUtc(pending.CreatedAt) + RescanMaxHold);
            if (pushedBack > AsUtc(pending.DueAt))
            {
                pending.DueAt = pushedBack;
            }
        }

        // A rescan waiting to retry keeps its time; when it goes out it covers this change too.
    }

    private static async Task AddChapterToBatchAsync(ComicMaintainerDbContext db, string target, NotificationEvent notification, DateTime now, CancellationToken cancellationToken)
    {
        var batch = await db.NotificationDeliveries.FirstOrDefaultAsync(
            delivery => delivery.Target == target
                && delivery.EventType == NotificationEventType.NewChapters
                && delivery.GroupKey == notification.GroupKey
                && delivery.Status == NotificationDeliveryStatus.Pending
                && delivery.AttemptCount == 0,
            cancellationToken);

        var label = LimitLength(notification.Body ?? string.Empty, 512);
        if (batch is null)
        {
            db.NotificationDeliveries.Add(new NotificationDeliveryEntity
            {
                Target = target,
                EventType = NotificationEventType.NewChapters,
                GroupKey = LimitLength(notification.GroupKey, 512),
                Title = LimitLength(notification.Title, 512),
                ItemCount = 1,
                ItemsJson = JsonSerializer.Serialize(new[] { label }),
                DueAt = now + BatchQuietPeriod,
                CreatedAt = now,
            });
            return;
        }

        var items = ReadItems(batch.ItemsJson);
        if (!items.Contains(label))
        {
            batch.ItemCount++;
            if (items.Count < MaxListedItems)
            {
                items.Add(label);
                batch.ItemsJson = JsonSerializer.Serialize(items);
            }
        }

        batch.Title = LimitLength(notification.Title, 512);
        batch.DueAt = Earliest(now + BatchQuietPeriod, AsUtc(batch.CreatedAt) + BatchMaxHold);
    }

    private static async Task AddJobEventAsync(ComicMaintainerDbContext db, string target, NotificationEvent notification, DateTime now, CancellationToken cancellationToken)
    {
        // A failed delivery does not count: that message never reached anyone.
        var since = now - RepeatSuppression;
        var alreadyReported = await db.NotificationDeliveries.AnyAsync(
            delivery => delivery.Target == target
                && delivery.EventType == notification.EventType
                && delivery.GroupKey == notification.GroupKey
                && delivery.CreatedAt >= since
                && (delivery.Status == NotificationDeliveryStatus.Pending || delivery.Status == NotificationDeliveryStatus.Sent),
            cancellationToken);
        if (alreadyReported)
        {
            return;
        }

        db.NotificationDeliveries.Add(new NotificationDeliveryEntity
        {
            Target = target,
            EventType = notification.EventType,
            GroupKey = LimitLength(notification.GroupKey, 512),
            Title = LimitLength(notification.Title, 512),
            Body = LimitLength(notification.Body, 4000),
            DueAt = now,
            CreatedAt = now,
        });
    }

    /// <summary>Keeps a target that stays down from growing the table without limit.</summary>
    private async Task EnforcePendingLimitAsync(ComicMaintainerDbContext db, string target, CancellationToken cancellationToken)
    {
        var excess = await db.NotificationDeliveries
            .CountAsync(delivery => delivery.Target == target && delivery.Status == NotificationDeliveryStatus.Pending, cancellationToken)
            - MaxPendingPerTarget;
        if (excess <= 0)
        {
            return;
        }

        var oldest = await db.NotificationDeliveries
            .AsNoTracking()
            .Where(delivery => delivery.Target == target && delivery.Status == NotificationDeliveryStatus.Pending)
            .OrderBy(delivery => delivery.Id)
            .Take(excess)
            .Select(delivery => delivery.Id)
            .ToListAsync(cancellationToken);
        await db.NotificationDeliveries.Where(delivery => oldest.Contains(delivery.Id)).ExecuteDeleteAsync(cancellationToken);
        _logger.LogWarning(
            "Dropped {Count} old notification(s) for {Target}: more than {Limit} were waiting",
            oldest.Count, target, MaxPendingPerTarget);
    }

    // ---------------------------------------------------------------------
    // From rows to the targets
    // ---------------------------------------------------------------------

    /// <summary>Handles the next delivery that is due. False when none is.</summary>
    private async Task<bool> DeliverNextDueAsync(CancellationToken cancellationToken)
    {
        var now = Now;
        var waiting = _nextSendAllowed.Where(entry => entry.Value > now).Select(entry => entry.Key).ToList();

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var delivery = await db.NotificationDeliveries
            .Where(row => row.Status == NotificationDeliveryStatus.Pending && row.DueAt <= now && !waiting.Contains(row.Target))
            .OrderBy(row => row.DueAt)
            .ThenBy(row => row.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (delivery is null)
        {
            return false;
        }

        await DeliverAsync(db, delivery, now, cancellationToken);
        return true;
    }

    private async Task DeliverAsync(ComicMaintainerDbContext db, NotificationDeliveryEntity delivery, DateTime now, CancellationToken cancellationToken)
    {
        // First, because the settings below exist only for a known target: a row left by a
        // different version must end here instead of stopping every delivery behind it.
        if (!_channels.TryGetValue(delivery.Target, out var channel))
        {
            FinishFailed(delivery, now, "Unknown notification target.");
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        var options = NotificationTargetOptions.For(_settings.CurrentValue, delivery.Target);
        var stillWanted = NotificationTarget.IsRescan(delivery.Target) ? options.Enabled : options.Receives(delivery.EventType);
        if (!stillWanted)
        {
            // Switched off since this was queued.
            delivery.Status = NotificationDeliveryStatus.Cancelled;
            delivery.CompletedAt = now;
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        if (NotificationTarget.IsRescan(delivery.Target) && await _activity.IsBusyAsync(cancellationToken))
        {
            delivery.DueAt = now + BusyRecheckDelay;
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        if (delivery.EventType == NotificationEventType.NewChapters && delivery.AttemptCount == 0)
        {
            await FoldIntoDigestIfManyAsync(db, delivery, now, cancellationToken);
        }

        // Saved first so a crash during the call still counts against the delivery.
        delivery.AttemptCount++;
        _nextSendAllowed[delivery.Target] = now + MinTimeBetweenSends;
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            await channel.SendAsync(NotificationMessageBuilder.Build(delivery), cancellationToken);
            delivery.Status = NotificationDeliveryStatus.Sent;
            delivery.CompletedAt = Now;
            delivery.LastError = null;
            _consecutiveFailures.Remove(delivery.Target);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // An orderly stop says nothing about this target, so it is not held against the delivery.
            delivery.AttemptCount--;
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        catch (NotificationDeliveryException ex) when (ex.IsTransient && delivery.AttemptCount < MaxAttempts)
        {
            ScheduleRetry(delivery, ex);
        }
        catch (NotificationDeliveryException ex)
        {
            FinishFailed(delivery, Now, ex.Message);
        }
        catch (NotificationNotConfiguredException ex)
        {
            FinishFailed(delivery, Now, ex.Message);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Unexpected error sending a notification to {Target}", delivery.Target);
            FinishFailed(delivery, Now, "Unexpected error while sending.");
        }

        await db.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>
    /// A changed setting may be the fix for a target that kept failing, so the target gets a
    /// fresh start. Noticed within a minute, which is the longest the loop sleeps.
    /// </summary>
    private async Task ReleaseReconfiguredTargetsAsync(CancellationToken cancellationToken)
    {
        var settings = _settings.CurrentValue;
        foreach (var target in NotificationTarget.All.Where(_channels.ContainsKey))
        {
            var current = NotificationTargetOptions.For(settings, target);
            if (_lastSeenOptions.TryGetValue(target, out var previous) && previous != current)
            {
                await ReleaseTargetAsync(target, cancellationToken);
            }

            // Only now, so a database failure above tries again on the next pass.
            _lastSeenOptions[target] = current;
        }
    }

    /// <summary>
    /// Ends a target's pause and makes what was waiting to retry due now, so a target that has
    /// been fixed is not left silent for hours. Batches still gathering chapters and rescans
    /// waiting for the library keep their time: those never failed.
    /// </summary>
    private async Task ReleaseTargetAsync(string target, CancellationToken cancellationToken)
    {
        _nextSendAllowed.Remove(target);
        _consecutiveFailures.Remove(target);

        var now = Now;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await db.NotificationDeliveries
            .Where(row => row.Target == target
                && row.Status == NotificationDeliveryStatus.Pending
                && row.AttemptCount > 0
                && row.DueAt > now)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.DueAt, now), cancellationToken);
    }

    private void ScheduleRetry(NotificationDeliveryEntity delivery, NotificationDeliveryException failure)
    {
        var now = Now;
        var delay = RetryDelays[Math.Clamp(delivery.AttemptCount - 1, 0, RetryDelays.Count - 1)];
        if (failure.RetryAfter is { } retryAfter && retryAfter > delay)
        {
            delay = retryAfter;
        }

        delivery.DueAt = now + delay;
        delivery.LastError = LimitLength(failure.Message, 1024);

        var failures = _consecutiveFailures[delivery.Target] = _consecutiveFailures.GetValueOrDefault(delivery.Target) + 1;
        var pause = RetryDelays[Math.Clamp(failures - 1, 0, RetryDelays.Count - 1)];
        _nextSendAllowed[delivery.Target] = now + (pause > delay ? pause : delay);

        _logger.LogWarning(
            "Notification to {Target} failed (attempt {Attempt} of {MaxAttempts}), retrying at {RetryAt:u}: {Reason}",
            delivery.Target, delivery.AttemptCount, MaxAttempts, delivery.DueAt, failure.Message);
    }

    private void FinishFailed(NotificationDeliveryEntity delivery, DateTime now, string reason)
    {
        delivery.Status = NotificationDeliveryStatus.Failed;
        delivery.CompletedAt = now;
        delivery.LastError = LimitLength(reason, 1024);
        _logger.LogWarning("Giving up on notification to {Target}: {Reason}", delivery.Target, reason);
    }

    /// <summary>
    /// When many series come due for one target at once (a first import of a library, say),
    /// sends one message listing them instead of one each.
    /// </summary>
    private static async Task FoldIntoDigestIfManyAsync(ComicMaintainerDbContext db, NotificationDeliveryEntity delivery, DateTime now, CancellationToken cancellationToken)
    {
        var others = await db.NotificationDeliveries
            .Where(row => row.Id != delivery.Id
                && row.Target == delivery.Target
                && row.EventType == NotificationEventType.NewChapters
                && row.Status == NotificationDeliveryStatus.Pending
                && row.AttemptCount == 0
                && row.DueAt <= now)
            .ToListAsync(cancellationToken);
        if (others.Count + 1 < DigestThreshold)
        {
            return;
        }

        var all = others.Prepend(delivery).ToList();
        // Read before the chapter count below is overwritten with the total.
        var listed = all.Select(row => $"{row.Title} ({row.ItemCount})").Take(MaxListedItems).ToList();
        delivery.ItemCount = all.Sum(row => row.ItemCount);
        delivery.ItemsJson = JsonSerializer.Serialize(listed);
        delivery.Body = all.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        delivery.GroupKey = DigestGroupKey;
        db.NotificationDeliveries.RemoveRange(others);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>How long until something can be delivered, at most <see cref="PollInterval"/>.</summary>
    private async Task<TimeSpan> TimeUntilNextDeliveryAsync(CancellationToken cancellationToken)
    {
        var now = Now;
        var waiting = _nextSendAllowed.Where(entry => entry.Value > now).ToList();
        var waitingTargets = waiting.Select(entry => entry.Key).ToList();

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var nextDue = await db.NotificationDeliveries
            .Where(row => row.Status == NotificationDeliveryStatus.Pending && !waitingTargets.Contains(row.Target))
            .MinAsync(row => (DateTime?)row.DueAt, cancellationToken);

        var candidates = waiting.Select(entry => entry.Value - now).ToList();
        if (nextDue is { } due)
        {
            candidates.Add(AsUtc(due) - now);
        }

        var wait = candidates.Count == 0 ? PollInterval : candidates.Min();
        if (wait < MinimumWait)
        {
            return MinimumWait;
        }

        return wait < PollInterval ? wait : PollInterval;
    }

    private static readonly TimeSpan MinimumWait = TimeSpan.FromMilliseconds(250);

    // ---------------------------------------------------------------------
    // Retention
    // ---------------------------------------------------------------------

    private async Task PruneIfDueAsync(CancellationToken cancellationToken)
    {
        var now = Now;
        if (now - _lastPruneAt < PruneInterval)
        {
            return;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var expired = now - PendingExpiry;
        await db.NotificationDeliveries
            .Where(row => row.Status == NotificationDeliveryStatus.Pending && row.CreatedAt < expired)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(row => row.Status, NotificationDeliveryStatus.Failed)
                    .SetProperty(row => row.CompletedAt, now)
                    .SetProperty(row => row.LastError, "Gave up: still undelivered after three days."),
                cancellationToken);

        var finishedBefore = now - FinishedRetention;
        await db.NotificationDeliveries
            .Where(row => row.Status != NotificationDeliveryStatus.Pending && row.CreatedAt < finishedBefore)
            .ExecuteDeleteAsync(cancellationToken);

        foreach (var target in NotificationTarget.All)
        {
            var keepFrom = await db.NotificationDeliveries
                .AsNoTracking()
                .Where(row => row.Target == target && row.Status != NotificationDeliveryStatus.Pending)
                .OrderByDescending(row => row.Id)
                .Skip(MaxFinishedPerTarget)
                .Select(row => (int?)row.Id)
                .FirstOrDefaultAsync(cancellationToken);
            if (keepFrom is { } lastDropped)
            {
                await db.NotificationDeliveries
                    .Where(row => row.Target == target && row.Status != NotificationDeliveryStatus.Pending && row.Id <= lastDropped)
                    .ExecuteDeleteAsync(cancellationToken);
            }
        }

        _lastPruneAt = now;
    }

    private static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static DateTime Earliest(DateTime first, DateTime second) => first < second ? first : second;

    private static List<string> ReadItems(string? itemsJson) =>
        string.IsNullOrEmpty(itemsJson)
            ? new List<string>()
            : JsonSerializer.Deserialize<List<string>>(itemsJson) ?? new List<string>();

    private static string LimitLength(string? value, int maxLength)
    {
        var text = value ?? string.Empty;
        return text.Length <= maxLength ? text : text[..maxLength];
    }
}

/// <summary>Turns a queued delivery into the message its channel sends.</summary>
internal static class NotificationMessageBuilder
{

    private const int ItemsListedInBody = 10;

    public static NotificationMessage Build(NotificationDeliveryEntity delivery)
    {
        var occurredAt = DateTime.SpecifyKind(delivery.CreatedAt, DateTimeKind.Utc);
        return delivery.EventType switch
        {
            NotificationEventType.NewChapters => BuildNewChapters(delivery, occurredAt),
            NotificationEventType.SuwayomiImportFailed => Simple(delivery, "Suwayomi import failed", failure: true, occurredAt),
            NotificationEventType.JobFailed => Simple(delivery, $"Scheduled job failed: {delivery.Title}", failure: true, occurredAt),
            NotificationEventType.JobFindings => Simple(delivery, $"{delivery.Title} reported findings", failure: false, occurredAt),
            _ => new NotificationMessage(
                NotificationEventType.LibraryChanged, "Library changed", "Files changed in the library, so a scan was requested.",
                false, null, 0, [], occurredAt),
        };
    }

    private static NotificationMessage Simple(NotificationDeliveryEntity delivery, string title, bool failure, DateTime occurredAt) =>
        new(delivery.EventType, title, delivery.Body ?? string.Empty, failure, null, 0, [], occurredAt);

    private static NotificationMessage BuildNewChapters(NotificationDeliveryEntity delivery, DateTime occurredAt)
    {
        var items = string.IsNullOrEmpty(delivery.ItemsJson)
            ? new List<string>()
            : JsonSerializer.Deserialize<List<string>>(delivery.ItemsJson) ?? new List<string>();

        if (delivery.GroupKey == NotificationDispatcher.DigestGroupKey)
        {
            var series = int.Parse(delivery.Body ?? "0", System.Globalization.CultureInfo.InvariantCulture);
            return new NotificationMessage(
                NotificationEventType.NewChapters,
                $"{delivery.ItemCount} new chapters in {series} series",
                Join(items, series - items.Count),
                false, null, delivery.ItemCount, items, occurredAt);
        }

        return new NotificationMessage(
            NotificationEventType.NewChapters,
            delivery.ItemCount == 1 ? $"New chapter: {delivery.Title}" : $"{delivery.ItemCount} new chapters: {delivery.Title}",
            Join(items, delivery.ItemCount - items.Count),
            false, delivery.Title, delivery.ItemCount, items, occurredAt);
    }

    /// <summary>The first few items, followed by how many more there are.</summary>
    private static string Join(IReadOnlyList<string> items, int notListed)
    {
        var shown = items.Take(ItemsListedInBody).ToList();
        var more = items.Count - shown.Count + Math.Max(0, notListed);
        return more > 0 ? $"{string.Join(", ", shown)} and {more} more" : string.Join(", ", shown);
    }
}
