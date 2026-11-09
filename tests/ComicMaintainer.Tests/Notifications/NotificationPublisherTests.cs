using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Notifications;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using ComicMaintainer.WebApi.Authorization;
using ComicMaintainer.WebApi.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace ComicMaintainer.Tests.Notifications;

public sealed class NotificationPublisherTests
{
    private static NotificationPublisher Publisher(AppSettings settings, ILogger<NotificationPublisher>? logger = null, TimeProvider? time = null) =>
        new(NotificationTestHttp.Options(settings), logger ?? NullLogger<NotificationPublisher>.Instance, time);

    private static List<NotificationEvent> Queued(NotificationPublisher publisher)
    {
        var events = new List<NotificationEvent>();
        while (publisher.Events.TryRead(out var item))
        {
            events.Add(item);
        }

        return events;
    }

    [Fact]
    public void AnEventNoTargetWants_IsNotQueued()
    {
        var publisher = Publisher(new AppSettings());

        publisher.NewChapter("Series", "1");
        publisher.LibraryChanged();
        publisher.ScheduledJobFailed("job", "Job", "boom");

        Assert.Empty(Queued(publisher));
    }

    [Fact]
    public void AnEventIsQueued_ForATargetThatWantsIt()
    {
        var publisher = Publisher(new AppSettings { NotifyGotifyEnabled = true });

        publisher.NewChapter("Berserk", "12", "/lib/x.cbz");

        var item = Assert.Single(Queued(publisher));
        Assert.Equal(NotificationEventType.NewChapters, item.EventType);
        Assert.Equal("Berserk", item.Title);
        Assert.Equal("12", item.Body);
        Assert.Equal("/lib/x.cbz", item.FilePath);
    }

    [Fact]
    public void ChaptersOfOneSeries_ShareAKey_WhateverTheirSpelling()
    {
        var publisher = Publisher(new AppSettings { NotifyGotifyEnabled = true });

        publisher.NewChapter("The Berserk!", "1");
        publisher.NewChapter("the berserk", "2");

        var items = Queued(publisher);
        Assert.Equal(items[0].GroupKey, items[1].GroupKey);
    }

    [Fact]
    public void ASeriesWithNoLettersOrDigits_StillGetsAKey()
    {
        var publisher = Publisher(new AppSettings { NotifyGotifyEnabled = true });

        publisher.NewChapter("!!!", "1");

        Assert.False(string.IsNullOrEmpty(Assert.Single(Queued(publisher)).GroupKey));
    }

    [Fact]
    public void JobFailureText_IsRedactedBeforeItLeavesTheMachine()
    {
        var publisher = Publisher(new AppSettings { NotifyGotifyEnabled = true });

        publisher.ScheduledJobFailed("job", "Job", "password=hunter2 could not reach C:\\Users\\Hannah\\Comics");

        var body = Assert.Single(Queued(publisher)).Body!;
        Assert.DoesNotContain("hunter2", body);
        Assert.DoesNotContain("Hannah", body);
    }

    [Fact]
    public void SuwayomiFailureText_IsRedacted()
    {
        var publisher = Publisher(new AppSettings { NotifyGotifyEnabled = true });

        publisher.SuwayomiImportFailed("could not read /home/hannah/downloads: password=abc");

        var body = Assert.Single(Queued(publisher)).Body!;
        Assert.DoesNotContain("abc", body);
        Assert.DoesNotContain("hannah", body);
    }

    [Fact]
    public void FindingsText_IsKeptAsIs()
    {
        var publisher = Publisher(new AppSettings { NotifyGotifyEnabled = true });

        publisher.ScheduledJobFindings("job", "Job", "3 series missing issues: Berserk (2)");

        Assert.Equal("3 series missing issues: Berserk (2)", Assert.Single(Queued(publisher)).Body);
    }

    [Fact]
    public void AFullQueue_DropsNewEvents_AndWarnsOncePerMinute()
    {
        var logger = new Mock<ILogger<NotificationPublisher>>();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero));
        var publisher = Publisher(new AppSettings { NotifyGotifyEnabled = true }, logger.Object, time);

        for (var i = 0; i < NotificationPublisher.Capacity + 25; i++)
        {
            publisher.ScheduledJobFindings("job", "Job", "x");
        }

        Assert.Equal(1, logger.Invocations.Count(i => i.Method.Name == nameof(ILogger.Log)));

        time.Advance(TimeSpan.FromMinutes(1));
        publisher.ScheduledJobFindings("job", "Job", "x");

        Assert.Equal(2, logger.Invocations.Count(i => i.Method.Name == nameof(ILogger.Log)));
        Assert.Equal(NotificationPublisher.Capacity, Queued(publisher).Count);
    }

    [Fact]
    public void ABugInPublishing_NeverReachesTheCaller()
    {
        var settings = new Mock<IOptionsMonitor<AppSettings>>();
        settings.Setup(s => s.CurrentValue).Throws(new InvalidOperationException("boom"));
        var publisher = new NotificationPublisher(settings.Object, NullLogger<NotificationPublisher>.Instance);

        publisher.NewChapter("Series", "1");
        publisher.LibraryChanged();
        publisher.SuwayomiImportFailed("x");
        publisher.ScheduledJobFailed("j", "J", "x");
        publisher.ScheduledJobFindings("j", "J", "x");
    }
}


public sealed class LibraryActivityMonitorTests : IDisposable
{
    private readonly SqliteTestDbFactory _db = new();
    private readonly Mock<IFileOperationLock> _fileLock = new();
    private readonly Mock<ILibraryMutationLock> _mutationLock = new();
    private readonly Mock<IBatchJobRegistry> _jobs = new();
    private readonly Mock<IFileWatcherService> _watcher = new();

    public void Dispose() => _db.Dispose();

    private LibraryActivityMonitor Monitor() =>
        new(_fileLock.Object, _mutationLock.Object, _jobs.Object, _watcher.Object, _db);

    private void AddPendingFile()
    {
        using var db = _db.CreateDbContext();
        db.ComicFiles.Add(new ComicFileEntity { FilePath = "/l/a.cbz", FileName = "a.cbz", Directory = "/l", IsProcessingPending = true });
        db.SaveChanges();
    }

    [Fact]
    public async Task AnIdleLibrary_IsNotBusy()
    {
        _watcher.Setup(w => w.IsRunning).Returns(true);

        Assert.False(await Monitor().IsBusyAsync(default));
    }

    [Fact]
    public async Task AHeldFileLock_IsBusy()
    {
        _fileLock.Setup(l => l.HasActiveOperations).Returns(true);

        Assert.True(await Monitor().IsBusyAsync(default));
    }

    [Fact]
    public async Task AHeldLibraryLock_IsBusy()
    {
        _mutationLock.Setup(l => l.IsHeld).Returns(true);

        Assert.True(await Monitor().IsBusyAsync(default));
    }

    [Fact]
    public async Task AnActiveBatchJob_IsBusy()
    {
        _jobs.Setup(j => j.GetActiveJob()).Returns(new ComicMaintainer.Core.Models.ProcessingJobSummary());

        Assert.True(await Monitor().IsBusyAsync(default));
    }

    [Fact]
    public async Task FilesTheWatcherHasQueued_IsBusy()
    {
        _watcher.Setup(w => w.IsRunning).Returns(true);
        AddPendingFile();

        Assert.True(await Monitor().IsBusyAsync(default));
    }

    [Fact]
    public async Task PendingFlagsOfAStoppedWatcher_AreNotBusy()
    {
        _watcher.Setup(w => w.IsRunning).Returns(false);
        AddPendingFile();

        Assert.False(await Monitor().IsBusyAsync(default));
    }

    [Fact]
    public async Task RealLocks_ReportActivity()
    {
        var fileLock = new FileOperationLock();
        Assert.False(fileLock.HasActiveOperations);
        using (await fileLock.AcquireAsync(Path.Join(Path.GetTempPath(), "a.cbz")))
        {
            Assert.True(fileLock.HasActiveOperations);
        }

        Assert.False(fileLock.HasActiveOperations);

        using var mutationLock = new LibraryMutationLock();
        Assert.False(mutationLock.IsHeld);
        mutationLock.Execute(() => Assert.True(mutationLock.IsHeld));
        Assert.False(mutationLock.IsHeld);
    }
}

public sealed class NotificationsControllerTests
{
    private readonly Mock<INotificationTester> _tester = new();

    private NotificationsController Controller() => new(_tester.Object, NullLogger<NotificationsController>.Instance);

    private static string Error(ActionResult result) => (string)((ProblemDetails)((ObjectResult)result).Value!).Extensions["error"]!;

    [Fact]
    public void TheTestEndpoint_IsForAdministratorsOnly()
    {
        var attribute = (AuthorizeAttribute)Attribute.GetCustomAttribute(typeof(NotificationsController), typeof(AuthorizeAttribute))!;

        Assert.Equal(AuthorizationPolicies.CanAdminister, attribute.Policy);
    }

    [Fact]
    public async Task ASentTest_Says200()
    {
        _tester.Setup(t => t.SendTestAsync(NotificationTarget.Ntfy, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var result = await Controller().SendTest(NotificationTarget.Ntfy, default);

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task AnUnknownTarget_Is404()
    {
        var result = await Controller().SendTest("email", default);

        Assert.Equal(StatusCodes.Status404NotFound, ((ObjectResult)result).StatusCode);
    }

    [Fact]
    public async Task IncompleteSettings_Are400_WithTheReason()
    {
        _tester.Setup(t => t.SendTestAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NotificationNotConfiguredException("No URL is set for ntfy."));

        var result = await Controller().SendTest(NotificationTarget.Ntfy, default);

        Assert.Equal(StatusCodes.Status400BadRequest, ((ObjectResult)result).StatusCode);
        Assert.Equal("No URL is set for ntfy.", Error(result));
    }

    [Fact]
    public async Task ARescanRefusedBecauseTheLibraryIsBusy_Is409()
    {
        _tester.Setup(t => t.SendTestAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NotificationLibraryBusyException("Busy right now."));

        var result = await Controller().SendTest(NotificationTarget.Komga, default);

        Assert.Equal(StatusCodes.Status409Conflict, ((ObjectResult)result).StatusCode);
        Assert.Equal("Busy right now.", Error(result));
    }

    [Fact]
    public async Task ATargetThatRefused_Is502_WithTheTargetsError()
    {
        _tester.Setup(t => t.SendTestAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NotificationDeliveryException("Gotify answered with HTTP 401: unauthorized", isTransient: false));

        var result = await Controller().SendTest(NotificationTarget.Gotify, default);

        Assert.Equal(StatusCodes.Status502BadGateway, ((ObjectResult)result).StatusCode);
        Assert.Equal("Gotify answered with HTTP 401: unauthorized", Error(result));
    }

    [Fact]
    public async Task AnAbortedRequest_IsNotReportedAsAFailure()
    {
        _tester.Setup(t => t.SendTestAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new OperationCanceledException());

        await Assert.ThrowsAsync<OperationCanceledException>(() => Controller().SendTest(NotificationTarget.Ntfy, default));
    }
}
