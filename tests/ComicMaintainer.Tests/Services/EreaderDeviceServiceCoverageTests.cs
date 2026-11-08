using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Covers the device lookups and the update/validation paths of
/// <see cref="EreaderDeviceService"/> not exercised by <see cref="EreaderDeviceServiceTests"/>.
/// </summary>
public class EreaderDeviceServiceCoverageTests
{
    private readonly EreaderDeviceService _service;

    public EreaderDeviceServiceCoverageTests()
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
    public async Task GetDeviceAsync_ReturnsTheStoredDevice()
    {
        var created = await _service.CreateDeviceAsync(TestEmailScopes.Everyone, "Kobo", "kobo@example.com", EmailDeliveryFormat.Epub);

        var device = await _service.GetDeviceAsync(TestEmailScopes.Everyone, created.Id);

        Assert.NotNull(device);
        Assert.Equal(created.Id, device!.Id);
        Assert.Equal("Kobo", device.Name);
        Assert.Equal("kobo@example.com", device.EmailAddress);
        Assert.Equal(EmailDeliveryFormat.Epub, device.DeliveryFormat);
    }

    [Fact]
    public async Task GetDeviceAsync_WithUnknownId_ReturnsNull()
    {
        Assert.Null(await _service.GetDeviceAsync(TestEmailScopes.Everyone, 12345));
    }

    [Fact]
    public async Task UpdateDeviceAsync_ChangesTheAddressAndFormat()
    {
        var device = await _service.CreateDeviceAsync(TestEmailScopes.Everyone, "Kindle", "kindle@kindle.com", null);

        var updated = await _service.UpdateDeviceAsync(TestEmailScopes.Everyone, device.Id, null, "  New@Kindle.com ", EmailDeliveryFormat.Azw3);

        Assert.NotNull(updated);
        Assert.Equal("Kindle", updated!.Name);
        Assert.Equal("New@Kindle.com", updated.EmailAddress);
        Assert.Equal(EmailDeliveryFormat.Azw3, updated.DeliveryFormat);

        var stored = await _service.GetDeviceAsync(TestEmailScopes.Everyone, device.Id);
        Assert.Equal(updated.EmailAddress, stored!.EmailAddress);
        Assert.Equal(EmailDeliveryFormat.Azw3, stored.DeliveryFormat);
    }

    [Fact]
    public async Task UpdateDeviceAsync_KeepingItsOwnAddress_IsNotAConflict()
    {
        var device = await _service.CreateDeviceAsync(TestEmailScopes.Everyone, "Kindle", "kindle@kindle.com", null);

        var updated = await _service.UpdateDeviceAsync(TestEmailScopes.Everyone, device.Id, null, "kindle@kindle.com", null);

        Assert.Equal("kindle@kindle.com", updated!.EmailAddress);
    }

    [Fact]
    public async Task UpdateDeviceAsync_RejectsAnAddressUsedByAnotherDevice()
    {
        await _service.CreateDeviceAsync(TestEmailScopes.Everyone, "Kindle", "kindle@kindle.com", null);
        var other = await _service.CreateDeviceAsync(TestEmailScopes.Everyone, "Kobo", "kobo@example.com", null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.UpdateDeviceAsync(TestEmailScopes.Everyone, other.Id, null, "kindle@kindle.com", null));

        Assert.Contains("kindle@kindle.com", ex.Message);
        Assert.Equal("kobo@example.com", (await _service.GetDeviceAsync(TestEmailScopes.Everyone, other.Id))!.EmailAddress);
    }

    [Fact]
    public async Task UpdateDeviceAsync_RejectsAnInvalidAddress()
    {
        var device = await _service.CreateDeviceAsync(TestEmailScopes.Everyone, "Kindle", "kindle@kindle.com", null);

        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.UpdateDeviceAsync(TestEmailScopes.Everyone, device.Id, null, "not-an-email", null));
    }

    [Fact]
    public async Task UpdateDeviceAsync_RejectsAnUnknownFormat()
    {
        var device = await _service.CreateDeviceAsync(TestEmailScopes.Everyone, "Kindle", "kindle@kindle.com", EmailDeliveryFormat.Epub);

        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.UpdateDeviceAsync(TestEmailScopes.Everyone, device.Id, null, null, "pdf"));

        Assert.Equal(EmailDeliveryFormat.Epub, (await _service.GetDeviceAsync(TestEmailScopes.Everyone, device.Id))!.DeliveryFormat);
    }

    [Fact]
    public async Task UpdateDeviceAsync_RejectsABlankName()
    {
        var device = await _service.CreateDeviceAsync(TestEmailScopes.Everyone, "Kindle", "kindle@kindle.com", null);

        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.UpdateDeviceAsync(TestEmailScopes.Everyone, device.Id, "   ", null, null));
    }

    [Fact]
    public async Task CreateDeviceAsync_RejectsABlankName()
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => _service.CreateDeviceAsync(TestEmailScopes.Everyone, "  ", "kindle@kindle.com", null));

        Assert.Equal("name", ex.ParamName);
        Assert.Empty(await _service.GetDevicesAsync(TestEmailScopes.Everyone));
    }

    [Fact]
    public async Task CreateDeviceAsync_TruncatesNamesLongerThan128Characters()
    {
        var longName = "  " + new string('k', 200) + "  ";

        var device = await _service.CreateDeviceAsync(TestEmailScopes.Everyone, longName, "kindle@kindle.com", null);

        Assert.Equal(new string('k', 128), device.Name);
    }

    [Fact]
    public async Task UpdateDeviceAsync_TruncatesNamesLongerThan128Characters()
    {
        var device = await _service.CreateDeviceAsync(TestEmailScopes.Everyone, "Kindle", "kindle@kindle.com", null);

        var updated = await _service.UpdateDeviceAsync(TestEmailScopes.Everyone, device.Id, new string('n', 129), null, null);

        Assert.Equal(new string('n', 128), updated!.Name);
    }

    [Fact]
    public async Task DeleteDeviceAsync_WithUnknownId_ReturnsFalse()
    {
        var device = await _service.CreateDeviceAsync(TestEmailScopes.Everyone, "Kindle", "kindle@kindle.com", null);

        Assert.False(await _service.DeleteDeviceAsync(TestEmailScopes.Everyone, device.Id + 1000));
        Assert.Single(await _service.GetDevicesAsync(TestEmailScopes.Everyone));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task UpsertSubscriptionAsync_RejectsABlankSeriesTitle(string title)
    {
        var device = await _service.CreateDeviceAsync(TestEmailScopes.Everyone, "Kindle", "kindle@kindle.com", null);

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => _service.UpsertSubscriptionAsync(TestEmailScopes.Everyone, title, device.Id, null, true));

        Assert.Equal("seriesTitle", ex.ParamName);
    }
}
