using System.Diagnostics.Metrics;
using System.Text.Json;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Core.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Services;

public sealed class ComicEmailServiceCoverageTests : IDisposable
{
    private readonly IDbContextFactory<ComicMaintainerDbContext> _dbFactory;
    private readonly Mock<IComicEmailSender> _sender = new();
    private readonly Mock<IEpubConversionService> _epub = new();
    private readonly Mock<IAzw3ConversionService> _azw3 = new();
    private readonly Mock<IComicEmailQueue> _queue = new();
    private readonly Mock<ISeriesMetadataCacheService> _seriesCache = new();
    private readonly Mock<ISeriesImageStore> _seriesImages = new();
    private readonly Mock<IOptionsMonitor<AppSettings>> _settingsMonitor = new();
    private readonly CapturingLogger _logger = new();
    private readonly EreaderDeviceService _devices;
    private readonly AppSettings _settings;
    private readonly string _root;
    private readonly string _watchedDir;

    public ComicEmailServiceCoverageTests()
    {
        _root = Path.Join(Path.GetTempPath(), $"email-coverage-{Guid.NewGuid():N}");
        _watchedDir = Path.Join(_root, "library");
        Directory.CreateDirectory(_watchedDir);

        var dbName = $"EmailCoverage_{Guid.NewGuid():N}";
        var services = new ServiceCollection();
        services.AddDbContextFactory<ComicMaintainerDbContext>(opt => opt.UseInMemoryDatabase(dbName));
        _dbFactory = services.BuildServiceProvider().GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>();

        _settings = new AppSettings
        {
            WatchedDirectory = _watchedDir,
            DuplicateDirectory = Path.Join(_root, "duplicates"),
            TempFileDirectory = Path.Join(_root, "temp"),
            EmailMaxAttachmentMegabytes = 25,
            SmtpHost = "smtp.example.com",
            EmailFromAddress = "library@example.com"
        };
        _settingsMonitor.Setup(s => s.CurrentValue).Returns(_settings);

        _sender.SetupGet(s => s.IsConfigured).Returns(true);
        _seriesCache.Setup(s => s.NormalizeKey(It.IsAny<string?>()))
            .Returns((string? value) => string.IsNullOrWhiteSpace(value)
                ? "unknown-series"
                : new string(value.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-'));

        _devices = new EreaderDeviceService(
            _dbFactory,
            _seriesCache.Object,
            new Mock<ILogger<EreaderDeviceService>>().Object);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }

    [Fact]
    public async Task QueueFilesAsync_ThrowsForAnUnknownDevice()
    {
        var file = CreateComic("Series 001.cbz");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateService().QueueFilesAsync(
            new[] { file }, 999, null, EmailDeliverySource.Manual, skipAlreadyDelivered: false));

        Assert.Equal("Ereader device 999 was not found.", ex.Message);
        Assert.Empty(await GetAllDeliveriesAsync());
    }

    [Fact]
    public async Task QueueCondensedFilesAsync_ThrowsForAnUnknownDevice()
    {
        var files = CreateSeries("Series", 2);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateService().QueueCondensedFilesAsync(
            files, 999, EmailCondenseMode.All, null, EmailDeliverySource.Manual, skipAlreadyDelivered: false));

        Assert.Equal("Ereader device 999 was not found.", ex.Message);
        Assert.Empty(await GetAllDeliveriesAsync());
    }

    [Fact]
    public async Task SendTestEmailAsync_ThrowsForAnUnknownDevice()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateService().SendTestEmailAsync(999));

        Assert.Equal("Ereader device 999 was not found.", ex.Message);
        _sender.Verify(s => s.SendAsync(It.IsAny<ComicEmailMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task QueueCondensedFilesAsync_ThrowsWhenEmailIsNotConfigured()
    {
        _sender.SetupGet(s => s.IsConfigured).Returns(false);
        var device = await _devices.CreateDeviceAsync("Kindle", "kindle@kindle.com", EmailDeliveryFormat.Epub);
        var files = CreateSeries("Series", 2);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateService().QueueCondensedFilesAsync(
            files, device.Id, EmailCondenseMode.All, null, EmailDeliverySource.Manual, skipAlreadyDelivered: false));

        Assert.Contains("Email delivery is not configured", ex.Message);
        Assert.Empty(await GetAllDeliveriesAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" None ")]
    public async Task QueueCondensedFilesAsync_RejectsAModeThatDoesNotCondense(string? condenseMode)
    {
        var device = await _devices.CreateDeviceAsync("Kindle", "kindle@kindle.com", EmailDeliveryFormat.Epub);
        var files = CreateSeries("Series", 2);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => CreateService().QueueCondensedFilesAsync(
            files, device.Id, condenseMode, null, EmailDeliverySource.Manual, skipAlreadyDelivered: false));

        Assert.Equal("condenseMode", ex.ParamName);
        Assert.StartsWith("Condensed delivery requires the 'count' or 'all' condense mode.", ex.Message);
        Assert.Empty(await GetAllDeliveriesAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData(EmailCondenseMode.None)]
    public async Task PlanCondensedDeliveryAsync_RejectsAModeThatDoesNotCondense(string? condenseMode)
    {
        var files = CreateSeries("Series", 2);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => CreateService().PlanCondensedDeliveryAsync(
            files, condenseMode, null));

        Assert.Equal("condenseMode", ex.ParamName);
    }

    [Fact]
    public async Task QueueCondensedFilesAsync_RecordsAnAutomaticSource()
    {
        var device = await _devices.CreateDeviceAsync("Kindle", "kindle@kindle.com", EmailDeliveryFormat.Azw3);
        var files = CreateSeries("Auto Series", 2);

        var result = await CreateService().QueueCondensedFilesAsync(
            files, device.Id, EmailCondenseMode.All, null, "AUTO", skipAlreadyDelivered: false);

        var queued = Assert.Single(result.Queued);
        Assert.Equal(EmailDeliverySource.Auto, queued.Source);
        Assert.Equal(EmailDeliveryFormat.Azw3, queued.DeliveryFormat);
        Assert.Equal(2, queued.IssueCount);
        Assert.Equal("Auto Series 001-002", queued.FileName);
        _queue.Verify(q => q.Enqueue(queued.Id), Times.Once);

        var stored = Assert.Single(await GetAllDeliveriesAsync());
        Assert.Equal(files, JsonSerializer.Deserialize<List<string>>(stored.CondensedFilePaths!));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public async Task CreateCondensedBookAsync_RejectsABookIndexOutsideThePlan(int bookIndex)
    {
        var files = CreateSeries("Series", 2);

        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => CreateService().CreateCondensedBookAsync(
            files, EmailCondenseMode.All, null, bookIndex));

        Assert.Equal("bookIndex", ex.ParamName);
        Assert.Contains($"Book {bookIndex + 1} does not exist; the selection condenses into 1 book(s).", ex.Message);
        _epub.Verify(e => e.ConvertToEpubAsync(
            It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<string>(),
            It.IsAny<EpubConversionOptions?>(),
            It.IsAny<CancellationToken>()), Times.Never);
        Assert.False(Directory.Exists(_settings.TempFileDirectory));
    }

    [Theory]
    [InlineData(EmailDeliveryFormat.Azw3, null, EmailDeliveryFormat.Azw3)]
    [InlineData(EmailDeliveryFormat.Azw3, " Device ", EmailDeliveryFormat.Azw3)]
    [InlineData(EmailDeliveryFormat.Original, EmailDeliveryFormat.Device, EmailDeliveryFormat.Epub)]
    [InlineData(EmailDeliveryFormat.Epub, "", EmailDeliveryFormat.Epub)]
    [InlineData(EmailDeliveryFormat.Azw3, EmailDeliveryFormat.Epub, EmailDeliveryFormat.Epub)]
    public async Task ResolveCondensedFormatAsync_InheritsTheDeviceDefaultUnlessAFormatIsChosen(
        string deviceFormat,
        string? requestedFormat,
        string expected)
    {
        var device = await _devices.CreateDeviceAsync("Kindle", "kindle@kindle.com", deviceFormat);

        var format = await CreateService().ResolveCondensedFormatAsync(requestedFormat, device.Id, CancellationToken.None);

        Assert.Equal(expected, format);
    }

    [Fact]
    public async Task ResolveCondensedFormatAsync_FallsBackToEpubForAnUnknownDevice()
    {
        var format = await CreateService().ResolveCondensedFormatAsync(null, 999, CancellationToken.None);

        Assert.Equal(EmailDeliveryFormat.Epub, format);
    }

    [Fact]
    public async Task PlanCondensedDeliveryAsync_ReportsWhyEachRequestedPathWasSkipped()
    {
        var valid = CreateComic("Series 001.cbz", folder: "Series");
        var missing = Path.Join(_watchedDir, "Series", "Series 404.cbz");
        var notAComic = Path.Join(_watchedDir, "Series", "notes.txt");
        await File.WriteAllTextAsync(notAComic, "not a comic");
        var outside = CreateFile(Path.Join(_root, "outside", "Escaped 001.cbz"));

        var plan = await CreateService().PlanCondensedDeliveryAsync(
            new[] { " ", valid, outside, missing, notAComic, valid },
            EmailCondenseMode.All,
            null);

        Assert.Equal(1, plan.TotalIssues);
        var book = Assert.Single(plan.Books);
        Assert.Equal(new[] { valid }, book.Files);
        Assert.Equal("Series 001", book.DisplayName);
        Assert.Equal(3, plan.Skipped.Count);
        Assert.Equal("File is outside the comic library", plan.Skipped[outside]);
        Assert.Equal("File not found", plan.Skipped[missing]);
        Assert.Equal("Not a supported comic archive", plan.Skipped[notAComic]);
    }

    [Fact]
    public async Task PlanCondensedDeliveryAsync_HasNoBooksWhenEveryPathIsSkipped()
    {
        var missing = Path.Join(_watchedDir, "Series 404.cbz");

        var plan = await CreateService().PlanCondensedDeliveryAsync(
            new[] { missing }, EmailCondenseMode.All, null);

        Assert.Equal(EmailCondenseMode.All, plan.Mode);
        Assert.Equal(0, plan.TotalIssues);
        Assert.Equal(1, plan.IssuesPerBook);
        Assert.Empty(plan.Books);
        Assert.False(plan.CanEmail);
        Assert.Equal("File not found", Assert.Single(plan.Skipped).Value);
    }

    [Fact]
    public async Task UnreadableCondensedMemberLists_AreTreatedAsSingleIssueDeliveries()
    {
        var device = await _devices.CreateDeviceAsync("Kindle", "kindle@kindle.com", EmailDeliveryFormat.Epub);
        var files = CreateSeries("Series", 3);
        await InsertDeliveryAsync(device.Id, files[0], EmailDeliveryStatus.Sent, condensedFilePaths: "not json");
        await InsertDeliveryAsync(device.Id, files[1], EmailDeliveryStatus.Pending, condensedFilePaths: "null");

        var plan = await CreateService().PlanCondensedDeliveryAsync(
            files,
            EmailCondenseMode.All,
            null,
            deviceId: device.Id,
            skipAlreadyDelivered: true);

        Assert.Equal("Already delivered to this device", plan.Skipped[files[0]]);
        Assert.Equal("Already delivered to this device", plan.Skipped[files[1]]);
        Assert.Equal(new[] { files[2] }, Assert.Single(plan.Books).Files);

        var recent = await CreateService().GetRecentDeliveriesAsync(10);
        Assert.Equal(2, recent.Count);
        Assert.All(recent, delivery => Assert.Equal(1, delivery.IssueCount));
    }

    [Fact]
    public async Task QueueCondensedFilesAsync_StillQueuesWhenAnIssueVanishesWhileTheBookIsSized()
    {
        var device = await _devices.CreateDeviceAsync("Kindle", "kindle@kindle.com", EmailDeliveryFormat.Epub);
        var files = CreateSeries("Vanish", 2);

        // The book is named (which consults the cache) before it is sized, so
        // deleting an issue here makes the size estimate hit a missing file.
        _seriesCache.Setup(s => s.GetByTitleAsync("Vanish", It.IsAny<CancellationToken>()))
            .Callback(() => File.Delete(files[1]))
            .ReturnsAsync((SeriesMetadataCacheRecord?)null);

        var result = await CreateService().QueueCondensedFilesAsync(
            files, device.Id, EmailCondenseMode.All, null, EmailDeliverySource.Manual, skipAlreadyDelivered: false);

        var queued = Assert.Single(result.Queued);
        Assert.Equal(2, queued.IssueCount);
        Assert.Equal("Vanish 001-002", queued.FileName);
        Assert.False(File.Exists(files[1]));
    }

    [Fact]
    public async Task PlanCondensedDeliveryAsync_CountsTheIssuesWhenNoIssueNumberCanBeRead()
    {
        var files = new[]
        {
            CreateComic("Alpha.cbz", folder: "Wordless"),
            CreateComic("Omega.cbz", folder: "Wordless")
        };

        var plan = await CreateService().PlanCondensedDeliveryAsync(files, EmailCondenseMode.All, null);

        Assert.Equal("Wordless (2 issues)", Assert.Single(plan.Books).DisplayName);
    }

    [Fact]
    public async Task PlanCondensedDeliveryAsync_CountsTheIssuesWhenOnlyTheFirstIssueIsNumbered()
    {
        var files = new[]
        {
            CreateComic("Issue 1.cbz", folder: "Mixed"),
            CreateComic("Zeta.cbz", folder: "Mixed")
        };

        var plan = await CreateService().PlanCondensedDeliveryAsync(files, EmailCondenseMode.All, null);

        Assert.Equal("Mixed (2 issues)", Assert.Single(plan.Books).DisplayName);
    }

    [Fact]
    public async Task PlanCondensedDeliveryAsync_KeepsTheFractionOfADecimalIssueNumber()
    {
        var files = new[]
        {
            CreateComic("Series #12.5.cbz", folder: "Decimal"),
            CreateComic("Series #13.cbz", folder: "Decimal")
        };

        var plan = await CreateService().PlanCondensedDeliveryAsync(files, EmailCondenseMode.All, null);

        Assert.Equal("Decimal 012.5-013", Assert.Single(plan.Books).DisplayName);
    }

    [Theory]
    [InlineData("Resolved Name", "Canonical Title", "Resolved Name 001-002")]
    [InlineData("", "Canonical Title", "Canonical Title 001-002")]
    [InlineData("   ", "Canonical Title", "Canonical Title 001-002")]
    public async Task PlanCondensedDeliveryAsync_NamesTheBookAfterTheCachedSeriesName(
        string resolvedName,
        string canonicalTitle,
        string expected)
    {
        var files = CreateSeries("Folder Name", 2);
        _seriesCache.Setup(s => s.GetByTitleAsync("Folder Name", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesMetadataCacheRecord
            {
                CanonicalTitle = canonicalTitle,
                ResolvedSeriesName = resolvedName
            });

        var plan = await CreateService().PlanCondensedDeliveryAsync(files, EmailCondenseMode.All, null);

        Assert.Equal(expected, Assert.Single(plan.Books).DisplayName);
    }

    [Fact]
    public async Task PlanCondensedDeliveryAsync_NamesTheBookAfterTheFolderWhenTheCacheFails()
    {
        var files = CreateSeries("Folder Name", 2);
        var failure = new InvalidOperationException("cache unavailable");
        _seriesCache.Setup(s => s.GetByTitleAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);

        var plan = await CreateService().PlanCondensedDeliveryAsync(files, EmailCondenseMode.All, null);

        Assert.Equal("Folder Name 001-002", Assert.Single(plan.Books).DisplayName);
        var warning = Assert.Single(_logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Same(failure, warning.Exception);
        Assert.Contains("Could not resolve the series name", warning.Message);
    }

    [Fact]
    public async Task PlanCondensedDeliveryAsync_DoesNotSwallowCancellationFromTheCache()
    {
        var files = CreateSeries("Folder Name", 2);
        _seriesCache.Setup(s => s.GetByTitleAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException("stopping"));

        await Assert.ThrowsAsync<OperationCanceledException>(() => CreateService().PlanCondensedDeliveryAsync(
            files, EmailCondenseMode.All, null));

        Assert.Empty(_logger.Entries);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task QueueAutoSendAsync_IgnoresABlankPath(string filePath)
    {
        var device = await _devices.CreateDeviceAsync("Kindle", "kindle@kindle.com", EmailDeliveryFormat.Epub);
        await _devices.UpsertSubscriptionAsync("Unknown Series", device.Id, EmailDeliveryFormat.Device, true);

        var count = await CreateService().QueueAutoSendAsync(filePath, "Unknown Series");

        Assert.Equal(0, count);
        Assert.Empty(await GetAllDeliveriesAsync());
    }

    [Fact]
    public async Task QueueAutoSendAsync_IgnoresAFileWithoutAUsableSeriesKey()
    {
        var device = await _devices.CreateDeviceAsync("Kindle", "kindle@kindle.com", EmailDeliveryFormat.Epub);
        await _devices.UpsertSubscriptionAsync("Unknown Series", device.Id, EmailDeliveryFormat.Device, true);
        var punctuationOnly = CreateComic("Issue 001.cbz", folder: "!!!");
        var service = CreateService();

        Assert.Equal(0, await service.QueueAutoSendAsync(punctuationOnly, "***"));
        Assert.Equal(0, await service.QueueAutoSendAsync("Issue 001.cbz", null));

        // Every candidate key normalized to nothing, so the subscription store
        // is never consulted and nothing is queued.
        Assert.Empty(await GetAllDeliveriesAsync());
    }

    [Fact]
    public async Task QueueAutoSendAsync_KeepsQueueingWhenOneSubscriptionFails()
    {
        var broken = await _devices.CreateDeviceAsync("Broken", "broken@kindle.com", EmailDeliveryFormat.Epub);
        var working = await _devices.CreateDeviceAsync("Working", "working@kindle.com", EmailDeliveryFormat.Epub);
        var brokenSubscription = await _devices.UpsertSubscriptionAsync("Auto Series", broken.Id, EmailDeliveryFormat.Device, true);
        await _devices.UpsertSubscriptionAsync("Auto Series", working.Id, EmailDeliveryFormat.Device, true);

        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var subscription = await db.SeriesEmailSubscriptions.FirstAsync(s => s.Id == brokenSubscription!.Id);
            subscription.DeliveryFormat = "bogus";
            await db.SaveChangesAsync();
        }

        var file = CreateComic("Auto Series 001.cbz", folder: "Auto Series");

        var count = await CreateService().QueueAutoSendAsync(file, null);

        Assert.Equal(1, count);
        var delivery = Assert.Single(await GetAllDeliveriesAsync());
        Assert.Equal(working.Id, delivery.DeviceId);
        Assert.Equal(EmailDeliverySource.Auto, delivery.Source);
        var error = Assert.Single(_logger.Entries, e => e.Level == LogLevel.Error);
        Assert.IsType<ArgumentException>(error.Exception);
        Assert.Contains("Failed to queue automatic delivery", error.Message);
        Assert.Contains($"device {broken.Id}", error.Message);
    }

    [Fact]
    public async Task ProcessDeliveryAsync_WarnsWhenTheDeliveryNoLongerExists()
    {
        await CreateService().ProcessDeliveryAsync(99999);

        var warning = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Equal("Comic email delivery 99999 no longer exists", warning.Message);
        _sender.Verify(s => s.SendAsync(It.IsAny<ComicEmailMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessDeliveryAsync_FailsWhenTheIssueWasDeletedAfterQueueing()
    {
        var device = await _devices.CreateDeviceAsync("Kindle", "kindle@kindle.com", EmailDeliveryFormat.Original);
        var file = CreateComic("Gone 001.cbz");
        var service = CreateService();
        var queued = Assert.Single((await service.QueueFilesAsync(
            new[] { file }, device.Id, null, EmailDeliverySource.Manual, false)).Queued);
        File.Delete(file);

        await service.ProcessDeliveryAsync(queued.Id);

        var row = await GetDeliveryAsync(queued.Id);
        Assert.Equal(EmailDeliveryStatus.Failed, row.Status);
        Assert.Equal("File not found: Gone 001.cbz", row.ErrorMessage);
        Assert.IsType<FileNotFoundException>(Assert.Single(_logger.Entries, e => e.Level == LogLevel.Error).Exception);
        _sender.Verify(s => s.SendAsync(It.IsAny<ComicEmailMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessDeliveryAsync_RefusesAStoredPathOutsideTheLibrary()
    {
        var device = await _devices.CreateDeviceAsync("Kindle", "kindle@kindle.com", EmailDeliveryFormat.Original);
        var outside = CreateFile(Path.Join(_root, "outside", "Escaped 001.cbz"));
        var deliveryId = await InsertDeliveryAsync(device.Id, outside, EmailDeliveryStatus.Pending);

        await CreateService().ProcessDeliveryAsync(deliveryId);

        var row = await GetDeliveryAsync(deliveryId);
        Assert.Equal(EmailDeliveryStatus.Failed, row.Status);
        Assert.Equal("File not found: Escaped 001.cbz", row.ErrorMessage);
        _sender.Verify(s => s.SendAsync(It.IsAny<ComicEmailMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessDeliveryAsync_FailsWhenTheConverterProducesAnEmptyBook()
    {
        var device = await _devices.CreateDeviceAsync("Kindle", "kindle@kindle.com", EmailDeliveryFormat.Epub);
        var file = CreateComic("Empty 001.cbz");
        var service = CreateService();
        var queued = Assert.Single((await service.QueueFilesAsync(
            new[] { file }, device.Id, null, EmailDeliverySource.Manual, false)).Queued);
        var converted = SetupEpubConversion(file, bytes: Array.Empty<byte>());

        await service.ProcessDeliveryAsync(queued.Id);

        var row = await GetDeliveryAsync(queued.Id);
        Assert.Equal(EmailDeliveryStatus.Failed, row.Status);
        Assert.Equal("Generated attachment for Empty 001.cbz is empty; nothing was sent.", row.ErrorMessage);
        Assert.False(File.Exists(converted()));
        _sender.Verify(s => s.SendAsync(It.IsAny<ComicEmailMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessDeliveryAsync_LeavesTheDeliveryPendingWhenSendingIsCancelled()
    {
        var device = await _devices.CreateDeviceAsync("Kindle", "kindle@kindle.com", EmailDeliveryFormat.Epub);
        var file = CreateComic("Cancelled 001.cbz");
        var service = CreateService();
        var queued = Assert.Single((await service.QueueFilesAsync(
            new[] { file }, device.Id, null, EmailDeliverySource.Manual, false)).Queued);
        var converted = SetupEpubConversion(file);
        _sender.Setup(s => s.SendAsync(It.IsAny<ComicEmailMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException("shutting down"));

        await Assert.ThrowsAsync<OperationCanceledException>(() => service.ProcessDeliveryAsync(queued.Id));

        var row = await GetDeliveryAsync(queued.Id);
        Assert.Equal(EmailDeliveryStatus.Pending, row.Status);
        Assert.Null(row.ErrorMessage);
        Assert.False(File.Exists(converted()));
        Assert.DoesNotContain(_logger.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task ProcessDeliveryAsync_UsesTheCachedSeriesArtworkAndName()
    {
        var device = await _devices.CreateDeviceAsync("Kindle", "kindle@kindle.com", EmailDeliveryFormat.Epub);
        var file = CreateComic("Cached 001.cbz", folder: "Cached Folder");
        CreateFile(Path.Join(_watchedDir, "Cached Folder", "cover.jpg"));
        var cachedImage = Path.Join(_root, "series-images", "cached.jpg");
        _seriesCache.Setup(s => s.GetByTitleAsync("Cached Folder", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesMetadataCacheRecord
            {
                CanonicalTitle = "Canonical Title",
                ResolvedSeriesName = "Resolved Series",
                LocalImageFile = "cached.jpg"
            });
        _seriesImages.Setup(i => i.ResolveAbsolutePath("cached.jpg")).Returns(cachedImage);

        var service = CreateService();
        var queued = Assert.Single((await service.QueueFilesAsync(
            new[] { file }, device.Id, null, EmailDeliverySource.Manual, false)).Queued);
        EpubConversionOptions? usedOptions = null;
        SetupEpubConversion(file, onConvert: (_, options) => usedOptions = options);
        _sender.Setup(s => s.SendAsync(It.IsAny<ComicEmailMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await service.ProcessDeliveryAsync(queued.Id);

        Assert.Equal(EmailDeliveryStatus.Sent, (await GetDeliveryAsync(queued.Id)).Status);
        Assert.NotNull(usedOptions);
        Assert.Equal(cachedImage, usedOptions!.SeriesImagePath);
        Assert.Equal("Resolved Series", usedOptions.SeriesTitle);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("missing.jpg")]
    public async Task ProcessDeliveryAsync_FallsBackToTheCanonicalTitleAndTheFolderCover(string? localImageFile)
    {
        var device = await _devices.CreateDeviceAsync("Kindle", "kindle@kindle.com", EmailDeliveryFormat.Epub);
        var file = CreateComic("Cached 001.cbz", folder: "Cached Folder");
        var sidecar = CreateFile(Path.Join(_watchedDir, "Cached Folder", "cover.png"));
        _seriesCache.Setup(s => s.GetByTitleAsync("Cached Folder", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesMetadataCacheRecord
            {
                CanonicalTitle = "Canonical Title",
                ResolvedSeriesName = " ",
                LocalImageFile = localImageFile
            });
        _seriesImages.Setup(i => i.ResolveAbsolutePath(It.IsAny<string>())).Returns(default(string?));

        var service = CreateService();
        var queued = Assert.Single((await service.QueueFilesAsync(
            new[] { file }, device.Id, null, EmailDeliverySource.Manual, false)).Queued);
        EpubConversionOptions? usedOptions = null;
        SetupEpubConversion(file, onConvert: (_, options) => usedOptions = options);
        _sender.Setup(s => s.SendAsync(It.IsAny<ComicEmailMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await service.ProcessDeliveryAsync(queued.Id);

        Assert.NotNull(usedOptions);
        Assert.Equal(sidecar, usedOptions!.SeriesImagePath);
        Assert.Equal("Canonical Title", usedOptions.SeriesTitle);
        _seriesImages.Verify(
            i => i.ResolveAbsolutePath(It.IsAny<string>()),
            localImageFile is null ? Times.Never() : Times.Once());
    }

    [Fact]
    public async Task ProcessDeliveryAsync_UsesTheSystemTempDirectoryWhenNoneIsConfigured()
    {
        _settings.TempFileDirectory = "  ";
        var device = await _devices.CreateDeviceAsync("Kindle", "kindle@kindle.com", EmailDeliveryFormat.Epub);
        var file = CreateComic("Temp 001.cbz");
        var service = CreateService();
        var queued = Assert.Single((await service.QueueFilesAsync(
            new[] { file }, device.Id, null, EmailDeliverySource.Manual, false)).Queued);
        string? workDirectory = null;
        SetupEpubConversion(file, onConvert: (outDir, _) => workDirectory = outDir);
        _sender.Setup(s => s.SendAsync(It.IsAny<ComicEmailMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await service.ProcessDeliveryAsync(queued.Id);

        Assert.Equal(EmailDeliveryStatus.Sent, (await GetDeliveryAsync(queued.Id)).Status);
        Assert.NotNull(workDirectory);
        Assert.Equal(
            Path.Join(Path.GetTempPath(), "ComicMaintainer", "email"),
            Path.GetDirectoryName(workDirectory));
    }

    [Theory]
    [InlineData("Rar Issue 001.cbr", "application/vnd.comicbook-rar")]
    [InlineData("Plain Issue 001.zip", "application/octet-stream")]
    public async Task ProcessDeliveryAsync_SendsOriginalArchivesWithTheirContentType(string fileName, string contentType)
    {
        var device = await _devices.CreateDeviceAsync("Kindle", "kindle@kindle.com", EmailDeliveryFormat.Original);
        var file = CreateComic(fileName);
        var service = CreateService();
        var queued = Assert.Single((await service.QueueFilesAsync(
            new[] { file }, device.Id, null, EmailDeliverySource.Manual, false)).Queued);
        ComicEmailMessage? sent = null;
        _sender.Setup(s => s.SendAsync(It.IsAny<ComicEmailMessage>(), It.IsAny<CancellationToken>()))
            .Callback<ComicEmailMessage, CancellationToken>((message, _) => sent = message)
            .Returns(Task.CompletedTask);

        await service.ProcessDeliveryAsync(queued.Id);

        Assert.NotNull(sent);
        Assert.Equal(contentType, sent!.AttachmentContentType);
        Assert.Equal(fileName, sent.AttachmentFileName);
        Assert.Equal(file, sent.AttachmentPath);
        Assert.True(File.Exists(file));
        Assert.Equal(EmailDeliveryStatus.Sent, (await GetDeliveryAsync(queued.Id)).Status);
    }

    [Fact]
    public async Task ProcessDeliveryAsync_TruncatesALongErrorMessage()
    {
        var device = await _devices.CreateDeviceAsync("Kindle", "kindle@kindle.com", EmailDeliveryFormat.Original);
        var file = CreateComic("Long Error 001.cbz");
        var service = CreateService();
        var queued = Assert.Single((await service.QueueFilesAsync(
            new[] { file }, device.Id, null, EmailDeliverySource.Manual, false)).Queued);
        _sender.Setup(s => s.SendAsync(It.IsAny<ComicEmailMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(new string('x', 2000)));

        await service.ProcessDeliveryAsync(queued.Id);

        var row = await GetDeliveryAsync(queued.Id);
        Assert.Equal(EmailDeliveryStatus.Failed, row.Status);
        Assert.Equal(new string('x', 1024), row.ErrorMessage);
    }

    [Fact]
    public async Task ProcessDeliveryAsync_RecordsTheOutcomeOfEachSendInTheMetrics()
    {
        using var metrics = new ComicMaintainerMetrics();
        var outcomes = new List<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (ReferenceEquals(instrument.Meter, metrics.Meter) &&
                instrument.Name == "comicmaintainer.email.sends")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "outcome")
                {
                    lock (outcomes)
                    {
                        outcomes.Add($"{tag.Value}:{measurement}");
                    }
                }
            }
        });
        listener.Start();

        var device = await _devices.CreateDeviceAsync("Kindle", "kindle@kindle.com", EmailDeliveryFormat.Original);
        var sentFile = CreateComic("Metrics 001.cbz");
        var failedFile = CreateComic("Metrics 002.cbz");
        var service = CreateService(metrics);
        var queued = (await service.QueueFilesAsync(
            new[] { sentFile, failedFile }, device.Id, null, EmailDeliverySource.Manual, false)).Queued;
        _sender.Setup(s => s.SendAsync(
                It.Is<ComicEmailMessage>(m => m.AttachmentPath == sentFile),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _sender.Setup(s => s.SendAsync(
                It.Is<ComicEmailMessage>(m => m.AttachmentPath == failedFile),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("rejected"));

        foreach (var delivery in queued)
        {
            await service.ProcessDeliveryAsync(delivery.Id);
        }

        Assert.Equal(new[] { "sent:1", "failed:1" }, outcomes);
    }

    private ComicEmailService CreateService(ComicMaintainerMetrics? metrics = null) => new(
        _dbFactory,
        _sender.Object,
        _epub.Object,
        _azw3.Object,
        _queue.Object,
        _seriesCache.Object,
        _seriesImages.Object,
        _settingsMonitor.Object,
        _logger,
        metrics);

    /// <summary>
    /// Makes the EPUB converter write <paramref name="bytes"/> to a book in its
    /// own directory under the test root and returns a getter for that path.
    /// </summary>
    private Func<string?> SetupEpubConversion(
        string source,
        byte[]? bytes = null,
        Action<string, EpubConversionOptions?>? onConvert = null)
    {
        string? bookPath = null;
        _epub.Setup(e => e.ConvertToEpubAsync(source, It.IsAny<string>(), It.IsAny<EpubConversionOptions?>(), It.IsAny<CancellationToken>()))
            .Returns<string, string, EpubConversionOptions?, CancellationToken>((_, outDir, options, _) =>
            {
                onConvert?.Invoke(outDir, options);
                var directory = Path.Join(_root, "converted", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                bookPath = Path.Join(directory, Path.GetFileNameWithoutExtension(source) + ".epub");
                File.WriteAllBytes(bookPath, bytes ?? new byte[] { 1, 2, 3, 4 });
                return Task.FromResult(bookPath);
            });

        return () => bookPath;
    }

    private List<string> CreateSeries(string folder, int count) =>
        Enumerable.Range(1, count)
            .Select(i => CreateComic($"{folder} {i:D3}.cbz", folder: folder))
            .ToList();

    private string CreateComic(string fileName, int sizeBytes = 16, string? folder = null)
    {
        var directory = folder is null ? _watchedDir : Path.Join(_watchedDir, folder);
        return CreateFile(Path.Join(directory, fileName), sizeBytes);
    }

    private static string CreateFile(string path, int sizeBytes = 16)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[sizeBytes]);
        return path;
    }

    private async Task<int> InsertDeliveryAsync(
        int deviceId,
        string filePath,
        string status,
        string? condensedFilePaths = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var device = await db.EreaderDevices.AsNoTracking().FirstAsync(d => d.Id == deviceId);
        var entity = new ComicEmailDeliveryEntity
        {
            FilePath = filePath,
            DeviceId = deviceId,
            DeviceName = device.Name,
            DeviceEmail = device.EmailAddress,
            DeliveryFormat = device.DeliveryFormat,
            Status = status,
            Source = EmailDeliverySource.Manual,
            CondensedFilePaths = condensedFilePaths,
            CreatedAt = DateTime.UtcNow
        };
        db.ComicEmailDeliveries.Add(entity);
        await db.SaveChangesAsync();
        return entity.Id;
    }

    private async Task<ComicEmailDeliveryEntity> GetDeliveryAsync(int id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.ComicEmailDeliveries.AsNoTracking().FirstAsync(d => d.Id == id);
    }

    private async Task<List<ComicEmailDeliveryEntity>> GetAllDeliveriesAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.ComicEmailDeliveries.AsNoTracking().OrderBy(d => d.Id).ToListAsync();
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    private sealed class CapturingLogger : ILogger<ComicEmailService>
    {
        private readonly List<LogEntry> _entries = new();

        public IReadOnlyList<LogEntry> Entries
        {
            get
            {
                lock (_entries)
                {
                    return _entries.ToList();
                }
            }
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
            {
                _entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
