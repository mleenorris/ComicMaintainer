using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Cancelling deliveries, paging the delivery history and the email rate limit.
/// </summary>
public class ComicEmailServiceCancellationAndRateLimitTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2024, 6, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Real time allowed for background work to reach the expected state.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly IDbContextFactory<ComicMaintainerDbContext> _dbFactory;
    private readonly Mock<IComicEmailSender> _sender = new();
    private readonly Mock<IEpubConversionService> _epub = new();
    private readonly Mock<ISeriesMetadataCacheService> _seriesCache = new();
    private readonly FakeTimeProvider _time = new(Start);
    private readonly CancellationTokenSource _applicationStopping = new();
    private readonly ComicEmailService _service;
    private readonly EreaderDeviceService _devices;
    private readonly Dictionary<string, int> _deviceIds = new(StringComparer.Ordinal);
    private readonly List<DateTimeOffset> _sendTimes = new();
    private readonly string _watchedDir;
    private readonly AppSettings _settings;

    public ComicEmailServiceCancellationAndRateLimitTests()
    {
        _watchedDir = Path.Combine(Path.GetTempPath(), $"email-cancel-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_watchedDir);

        var dbName = $"TestDb_{Guid.NewGuid()}";
        var services = new ServiceCollection();
        services.AddDbContextFactory<ComicMaintainerDbContext>(opt => opt.UseInMemoryDatabase(dbName));
        _dbFactory = services.BuildServiceProvider().GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>();

        _settings = new AppSettings
        {
            WatchedDirectory = _watchedDir,
            DuplicateDirectory = Path.Combine(_watchedDir, "..", "duplicates-none"),
            TempFileDirectory = Path.Combine(_watchedDir, "temp"),
            EmailMaxAttachmentMegabytes = 25,
            SmtpHost = "smtp.example.com",
            EmailFromAddress = "library@example.com"
        };

        var settingsMonitor = new Mock<IOptionsMonitor<AppSettings>>();
        settingsMonitor.Setup(s => s.CurrentValue).Returns(_settings);

        _sender.SetupGet(s => s.IsConfigured).Returns(true);
        _sender.Setup(s => s.SendAsync(It.IsAny<ComicEmailMessage>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                RecordSend();
                return Task.CompletedTask;
            });

        _seriesCache.Setup(s => s.NormalizeKey(It.IsAny<string?>()))
            .Returns((string? value) => string.IsNullOrWhiteSpace(value)
                ? "unknown-series"
                : new string(value.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-'));

        var lifetime = new Mock<IHostApplicationLifetime>();
        lifetime.SetupGet(l => l.ApplicationStopping).Returns(_applicationStopping.Token);

        _service = new ComicEmailService(
            _dbFactory,
            _sender.Object,
            _epub.Object,
            new Mock<IAzw3ConversionService>().Object,
            new Mock<IComicEmailQueue>().Object,
            _seriesCache.Object,
            new Mock<ISeriesImageStore>().Object,
            settingsMonitor.Object,
            new Mock<ILogger<ComicEmailService>>().Object,
            metrics: null,
            timeProvider: _time,
            applicationLifetime: lifetime.Object);

        _devices = new EreaderDeviceService(
            _dbFactory,
            _seriesCache.Object,
            new Mock<ILogger<EreaderDeviceService>>().Object);
    }

    [Fact]
    public async Task CancelDeliveryAsync_CancelsAQueuedDeliveryBeforeItIsProcessed()
    {
        var delivery = await QueueAsync("Series - Chapter 0001.cbz");

        var result = await _service.CancelDeliveryAsync(delivery.Id);

        Assert.Equal(EmailDeliveryCancelOutcome.Cancelled, result.Outcome);
        Assert.Equal(EmailDeliveryStatus.Cancelled, result.Delivery?.Status);

        // The queue still holds the id; processing it must not send anything.
        await _service.ProcessDeliveryAsync(delivery.Id);

        Assert.Equal(EmailDeliveryStatus.Cancelled, (await GetDeliveryAsync(delivery.Id)).Status);
        Assert.Equal(0, SendCount);
    }

    [Fact]
    public async Task CancelDeliveryAsync_ReportsAnUnknownDelivery()
    {
        var result = await _service.CancelDeliveryAsync(12345);

        Assert.Equal(EmailDeliveryCancelOutcome.NotFound, result.Outcome);
        Assert.Null(result.Delivery);
    }

    [Fact]
    public async Task CancelDeliveryAsync_LeavesAFinishedDeliveryAlone()
    {
        var sent = await QueueAsync("Series - Chapter 0001.cbz");
        await _service.ProcessDeliveryAsync(sent.Id);
        var cancelled = await QueueAsync("Series - Chapter 0002.cbz");
        await _service.CancelDeliveryAsync(cancelled.Id);

        var sentResult = await _service.CancelDeliveryAsync(sent.Id);
        var cancelledResult = await _service.CancelDeliveryAsync(cancelled.Id);

        Assert.Equal(EmailDeliveryCancelOutcome.AlreadyFinished, sentResult.Outcome);
        Assert.Equal(EmailDeliveryStatus.Sent, sentResult.Delivery?.Status);
        Assert.Equal(EmailDeliveryCancelOutcome.AlreadyFinished, cancelledResult.Outcome);
        Assert.Equal(EmailDeliveryStatus.Cancelled, cancelledResult.Delivery?.Status);
        Assert.Equal(EmailDeliveryStatus.Sent, (await GetDeliveryAsync(sent.Id)).Status);
    }

    [Fact]
    public async Task CancelDeliveryAsync_StopsADeliveryThatIsBeingConverted()
    {
        var conversion = BlockConversion(honourCancellation: true);
        var delivery = await QueueAsync("Series - Chapter 0001.cbz", EmailDeliveryFormat.Epub);

        var processing = _service.ProcessDeliveryAsync(delivery.Id);
        await conversion.Started.Task.WaitAsync(Patience);

        var result = await _service.CancelDeliveryAsync(delivery.Id).WaitAsync(Patience);
        await processing.WaitAsync(Patience);

        Assert.Equal(EmailDeliveryCancelOutcome.Cancelled, result.Outcome);
        Assert.Equal(EmailDeliveryStatus.Cancelled, result.Delivery?.Status);
        Assert.Equal(EmailDeliveryStatus.Cancelled, (await GetDeliveryAsync(delivery.Id)).Status);
        Assert.Equal(0, SendCount);
    }

    [Fact]
    public async Task CancelDeliveryAsync_CannotStopAMessageThatIsBeingSent()
    {
        var sending = BlockSending();
        var delivery = await QueueAsync("Series - Chapter 0001.cbz");

        var processing = _service.ProcessDeliveryAsync(delivery.Id);
        await sending.Started.Task.WaitAsync(Patience);

        var result = await _service.CancelDeliveryAsync(delivery.Id).WaitAsync(Patience);

        Assert.Equal(EmailDeliveryCancelOutcome.Sending, result.Outcome);
        Assert.Equal(EmailDeliveryStatus.Pending, result.Delivery?.Status);

        sending.Release.TrySetResult();
        await processing.WaitAsync(Patience);

        Assert.Equal(EmailDeliveryStatus.Sent, (await GetDeliveryAsync(delivery.Id)).Status);
    }

    [Fact]
    public async Task CancelDeliveryAsync_ReportsADeliveryThatIsSlowToStop()
    {
        var conversion = BlockConversion(honourCancellation: false);
        var delivery = await QueueAsync("Series - Chapter 0001.cbz", EmailDeliveryFormat.Epub);

        var processing = _service.ProcessDeliveryAsync(delivery.Id);
        await conversion.Started.Task.WaitAsync(Patience);

        var cancelling = _service.CancelDeliveryAsync(delivery.Id);
        await AdvanceUntilAsync(() => cancelling.IsCompleted, TimeSpan.FromSeconds(1));
        var result = await cancelling;

        Assert.Equal(EmailDeliveryCancelOutcome.Stopping, result.Outcome);
        Assert.Equal(EmailDeliveryStatus.Pending, result.Delivery?.Status);

        // The conversion finishing after all must not let the message go out.
        conversion.Release.TrySetResult();
        await processing.WaitAsync(Patience);

        Assert.Equal(EmailDeliveryStatus.Cancelled, (await GetDeliveryAsync(delivery.Id)).Status);
        Assert.Equal(0, SendCount);
    }

    [Fact]
    public async Task CancelPendingDeliveriesAsync_CancelsQueuedAndInProgressDeliveries()
    {
        var conversion = BlockConversion(honourCancellation: true);
        var deliveries = await QueueManyAsync(3, EmailDeliveryFormat.Epub);

        var processing = _service.ProcessDeliveryAsync(deliveries[0].Id);
        await conversion.Started.Task.WaitAsync(Patience);

        var result = await _service.CancelPendingDeliveriesAsync().WaitAsync(Patience);
        await processing.WaitAsync(Patience);

        Assert.Equal(new EmailDeliveryBulkCancelResult(Cancelled: 3, Stopping: 0, Sending: 0), result);
        foreach (var delivery in deliveries)
        {
            Assert.Equal(EmailDeliveryStatus.Cancelled, (await GetDeliveryAsync(delivery.Id)).Status);
        }

        Assert.Equal(0, SendCount);
    }

    [Fact]
    public async Task CancelPendingDeliveriesAsync_LeavesFinishedDeliveriesAndTheMessageBeingSent()
    {
        var finished = await QueueAsync("Series - Chapter 0001.cbz");
        await _service.ProcessDeliveryAsync(finished.Id);

        var sending = BlockSending();
        var inProgress = await QueueAsync("Series - Chapter 0002.cbz");
        var queued = await QueueAsync("Series - Chapter 0003.cbz");

        var processing = _service.ProcessDeliveryAsync(inProgress.Id);
        await sending.Started.Task.WaitAsync(Patience);

        var result = await _service.CancelPendingDeliveriesAsync().WaitAsync(Patience);

        Assert.Equal(new EmailDeliveryBulkCancelResult(Cancelled: 1, Stopping: 0, Sending: 1), result);

        sending.Release.TrySetResult();
        await processing.WaitAsync(Patience);

        Assert.Equal(EmailDeliveryStatus.Sent, (await GetDeliveryAsync(finished.Id)).Status);
        Assert.Equal(EmailDeliveryStatus.Sent, (await GetDeliveryAsync(inProgress.Id)).Status);
        Assert.Equal(EmailDeliveryStatus.Cancelled, (await GetDeliveryAsync(queued.Id)).Status);
    }

    [Fact]
    public async Task CancelPendingDeliveriesAsync_ReportsWhenNothingIsPending()
    {
        var result = await _service.CancelPendingDeliveriesAsync();

        Assert.Equal(new EmailDeliveryBulkCancelResult(Cancelled: 0, Stopping: 0, Sending: 0), result);
    }

    [Fact]
    public async Task CancelledDelivery_DoesNotCountAsDelivered()
    {
        var file = CreateComic("Series - Chapter 0001.cbz");
        var deviceId = await GetDeviceIdAsync(EmailDeliveryFormat.Original);
        var first = await _service.QueueFilesAsync(
            new[] { file }, deviceId, null, EmailDeliverySource.Manual, skipAlreadyDelivered: false);
        await _service.CancelDeliveryAsync(Assert.Single(first.Queued).Id);

        var again = await _service.QueueFilesAsync(
            new[] { file },
            deviceId,
            null,
            EmailDeliverySource.Manual,
            skipAlreadyDelivered: true);

        Assert.Single(again.Queued);
        Assert.Empty(again.Skipped);
    }

    [Fact]
    public async Task ProcessDeliveryAsync_LeavesTheDeliveryPendingWhenStoppedForShutdown()
    {
        var conversion = BlockConversion(honourCancellation: true);
        var delivery = await QueueAsync("Series - Chapter 0001.cbz", EmailDeliveryFormat.Epub);
        using var shutdown = new CancellationTokenSource();

        var processing = _service.ProcessDeliveryAsync(delivery.Id, shutdown.Token);
        await conversion.Started.Task.WaitAsync(Patience);
        shutdown.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing.WaitAsync(Patience));
        Assert.Equal(EmailDeliveryStatus.Pending, (await GetDeliveryAsync(delivery.Id)).Status);

        // No longer in progress, so it can be cancelled outright.
        var result = await _service.CancelDeliveryAsync(delivery.Id);
        Assert.Equal(EmailDeliveryCancelOutcome.Cancelled, result.Outcome);
    }

    [Fact]
    public async Task GetDeliveriesPageAsync_ReturnsTheRequestedPageNewestFirst()
    {
        var deliveries = await QueueManyAsync(5);
        await _service.CancelDeliveryAsync(deliveries[0].Id);

        var page = await _service.GetDeliveriesPageAsync(offset: 1, limit: 2);

        Assert.Equal(new[] { deliveries[3].Id, deliveries[2].Id }, page.Deliveries.Select(d => d.Id));
        Assert.Equal(5, page.Total);
        Assert.Equal(1, page.Offset);
        Assert.Equal(2, page.Limit);
        Assert.Equal(4, page.PendingCount);
        Assert.Null(page.RateLimitedUntil);
    }

    [Theory]
    [InlineData(-5, 2, 0, 2)]
    [InlineData(0, 0, 0, 1)]
    [InlineData(0, 10_000, 0, 500)]
    public async Task GetDeliveriesPageAsync_ClampsTheRequestedRange(
        int offset,
        int limit,
        int expectedOffset,
        int expectedLimit)
    {
        await QueueManyAsync(3);

        var page = await _service.GetDeliveriesPageAsync(offset, limit);

        Assert.Equal(expectedOffset, page.Offset);
        Assert.Equal(expectedLimit, page.Limit);
        Assert.Equal(Math.Min(3, expectedLimit), page.Deliveries.Count);
        Assert.Equal(3, page.Total);
    }

    [Fact]
    public async Task GetDeliveriesPageAsync_ReturnsAnEmptyPagePastTheEnd()
    {
        await QueueManyAsync(2);

        var page = await _service.GetDeliveriesPageAsync(offset: 50, limit: 50);

        Assert.Empty(page.Deliveries);
        Assert.Equal(2, page.Total);
        Assert.Equal(2, page.PendingCount);
    }

    [Theory]
    // Two an hour: the older of the last two sends leaves the hour in 10 minutes.
    [InlineData(2, 0, 0, new[] { -120, -50, -10 }, 10)]
    // One a day: the send 23 hours ago leaves the day in an hour.
    [InlineData(0, 1, 0, new[] { -23 * 60 }, 60)]
    // Five minutes between emails, the last one two minutes ago.
    [InlineData(0, 0, 300, new[] { -2 }, 3)]
    // The strictest limit wins.
    [InlineData(2, 10, 1800, new[] { -50, -10 }, 20)]
    public async Task ProcessDeliveryAsync_HoldsTheDeliveryUntilTheRateLimitAllowsIt(
        int maxPerHour,
        int maxPerDay,
        int minSecondsBetween,
        int[] sentMinutesAgo,
        int expectedWaitMinutes)
    {
        _settings.EmailMaxSendsPerHour = maxPerHour;
        _settings.EmailMaxSendsPerDay = maxPerDay;
        _settings.EmailMinSecondsBetweenSends = minSecondsBetween;
        await SeedDeliveriesAsync(EmailDeliveryStatus.Sent, sentMinutesAgo.Select(m => Start.AddMinutes(m)).ToArray());
        var delivery = await QueueAsync("Series - Chapter 0001.cbz");
        var expectedSlot = Start.AddMinutes(expectedWaitMinutes);

        var processing = _service.ProcessDeliveryAsync(delivery.Id);
        await WaitUntilAsync(() => _service.RateLimitedUntil is not null);

        Assert.Equal(expectedSlot.UtcDateTime, _service.RateLimitedUntil);
        var page = await _service.GetDeliveriesPageAsync(0, 50);
        Assert.Equal(expectedSlot.UtcDateTime, page.RateLimitedUntil);
        Assert.Equal(1, page.PendingCount);
        Assert.Equal(0, SendCount);

        await AdvanceUntilAsync(() => processing.IsCompleted, TimeSpan.FromMinutes(1));
        await processing;

        Assert.True(SendTimes.Single() >= expectedSlot, "The delivery was sent before the rate limit allowed it");
        Assert.Equal(EmailDeliveryStatus.Sent, (await GetDeliveryAsync(delivery.Id)).Status);
        Assert.Null(_service.RateLimitedUntil);
    }

    [Fact]
    public async Task ProcessDeliveryAsync_SpacesOutConsecutiveDeliveries()
    {
        _settings.EmailMinSecondsBetweenSends = 90;
        var first = await QueueAsync("Series - Chapter 0001.cbz");
        var second = await QueueAsync("Series - Chapter 0002.cbz");

        await _service.ProcessDeliveryAsync(first.Id).WaitAsync(Patience);
        var processing = _service.ProcessDeliveryAsync(second.Id);
        await WaitUntilAsync(() => _service.RateLimitedUntil is not null);

        Assert.Equal(Start.AddSeconds(90).UtcDateTime, _service.RateLimitedUntil);

        await AdvanceUntilAsync(() => processing.IsCompleted, TimeSpan.FromSeconds(10));
        await processing;

        var sendTimes = SendTimes;
        Assert.Equal(2, sendTimes.Count);
        Assert.True(sendTimes[1] - sendTimes[0] >= TimeSpan.FromSeconds(90), "The second email went out too soon");
    }

    [Fact]
    public async Task ProcessDeliveryAsync_CountsOnlySentMessagesTowardsTheRateLimit()
    {
        _settings.EmailMaxSendsPerHour = 1;
        _settings.EmailMinSecondsBetweenSends = 600;
        await SeedDeliveriesAsync(EmailDeliveryStatus.Failed, Start.AddMinutes(-1));
        await SeedDeliveriesAsync(EmailDeliveryStatus.Cancelled, Start.AddMinutes(-1));
        var delivery = await QueueAsync("Series - Chapter 0001.cbz");

        await _service.ProcessDeliveryAsync(delivery.Id).WaitAsync(Patience);

        Assert.Equal(EmailDeliveryStatus.Sent, (await GetDeliveryAsync(delivery.Id)).Status);
        Assert.Equal(Start, Assert.Single(SendTimes));
    }

    [Fact]
    public async Task ProcessDeliveryAsync_PicksUpALimitLiftedWhileWaiting()
    {
        _settings.EmailMinSecondsBetweenSends = 3600;
        await SeedDeliveriesAsync(EmailDeliveryStatus.Sent, Start.AddMinutes(-1));
        var delivery = await QueueAsync("Series - Chapter 0001.cbz");

        var processing = _service.ProcessDeliveryAsync(delivery.Id);
        await WaitUntilAsync(() => _service.RateLimitedUntil is not null);
        _settings.EmailMinSecondsBetweenSends = 0;

        await AdvanceUntilAsync(() => processing.IsCompleted, TimeSpan.FromSeconds(30));
        await processing;

        Assert.True(Assert.Single(SendTimes) < Start.AddMinutes(59), "The delivery waited out a limit that was lifted");
    }

    [Fact]
    public async Task CancelDeliveryAsync_CancelsADeliveryWaitingForTheRateLimit()
    {
        _settings.EmailMinSecondsBetweenSends = 3600;
        await SeedDeliveriesAsync(EmailDeliveryStatus.Sent, Start.AddMinutes(-1));
        var delivery = await QueueAsync("Series - Chapter 0001.cbz", EmailDeliveryFormat.Epub);

        var processing = _service.ProcessDeliveryAsync(delivery.Id);
        await WaitUntilAsync(() => _service.RateLimitedUntil is not null);

        var result = await _service.CancelDeliveryAsync(delivery.Id).WaitAsync(Patience);
        await processing.WaitAsync(Patience);

        Assert.Equal(EmailDeliveryCancelOutcome.Cancelled, result.Outcome);
        Assert.Equal(EmailDeliveryStatus.Cancelled, (await GetDeliveryAsync(delivery.Id)).Status);
        Assert.Null(_service.RateLimitedUntil);
        Assert.Equal(0, SendCount);
        _epub.Verify(
            e => e.ConvertToEpubAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<EpubConversionOptions?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ProcessDeliveryAsync_LeavesRateLimitedDeliveriesPendingWhenTheApplicationStops()
    {
        _settings.EmailMinSecondsBetweenSends = 3600;
        await SeedDeliveriesAsync(EmailDeliveryStatus.Sent, Start.AddMinutes(-1));
        var waiting = await QueueAsync("Series - Chapter 0001.cbz");
        var next = await QueueAsync("Series - Chapter 0002.cbz");

        var processing = _service.ProcessDeliveryAsync(waiting.Id);
        await WaitUntilAsync(() => _service.RateLimitedUntil is not null);

        await _applicationStopping.CancelAsync();
        await processing.WaitAsync(Patience);

        // The queue drains what is left on shutdown; nothing may wait for the limit.
        await _service.ProcessDeliveryAsync(next.Id).WaitAsync(Patience);

        Assert.Equal(EmailDeliveryStatus.Pending, (await GetDeliveryAsync(waiting.Id)).Status);
        Assert.Equal(EmailDeliveryStatus.Pending, (await GetDeliveryAsync(next.Id)).Status);
        Assert.Null(_service.RateLimitedUntil);
        Assert.Equal(0, SendCount);
    }

    [Fact]
    public async Task ProcessDeliveryAsync_StillSendsWhileTheApplicationStopsWhenNotRateLimited()
    {
        await _applicationStopping.CancelAsync();
        var delivery = await QueueAsync("Series - Chapter 0001.cbz");

        await _service.ProcessDeliveryAsync(delivery.Id).WaitAsync(Patience);

        Assert.Equal(EmailDeliveryStatus.Sent, (await GetDeliveryAsync(delivery.Id)).Status);
    }

    /// <summary>A background step a test holds open: it signals when it starts and waits to be released.</summary>
    private sealed record BlockedStep(TaskCompletionSource Started, TaskCompletionSource Release);

    private static BlockedStep NewBlockedStep() => new(
        new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

    /// <summary>
    /// Makes EPUB conversion signal when it starts and then wait: for the
    /// delivery to be cancelled when <paramref name="honourCancellation"/> is
    /// set, otherwise until released, after which it produces a book.
    /// </summary>
    private BlockedStep BlockConversion(bool honourCancellation)
    {
        var step = NewBlockedStep();
        _epub.Setup(e => e.ConvertToEpubAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<EpubConversionOptions?>(),
                It.IsAny<CancellationToken>()))
            .Returns<string, string, EpubConversionOptions?, CancellationToken>(async (source, outDir, _, token) =>
            {
                step.Started.TrySetResult();
                if (honourCancellation)
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                else
                {
                    await step.Release.Task;
                }

                Directory.CreateDirectory(outDir);
                var book = Path.Combine(outDir, Path.GetFileNameWithoutExtension(source) + ".epub");
                await File.WriteAllTextAsync(book, "epub-bytes", CancellationToken.None);
                return book;
            });

        return step;
    }

    /// <summary>Makes the mail server hold every message until released.</summary>
    private BlockedStep BlockSending()
    {
        var step = NewBlockedStep();
        _sender.Setup(s => s.SendAsync(It.IsAny<ComicEmailMessage>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                step.Started.TrySetResult();
                await step.Release.Task;
                RecordSend();
            });

        return step;
    }

    private void RecordSend()
    {
        lock (_sendTimes)
        {
            _sendTimes.Add(_time.GetUtcNow());
        }
    }

    private List<DateTimeOffset> SendTimes
    {
        get
        {
            lock (_sendTimes)
            {
                return _sendTimes.ToList();
            }
        }
    }

    private int SendCount => SendTimes.Count;

    /// <summary>
    /// Moves the clock forward in steps until <paramref name="condition"/>
    /// holds, giving the service a moment to react after each step.
    /// </summary>
    private async Task AdvanceUntilAsync(Func<bool> condition, TimeSpan step)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out advancing the clock");
            _time.Advance(step);
            await Task.Delay(10);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the service");
            await Task.Delay(10);
        }
    }

    private async Task<int> GetDeviceIdAsync(string format)
    {
        if (!_deviceIds.TryGetValue(format, out var id))
        {
            id = (await _devices.CreateDeviceAsync($"Reader {format}", $"{format}@kindle.com", format)).Id;
            _deviceIds[format] = id;
        }

        return id;
    }

    private async Task<ComicEmailDeliveryDto> QueueAsync(string fileName, string format = EmailDeliveryFormat.Original)
    {
        var result = await _service.QueueFilesAsync(
            new[] { CreateComic(fileName) },
            await GetDeviceIdAsync(format),
            null,
            EmailDeliverySource.Manual,
            skipAlreadyDelivered: false);
        return Assert.Single(result.Queued);
    }

    /// <summary>Queues <paramref name="count"/> issues, oldest delivery first.</summary>
    private async Task<List<ComicEmailDeliveryDto>> QueueManyAsync(int count, string format = EmailDeliveryFormat.Original)
    {
        var files = Enumerable.Range(1, count)
            .Select(i => CreateComic($"Series - Chapter {i:D4}.cbz"))
            .ToList();
        var result = await _service.QueueFilesAsync(
            files,
            await GetDeviceIdAsync(format),
            null,
            EmailDeliverySource.Manual,
            skipAlreadyDelivered: false);
        Assert.Equal(count, result.Queued.Count);
        return result.Queued.OrderBy(d => d.Id).ToList();
    }

    /// <summary>Adds earlier deliveries with the given status, one per timestamp.</summary>
    private async Task SeedDeliveriesAsync(string status, params DateTimeOffset[] sentAt)
    {
        var deviceId = await GetDeviceIdAsync(EmailDeliveryFormat.Original);
        await using var db = await _dbFactory.CreateDbContextAsync();
        foreach (var time in sentAt)
        {
            db.ComicEmailDeliveries.Add(new ComicEmailDeliveryEntity
            {
                FilePath = Path.Combine(_watchedDir, $"Earlier {Guid.NewGuid():N}.cbz"),
                DeviceId = deviceId,
                DeviceName = "Reader original",
                DeviceEmail = "original@kindle.com",
                Status = status,
                CreatedAt = time.UtcDateTime,
                SentAt = time.UtcDateTime,
            });
        }

        await db.SaveChangesAsync();
    }

    private string CreateComic(string fileName)
    {
        var path = Path.Combine(_watchedDir, fileName);
        File.WriteAllBytes(path, new byte[16]);
        return path;
    }

    private async Task<ComicEmailDeliveryEntity> GetDeliveryAsync(int id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.ComicEmailDeliveries.AsNoTracking().FirstAsync(d => d.Id == id);
    }

    public void Dispose()
    {
        _applicationStopping.Dispose();
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
