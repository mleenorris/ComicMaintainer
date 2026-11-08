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
/// Deliveries that outlive the process that queued them: pending rows left by
/// a restart are resumed, and they never block the already-delivered check
/// for good.
/// </summary>
public class ComicEmailDeliveryRecoveryTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly IDbContextFactory<ComicMaintainerDbContext> _dbFactory;
    private readonly Mock<IComicEmailSender> _sender = new();
    private readonly Mock<ISeriesMetadataCacheService> _seriesCache = new();
    private readonly Mock<IOptionsMonitor<AppSettings>> _settingsMonitor = new();
    private readonly EreaderDeviceService _devices;
    private readonly string _watchedDir;
    private readonly List<string> _sentSubjects = new();

    public ComicEmailDeliveryRecoveryTests()
    {
        _watchedDir = Path.Join(Path.GetTempPath(), $"email-recovery-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_watchedDir);

        var dbName = $"TestDb_{Guid.NewGuid()}";
        var services = new ServiceCollection();
        services.AddDbContextFactory<ComicMaintainerDbContext>(opt => opt.UseInMemoryDatabase(dbName));
        _dbFactory = services.BuildServiceProvider().GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>();

        var settings = new AppSettings
        {
            WatchedDirectory = _watchedDir,
            DuplicateDirectory = Path.Join(_watchedDir, "..", "duplicates-none"),
            TempFileDirectory = Path.Join(_watchedDir, "temp"),
            EmailMaxAttachmentMegabytes = 25,
            SmtpHost = "smtp.example.com",
            EmailFromAddress = "library@example.com"
        };
        _settingsMonitor.Setup(s => s.CurrentValue).Returns(settings);

        _sender.SetupGet(s => s.IsConfigured).Returns(true);
        _sender.Setup(s => s.SendAsync(It.IsAny<ComicEmailMessage>(), It.IsAny<CancellationToken>()))
            .Returns<ComicEmailMessage, CancellationToken>((message, _) =>
            {
                lock (_sentSubjects)
                {
                    _sentSubjects.Add(message.Subject);
                }

                return Task.CompletedTask;
            });

        _seriesCache.Setup(s => s.NormalizeKey(It.IsAny<string?>()))
            .Returns((string? value) => string.IsNullOrWhiteSpace(value)
                ? "unknown-series"
                : new string(value.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-'));

        _devices = new EreaderDeviceService(
            _dbFactory,
            _seriesCache.Object,
            new Mock<ILogger<EreaderDeviceService>>().Object);
    }

    [Fact]
    public async Task ResumePendingDeliveriesAsync_QueuesOnlyPendingDeliveriesTheQueueDoesNotHold_OldestFirst()
    {
        var device = await _devices.CreateDeviceAsync(TestEmailScopes.Everyone, "Kindle", "kindle@kindle.com", EmailDeliveryFormat.Original);
        var sent = await AddDeliveryAsync(device.Id, "Series 001.cbz", EmailDeliveryStatus.Sent);
        var first = await AddDeliveryAsync(device.Id, "Series 002.cbz", EmailDeliveryStatus.Pending);
        var failed = await AddDeliveryAsync(device.Id, "Series 003.cbz", EmailDeliveryStatus.Failed);
        var alreadyQueued = await AddDeliveryAsync(device.Id, "Series 004.cbz", EmailDeliveryStatus.Pending);
        var cancelled = await AddDeliveryAsync(device.Id, "Series 005.cbz", EmailDeliveryStatus.Cancelled);
        var second = await AddDeliveryAsync(device.Id, "Series 006.cbz", EmailDeliveryStatus.Pending);

        var queue = new Mock<IComicEmailQueue>();
        var enqueued = new List<int>();
        queue.Setup(q => q.IsQueued(alreadyQueued)).Returns(true);
        queue.Setup(q => q.Enqueue(It.IsAny<int>())).Callback<int>(enqueued.Add);
        var service = CreateService(queue.Object);

        var resumed = await service.ResumePendingDeliveriesAsync();

        Assert.Equal(2, resumed);
        Assert.Equal(new[] { first, second }, enqueued);
        queue.Verify(q => q.Enqueue(sent), Times.Never);
        queue.Verify(q => q.Enqueue(failed), Times.Never);
        queue.Verify(q => q.Enqueue(cancelled), Times.Never);
    }

    [Fact]
    public async Task QueueStart_SendsTheDeliveriesAnEarlierProcessLeftPending()
    {
        // An earlier process queued an automatic delivery and stopped before
        // its queue got to it: the row is pending and nothing holds its id.
        var device = await _devices.CreateDeviceAsync(TestEmailScopes.Everyone, "Kindle", "kindle@kindle.com", EmailDeliveryFormat.Original);
        await _devices.UpsertSubscriptionAsync(TestEmailScopes.Everyone, "My Series", device.Id, EmailDeliveryFormat.Device, enabled: true);
        var file = CreateComic("My Series", "My Series - Chapter 0005.cbz");
        var previousRun = CreateService(new Mock<IComicEmailQueue>().Object);
        Assert.Equal(1, await previousRun.QueueAutoSendAsync(file, "My Series"));

        // The restarted process starts its queue, which resumes the delivery.
        var (service, queue) = CreateServiceWithRealQueue();
        await queue.StartAsync();
        try
        {
            await WaitUntilAsync(async () => (await GetDeliveriesAsync()).Single().Status == EmailDeliveryStatus.Sent);
        }
        finally
        {
            await queue.StopAsync();
        }

        Assert.Equal(new[] { "My Series - Chapter 0005" }, _sentSubjects);

        // The file now counts as delivered because it was, not because a
        // stranded pending row claims so; processing it again sends nothing new.
        Assert.Equal(0, await service.QueueAutoSendAsync(file, "My Series"));
        Assert.Single(_sentSubjects);
    }

    [Fact]
    public async Task QueueStart_DoesNotSendADeliveryTwiceWhenItIsAlsoQueuedByARequest()
    {
        var device = await _devices.CreateDeviceAsync(TestEmailScopes.Everyone, "Kindle", "kindle@kindle.com", EmailDeliveryFormat.Original);
        var (service, queue) = CreateServiceWithRealQueue();
        var file = CreateComic("Series", "Series 001.cbz");
        var result = await service.QueueFilesAsync(
            TestEmailScopes.Everyone,
            new[] { file }, device.Id, null, EmailDeliverySource.Manual, skipAlreadyDelivered: true);
        var id = Assert.Single(result.Queued).Id;

        // Queued by the request and found again by the sweep when the queue starts.
        Assert.True(queue.IsQueued(id));
        Assert.Equal(0, await service.ResumePendingDeliveriesAsync());

        await queue.StartAsync();
        try
        {
            await WaitUntilAsync(async () => (await GetDeliveriesAsync()).Single().Status == EmailDeliveryStatus.Sent);
            await service.ResumePendingDeliveriesAsync();
        }
        finally
        {
            await queue.StopAsync();
        }

        Assert.Single(_sentSubjects);
        Assert.False(queue.IsQueued(id));
    }

    [Fact]
    public async Task QueueStart_CancelsADeliveryWhoseDeviceWasDeletedInsteadOfSendingIt()
    {
        var device = await _devices.CreateDeviceAsync(TestEmailScopes.Everyone, "Kindle", "kindle@kindle.com", EmailDeliveryFormat.Original);
        await AddDeliveryAsync(device.Id, "Series 001.cbz", EmailDeliveryStatus.Pending);
        await _devices.DeleteDeviceAsync(TestEmailScopes.Everyone, device.Id);

        var (_, queue) = CreateServiceWithRealQueue();
        await queue.StartAsync();
        try
        {
            await WaitUntilAsync(async () => (await GetDeliveriesAsync()).Single().Status == EmailDeliveryStatus.Cancelled);
        }
        finally
        {
            await queue.StopAsync();
        }

        Assert.Empty(_sentSubjects);
    }

    [Fact]
    public async Task ResumePendingDeliveriesAsync_FailsADeliveryThatKeepsBeingInterruptedInsteadOfQueueingIt()
    {
        var device = await _devices.CreateDeviceAsync(TestEmailScopes.Everyone, "Kindle", "kindle@kindle.com", EmailDeliveryFormat.Original);
        var stuck = await AddDeliveryAsync(device.Id, "Series 001.cbz", EmailDeliveryStatus.Pending, ComicEmailService.MaxStartsWithoutOutcome);
        var oneTryLeft = await AddDeliveryAsync(device.Id, "Series 002.cbz", EmailDeliveryStatus.Pending, ComicEmailService.MaxStartsWithoutOutcome - 1);

        var queue = new Mock<IComicEmailQueue>();
        var enqueued = new List<int>();
        queue.Setup(q => q.Enqueue(It.IsAny<int>())).Callback<int>(enqueued.Add);

        Assert.Equal(1, await CreateService(queue.Object).ResumePendingDeliveriesAsync());

        Assert.Equal(new[] { oneTryLeft }, enqueued);
        var deliveries = await GetDeliveriesAsync();
        var failed = deliveries.Single(d => d.Id == stuck);
        Assert.Equal(EmailDeliveryStatus.Failed, failed.Status);
        Assert.Contains("interrupted before finishing", failed.ErrorMessage);
        Assert.Equal(EmailDeliveryStatus.Pending, deliveries.Single(d => d.Id == oneTryLeft).Status);
    }

    [Fact]
    public async Task ADeliveryThatTakesTheProcessDownEveryTime_IsGivenUpOnInsteadOfReplayedForever()
    {
        var device = await _devices.CreateDeviceAsync(TestEmailScopes.Everyone, "Kindle", "kindle@kindle.com", EmailDeliveryFormat.Original);
        await AddDeliveryAsync(device.Id, "Series 001.cbz", EmailDeliveryStatus.Pending);

        // A process that dies mid-work records no result: the send never
        // returns, and the "restarted" run knows nothing of the previous one.
        using var reachedSend = new SemaphoreSlim(0);
        var neverFinishes = new TaskCompletionSource();
        _sender.Setup(s => s.SendAsync(It.IsAny<ComicEmailMessage>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                reachedSend.Release();
                return neverFinishes.Task;
            });

        for (var start = 1; start <= ComicEmailService.MaxStartsWithoutOutcome; start++)
        {
            var (_, killedRun) = CreateServiceWithRealQueue();
            await killedRun.StartAsync();
            Assert.True(await reachedSend.WaitAsync(Patience));

            var row = (await GetDeliveriesAsync()).Single();
            Assert.Equal(EmailDeliveryStatus.Pending, row.Status);
            Assert.Equal(start, row.StartsWithoutOutcome);
        }

        var (_, queue) = CreateServiceWithRealQueue();
        await queue.StartAsync();
        try
        {
            await WaitUntilAsync(async () => (await GetDeliveriesAsync()).Single().Status == EmailDeliveryStatus.Failed);
        }
        finally
        {
            await queue.StopAsync();
        }

        _sender.Verify(
            s => s.SendAsync(It.IsAny<ComicEmailMessage>(), It.IsAny<CancellationToken>()),
            Times.Exactly(ComicEmailService.MaxStartsWithoutOutcome));
    }

    [Fact]
    public async Task ProcessDeliveryAsync_RecordsTheStartBeforeWorkBeginsAndClearsItWithTheResult()
    {
        var device = await _devices.CreateDeviceAsync(TestEmailScopes.Everyone, "Kindle", "kindle@kindle.com", EmailDeliveryFormat.Original);
        var id = await AddDeliveryAsync(device.Id, "Series 001.cbz", EmailDeliveryStatus.Pending);

        var startsSeenWhileSending = -1;
        _sender.Setup(s => s.SendAsync(It.IsAny<ComicEmailMessage>(), It.IsAny<CancellationToken>()))
            .Returns(async () => startsSeenWhileSending = (await GetDeliveriesAsync()).Single().StartsWithoutOutcome);

        await CreateService(new Mock<IComicEmailQueue>().Object).ProcessDeliveryAsync(id);

        Assert.Equal(1, startsSeenWhileSending);
        var row = (await GetDeliveriesAsync()).Single();
        Assert.Equal(EmailDeliveryStatus.Sent, row.Status);
        Assert.Equal(0, row.StartsWithoutOutcome);
    }

    [Fact]
    public async Task ADeliveryWaitingForARetry_HasNoInterruptedStartAgainstIt()
    {
        var device = await _devices.CreateDeviceAsync(TestEmailScopes.Everyone, "Kindle", "kindle@kindle.com", EmailDeliveryFormat.Original);
        var id = await AddDeliveryAsync(device.Id, "Series 001.cbz", EmailDeliveryStatus.Pending);
        _sender.Setup(s => s.SendAsync(It.IsAny<ComicEmailMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TransientEmailSendException("busy", new TimeoutException()));

        await CreateService(new Mock<IComicEmailQueue>().Object).ProcessDeliveryAsync(id);

        var row = (await GetDeliveriesAsync()).Single();
        Assert.Equal(EmailDeliveryStatus.Pending, row.Status);
        Assert.NotNull(row.NextAttemptAt);
        Assert.Equal(0, row.StartsWithoutOutcome);
    }

    [Fact]
    public async Task AnOrderlyStopDuringADelivery_IsNotHeldAgainstIt()
    {
        var device = await _devices.CreateDeviceAsync(TestEmailScopes.Everyone, "Kindle", "kindle@kindle.com", EmailDeliveryFormat.Original);
        await AddDeliveryAsync(device.Id, "Series 001.cbz", EmailDeliveryStatus.Pending, startsWithoutOutcome: 1);

        var sending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _sender.Setup(s => s.SendAsync(It.IsAny<ComicEmailMessage>(), It.IsAny<CancellationToken>()))
            .Returns<ComicEmailMessage, CancellationToken>(async (_, token) =>
            {
                sending.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            });

        var (_, queue) = CreateServiceWithRealQueue();
        await queue.StartAsync();
        await sending.Task.WaitAsync(Patience);
        Assert.Equal(2, (await GetDeliveriesAsync()).Single().StartsWithoutOutcome);

        using var deadlinePassed = new CancellationTokenSource();
        await deadlinePassed.CancelAsync();
        await queue.StopAsync(deadlinePassed.Token);

        var row = (await GetDeliveriesAsync()).Single();
        Assert.Equal(EmailDeliveryStatus.Pending, row.Status);
        Assert.Equal(1, row.StartsWithoutOutcome);
    }

    [Fact]
    public async Task ProcessDeliveryAsync_RunningOutOfMemory_FailsThatDeliveryAndReturnsNormally()
    {
        var device = await _devices.CreateDeviceAsync(TestEmailScopes.Everyone, "Kindle", "kindle@kindle.com", EmailDeliveryFormat.Original);
        var id = await AddDeliveryAsync(device.Id, "Series 001.cbz", EmailDeliveryStatus.Pending);
        _sender.Setup(s => s.SendAsync(It.IsAny<ComicEmailMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OutOfMemoryException());

        await CreateService(new Mock<IComicEmailQueue>().Object).ProcessDeliveryAsync(id);

        var row = (await GetDeliveriesAsync()).Single();
        Assert.Equal(EmailDeliveryStatus.Failed, row.Status);
        Assert.Contains("ran out of memory", row.ErrorMessage);
        Assert.Equal(0, row.StartsWithoutOutcome);
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
        new Mock<ILogger<ComicEmailService>>().Object);

    private (ComicEmailService Service, ComicEmailQueue Queue) CreateServiceWithRealQueue()
    {
        ComicEmailService? service = null;
        var queue = new ComicEmailQueue(() => service!, new Mock<ILogger<ComicEmailQueue>>().Object);
        service = CreateService(queue);
        return (service, queue);
    }

    private async Task<int> AddDeliveryAsync(int deviceId, string fileName, string status, int startsWithoutOutcome = 0)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var delivery = new ComicEmailDeliveryEntity
        {
            FilePath = CreateComic("Series", fileName),
            DeviceId = deviceId,
            DeviceName = "Kindle",
            DeviceEmail = "kindle@kindle.com",
            DeliveryFormat = EmailDeliveryFormat.Original,
            Status = status,
            Source = EmailDeliverySource.Auto,
            CreatedAt = DateTime.UtcNow,
            StartsWithoutOutcome = startsWithoutOutcome
        };
        db.ComicEmailDeliveries.Add(delivery);
        await db.SaveChangesAsync();
        return delivery.Id;
    }

    private async Task<List<ComicEmailDeliveryEntity>> GetDeliveriesAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.ComicEmailDeliveries.AsNoTracking().OrderBy(d => d.Id).ToListAsync();
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The condition was not met in time.");
            }

            await Task.Delay(20);
        }
    }

    private string CreateComic(string folder, string fileName)
    {
        var directory = Path.Join(_watchedDir, folder);
        Directory.CreateDirectory(directory);
        var path = Path.Join(directory, fileName);
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
