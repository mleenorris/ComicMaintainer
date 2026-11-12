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

/// <summary>
/// E-reader devices, their subscriptions and the deliveries sent to them are
/// private to the device's owner: another user cannot list, read, change,
/// send to, subscribe, or cancel them, and gets the same answer as for a row
/// that does not exist. Administrators can reach everyone's.
/// </summary>
public sealed class EmailOwnershipTests : IDisposable
{
    private static readonly EmailAccessScope Alice = EmailAccessScope.ForUser("alice");
    private static readonly EmailAccessScope Bob = EmailAccessScope.ForUser("bob");
    private static readonly EmailAccessScope Admin = EmailAccessScope.ForUser("admin", isAdministrator: true);
    private static readonly EmailAccessScope AdminListingEveryone =
        EmailAccessScope.ForUser("admin", isAdministrator: true, listEveryone: true);

    private readonly IDbContextFactory<ComicMaintainerDbContext> _dbFactory;
    private readonly Mock<IComicEmailSender> _sender = new();
    private readonly ComicEmailService _email;
    private readonly EreaderDeviceService _devices;
    private readonly string _watchedDir;

    public EmailOwnershipTests()
    {
        _watchedDir = Path.Join(Path.GetTempPath(), $"email-ownership-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_watchedDir);

        var dbName = $"TestDb_{Guid.NewGuid()}";
        var services = new ServiceCollection();
        services.AddDbContextFactory<ComicMaintainerDbContext>(opt => opt.UseInMemoryDatabase(dbName));
        _dbFactory = services.BuildServiceProvider().GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>();

        var settings = new Mock<IOptionsMonitor<AppSettings>>();
        settings.Setup(s => s.CurrentValue).Returns(new AppSettings
        {
            WatchedDirectory = _watchedDir,
            DuplicateDirectory = Path.Join(_watchedDir, "..", "duplicates-none"),
            TempFileDirectory = Path.Join(_watchedDir, "temp"),
            SmtpHost = "smtp.example.com",
            EmailFromAddress = "library@example.com"
        });

        _sender.SetupGet(s => s.IsConfigured).Returns(true);

        var seriesCache = new Mock<ISeriesMetadataCacheService>();
        seriesCache.Setup(s => s.NormalizeKey(It.IsAny<string?>()))
            .Returns((string? value) => string.IsNullOrWhiteSpace(value)
                ? "unknown-series"
                : new string(value.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-'));

        _email = new ComicEmailService(
            _dbFactory,
            _sender.Object,
            new Mock<IEpubConversionService>().Object,
            new Mock<IAzw3ConversionService>().Object,
            new Mock<IComicEmailQueue>().Object,
            seriesCache.Object,
            new Mock<ISeriesImageStore>().Object,
            settings.Object,
            new Mock<ILogger<ComicEmailService>>().Object);

        _devices = new EreaderDeviceService(
            _dbFactory,
            seriesCache.Object,
            new Mock<ILogger<EreaderDeviceService>>().Object);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_watchedDir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }

    [Fact]
    public async Task Devices_AreListedOnlyForTheirOwner()
    {
        var alices = await _devices.CreateDeviceAsync(Alice, "Alice's Kindle", "alice@kindle.com", null);
        var bobs = await _devices.CreateDeviceAsync(Bob, "Bob's Kobo", "bob@kobo.com", null);

        Assert.Equal("alice", alices.OwnerUserId);
        Assert.Equal(new[] { alices.Id }, (await _devices.GetDevicesAsync(Alice)).Select(d => d.Id));
        Assert.Equal(new[] { bobs.Id }, (await _devices.GetDevicesAsync(Bob)).Select(d => d.Id));

        // An administrator lists their own (none here) unless asked for everyone's.
        Assert.Empty(await _devices.GetDevicesAsync(Admin));
        Assert.Equal(
            new[] { alices.Id, bobs.Id },
            (await _devices.GetDevicesAsync(AdminListingEveryone)).Select(d => d.Id).Order());
    }

    [Fact]
    public async Task AnotherUsersDevice_CannotBeReadChangedOrDeleted()
    {
        var device = await _devices.CreateDeviceAsync(Alice, "Alice's Kindle", "alice@kindle.com", null);

        Assert.Null(await _devices.GetDeviceAsync(Bob, device.Id));
        Assert.Null(await _devices.UpdateDeviceAsync(Bob, device.Id, "Mine now", "bob@kindle.com", null));
        Assert.False(await _devices.DeleteDeviceAsync(Bob, device.Id));

        var unchanged = await _devices.GetDeviceAsync(Alice, device.Id);
        Assert.Equal("Alice's Kindle", unchanged!.Name);
        Assert.Equal("alice@kindle.com", unchanged.EmailAddress);

        // An administrator may manage it, without taking it over.
        var renamed = await _devices.UpdateDeviceAsync(Admin, device.Id, "Renamed", null, null);
        Assert.Equal("alice", renamed!.OwnerUserId);
        Assert.True(await _devices.DeleteDeviceAsync(Admin, device.Id));
    }

    [Fact]
    public async Task TheSameAddress_CanBeSavedByTwoUsersButNotTwiceByOne()
    {
        await _devices.CreateDeviceAsync(Alice, "Kindle", "shared@kindle.com", null);

        // Telling Bob the address is taken would reveal Alice's device.
        var bobs = await _devices.CreateDeviceAsync(Bob, "Kindle", "shared@kindle.com", null);
        Assert.Equal("bob", bobs.OwnerUserId);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _devices.CreateDeviceAsync(Alice, "Kindle again", "shared@kindle.com", null));

        var other = await _devices.CreateDeviceAsync(Alice, "Kobo", "alice@kobo.com", null);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _devices.UpdateDeviceAsync(Alice, other.Id, null, "shared@kindle.com", null));
    }

    [Fact]
    public async Task ADevice_CanOnlyBeSavedByAUser()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _devices.CreateDeviceAsync(EmailAccessScope.System, "Kindle", "kindle@kindle.com", null));
    }

    [Fact]
    public async Task Subscriptions_BelongToTheOwnerOfTheirDevice()
    {
        var device = await _devices.CreateDeviceAsync(Alice, "Alice's Kindle", "alice@kindle.com", null);
        var subscription = await _devices.UpsertSubscriptionAsync(Alice, "Saga", device.Id, null, enabled: true);
        Assert.Equal("alice", subscription!.OwnerUserId);

        Assert.Null(await _devices.UpsertSubscriptionAsync(Bob, "Other Series", device.Id, null, enabled: true));
        Assert.Empty(await _devices.GetSubscriptionsAsync(Bob));
        Assert.Empty(await _devices.GetSubscriptionsAsync(Bob, "Saga"));
        Assert.False(await _devices.DeleteSubscriptionAsync(Bob, subscription.Id));

        Assert.Equal(new[] { subscription.Id }, (await _devices.GetSubscriptionsAsync(Alice)).Select(s => s.Id));
        Assert.Equal(new[] { subscription.Id }, (await _devices.GetSubscriptionsAsync(AdminListingEveryone)).Select(s => s.Id));
        Assert.True(await _devices.DeleteSubscriptionAsync(Admin, subscription.Id));
    }

    [Fact]
    public async Task UnownedLegacyDevices_AreReachableByAdministratorsOnly()
    {
        int legacyId;
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var legacy = new EreaderDeviceEntity { Name = "Shared Kindle", EmailAddress = "shared@kindle.com" };
            db.EreaderDevices.Add(legacy);
            await db.SaveChangesAsync();
            legacyId = legacy.Id;
        }

        Assert.Empty(await _devices.GetDevicesAsync(Alice));
        Assert.Null(await _devices.GetDeviceAsync(Alice, legacyId));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _email.SendTestEmailAsync(Alice, legacyId));

        Assert.Equal(new[] { legacyId }, (await _devices.GetDevicesAsync(Admin)).Select(d => d.Id));
        Assert.NotNull(await _devices.GetDeviceAsync(Admin, legacyId));
    }

    [Fact]
    public async Task SendingToAnotherUsersDevice_IsRefusedLikeAMissingDevice()
    {
        var device = await _devices.CreateDeviceAsync(Alice, "Alice's Kindle", "alice@kindle.com", null);
        var file = CreateComic("Saga - Chapter 0001.cbz");

        var queue = await Assert.ThrowsAsync<InvalidOperationException>(() => _email.QueueFilesAsync(
            Bob, new[] { file }, device.Id, null, EmailDeliverySource.Manual, skipAlreadyDelivered: false));
        var condensed = await Assert.ThrowsAsync<InvalidOperationException>(() => _email.QueueCondensedFilesAsync(
            Bob, new[] { file }, device.Id, EmailCondenseMode.All, null, EmailDeliverySource.Manual, skipAlreadyDelivered: false));
        var test = await Assert.ThrowsAsync<InvalidOperationException>(() => _email.SendTestEmailAsync(Bob, device.Id));

        var missing = $"Ereader device {device.Id} was not found.";
        Assert.All(new[] { queue, condensed, test }, ex => Assert.Equal(missing, ex.Message));
        Assert.Empty(await _email.GetRecentDeliveriesAsync(AdminListingEveryone, 10));
        _sender.Verify(s => s.SendAsync(It.IsAny<ComicEmailMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Deliveries_BelongToTheDevicesOwnerAndAreListedOnlyForThem()
    {
        var device = await _devices.CreateDeviceAsync(Alice, "Alice's Kindle", "alice@kindle.com", null);
        var file = CreateComic("Saga - Chapter 0001.cbz");

        // Queued by an administrator, the delivery is still Alice's.
        var queued = Assert.Single((await _email.QueueFilesAsync(
            Admin, new[] { file }, device.Id, null, EmailDeliverySource.Manual, skipAlreadyDelivered: false)).Queued);
        Assert.Equal("alice", queued.OwnerUserId);

        var alices = await _email.GetDeliveriesPageAsync(Alice, 0, 50);
        Assert.Equal(new[] { queued.Id }, alices.Deliveries.Select(d => d.Id));
        Assert.Equal(1, alices.Total);
        Assert.Equal(1, alices.PendingCount);

        var bobs = await _email.GetDeliveriesPageAsync(Bob, 0, 50);
        Assert.Empty(bobs.Deliveries);
        Assert.Equal(0, bobs.Total);
        Assert.Equal(0, bobs.PendingCount);
        Assert.Empty(await _email.GetRecentDeliveriesAsync(Bob, 50));

        Assert.Empty((await _email.GetDeliveriesPageAsync(Admin, 0, 50)).Deliveries);
        Assert.Single((await _email.GetDeliveriesPageAsync(AdminListingEveryone, 0, 50)).Deliveries);
    }

    [Fact]
    public async Task AnotherUsersDelivery_CannotBeCancelled()
    {
        var device = await _devices.CreateDeviceAsync(Alice, "Alice's Kindle", "alice@kindle.com", null);
        var queued = Assert.Single((await _email.QueueFilesAsync(
            Alice,
            new[] { CreateComic("Saga - Chapter 0001.cbz") },
            device.Id,
            null,
            EmailDeliverySource.Manual,
            skipAlreadyDelivered: false)).Queued);

        var single = await _email.CancelDeliveryAsync(Bob, queued.Id);
        Assert.Equal(EmailDeliveryCancelOutcome.NotFound, single.Outcome);
        Assert.Null(single.Delivery);

        var bulk = await _email.CancelPendingDeliveriesAsync(Bob);
        Assert.Equal(0, bulk.Cancelled);
        Assert.Equal(EmailDeliveryStatus.Pending, await GetStatusAsync(queued.Id));

        Assert.Equal(1, (await _email.CancelPendingDeliveriesAsync(Alice)).Cancelled);
        Assert.Equal(EmailDeliveryStatus.Cancelled, await GetStatusAsync(queued.Id));
    }

    [Fact]
    public async Task AutomaticDeliveries_AreQueuedForTheSubscriptionsOwner()
    {
        var device = await _devices.CreateDeviceAsync(Alice, "Alice's Kindle", "alice@kindle.com", null);
        await _devices.UpsertSubscriptionAsync(Alice, "Saga", device.Id, null, enabled: true);
        var file = CreateComic("Saga - Chapter 0002.cbz", folder: "Saga");

        Assert.Equal(1, await _email.QueueAutoSendAsync(file, "Saga"));

        var delivery = Assert.Single((await _email.GetDeliveriesPageAsync(Alice, 0, 50)).Deliveries);
        Assert.Equal(EmailDeliverySource.Auto, delivery.Source);
        Assert.Equal("alice", delivery.OwnerUserId);
        Assert.Empty((await _email.GetDeliveriesPageAsync(Bob, 0, 50)).Deliveries);
    }

    [Fact]
    public async Task CondensedBooks_IgnoreTheHistoryAndDefaultsOfAnotherUsersDevice()
    {
        var device = await _devices.CreateDeviceAsync(Alice, "Alice's Kindle", "alice@kindle.com", EmailDeliveryFormat.Azw3);
        var first = CreateComic("Saga - Chapter 0001.cbz", folder: "Saga");
        var second = CreateComic("Saga - Chapter 0002.cbz", folder: "Saga");
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.ComicEmailDeliveries.Add(new ComicEmailDeliveryEntity
            {
                OwnerUserId = "alice",
                FilePath = first,
                DeviceId = device.Id,
                DeviceName = device.Name,
                DeviceEmail = device.EmailAddress,
                Status = EmailDeliveryStatus.Sent
            });
            await db.SaveChangesAsync();
        }

        var alicesPlan = await _email.PlanCondensedDeliveryAsync(
            Alice, new[] { first, second }, EmailCondenseMode.All, null, deviceId: device.Id, skipAlreadyDelivered: true);
        Assert.Equal(1, alicesPlan.TotalIssues);
        Assert.Contains(first, alicesPlan.Skipped.Keys);

        // Which issues went to Alice's Kindle is not Bob's to learn.
        var bobsPlan = await _email.PlanCondensedDeliveryAsync(
            Bob, new[] { first, second }, EmailCondenseMode.All, null, deviceId: device.Id, skipAlreadyDelivered: true);
        Assert.Equal(2, bobsPlan.TotalIssues);
        Assert.Empty(bobsPlan.Skipped);

        Assert.Equal(EmailDeliveryFormat.Azw3, await _email.ResolveCondensedFormatAsync(Alice, null, device.Id, CancellationToken.None));
        Assert.Equal(EmailDeliveryFormat.Epub, await _email.ResolveCondensedFormatAsync(Bob, null, device.Id, CancellationToken.None));
    }

    private async Task<string> GetStatusAsync(int deliveryId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return (await db.ComicEmailDeliveries.AsNoTracking().FirstAsync(d => d.Id == deliveryId)).Status;
    }

    private string CreateComic(string fileName, string? folder = null)
    {
        var directory = folder is null ? _watchedDir : Path.Join(_watchedDir, folder);
        Directory.CreateDirectory(directory);
        var path = Path.Join(directory, fileName);
        File.WriteAllBytes(path, new byte[16]);
        return path;
    }
}
