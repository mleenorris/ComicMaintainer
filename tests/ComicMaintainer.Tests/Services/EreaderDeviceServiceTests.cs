using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Core.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace ComicMaintainer.Tests.Services;

public class EreaderDeviceServiceTests
{
    private readonly EreaderDeviceService _service;

    public EreaderDeviceServiceTests()
    {
        var dbName = $"TestDb_{Guid.NewGuid()}";
        var services = new ServiceCollection();
        services.AddDbContext<ComicMaintainerDbContext>(opt => opt.UseInMemoryDatabase(dbName));
        services.AddDbContextFactory<ComicMaintainerDbContext>(opt => opt.UseInMemoryDatabase(dbName));
        var dbFactory = services.BuildServiceProvider().GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>();

        var seriesCache = new Mock<ISeriesMetadataCacheService>();
        seriesCache.Setup(s => s.NormalizeKey(It.IsAny<string?>()))
            .Returns((string? value) => string.IsNullOrWhiteSpace(value)
                ? "unknown-series"
                : new string(value.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-'));

        _service = new EreaderDeviceService(
            dbFactory,
            seriesCache.Object,
            new Mock<ILogger<EreaderDeviceService>>().Object);
    }

    [Fact]
    public async Task CreateDeviceAsync_DefaultsToOriginalFormat()
    {
        var device = await _service.CreateDeviceAsync("Kindle", " kindle@kindle.com ", null);

        Assert.Equal("Kindle", device.Name);
        Assert.Equal("kindle@kindle.com", device.EmailAddress);
        Assert.Equal(EmailDeliveryFormat.Original, device.DeliveryFormat);
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("missing@domain")]
    [InlineData("two addresses@x.com,b@x.com")]
    [InlineData("")]
    public async Task CreateDeviceAsync_RejectsInvalidAddresses(string address)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateDeviceAsync("Device", address, null));
    }

    [Fact]
    public async Task CreateDeviceAsync_RejectsUnknownFormat()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.CreateDeviceAsync("Device", "a@b.com", "pdf"));
    }

    [Fact]
    public async Task CreateDeviceAsync_RejectsDuplicateAddress()
    {
        await _service.CreateDeviceAsync("Kindle", "kindle@kindle.com", null);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.CreateDeviceAsync("Kindle 2", "kindle@kindle.com", null));
    }

    [Fact]
    public async Task UpdateDeviceAsync_LeavesNullFieldsUnchanged()
    {
        var device = await _service.CreateDeviceAsync("Kindle", "kindle@kindle.com", EmailDeliveryFormat.Epub);

        var updated = await _service.UpdateDeviceAsync(device.Id, "Living Room Kindle", null, null);

        Assert.NotNull(updated);
        Assert.Equal("Living Room Kindle", updated!.Name);
        Assert.Equal("kindle@kindle.com", updated.EmailAddress);
        Assert.Equal(EmailDeliveryFormat.Epub, updated.DeliveryFormat);
    }

    [Fact]
    public async Task UpdateDeviceAsync_WithUnknownId_ReturnsNull()
    {
        Assert.Null(await _service.UpdateDeviceAsync(999, "x", null, null));
    }

    [Fact]
    public async Task DeleteDeviceAsync_RemovesDeviceAndItsSubscriptions()
    {
        var device = await _service.CreateDeviceAsync("Kindle", "kindle@kindle.com", null);
        await _service.UpsertSubscriptionAsync("My Series", device.Id, null, true);

        Assert.True(await _service.DeleteDeviceAsync(device.Id));
        Assert.Empty(await _service.GetDevicesAsync());
        Assert.Empty(await _service.GetSubscriptionsAsync());
    }

    [Fact]
    public async Task UpsertSubscriptionAsync_IsIdempotentPerSeriesAndDevice()
    {
        var device = await _service.CreateDeviceAsync("Kindle", "kindle@kindle.com", null);

        var first = await _service.UpsertSubscriptionAsync("My Series", device.Id, EmailDeliveryFormat.Epub, true);
        var second = await _service.UpsertSubscriptionAsync("my series!", device.Id, EmailDeliveryFormat.Original, false);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first!.Id, second!.Id);
        Assert.Equal(EmailDeliveryFormat.Original, second.DeliveryFormat);
        Assert.False(second.Enabled);
        Assert.Single(await _service.GetSubscriptionsAsync());
    }

    [Fact]
    public async Task UpsertSubscriptionAsync_DefaultsToDeviceFormat()
    {
        var device = await _service.CreateDeviceAsync("Kindle", "kindle@kindle.com", null);

        var subscription = await _service.UpsertSubscriptionAsync("My Series", device.Id, null, true);

        Assert.Equal(EmailDeliveryFormat.Device, subscription!.DeliveryFormat);
    }

    [Fact]
    public async Task UpsertSubscriptionAsync_WithUnknownDevice_ReturnsNull()
    {
        Assert.Null(await _service.UpsertSubscriptionAsync("My Series", 42, null, true));
    }

    [Fact]
    public async Task GetSubscriptionsAsync_FiltersBySeriesTitle()
    {
        var device = await _service.CreateDeviceAsync("Kindle", "kindle@kindle.com", null);
        await _service.UpsertSubscriptionAsync("Series A", device.Id, null, true);
        await _service.UpsertSubscriptionAsync("Series B", device.Id, null, true);

        var filtered = await _service.GetSubscriptionsAsync("series a");

        Assert.Equal("Series A", Assert.Single(filtered).SeriesTitle);
    }

    [Fact]
    public async Task DeleteSubscriptionAsync_RemovesOnlyThatSubscription()
    {
        var device = await _service.CreateDeviceAsync("Kindle", "kindle@kindle.com", null);
        var a = await _service.UpsertSubscriptionAsync("Series A", device.Id, null, true);
        await _service.UpsertSubscriptionAsync("Series B", device.Id, null, true);

        Assert.True(await _service.DeleteSubscriptionAsync(a!.Id));
        Assert.False(await _service.DeleteSubscriptionAsync(a.Id));
        Assert.Equal("Series B", Assert.Single(await _service.GetSubscriptionsAsync()).SeriesTitle);
    }

    [Theory]
    [InlineData("user@example.com", true)]
    [InlineData("user+tag@sub.example.co.uk", true)]
    [InlineData("Name <user@example.com>", false)]
    [InlineData("user@localhost", false)]
    [InlineData("user example@x.com", false)]
    [InlineData(null, false)]
    public void EmailAddressUtils_ValidatesBareMailboxes(string? value, bool expected)
    {
        Assert.Equal(expected, EmailAddressUtils.IsValid(value));
    }
}
