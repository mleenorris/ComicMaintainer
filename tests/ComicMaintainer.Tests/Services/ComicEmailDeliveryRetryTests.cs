using System.Net.Sockets;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Temporary SMTP failures (4xx replies, dropped connections, timeouts) are
/// retried with back-off up to a cap; permanent ones fail right away.
/// </summary>
public class ComicEmailDeliveryRetryTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly IDbContextFactory<ComicMaintainerDbContext> _dbFactory;
    private readonly Mock<IComicEmailSender> _sender = new();
    private readonly Mock<IComicEmailQueue> _queue = new();
    private readonly Mock<ISeriesMetadataCacheService> _seriesCache = new();
    private readonly Mock<IOptionsMonitor<AppSettings>> _settingsMonitor = new();
    private readonly TimerTrackingTimeProvider _time = new(Start);
    private readonly EreaderDeviceService _devices;
    private readonly string _watchedDir;
    private readonly List<(int DeliveryId, DateTimeOffset DueAt)> _scheduled = new();
    private int _sendAttempts;

    public ComicEmailDeliveryRetryTests()
    {
        _watchedDir = Path.Join(Path.GetTempPath(), $"email-retry-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_watchedDir);

        var dbName = $"TestDb_{Guid.NewGuid()}";
        var services = new ServiceCollection();
        services.AddDbContextFactory<ComicMaintainerDbContext>(opt => opt.UseInMemoryDatabase(dbName));
        _dbFactory = services.BuildServiceProvider().GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>();

        _settingsMonitor.Setup(s => s.CurrentValue).Returns(new AppSettings
        {
            WatchedDirectory = _watchedDir,
            DuplicateDirectory = Path.Join(_watchedDir, "..", "duplicates-none"),
            TempFileDirectory = Path.Join(_watchedDir, "temp"),
            EmailMaxAttachmentMegabytes = 25,
            SmtpHost = "smtp.example.com",
            EmailFromAddress = "library@example.com"
        });

        _sender.SetupGet(s => s.IsConfigured).Returns(true);
        _queue.Setup(q => q.Schedule(It.IsAny<int>(), It.IsAny<DateTimeOffset>()))
            .Callback<int, DateTimeOffset>((id, due) => _scheduled.Add((id, due)));
        _seriesCache.Setup(s => s.NormalizeKey(It.IsAny<string?>()))
            .Returns((string? value) => (value ?? string.Empty).Trim().ToLowerInvariant());

        _devices = new EreaderDeviceService(
            _dbFactory,
            _seriesCache.Object,
            new Mock<ILogger<EreaderDeviceService>>().Object);
    }

    [Fact]
    public async Task ProcessDeliveryAsync_KeepsATemporarilyFailedDeliveryPendingAndSchedulesARetry()
    {
        FailSendsWith(Transient421());
        var service = CreateService(_queue.Object);
        var id = await QueueAsync(service, "Series 001.cbz");

        await service.ProcessDeliveryAsync(id);

        var delivery = await GetDeliveryAsync(id);
        Assert.Equal(EmailDeliveryStatus.Pending, delivery.Status);
        Assert.Equal(1, delivery.AttemptCount);
        var expectedRetry = Start + ComicEmailService.TransientRetryDelays[0];
        Assert.Equal(expectedRetry.UtcDateTime, delivery.NextAttemptAt);
        Assert.Contains($"Attempt 1 of {ComicEmailService.MaxDeliveryAttempts} failed", delivery.ErrorMessage);
        Assert.Contains("421", delivery.ErrorMessage);
        Assert.Equal(new[] { (id, expectedRetry) }, _scheduled);

        // The history shows it is waiting for a retry.
        var dto = Assert.Single((await service.GetDeliveriesPageAsync(0, 10)).Deliveries);
        Assert.Equal(1, dto.AttemptCount);
        Assert.Equal(expectedRetry.UtcDateTime, dto.NextAttemptAt);
    }

    [Fact]
    public async Task ProcessDeliveryAsync_DoesNotSendARetryBeforeItIsDue()
    {
        FailSendsWith(Transient421());
        var service = CreateService(_queue.Object);
        var id = await QueueAsync(service, "Series 001.cbz");
        await service.ProcessDeliveryAsync(id);
        _scheduled.Clear();

        // Queued again early (say by the pending sweep): it waits for its time.
        _time.Advance(ComicEmailService.TransientRetryDelays[0] - TimeSpan.FromSeconds(1));
        await service.ProcessDeliveryAsync(id);

        Assert.Equal(1, _sendAttempts);
        var due = Start + ComicEmailService.TransientRetryDelays[0];
        Assert.Equal(new[] { (id, due) }, _scheduled);
        Assert.Equal(1, (await GetDeliveryAsync(id)).AttemptCount);
    }

    [Fact]
    public async Task ProcessDeliveryAsync_BacksOffBetweenAttemptsAndGivesUpAtTheCap()
    {
        FailSendsWith(new TransientEmailSendException(
            "Could not reach the mail server: Connection refused",
            new SocketException((int)SocketError.ConnectionRefused)));
        var service = CreateService(_queue.Object);
        var id = await QueueAsync(service, "Series 001.cbz");

        var delays = new List<TimeSpan>();
        for (var attempt = 1; attempt <= ComicEmailService.MaxDeliveryAttempts; attempt++)
        {
            var before = _time.GetUtcNow();
            await service.ProcessDeliveryAsync(id);
            var delivery = await GetDeliveryAsync(id);
            Assert.Equal(attempt, delivery.AttemptCount);

            if (attempt < ComicEmailService.MaxDeliveryAttempts)
            {
                Assert.Equal(EmailDeliveryStatus.Pending, delivery.Status);
                var due = new DateTimeOffset(DateTime.SpecifyKind(delivery.NextAttemptAt!.Value, DateTimeKind.Utc));
                delays.Add(due - before);
                _time.Advance(due - before);
            }
            else
            {
                Assert.Equal(EmailDeliveryStatus.Failed, delivery.Status);
                Assert.Null(delivery.NextAttemptAt);
                Assert.StartsWith($"Failed after {ComicEmailService.MaxDeliveryAttempts} attempts:", delivery.ErrorMessage);
            }
        }

        Assert.Equal(ComicEmailService.TransientRetryDelays, delays);
        Assert.True(delays.SequenceEqual(delays.Order()), "Each retry waits at least as long as the one before.");
        Assert.Equal(ComicEmailService.MaxDeliveryAttempts, _sendAttempts);
        Assert.Equal(ComicEmailService.MaxDeliveryAttempts - 1, _scheduled.Count);
    }

    [Fact]
    public async Task ProcessDeliveryAsync_FailsAPermanentRejectionWithoutRetrying()
    {
        FailSendsWith(new InvalidOperationException("550 5.1.1 The email account does not exist."));
        var service = CreateService(_queue.Object);
        var id = await QueueAsync(service, "Series 001.cbz");

        await service.ProcessDeliveryAsync(id);

        var delivery = await GetDeliveryAsync(id);
        Assert.Equal(EmailDeliveryStatus.Failed, delivery.Status);
        Assert.Equal(1, delivery.AttemptCount);
        Assert.Null(delivery.NextAttemptAt);
        Assert.Equal("550 5.1.1 The email account does not exist.", delivery.ErrorMessage);
        Assert.Empty(_scheduled);
    }

    [Fact]
    public async Task ARetryThatSucceeds_IsRecordedAsSentAndClearsTheError()
    {
        var failures = 1;
        _sender.Setup(s => s.SendAsync(It.IsAny<ComicEmailMessage>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                Interlocked.Increment(ref _sendAttempts);
                return failures-- > 0 ? Task.FromException(Transient421()) : Task.CompletedTask;
            });
        var service = CreateService(_queue.Object);
        var id = await QueueAsync(service, "Series 001.cbz");

        await service.ProcessDeliveryAsync(id);
        _time.Advance(ComicEmailService.TransientRetryDelays[0]);
        await service.ProcessDeliveryAsync(id);

        var delivery = await GetDeliveryAsync(id);
        Assert.Equal(EmailDeliveryStatus.Sent, delivery.Status);
        Assert.Equal(2, delivery.AttemptCount);
        Assert.Null(delivery.NextAttemptAt);
        Assert.Null(delivery.ErrorMessage);
    }

    [Fact]
    public async Task ADeliveryWaitingForARetry_StillCountsAsQueuedForTheAlreadyDeliveredCheck()
    {
        FailSendsWith(Transient421());
        var service = CreateService(_queue.Object);
        var device = await _devices.CreateDeviceAsync("Kindle", "kindle@kindle.com", EmailDeliveryFormat.Original);
        var file = CreateComic("Series 001.cbz");
        var first = await service.QueueFilesAsync(new[] { file }, device.Id, null, EmailDeliverySource.Auto, true);
        await service.ProcessDeliveryAsync(Assert.Single(first.Queued).Id);

        var second = await service.QueueFilesAsync(new[] { file }, device.Id, null, EmailDeliverySource.Auto, true);

        Assert.Empty(second.Queued);
        Assert.Equal("Already delivered to this device", second.Skipped[file]);
    }

    [Fact]
    public async Task ResumePendingDeliveriesAsync_KeepsTheRetryTimeOfADeliveryLeftWaiting()
    {
        FailSendsWith(Transient421());
        var previousRun = CreateService(_queue.Object);
        var waiting = await QueueAsync(previousRun, "Series 001.cbz");
        await previousRun.ProcessDeliveryAsync(waiting);
        var ready = await QueueAsync(previousRun, "Series 002.cbz");
        _scheduled.Clear();

        var restarted = new Mock<IComicEmailQueue>();
        var scheduled = new List<(int, DateTimeOffset)>();
        restarted.Setup(q => q.Schedule(It.IsAny<int>(), It.IsAny<DateTimeOffset>()))
            .Callback<int, DateTimeOffset>((id, due) => scheduled.Add((id, due)));
        var service = CreateService(restarted.Object);

        Assert.Equal(2, await service.ResumePendingDeliveriesAsync());

        Assert.Equal(new[] { (waiting, Start + ComicEmailService.TransientRetryDelays[0]) }, scheduled);
        restarted.Verify(q => q.Enqueue(ready), Times.Once);
        restarted.Verify(q => q.Enqueue(waiting), Times.Never);
    }

    [Fact]
    public async Task WithTheRealQueue_ATemporarilyFailedDeliveryIsSentOnceTheBackOffHasPassed()
    {
        var failures = 1;
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _sender.Setup(s => s.SendAsync(It.IsAny<ComicEmailMessage>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                Interlocked.Increment(ref _sendAttempts);
                if (Interlocked.Decrement(ref failures) >= 0)
                {
                    return Task.FromException(Transient421());
                }

                sent.TrySetResult();
                return Task.CompletedTask;
            });

        ComicEmailService? service = null;
        var queue = new ComicEmailQueue(() => service!, new Mock<ILogger<ComicEmailQueue>>().Object, timeProvider: _time);
        service = CreateService(queue);
        var id = await QueueAsync(service, "Series 001.cbz");

        await queue.StartAsync();
        try
        {
            // The sweep's delay is the first timer; the retry is the second.
            await _time.WaitForTimersAsync(2);
            Assert.Equal(1, Volatile.Read(ref _sendAttempts));
            Assert.True(queue.IsQueued(id));

            _time.Advance(ComicEmailService.TransientRetryDelays[0]);
            await sent.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await queue.StopAsync();
        }

        var delivery = await GetDeliveryAsync(id);
        Assert.Equal(EmailDeliveryStatus.Sent, delivery.Status);
        Assert.Equal(2, delivery.AttemptCount);
        Assert.False(queue.IsQueued(id));
    }

    private static TransientEmailSendException Transient421() => new(
        "The mail server temporarily refused the message (421): 4.7.0 Try again later",
        new IOException("421"));

    private void FailSendsWith(Exception exception)
    {
        _sender.Setup(s => s.SendAsync(It.IsAny<ComicEmailMessage>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                Interlocked.Increment(ref _sendAttempts);
                return Task.FromException(exception);
            });
    }

    private ComicEmailService CreateService(IComicEmailQueue queue) => new(
        _dbFactory,
        _sender.Object,
        new Mock<IEpubConversionService>().Object,
        new Mock<IAzw3ConversionService>().Object,
        queue,
        _seriesCache.Object,
        new Mock<ISeriesImageStore>().Object,
        _settingsMonitor.Object,
        new Mock<ILogger<ComicEmailService>>().Object,
        timeProvider: _time);

    private async Task<int> QueueAsync(ComicEmailService service, string fileName)
    {
        var devices = await _devices.GetDevicesAsync();
        var deviceId = devices.Count > 0
            ? devices[0].Id
            : (await _devices.CreateDeviceAsync("Kindle", "kindle@kindle.com", EmailDeliveryFormat.Original)).Id;
        var result = await service.QueueFilesAsync(
            new[] { CreateComic(fileName) }, deviceId, null, EmailDeliverySource.Manual, skipAlreadyDelivered: false);
        return Assert.Single(result.Queued).Id;
    }

    private async Task<ComicEmailDeliveryEntity> GetDeliveryAsync(int id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.ComicEmailDeliveries.AsNoTracking().FirstAsync(d => d.Id == id);
    }

    private string CreateComic(string fileName)
    {
        var path = Path.Join(_watchedDir, fileName);
        File.WriteAllBytes(path, new byte[16]);
        return path;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_watchedDir))
            {
                Directory.Delete(_watchedDir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }

        GC.SuppressFinalize(this);
    }
}
