using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Services;

public class ComicEmailServiceTests : IDisposable
{
    private readonly IDbContextFactory<ComicMaintainerDbContext> _dbFactory;
    private readonly Mock<IComicEmailSender> _sender = new();
    private readonly Mock<IEpubConversionService> _epub = new();
    private readonly Mock<IComicEmailQueue> _queue = new();
    private readonly ComicEmailService _service;
    private readonly EreaderDeviceService _devices;
    private readonly string _watchedDir;
    private readonly AppSettings _settings;

    public ComicEmailServiceTests()
    {
        _watchedDir = Path.Combine(Path.GetTempPath(), $"email-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_watchedDir);

        var dbName = $"TestDb_{Guid.NewGuid()}";
        var services = new ServiceCollection();
        services.AddDbContext<ComicMaintainerDbContext>(opt => opt.UseInMemoryDatabase(dbName));
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

        var seriesCache = new Mock<ISeriesMetadataCacheService>();
        seriesCache.Setup(s => s.NormalizeKey(It.IsAny<string?>()))
            .Returns((string? value) => string.IsNullOrWhiteSpace(value)
                ? "unknown-series"
                : new string(value.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-'));

        _service = new ComicEmailService(
            _dbFactory,
            _sender.Object,
            _epub.Object,
            _queue.Object,
            seriesCache.Object,
            new Mock<ISeriesImageStore>().Object,
            settingsMonitor.Object,
            new Mock<ILogger<ComicEmailService>>().Object);

        _devices = new EreaderDeviceService(
            _dbFactory,
            seriesCache.Object,
            new Mock<ILogger<EreaderDeviceService>>().Object);
    }

    [Fact]
    public async Task QueueFilesAsync_CreatesPendingDeliveriesAndEnqueuesThem()
    {
        var device = await _devices.CreateDeviceAsync("Kindle", "kindle@kindle.com", EmailDeliveryFormat.Epub);
        var file = CreateComic("Series - Chapter 0001.cbz");

        var result = await _service.QueueFilesAsync(
            new[] { file }, device.Id, null, EmailDeliverySource.Manual, skipAlreadyDelivered: false);

        var queued = Assert.Single(result.Queued);
        Assert.Equal(EmailDeliveryFormat.Epub, queued.DeliveryFormat);
        Assert.Equal(EmailDeliveryStatus.Pending, queued.Status);
        Assert.Empty(result.Skipped);
        _queue.Verify(q => q.Enqueue(queued.Id), Times.Once);
    }

    [Fact]
    public async Task QueueFilesAsync_HonoursExplicitFormatOverride()
    {
        var device = await _devices.CreateDeviceAsync("Kindle", "kindle@kindle.com", EmailDeliveryFormat.Original);
        var file = CreateComic("Series - Chapter 0001.cbz");

        var result = await _service.QueueFilesAsync(
            new[] { file }, device.Id, EmailDeliveryFormat.Epub, EmailDeliverySource.Manual, false);

        Assert.Equal(EmailDeliveryFormat.Epub, Assert.Single(result.Queued).DeliveryFormat);
    }

    [Fact]
    public async Task QueueFilesAsync_SkipsFilesOutsideTheLibrary()
    {
        var device = await _devices.CreateDeviceAsync("Kindle", "kindle@kindle.com", null);
        var outside = Path.Combine(Path.GetTempPath(), $"outside-{Guid.NewGuid():N}.cbz");
        await File.WriteAllTextAsync(outside, "x");

        try
        {
            var result = await _service.QueueFilesAsync(
                new[] { outside }, device.Id, null, EmailDeliverySource.Manual, false);

            Assert.Empty(result.Queued);
            Assert.Equal("File is outside the comic library", result.Skipped[outside]);
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task QueueFilesAsync_SkipsSymlinksPointingOutsideTheLibrary()
    {
        var device = await _devices.CreateDeviceAsync("Kindle", "kindle@kindle.com", null);
        var outside = Path.Combine(Path.GetTempPath(), $"outside-{Guid.NewGuid():N}.cbz");
        await File.WriteAllTextAsync(outside, "x");
        var link = Path.Combine(_watchedDir, "link.cbz");

        try
        {
            File.CreateSymbolicLink(link, outside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // The platform/filesystem does not allow creating links here.
            File.Delete(outside);
            return;
        }

        try
        {
            var result = await _service.QueueFilesAsync(
                new[] { link }, device.Id, null, EmailDeliverySource.Manual, false);

            Assert.Empty(result.Queued);
            Assert.Equal("File is outside the comic library", result.Skipped[link]);
        }
        finally
        {
            File.Delete(link);
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task QueueAutoSendAsync_DoesNotStampLastSentUntilTheDeliverySucceeds()
    {
        var device = await _devices.CreateDeviceAsync("Kindle", "kindle@kindle.com", EmailDeliveryFormat.Original);
        var subscription = await _devices.UpsertSubscriptionAsync(
            "My Series", device.Id, EmailDeliveryFormat.Device, enabled: true);
        var file = CreateComic("My Series - Chapter 0005.cbz");

        Assert.Equal(1, await _service.QueueAutoSendAsync(file, "My Series"));
        Assert.Null(await GetSubscriptionLastSentAsync(subscription!.Id));

        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var pending = Assert.Single(db.ComicEmailDeliveries.ToList());
            Assert.Equal(subscription.Id, pending.SubscriptionId);
        }

        var delivery = Assert.Single(await _service.GetRecentDeliveriesAsync(10));

        _sender.Setup(s => s.SendAsync(It.IsAny<ComicEmailMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("smtp exploded"));
        await _service.ProcessDeliveryAsync(delivery.Id);
        Assert.Null(await GetSubscriptionLastSentAsync(subscription.Id));

        // Requeue and let the send succeed this time.
        _sender.Setup(s => s.SendAsync(It.IsAny<ComicEmailMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        Assert.Equal(1, await _service.QueueAutoSendAsync(file, "My Series"));
        var retry = (await _service.GetRecentDeliveriesAsync(10))[0];
        await _service.ProcessDeliveryAsync(retry.Id);

        Assert.NotNull(await GetSubscriptionLastSentAsync(subscription.Id));
    }

    [Fact]
    public async Task QueueAutoSendAsync_DoesNotStampSubscriptionsForOtherDevices()
    {
        var kindle = await _devices.CreateDeviceAsync("Kindle", "kindle@kindle.com", EmailDeliveryFormat.Original);
        var kobo = await _devices.CreateDeviceAsync("Kobo", "kobo@kobo.com", EmailDeliveryFormat.Original);
        var kindleSub = await _devices.UpsertSubscriptionAsync("My Series", kindle.Id, EmailDeliveryFormat.Device, true);
        var koboSub = await _devices.UpsertSubscriptionAsync("My Series", kobo.Id, EmailDeliveryFormat.Device, true);
        var file = CreateComic("My Series - Chapter 0006.cbz");

        await _service.QueueAutoSendAsync(file, "My Series");

        // Only the Kindle delivery is processed.
        var kindleDelivery = (await _service.GetRecentDeliveriesAsync(10))
            .First(d => d.DeviceId == kindle.Id);
        _sender.Setup(s => s.SendAsync(It.IsAny<ComicEmailMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        await _service.ProcessDeliveryAsync(kindleDelivery.Id);

        Assert.NotNull(await GetSubscriptionLastSentAsync(kindleSub!.Id));
        Assert.Null(await GetSubscriptionLastSentAsync(koboSub!.Id));
    }

    [Fact]
    public async Task QueueFilesAsync_SkipsMissingAndUnsupportedFiles()
    {
        var device = await _devices.CreateDeviceAsync("Kindle", "kindle@kindle.com", null);
        var missing = Path.Combine(_watchedDir, "gone.cbz");
        var notAComic = Path.Combine(_watchedDir, "notes.txt");
        await File.WriteAllTextAsync(notAComic, "x");

        var result = await _service.QueueFilesAsync(
            new[] { missing, notAComic }, device.Id, null, EmailDeliverySource.Manual, false);

        Assert.Empty(result.Queued);
        Assert.Equal("File not found", result.Skipped[missing]);
        Assert.Equal("Not a supported comic archive", result.Skipped[notAComic]);
    }

    [Fact]
    public async Task QueueFilesAsync_WhenSkipAlreadyDelivered_DoesNotDuplicateSends()
    {
        var device = await _devices.CreateDeviceAsync("Kindle", "kindle@kindle.com", null);
        var file = CreateComic("Series - Chapter 0001.cbz");

        var first = await _service.QueueFilesAsync(new[] { file }, device.Id, null, EmailDeliverySource.Auto, true);
        var second = await _service.QueueFilesAsync(new[] { file }, device.Id, null, EmailDeliverySource.Auto, true);

        Assert.Single(first.Queued);
        Assert.Empty(second.Queued);
        Assert.Equal("Already delivered to this device", second.Skipped[file]);
    }

    [Fact]
    public async Task QueueFilesAsync_WhenNotConfigured_Throws()
    {
        _sender.SetupGet(s => s.IsConfigured).Returns(false);
        var device = await _devices.CreateDeviceAsync("Kindle", "kindle@kindle.com", null);
        var file = CreateComic("Series - Chapter 0001.cbz");

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.QueueFilesAsync(
            new[] { file }, device.Id, null, EmailDeliverySource.Manual, false));
    }

    [Fact]
    public async Task ProcessDeliveryAsync_SendsOriginalArchiveAndMarksSent()
    {
        var device = await _devices.CreateDeviceAsync("Kindle", "kindle@kindle.com", EmailDeliveryFormat.Original);
        var file = CreateComic("Series - Chapter 0001.cbz");
        var queued = Assert.Single((await _service.QueueFilesAsync(
            new[] { file }, device.Id, null, EmailDeliverySource.Manual, false)).Queued);

        ComicEmailMessage? sent = null;
        _sender.Setup(s => s.SendAsync(It.IsAny<ComicEmailMessage>(), It.IsAny<CancellationToken>()))
            .Callback<ComicEmailMessage, CancellationToken>((m, _) => sent = m)
            .Returns(Task.CompletedTask);

        await _service.ProcessDeliveryAsync(queued.Id);

        Assert.NotNull(sent);
        Assert.Equal("kindle@kindle.com", sent!.ToAddress);
        Assert.Equal(file, sent.AttachmentPath);
        Assert.Equal("application/vnd.comicbook+zip", sent.AttachmentContentType);

        var delivery = await GetDeliveryAsync(queued.Id);
        Assert.Equal(EmailDeliveryStatus.Sent, delivery.Status);
        Assert.NotNull(delivery.SentAt);
        Assert.Null(delivery.ErrorMessage);
    }

    [Fact]
    public async Task ProcessDeliveryAsync_ConvertsToEpubAndCleansUpTheTemporaryFile()
    {
        var device = await _devices.CreateDeviceAsync("Kindle", "kindle@kindle.com", EmailDeliveryFormat.Epub);
        var file = CreateComic("Series - Chapter 0001.cbz");
        var queued = Assert.Single((await _service.QueueFilesAsync(
            new[] { file }, device.Id, null, EmailDeliverySource.Manual, false)).Queued);

        string? epubPath = null;
        EpubConversionOptions? usedOptions = null;
        _epub.Setup(e => e.ConvertToEpubAsync(file, It.IsAny<string>(), It.IsAny<EpubConversionOptions?>(), It.IsAny<CancellationToken>()))
            .Returns<string, string, EpubConversionOptions?, CancellationToken>((_, outDir, options, _) =>
            {
                usedOptions = options;
                Directory.CreateDirectory(outDir);
                epubPath = Path.Combine(outDir, "Series - Chapter 0001.epub");
                File.WriteAllText(epubPath, "epub-bytes");
                return Task.FromResult(epubPath);
            });

        ComicEmailMessage? sent = null;
        _sender.Setup(s => s.SendAsync(It.IsAny<ComicEmailMessage>(), It.IsAny<CancellationToken>()))
            .Callback<ComicEmailMessage, CancellationToken>((m, _) =>
            {
                sent = m;
                // The attachment must still exist while the message is being sent.
                Assert.True(File.Exists(m.AttachmentPath!));
            })
            .Returns(Task.CompletedTask);

        await _service.ProcessDeliveryAsync(queued.Id);

        Assert.Equal("application/epub+zip", sent!.AttachmentContentType);
        Assert.Equal(EmailDeliveryStatus.Sent, (await GetDeliveryAsync(queued.Id)).Status);
        Assert.False(File.Exists(epubPath!));
        Assert.NotNull(usedOptions);
        Assert.Equal(25L * 1024 * 1024, usedOptions!.MaxSizeBytes);
    }

    [Fact]
    public async Task ProcessDeliveryAsync_RecordsFailureWhenSendThrows()
    {
        var device = await _devices.CreateDeviceAsync("Kindle", "kindle@kindle.com", null);
        var file = CreateComic("Series - Chapter 0001.cbz");
        var queued = Assert.Single((await _service.QueueFilesAsync(
            new[] { file }, device.Id, null, EmailDeliverySource.Manual, false)).Queued);

        _sender.Setup(s => s.SendAsync(It.IsAny<ComicEmailMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("smtp exploded"));

        await _service.ProcessDeliveryAsync(queued.Id);

        var delivery = await GetDeliveryAsync(queued.Id);
        Assert.Equal(EmailDeliveryStatus.Failed, delivery.Status);
        Assert.Equal("smtp exploded", delivery.ErrorMessage);
        Assert.Null(delivery.SentAt);
    }

    [Fact]
    public async Task ProcessDeliveryAsync_FailsWhenAttachmentExceedsSizeLimit()
    {
        _settings.EmailMaxAttachmentMegabytes = 1;
        var device = await _devices.CreateDeviceAsync("Kindle", "kindle@kindle.com", null);
        var file = CreateComic("Big - Chapter 0001.cbz", sizeBytes: 2 * 1024 * 1024);
        var queued = Assert.Single((await _service.QueueFilesAsync(
            new[] { file }, device.Id, null, EmailDeliverySource.Manual, false)).Queued);

        await _service.ProcessDeliveryAsync(queued.Id);

        var delivery = await GetDeliveryAsync(queued.Id);
        Assert.Equal(EmailDeliveryStatus.Failed, delivery.Status);
        Assert.Contains("exceeds", delivery.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
        _sender.Verify(s => s.SendAsync(It.IsAny<ComicEmailMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessDeliveryAsync_IgnoresDeliveriesThatAreNoLongerPending()
    {
        var device = await _devices.CreateDeviceAsync("Kindle", "kindle@kindle.com", null);
        var file = CreateComic("Series - Chapter 0001.cbz");
        var queued = Assert.Single((await _service.QueueFilesAsync(
            new[] { file }, device.Id, null, EmailDeliverySource.Manual, false)).Queued);

        await _service.ProcessDeliveryAsync(queued.Id);
        await _service.ProcessDeliveryAsync(queued.Id);

        _sender.Verify(s => s.SendAsync(It.IsAny<ComicEmailMessage>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task QueueAutoSendAsync_QueuesForEnabledSubscriptionsOnly()
    {
        var kindle = await _devices.CreateDeviceAsync("Kindle", "kindle@kindle.com", EmailDeliveryFormat.Epub);
        var kobo = await _devices.CreateDeviceAsync("Kobo", "kobo@kobo.com", EmailDeliveryFormat.Original);

        await _devices.UpsertSubscriptionAsync("My Series", kindle.Id, EmailDeliveryFormat.Device, enabled: true);
        await _devices.UpsertSubscriptionAsync("My Series", kobo.Id, EmailDeliveryFormat.Device, enabled: false);

        var file = CreateComic("My Series - Chapter 0002.cbz");

        var count = await _service.QueueAutoSendAsync(file, "My Series");

        Assert.Equal(1, count);
        await using var db = await _dbFactory.CreateDbContextAsync();
        var delivery = Assert.Single(db.ComicEmailDeliveries.ToList());
        Assert.Equal(kindle.Id, delivery.DeviceId);
        // Subscription inherits the device default format.
        Assert.Equal(EmailDeliveryFormat.Epub, delivery.DeliveryFormat);
        Assert.Equal(EmailDeliverySource.Auto, delivery.Source);
    }

    [Fact]
    public async Task QueueAutoSendAsync_MatchesSubscriptionCreatedFromFolderName()
    {
        var device = await _devices.CreateDeviceAsync("Kindle", "kindle@kindle.com", null);
        await _devices.UpsertSubscriptionAsync("Folder Series", device.Id, EmailDeliveryFormat.Original, true);

        var folder = Path.Combine(_watchedDir, "Folder Series");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "Folder Series - Chapter 0003.cbz");
        await File.WriteAllTextAsync(file, "comic");

        // Metadata series differs from the folder the library groups by.
        var count = await _service.QueueAutoSendAsync(file, "Some Other Metadata Title");

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task QueueAutoSendAsync_WithoutSubscriptions_DoesNothing()
    {
        var file = CreateComic("Unwatched - Chapter 0001.cbz");

        Assert.Equal(0, await _service.QueueAutoSendAsync(file, "Unwatched"));
        _queue.Verify(q => q.Enqueue(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task QueueAutoSendAsync_WhenSmtpNotConfigured_SkipsWithoutQueueing()
    {
        var device = await _devices.CreateDeviceAsync("Kindle", "kindle@kindle.com", null);
        await _devices.UpsertSubscriptionAsync("My Series", device.Id, EmailDeliveryFormat.Original, true);
        _sender.SetupGet(s => s.IsConfigured).Returns(false);

        var file = CreateComic("My Series - Chapter 0004.cbz");

        Assert.Equal(0, await _service.QueueAutoSendAsync(file, "My Series"));
        _queue.Verify(q => q.Enqueue(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task SendTestEmailAsync_SendsMessageWithoutAttachment()
    {
        var device = await _devices.CreateDeviceAsync("Kindle", "kindle@kindle.com", null);
        ComicEmailMessage? sent = null;
        _sender.Setup(s => s.SendAsync(It.IsAny<ComicEmailMessage>(), It.IsAny<CancellationToken>()))
            .Callback<ComicEmailMessage, CancellationToken>((m, _) => sent = m)
            .Returns(Task.CompletedTask);

        await _service.SendTestEmailAsync(device.Id);

        Assert.Null(sent!.AttachmentPath);
        Assert.Equal("kindle@kindle.com", sent.ToAddress);
    }

    [Fact]
    public async Task GetRecentDeliveriesAsync_ReturnsNewestFirst()
    {
        var device = await _devices.CreateDeviceAsync("Kindle", "kindle@kindle.com", null);
        var first = CreateComic("A - Chapter 0001.cbz");
        var second = CreateComic("B - Chapter 0001.cbz");

        await _service.QueueFilesAsync(new[] { first }, device.Id, null, EmailDeliverySource.Manual, false);
        await _service.QueueFilesAsync(new[] { second }, device.Id, null, EmailDeliverySource.Manual, false);

        var deliveries = await _service.GetRecentDeliveriesAsync(10);

        Assert.Equal(2, deliveries.Count);
        Assert.Equal(Path.GetFileName(second), deliveries[0].FileName);
    }

    private string CreateComic(string fileName, int sizeBytes = 16)
    {
        var path = Path.Combine(_watchedDir, fileName);
        File.WriteAllBytes(path, new byte[sizeBytes]);
        return path;
    }

    private async Task<DateTime?> GetSubscriptionLastSentAsync(int subscriptionId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var subscription = await db.SeriesEmailSubscriptions.AsNoTracking()
            .FirstAsync(s => s.Id == subscriptionId);
        return subscription.LastSentAt;
    }

    private async Task<ComicEmailDeliveryEntity> GetDeliveryAsync(int id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.ComicEmailDeliveries.AsNoTracking().FirstAsync(d => d.Id == id);
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
