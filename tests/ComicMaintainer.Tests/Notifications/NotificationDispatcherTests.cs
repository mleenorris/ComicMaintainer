using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Notifications;
using ComicMaintainer.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace ComicMaintainer.Tests.Notifications;

/// <summary>
/// The durable queue: batching per series, debouncing rescans, retries, rate limits, pruning and
/// surviving a restart. Each test drives the dispatcher's own loop body with a fake clock.
/// </summary>
public sealed class NotificationDispatcherTests : IDisposable
{
    private const string Series = "Berserk";

    private readonly SqliteTestDbFactory _db = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero));
    private readonly AppSettings _settings;
    private readonly NotificationPublisher _publisher;
    private readonly RecordingChannel _ntfy = new(NotificationTarget.Ntfy);
    private readonly RecordingChannel _discord = new(NotificationTarget.Discord);
    private readonly RecordingChannel _komga = new(NotificationTarget.Komga);
    private readonly RecordingChannel _kavita = new(NotificationTarget.Kavita);
    private readonly Mock<ILibraryActivityMonitor> _activity = new();

    public NotificationDispatcherTests()
    {
        _settings = new AppSettings
        {
            NotifyNtfyEnabled = true,
            NotifyDiscordEnabled = true,
            NotifyKomgaEnabled = true,
            NotifyKavitaEnabled = true,
        };
        _publisher = new NotificationPublisher(NotificationTestHttp.Options(_settings), NullLogger<NotificationPublisher>.Instance, _time);
    }

    public void Dispose() => _db.Dispose();

    private NotificationDispatcher CreateDispatcher(IEnumerable<INotificationChannel>? channels = null) =>
        new(
            _db,
            _publisher,
            channels ?? new INotificationChannel[] { _ntfy, _discord, _komga, _kavita },
            NotificationTestHttp.Options(_settings),
            _activity.Object,
            NullLogger<NotificationDispatcher>.Instance,
            _time);

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private List<NotificationDeliveryEntity> Rows()
    {
        using var db = _db.CreateDbContext();
        return db.NotificationDeliveries.AsNoTracking().OrderBy(d => d.Id).ToList();
    }

    private List<NotificationDeliveryEntity> Rows(string target) =>
        Rows().Where(r => r.Target == target).ToList();

    private void SeedRow(NotificationDeliveryEntity row)
    {
        using var db = _db.CreateDbContext();
        db.NotificationDeliveries.Add(row);
        db.SaveChanges();
    }

    private void OnlyNtfy()
    {
        _settings.NotifyDiscordEnabled = false;
        _settings.NotifyKomgaEnabled = false;
        _settings.NotifyKavitaEnabled = false;
    }

    // Batching -------------------------------------------------------------

    [Fact]
    public async Task NewChapters_OfOneSeries_GoOutAsOneMessage()
    {
        OnlyNtfy();
        var dispatcher = CreateDispatcher();

        _publisher.NewChapter(Series, "1");
        _publisher.NewChapter(Series, "2");
        _publisher.NewChapter(Series, "3");
        await dispatcher.RunOnceAsync(default);
        Assert.Empty(_ntfy.Sent);

        _time.Advance(NotificationDispatcher.BatchQuietPeriod);
        await dispatcher.RunOnceAsync(default);

        var message = Assert.Single(_ntfy.Sent);
        Assert.Equal("3 new chapters: Berserk", message.Title);
        Assert.Equal("1, 2, 3", message.Body);
        Assert.Equal(3, message.ItemCount);
        Assert.Equal(Series, message.Series);
        Assert.Equal(NotificationDeliveryStatus.Sent, Assert.Single(Rows()).Status);
    }

    [Fact]
    public async Task NewChapters_AChapterSeenTwiceInABatch_IsCountedOnce()
    {
        OnlyNtfy();
        var dispatcher = CreateDispatcher();

        _publisher.NewChapter(Series, "7");
        _publisher.NewChapter(Series, "7");
        await dispatcher.RunOnceAsync(default);
        _time.Advance(NotificationDispatcher.BatchQuietPeriod);
        await dispatcher.RunOnceAsync(default);

        Assert.Equal(1, Assert.Single(_ntfy.Sent).ItemCount);
    }

    [Fact]
    public async Task NewChapters_ListOnlyTheFirstFew_ButCountThemAll()
    {
        OnlyNtfy();
        var dispatcher = CreateDispatcher();

        for (var i = 1; i <= NotificationDispatcher.MaxListedItems + 5; i++)
        {
            _publisher.NewChapter(Series, i.ToString());
        }

        await dispatcher.RunOnceAsync(default);
        _time.Advance(NotificationDispatcher.BatchQuietPeriod);
        await dispatcher.RunOnceAsync(default);

        var message = Assert.Single(_ntfy.Sent);
        Assert.Equal(NotificationDispatcher.MaxListedItems + 5, message.ItemCount);
        Assert.Contains("and 45 more", message.Body);
    }

    [Fact]
    public async Task NewChapters_OneChapterHasSingularWording()
    {
        OnlyNtfy();
        var dispatcher = CreateDispatcher();

        _publisher.NewChapter(Series, "9");
        await dispatcher.RunOnceAsync(default);
        _time.Advance(NotificationDispatcher.BatchQuietPeriod);
        await dispatcher.RunOnceAsync(default);

        Assert.Equal("New chapter: Berserk", Assert.Single(_ntfy.Sent).Title);
    }

    [Fact]
    public async Task NewChapters_ADeliveryRowIsMadeForEachTargetThatChoseThem()
    {
        _settings.NotifyKomgaEnabled = false;
        _settings.NotifyKavitaEnabled = false;
        _settings.NotifyDiscordEvents = NotificationEventType.JobFailed;
        var dispatcher = CreateDispatcher();

        _publisher.NewChapter(Series, "1");
        await dispatcher.RunOnceAsync(default);

        Assert.Single(Rows(NotificationTarget.Ntfy));
        Assert.Empty(Rows(NotificationTarget.Discord));
    }

    [Fact]
    public async Task NewChapters_AreHeldWhileChaptersKeepArriving_OnlyUpToTheLimit()
    {
        OnlyNtfy();
        var dispatcher = CreateDispatcher();

        _publisher.NewChapter(Series, "1");
        await dispatcher.RunOnceAsync(default);

        // Quiet period keeps restarting, but the batch must not wait forever.
        var waited = TimeSpan.Zero;
        var label = 2;
        while (waited < NotificationDispatcher.BatchMaxHold - NotificationDispatcher.BatchQuietPeriod)
        {
            _time.Advance(TimeSpan.FromMinutes(1));
            waited += TimeSpan.FromMinutes(1);
            _publisher.NewChapter(Series, (label++).ToString());
            await dispatcher.RunOnceAsync(default);
            Assert.Empty(_ntfy.Sent);
        }

        _time.Advance(NotificationDispatcher.BatchMaxHold - waited);
        await dispatcher.RunOnceAsync(default);

        Assert.Single(_ntfy.Sent);
    }

    [Fact]
    public async Task NewChapters_ManySeriesDueTogether_BecomeOneDigest()
    {
        OnlyNtfy();
        var dispatcher = CreateDispatcher();

        for (var i = 0; i < NotificationDispatcher.DigestThreshold; i++)
        {
            _publisher.NewChapter($"Series {i}", "1");
        }

        await dispatcher.RunOnceAsync(default);
        _time.Advance(NotificationDispatcher.BatchQuietPeriod);
        await dispatcher.RunOnceAsync(default);

        var message = Assert.Single(_ntfy.Sent);
        Assert.Equal($"{NotificationDispatcher.DigestThreshold} new chapters in {NotificationDispatcher.DigestThreshold} series", message.Title);
        Assert.Contains("Series 0 (1)", message.Body);
        Assert.Null(message.Series);
        Assert.Single(Rows());
    }

    [Fact]
    public async Task NewChapters_FewerSeriesThanTheThreshold_GetTheirOwnMessages()
    {
        OnlyNtfy();
        var dispatcher = CreateDispatcher();

        _publisher.NewChapter("One", "1");
        _publisher.NewChapter("Two", "1");
        await dispatcher.RunOnceAsync(default);
        _time.Advance(NotificationDispatcher.BatchQuietPeriod);

        await dispatcher.RunOnceAsync(default);
        _time.Advance(NotificationDispatcher.MinTimeBetweenSends);
        await dispatcher.RunOnceAsync(default);

        Assert.Equal(new[] { "New chapter: One", "New chapter: Two" }, _ntfy.Sent.Select(m => m.Title));
    }

    [Fact]
    public async Task NewChapters_ADigestListsAtMostTheFirstSeries_AndSaysHowManyMore()
    {
        OnlyNtfy();
        var dispatcher = CreateDispatcher();

        for (var i = 0; i < NotificationDispatcher.MaxListedItems + 5; i++)
        {
            _publisher.NewChapter($"Series {i}", "1");
        }

        await dispatcher.RunOnceAsync(default);
        _time.Advance(NotificationDispatcher.BatchQuietPeriod);
        await dispatcher.RunOnceAsync(default);

        var message = Assert.Single(_ntfy.Sent);
        Assert.Contains("and 45 more", message.Body);
        Assert.Equal(NotificationDispatcher.MaxListedItems + 5, message.ItemCount);
    }

    // Not really new --------------------------------------------------------

    [Fact]
    public async Task NewChapter_OfAFileAddedLongAgo_IsNotAnnounced()
    {
        OnlyNtfy();
        using (var db = _db.CreateDbContext())
        {
            db.ComicFiles.Add(new ComicFileEntity { FilePath = "/lib/old.cbz", FileName = "old.cbz", Directory = "/lib", CreatedAt = Now - NotificationDispatcher.NewFileWindow - TimeSpan.FromHours(1) });
            db.SaveChanges();
        }

        var dispatcher = CreateDispatcher();

        _publisher.NewChapter(Series, "1", "/lib/old.cbz");
        await dispatcher.RunOnceAsync(default);

        Assert.Empty(Rows());
    }

    [Fact]
    public async Task NewChapter_OfAFileAddedRecently_IsAnnounced()
    {
        OnlyNtfy();
        using (var db = _db.CreateDbContext())
        {
            db.ComicFiles.Add(new ComicFileEntity { FilePath = "/lib/new.cbz", FileName = "new.cbz", Directory = "/lib", CreatedAt = Now - TimeSpan.FromHours(1) });
            db.SaveChanges();
        }

        var dispatcher = CreateDispatcher();

        _publisher.NewChapter(Series, "1", "/lib/new.cbz");
        await dispatcher.RunOnceAsync(default);

        Assert.Single(Rows());
    }

    [Fact]
    public async Task NewChapter_OfAnUntrackedFile_IsNotAnnounced()
    {
        OnlyNtfy();
        var dispatcher = CreateDispatcher();

        _publisher.NewChapter(Series, "1", "/lib/gone.cbz");
        await dispatcher.RunOnceAsync(default);

        Assert.Empty(Rows());
    }

    // Job and import events -------------------------------------------------

    [Fact]
    public async Task JobFailure_IsSentRightAway()
    {
        OnlyNtfy();
        var dispatcher = CreateDispatcher();

        _publisher.ScheduledJobFailed("job", "Metadata Audit", "boom");
        await dispatcher.RunOnceAsync(default);

        var message = Assert.Single(_ntfy.Sent);
        Assert.Equal("Scheduled job failed: Metadata Audit", message.Title);
        Assert.Equal("boom", message.Body);
        Assert.True(message.IsFailure);
    }

    [Fact]
    public async Task Findings_AndSuwayomiFailures_AreSentWithTheirOwnWording()
    {
        OnlyNtfy();
        var dispatcher = CreateDispatcher();

        _publisher.ScheduledJobFindings("audit", "Metadata Audit", "3 mismatches");
        _publisher.SuwayomiImportFailed("2 could not be moved");
        await dispatcher.RunOnceAsync(default);
        _time.Advance(NotificationDispatcher.MinTimeBetweenSends);
        await dispatcher.RunOnceAsync(default);

        Assert.Equal("Metadata Audit reported findings", _ntfy.Sent[0].Title);
        Assert.False(_ntfy.Sent[0].IsFailure);
        Assert.Equal("Suwayomi import failed", _ntfy.Sent[1].Title);
        Assert.True(_ntfy.Sent[1].IsFailure);
    }

    [Fact]
    public async Task ARepeatedJobEvent_IsNotSentAgainWithinTheWindow_ButIsAfterIt()
    {
        OnlyNtfy();
        var dispatcher = CreateDispatcher();

        _publisher.ScheduledJobFailed("job", "Job", "boom");
        await dispatcher.RunOnceAsync(default);
        _time.Advance(TimeSpan.FromMinutes(15));
        _publisher.ScheduledJobFailed("job", "Job", "boom again");
        await dispatcher.RunOnceAsync(default);
        Assert.Single(_ntfy.Sent);

        _time.Advance(NotificationDispatcher.RepeatSuppression);
        _publisher.ScheduledJobFailed("job", "Job", "still failing");
        await dispatcher.RunOnceAsync(default);

        Assert.Equal(2, _ntfy.Sent.Count);
        Assert.Equal("still failing", _ntfy.Sent[1].Body);
    }

    [Fact]
    public async Task ADifferentJob_IsNotSuppressedByAnother()
    {
        OnlyNtfy();
        var dispatcher = CreateDispatcher();

        _publisher.ScheduledJobFailed("one", "One", "boom");
        await dispatcher.RunOnceAsync(default);
        _time.Advance(NotificationDispatcher.MinTimeBetweenSends);
        _publisher.ScheduledJobFailed("two", "Two", "boom");
        await dispatcher.RunOnceAsync(default);

        Assert.Equal(2, _ntfy.Sent.Count);
    }

    [Fact]
    public async Task AFailedDelivery_DoesNotSuppressTheNextEvent()
    {
        OnlyNtfy();
        _ntfy.OnSend = (_, _) => throw new NotificationDeliveryException("rejected", isTransient: false);
        var dispatcher = CreateDispatcher();

        _publisher.ScheduledJobFailed("job", "Job", "boom");
        await dispatcher.RunOnceAsync(default);
        _time.Advance(TimeSpan.FromMinutes(1));

        _ntfy.OnSend = null;
        _publisher.ScheduledJobFailed("job", "Job", "boom");
        await dispatcher.RunOnceAsync(default);

        Assert.Single(_ntfy.Sent);
    }

    [Fact]
    public async Task EventsArePerTargetChoice()
    {
        _settings.NotifyKomgaEnabled = false;
        _settings.NotifyKavitaEnabled = false;
        _settings.NotifyNtfyEvents = NotificationEventType.JobFailed;
        _settings.NotifyDiscordEvents = NotificationEventType.JobFindings;
        var dispatcher = CreateDispatcher();

        _publisher.ScheduledJobFailed("job", "Job", "boom");
        await dispatcher.RunOnceAsync(default);

        Assert.Single(_ntfy.Sent);
        Assert.Empty(_discord.Sent);
    }

    // Rescans ---------------------------------------------------------------

    private void OnlyKomga()
    {
        _settings.NotifyNtfyEnabled = false;
        _settings.NotifyDiscordEnabled = false;
        _settings.NotifyKavitaEnabled = false;
    }

    [Fact]
    public async Task Rescan_ManyChanges_RequestOneScan_AfterTheLibraryGoesQuiet()
    {
        OnlyKomga();
        var dispatcher = CreateDispatcher();

        for (var i = 0; i < 25; i++)
        {
            _publisher.LibraryChanged();
            _publisher.NewChapter(Series, i.ToString());
            await dispatcher.RunOnceAsync(default);
            _time.Advance(TimeSpan.FromSeconds(5));
        }

        Assert.Empty(_komga.Sent);
        Assert.Single(Rows(NotificationTarget.Komga));

        _time.Advance(NotificationDispatcher.RescanDebounce);
        await dispatcher.RunOnceAsync(default);

        Assert.Single(_komga.Sent);
    }

    [Fact]
    public async Task Rescan_NeverGoesOutWhileTheLibraryIsBusy_AndDoesOnceItIsIdle()
    {
        OnlyKomga();
        _activity.SetupSequence(a => a.IsBusyAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true)
            .ReturnsAsync(true)
            .ReturnsAsync(false);
        var dispatcher = CreateDispatcher();

        _publisher.LibraryChanged();
        await dispatcher.RunOnceAsync(default);
        _time.Advance(NotificationDispatcher.RescanDebounce);

        await dispatcher.RunOnceAsync(default);
        Assert.Empty(_komga.Sent);
        Assert.Equal(Now + NotificationDispatcher.BusyRecheckDelay, Assert.Single(Rows(NotificationTarget.Komga)).DueAt);

        _time.Advance(NotificationDispatcher.BusyRecheckDelay);
        await dispatcher.RunOnceAsync(default);
        Assert.Empty(_komga.Sent);

        _time.Advance(NotificationDispatcher.BusyRecheckDelay);
        await dispatcher.RunOnceAsync(default);
        Assert.Single(_komga.Sent);
        Assert.Equal(1, Assert.Single(Rows(NotificationTarget.Komga)).AttemptCount);
    }

    [Fact]
    public async Task Rescan_ANeverQuietLibrary_StillFiresAfterTheLimit()
    {
        OnlyKomga();
        var dispatcher = CreateDispatcher();

        _publisher.LibraryChanged();
        await dispatcher.RunOnceAsync(default);

        // Changes arrive just inside the debounce, right up to the limit on pushing the scan back.
        var created = Now;
        while (Now - created + NotificationDispatcher.RescanDebounce < NotificationDispatcher.RescanMaxHold)
        {
            _time.Advance(TimeSpan.FromMinutes(1));
            _publisher.LibraryChanged();
            await dispatcher.RunOnceAsync(default);
            Assert.Empty(_komga.Sent);
        }

        _time.Advance(created + NotificationDispatcher.RescanMaxHold - Now);
        await dispatcher.RunOnceAsync(default);

        Assert.Single(_komga.Sent);
    }

    [Fact]
    public async Task Rescan_AFailedRequest_RetriesOnItsOwnTime_AndLaterChangesJoinIt()
    {
        OnlyKomga();
        _komga.OnSend = (_, _) => throw new NotificationDeliveryException("down", isTransient: true);
        var dispatcher = CreateDispatcher();

        _publisher.LibraryChanged();
        await dispatcher.RunOnceAsync(default);
        _time.Advance(NotificationDispatcher.RescanDebounce);
        await dispatcher.RunOnceAsync(default);
        var retryAt = Assert.Single(Rows(NotificationTarget.Komga)).DueAt;

        _publisher.LibraryChanged();
        await dispatcher.RunOnceAsync(default);

        var row = Assert.Single(Rows(NotificationTarget.Komga));
        Assert.Equal(retryAt, row.DueAt);

        _komga.OnSend = null;
        _time.Advance(NotificationDispatcher.RetryDelays[0]);
        await dispatcher.RunOnceAsync(default);

        Assert.Single(_komga.Sent);
        Assert.Equal(NotificationDeliveryStatus.Sent, Assert.Single(Rows(NotificationTarget.Komga)).Status);
    }

    [Fact]
    public async Task Rescan_TargetChoosingOnlyNewChapters_IgnoresOtherChanges()
    {
        OnlyKomga();
        _settings.NotifyKomgaEvents = NotificationEventType.NewChapters;
        var dispatcher = CreateDispatcher();

        _publisher.LibraryChanged();
        await dispatcher.RunOnceAsync(default);
        Assert.Empty(Rows());

        _publisher.NewChapter(Series, "1");
        await dispatcher.RunOnceAsync(default);
        Assert.Single(Rows(NotificationTarget.Komga));
    }

    [Fact]
    public async Task Rescan_MessageSaysALibraryScanWasRequested()
    {
        OnlyKomga();
        var dispatcher = CreateDispatcher();

        _publisher.LibraryChanged();
        await dispatcher.RunOnceAsync(default);
        _time.Advance(NotificationDispatcher.RescanDebounce);
        await dispatcher.RunOnceAsync(default);

        Assert.Contains("scan", Assert.Single(_komga.Sent).Body);
    }

    // Retries, rate limits ---------------------------------------------------

    private void QueueJobFailure(int number = 1) => _publisher.ScheduledJobFailed($"job{number}", "Job", "boom");

    [Fact]
    public async Task TemporaryFailure_IsRetriedAfterItsBackoff()
    {
        OnlyNtfy();
        var failures = 1;
        _ntfy.OnSend = (_, _) =>
        {
            if (failures-- > 0)
            {
                throw new NotificationDeliveryException("server down", isTransient: true);
            }

            return Task.CompletedTask;
        };
        var dispatcher = CreateDispatcher();

        QueueJobFailure();
        await dispatcher.RunOnceAsync(default);

        var row = Assert.Single(Rows());
        Assert.Equal(1, row.AttemptCount);
        Assert.Equal(NotificationDeliveryStatus.Pending, row.Status);
        Assert.Equal("server down", row.LastError);
        Assert.Equal(Now + NotificationDispatcher.RetryDelays[0], row.DueAt);

        _time.Advance(NotificationDispatcher.RetryDelays[0]);
        await dispatcher.RunOnceAsync(default);

        Assert.Single(_ntfy.Sent);
        Assert.Equal(NotificationDeliveryStatus.Sent, Assert.Single(Rows()).Status);
    }

    [Fact]
    public async Task RepeatedTemporaryFailures_AreCappedAndGivenUpOn()
    {
        OnlyNtfy();
        var attempts = 0;
        _ntfy.OnSend = (_, _) =>
        {
            attempts++;
            throw new NotificationDeliveryException("server down", isTransient: true);
        };
        var dispatcher = CreateDispatcher();

        QueueJobFailure();
        for (var i = 0; i < NotificationDispatcher.MaxAttempts; i++)
        {
            await dispatcher.RunOnceAsync(default);
            _time.Advance(TimeSpan.FromHours(7));
        }

        Assert.Equal(NotificationDispatcher.MaxAttempts, attempts);
        var row = Assert.Single(Rows());
        Assert.Equal(NotificationDeliveryStatus.Failed, row.Status);
        Assert.Contains("server down", row.LastError);
        Assert.NotNull(row.CompletedAt);

        await dispatcher.RunOnceAsync(default);
        Assert.Equal(NotificationDispatcher.MaxAttempts, attempts);
    }

    [Fact]
    public async Task PermanentFailure_IsNotRetried()
    {
        OnlyNtfy();
        var attempts = 0;
        _ntfy.OnSend = (_, _) =>
        {
            attempts++;
            throw new NotificationDeliveryException("forbidden", isTransient: false);
        };
        var dispatcher = CreateDispatcher();

        QueueJobFailure();
        await dispatcher.RunOnceAsync(default);
        _time.Advance(TimeSpan.FromHours(1));
        await dispatcher.RunOnceAsync(default);

        Assert.Equal(1, attempts);
        Assert.Equal(NotificationDeliveryStatus.Failed, Assert.Single(Rows()).Status);
    }

    [Fact]
    public async Task MissingConfiguration_IsNotRetried()
    {
        OnlyNtfy();
        _ntfy.OnSend = (_, _) => throw new NotificationNotConfiguredException("No topic is set for ntfy.");
        var dispatcher = CreateDispatcher();

        QueueJobFailure();
        await dispatcher.RunOnceAsync(default);

        var row = Assert.Single(Rows());
        Assert.Equal(NotificationDeliveryStatus.Failed, row.Status);
        Assert.Equal("No topic is set for ntfy.", row.LastError);
    }

    [Fact]
    public async Task AnUnexpectedError_FailsTheDelivery_AndTheQueueCarriesOn()
    {
        OnlyNtfy();
        _ntfy.OnSend = (_, _) => throw new InvalidCastException("a bug");
        var dispatcher = CreateDispatcher();

        QueueJobFailure();
        await dispatcher.RunOnceAsync(default);

        Assert.Equal(NotificationDeliveryStatus.Failed, Assert.Single(Rows()).Status);
    }

    [Fact]
    public async Task ATargetsRetryAfter_IsHonoured()
    {
        OnlyNtfy();
        _ntfy.OnSend = (_, _) => throw new NotificationDeliveryException("slow down", isTransient: true, TimeSpan.FromMinutes(20));
        var dispatcher = CreateDispatcher();

        QueueJobFailure();
        await dispatcher.RunOnceAsync(default);

        Assert.Equal(Now + TimeSpan.FromMinutes(20), Assert.Single(Rows()).DueAt);
    }

    [Fact]
    public async Task DeliveriesToOneTarget_AreSpacedOut()
    {
        OnlyNtfy();
        var dispatcher = CreateDispatcher();

        QueueJobFailure(1);
        QueueJobFailure(2);
        QueueJobFailure(3);

        var wait = await dispatcher.RunOnceAsync(default);
        Assert.Single(_ntfy.Sent);
        Assert.InRange(wait, TimeSpan.FromMilliseconds(250), NotificationDispatcher.MinTimeBetweenSends);

        _time.Advance(NotificationDispatcher.MinTimeBetweenSends);
        await dispatcher.RunOnceAsync(default);
        Assert.Equal(2, _ntfy.Sent.Count);
    }

    [Fact]
    public async Task ATargetThatKeepsFailing_IsLeftAloneLonger_AndDoesNotHoldUpOthers()
    {
        _settings.NotifyKomgaEnabled = false;
        _settings.NotifyKavitaEnabled = false;
        var attempts = 0;
        _ntfy.OnSend = (_, _) =>
        {
            attempts++;
            throw new NotificationDeliveryException("down", isTransient: true);
        };
        var dispatcher = CreateDispatcher();

        // Three jobs fail; each is due now for both targets, but ntfy only gets one attempt.
        QueueJobFailure(1);
        QueueJobFailure(2);
        QueueJobFailure(3);
        await dispatcher.RunOnceAsync(default);

        Assert.Equal(1, attempts);
        Assert.Equal(1, _discord.Sent.Count);

        // After the first pause only one more attempt happens, then the pause grows.
        _time.Advance(NotificationDispatcher.RetryDelays[0]);
        await dispatcher.RunOnceAsync(default);
        Assert.Equal(2, attempts);

        _time.Advance(NotificationDispatcher.RetryDelays[0]);
        await dispatcher.RunOnceAsync(default);
        Assert.Equal(2, attempts);

        _time.Advance(NotificationDispatcher.RetryDelays[1]);
        await dispatcher.RunOnceAsync(default);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task ATargetThatRecovers_StopsBeingPaused()
    {
        OnlyNtfy();
        var failing = true;
        _ntfy.OnSend = (_, _) => failing ? throw new NotificationDeliveryException("down", isTransient: true) : Task.CompletedTask;
        var dispatcher = CreateDispatcher();

        QueueJobFailure(1);
        await dispatcher.RunOnceAsync(default);
        _time.Advance(NotificationDispatcher.RetryDelays[0]);
        failing = false;
        await dispatcher.RunOnceAsync(default);
        Assert.Equal(1, _ntfy.Sent.Count);

        // A later failure starts again at the shortest pause.
        failing = true;
        QueueJobFailure(2);
        _time.Advance(NotificationDispatcher.MinTimeBetweenSends);
        await dispatcher.RunOnceAsync(default);

        Assert.Equal(Now + NotificationDispatcher.RetryDelays[0], Rows()[1].DueAt);
    }

    [Fact]
    public async Task TheWait_IsCappedAtAMinute_WhenNothingIsDue()
    {
        var dispatcher = CreateDispatcher();

        Assert.Equal(TimeSpan.FromMinutes(1), await dispatcher.RunOnceAsync(default));
    }

    // Disabled ---------------------------------------------------------------

    [Fact]
    public async Task ADeliveryForATargetSwitchedOffLater_IsCancelled()
    {
        OnlyNtfy();
        var dispatcher = CreateDispatcher();

        _publisher.NewChapter(Series, "1");
        await dispatcher.RunOnceAsync(default);

        _settings.NotifyNtfyEnabled = false;
        _time.Advance(NotificationDispatcher.BatchQuietPeriod);
        await dispatcher.RunOnceAsync(default);

        Assert.Empty(_ntfy.Sent);
        Assert.Equal(NotificationDeliveryStatus.Cancelled, Assert.Single(Rows()).Status);
    }

    [Fact]
    public async Task ADeliveryForAnEventNoLongerChosen_IsCancelled()
    {
        OnlyNtfy();
        var dispatcher = CreateDispatcher();

        _publisher.NewChapter(Series, "1");
        await dispatcher.RunOnceAsync(default);

        _settings.NotifyNtfyEvents = NotificationEventType.JobFailed;
        _time.Advance(NotificationDispatcher.BatchQuietPeriod);
        await dispatcher.RunOnceAsync(default);

        Assert.Equal(NotificationDeliveryStatus.Cancelled, Assert.Single(Rows()).Status);
    }

    [Fact]
    public async Task AQueuedRowWithNoChannel_IsFailedRatherThanStuck()
    {
        OnlyNtfy();
        SeedRow(new NotificationDeliveryEntity { Target = NotificationTarget.Ntfy, EventType = NotificationEventType.JobFailed, Title = "t", DueAt = Now, CreatedAt = Now });
        var dispatcher = CreateDispatcher(Array.Empty<INotificationChannel>());

        await dispatcher.RunOnceAsync(default);

        Assert.Equal(NotificationDeliveryStatus.Failed, Assert.Single(Rows()).Status);
    }

    [Fact]
    public async Task AnEventIsSkippedForATargetWithNoChannel()
    {
        OnlyNtfy();
        var dispatcher = CreateDispatcher(Array.Empty<INotificationChannel>());

        QueueJobFailure();
        await dispatcher.RunOnceAsync(default);

        Assert.Empty(Rows());
    }

    // Durability --------------------------------------------------------------

    [Fact]
    public async Task ARestart_DeliversWhatTheLastRunLeft()
    {
        OnlyNtfy();
        var firstRun = CreateDispatcher();
        _publisher.NewChapter(Series, "1");
        await firstRun.RunOnceAsync(default);

        // A new process, a new queue: the same rows.
        var secondRun = CreateDispatcher();
        _time.Advance(NotificationDispatcher.BatchQuietPeriod);
        await secondRun.RunOnceAsync(default);

        Assert.Single(_ntfy.Sent);
    }

    [Fact]
    public async Task AttemptCount_IsSavedBeforeTheCall_SoACrashStillCounts()
    {
        OnlyNtfy();
        int savedAttempts = -1;
        _ntfy.OnSend = (_, _) =>
        {
            using var db = _db.CreateDbContext();
            savedAttempts = db.NotificationDeliveries.AsNoTracking().Single().AttemptCount;
            return Task.CompletedTask;
        };
        var dispatcher = CreateDispatcher();

        QueueJobFailure();
        await dispatcher.RunOnceAsync(default);

        Assert.Equal(1, savedAttempts);
    }

    [Fact]
    public async Task AnOrderlyShutdownDuringASend_IsNotCountedAgainstTheDelivery()
    {
        OnlyNtfy();
        using var stop = new CancellationTokenSource();
        _ntfy.OnSend = async (_, token) =>
        {
            await stop.CancelAsync();
            token.ThrowIfCancellationRequested();
        };
        var dispatcher = CreateDispatcher();

        QueueJobFailure();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatcher.RunOnceAsync(stop.Token));

        var row = Assert.Single(Rows());
        Assert.Equal(0, row.AttemptCount);
        Assert.Equal(NotificationDeliveryStatus.Pending, row.Status);
    }

    [Fact]
    public async Task AnEventIsKept_WhenTheDatabaseCouldNotTakeIt()
    {
        OnlyNtfy();
        var dispatcher = CreateDispatcher();

        QueueJobFailure();
        _db.FailNextCreations = 1;
        await Assert.ThrowsAsync<InvalidOperationException>(() => dispatcher.RunOnceAsync(default));

        await dispatcher.RunOnceAsync(default);

        Assert.Single(_ntfy.Sent);
    }

    [Fact]
    public async Task ExecuteAsync_DeliversInTheBackground_AndKeepsGoingAfterAnError()
    {
        OnlyNtfy();
        var clock = TimeProvider.System;
        var channel = new RecordingChannel(NotificationTarget.Ntfy);
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.OnSend = (_, _) =>
        {
            sent.TrySetResult();
            return Task.CompletedTask;
        };
        var publisher = new NotificationPublisher(NotificationTestHttp.Options(_settings), NullLogger<NotificationPublisher>.Instance);
        using var dispatcher = new NotificationDispatcher(
            _db,
            publisher,
            new INotificationChannel[] { channel },
            NotificationTestHttp.Options(_settings),
            _activity.Object,
            NullLogger<NotificationDispatcher>.Instance,
            clock);

        await dispatcher.StartAsync(CancellationToken.None);
        publisher.ScheduledJobFailed("job", "Job", "boom");
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await dispatcher.StopAsync(CancellationToken.None);

        using var db = _db.CreateDbContext();
        Assert.Equal(NotificationDeliveryStatus.Sent, db.NotificationDeliveries.AsNoTracking().Single().Status);
    }

    [Fact]
    public async Task ExecuteAsync_WakesFromItsWaitWhenAnEventIsRaised()
    {
        // Waiting on the clock, so the only way past the wait is the raised event.
        OnlyNtfy();
        var clock = new TimerTrackingTimeProvider(new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero));
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var channel = new RecordingChannel(NotificationTarget.Ntfy)
        {
            OnSend = (_, _) =>
            {
                sent.TrySetResult();
                return Task.CompletedTask;
            }
        };
        var publisher = new NotificationPublisher(NotificationTestHttp.Options(_settings), NullLogger<NotificationPublisher>.Instance, clock);
        using var dispatcher = new NotificationDispatcher(
            _db,
            publisher,
            new INotificationChannel[] { channel },
            NotificationTestHttp.Options(_settings),
            _activity.Object,
            NullLogger<NotificationDispatcher>.Instance,
            clock);

        await dispatcher.StartAsync(CancellationToken.None);
        await clock.WaitForTimersAsync(1, TimeSpan.FromSeconds(30));
        publisher.ScheduledJobFailed("job", "Job", "boom");
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await dispatcher.StopAsync(CancellationToken.None);

        Assert.False(dispatcher.ExecuteTask!.IsFaulted);
        Assert.Single(channel.Sent);
    }

    [Fact]
    public async Task ExecuteAsync_ADatabaseOutage_DoesNotEndTheLoop()
    {
        OnlyNtfy();
        var clock = new TimerTrackingTimeProvider(new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero));
        _db.FailNextCreations = 1;
        var publisher = new NotificationPublisher(NotificationTestHttp.Options(_settings), NullLogger<NotificationPublisher>.Instance, clock);
        var channel = new RecordingChannel(NotificationTarget.Ntfy);
        using var dispatcher = new NotificationDispatcher(
            _db,
            publisher,
            new INotificationChannel[] { channel },
            NotificationTestHttp.Options(_settings),
            _activity.Object,
            NullLogger<NotificationDispatcher>.Instance,
            clock);

        publisher.ScheduledJobFailed("job", "Job", "boom");
        await dispatcher.StartAsync(CancellationToken.None);

        // The failed pass waits out its backoff; the next one picks the event up.
        await clock.WaitForTimersAsync(1, TimeSpan.FromSeconds(30));
        clock.Advance(TimeSpan.FromSeconds(30));
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (channel.Sent.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        await dispatcher.StopAsync(CancellationToken.None);

        Assert.Single(channel.Sent);
    }

    [Fact]
    public async Task Stop_ADatabaseFailureDuringTheLastSave_DoesNotFaultTheService()
    {
        OnlyNtfy();
        var clock = new TimerTrackingTimeProvider(new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero));
        _db.FailNextCreations = 1000;
        var publisher = new NotificationPublisher(NotificationTestHttp.Options(_settings), NullLogger<NotificationPublisher>.Instance, clock);
        using var dispatcher = new NotificationDispatcher(
            _db,
            publisher,
            new INotificationChannel[] { new RecordingChannel(NotificationTarget.Ntfy) },
            NotificationTestHttp.Options(_settings),
            _activity.Object,
            NullLogger<NotificationDispatcher>.Instance,
            clock);

        publisher.ScheduledJobFailed("job", "Job", "boom");
        await dispatcher.StartAsync(CancellationToken.None);
        await clock.WaitForTimersAsync(1, TimeSpan.FromSeconds(30));
        await dispatcher.StopAsync(CancellationToken.None);

        Assert.True(dispatcher.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Stop_SavesAnEventRaisedAtTheLastMoment()
    {
        OnlyNtfy();
        var firstSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ntfy.OnSend = (_, _) =>
        {
            firstSent.TrySetResult();
            return Task.CompletedTask;
        };
        using var dispatcher = CreateDispatcher();

        await dispatcher.StartAsync(CancellationToken.None);
        _publisher.ScheduledJobFailed("first", "First", "boom");
        await firstSent.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // The loop is running now; this one has no chance to be delivered before the stop.
        _publisher.ScheduledJobFailed("second", "Second", "boom");
        await dispatcher.StopAsync(CancellationToken.None);

        Assert.Equal(2, Rows().Count);
    }

    // Pruning -----------------------------------------------------------------

    [Fact]
    public async Task Prune_GivesUpOnStaleRows_AndRemovesOldFinishedOnes()
    {
        OnlyNtfy();
        var longAgo = Now - NotificationDispatcher.FinishedRetention - TimeSpan.FromHours(1);
        var expired = Now - NotificationDispatcher.PendingExpiry - TimeSpan.FromHours(1);
        SeedRow(new NotificationDeliveryEntity { Target = NotificationTarget.Ntfy, EventType = "x", Title = "stale", DueAt = expired, CreatedAt = expired });
        SeedRow(new NotificationDeliveryEntity { Target = NotificationTarget.Ntfy, EventType = "x", Title = "old sent", Status = NotificationDeliveryStatus.Sent, DueAt = longAgo, CreatedAt = longAgo });
        var dispatcher = CreateDispatcher();

        await dispatcher.RunOnceAsync(default);

        var rows = Rows();
        var stale = Assert.Single(rows, r => r.Title == "stale");
        Assert.Equal(NotificationDeliveryStatus.Failed, stale.Status);
        Assert.Contains("Gave up", stale.LastError);
        Assert.DoesNotContain(rows, r => r.Title == "old sent");
    }

    [Fact]
    public async Task Prune_KeepsAtMostTheNewestFinishedRowsPerTarget()
    {
        OnlyNtfy();
        var recent = Now - TimeSpan.FromMinutes(5);
        for (var i = 0; i < NotificationDispatcher.MaxFinishedPerTarget + 3; i++)
        {
            SeedRow(new NotificationDeliveryEntity { Target = NotificationTarget.Ntfy, EventType = "x", Title = $"r{i}", Status = NotificationDeliveryStatus.Sent, DueAt = recent, CreatedAt = recent });
        }

        var dispatcher = CreateDispatcher();
        await dispatcher.RunOnceAsync(default);

        Assert.Equal(NotificationDispatcher.MaxFinishedPerTarget, Rows().Count);
        Assert.DoesNotContain(Rows(), r => r.Title == "r0");
        Assert.Contains(Rows(), r => r.Title == $"r{NotificationDispatcher.MaxFinishedPerTarget + 2}");
    }

    [Fact]
    public async Task Prune_RunsOnlyOncePerHour()
    {
        OnlyNtfy();
        var dispatcher = CreateDispatcher();
        await dispatcher.RunOnceAsync(default);

        var longAgo = Now - NotificationDispatcher.FinishedRetention - TimeSpan.FromHours(1);
        SeedRow(new NotificationDeliveryEntity { Target = NotificationTarget.Ntfy, EventType = "x", Title = "old", Status = NotificationDeliveryStatus.Sent, DueAt = longAgo, CreatedAt = longAgo });

        await dispatcher.RunOnceAsync(default);
        Assert.Single(Rows());

        _time.Advance(TimeSpan.FromHours(1));
        await dispatcher.RunOnceAsync(default);
        Assert.Empty(Rows());
    }

    [Fact]
    public async Task ATargetThatStaysDown_CannotGrowTheTableWithoutLimit()
    {
        OnlyNtfy();
        _ntfy.OnSend = (_, _) => throw new NotificationDeliveryException("down", isTransient: true);
        var dispatcher = CreateDispatcher();

        var due = Now.AddHours(1);
        for (var i = 0; i < NotificationDispatcher.MaxPendingPerTarget + 2; i++)
        {
            SeedRow(new NotificationDeliveryEntity { Target = NotificationTarget.Ntfy, EventType = NotificationEventType.JobFailed, GroupKey = $"seed{i}", Title = $"r{i}", DueAt = due, CreatedAt = Now });
        }

        QueueJobFailure(99);
        await dispatcher.RunOnceAsync(default);

        var pending = Rows().Where(r => r.Status == NotificationDeliveryStatus.Pending).ToList();
        Assert.Equal(NotificationDispatcher.MaxPendingPerTarget, pending.Count);
        Assert.DoesNotContain(pending, r => r.Title == "r0");
        Assert.Contains(pending, r => r.GroupKey == "job99");
    }
}
