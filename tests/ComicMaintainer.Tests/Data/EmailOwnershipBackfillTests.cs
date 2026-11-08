using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Models.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ComicMaintainer.Tests.Data;

/// <summary>
/// The migration that gives e-reader devices and deliveries owners hands the
/// rows saved before that to the first administrator, so nothing a library
/// already relied on disappears and nothing becomes visible to other users.
/// </summary>
public sealed class EmailOwnershipBackfillTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private DbContextOptions<ComicMaintainerDbContext> _options = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _options = new DbContextOptionsBuilder<ComicMaintainerDbContext>().UseSqlite(_connection).Options;

        // The full migration chain, including the one that runs the backfill.
        await using var db = new ComicMaintainerDbContext(_options);
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task UnownedRows_GoToTheFirstActiveAdministrator()
    {
        await using (var db = new ComicMaintainerDbContext(_options))
        {
            db.Roles.AddRange(
                new ApplicationRole { Id = "role-admin", Name = "Admin", NormalizedName = "ADMIN" },
                new ApplicationRole { Id = "role-user", Name = "User", NormalizedName = "USER" });
            db.Users.AddRange(
                User("plain-user", new DateTime(2019, 1, 1)),
                User("retired-admin", new DateTime(2020, 1, 1), isActive: false),
                User("first-admin", new DateTime(2021, 1, 1)),
                User("later-admin", new DateTime(2022, 1, 1)));
            db.UserRoles.AddRange(
                new IdentityUserRole<string> { UserId = "plain-user", RoleId = "role-user" },
                new IdentityUserRole<string> { UserId = "retired-admin", RoleId = "role-admin" },
                new IdentityUserRole<string> { UserId = "first-admin", RoleId = "role-admin" },
                new IdentityUserRole<string> { UserId = "later-admin", RoleId = "role-admin" });

            db.EreaderDevices.AddRange(
                new EreaderDeviceEntity { Id = 1, Name = "Shared Kindle", EmailAddress = "shared@kindle.com" },
                new EreaderDeviceEntity { Id = 2, Name = "Own Kobo", EmailAddress = "own@kobo.com", OwnerUserId = "plain-user" });
            db.ComicEmailDeliveries.AddRange(
                Delivery(1, deviceId: 1),
                Delivery(2, deviceId: 2),
                // Its device has been deleted since.
                Delivery(3, deviceId: 99),
                Delivery(4, deviceId: 2, ownerUserId: "plain-user"));
            await db.SaveChangesAsync();

            await db.Database.ExecuteSqlRawAsync(EmailOwnershipBackfill.Sql);
        }

        await using var check = new ComicMaintainerDbContext(_options);
        var devices = await check.EreaderDevices.AsNoTracking().OrderBy(d => d.Id).ToListAsync();
        Assert.Equal(new[] { "first-admin", "plain-user" }, devices.Select(d => d.OwnerUserId));

        var deliveries = await check.ComicEmailDeliveries.AsNoTracking().OrderBy(d => d.Id).ToListAsync();
        Assert.Equal(
            new[] { "first-admin", "plain-user", "first-admin", "plain-user" },
            deliveries.Select(d => d.OwnerUserId));
    }

    [Fact]
    public async Task WithoutAnAdministrator_TheRowsStayUnownedForAdministratorsOnly()
    {
        await using (var db = new ComicMaintainerDbContext(_options))
        {
            db.Users.Add(User("plain-user", new DateTime(2019, 1, 1)));
            db.EreaderDevices.Add(new EreaderDeviceEntity { Id = 1, Name = "Shared Kindle", EmailAddress = "shared@kindle.com" });
            db.ComicEmailDeliveries.Add(Delivery(1, deviceId: 1));
            await db.SaveChangesAsync();

            await db.Database.ExecuteSqlRawAsync(EmailOwnershipBackfill.Sql);
        }

        await using var check = new ComicMaintainerDbContext(_options);
        Assert.Null((await check.EreaderDevices.AsNoTracking().SingleAsync()).OwnerUserId);
        Assert.Null((await check.ComicEmailDeliveries.AsNoTracking().SingleAsync()).OwnerUserId);
    }

    private static ApplicationUser User(string id, DateTime createdAt, bool isActive = true) => new()
    {
        Id = id,
        UserName = id,
        NormalizedUserName = id.ToUpperInvariant(),
        CreatedAt = createdAt,
        IsActive = isActive
    };

    private static ComicEmailDeliveryEntity Delivery(int id, int deviceId, string? ownerUserId = null) => new()
    {
        Id = id,
        OwnerUserId = ownerUserId,
        FilePath = $"/library/Saga {id}.cbz",
        DeviceId = deviceId,
        DeviceName = "Device",
        DeviceEmail = "device@example.com",
        Status = EmailDeliveryStatus.Sent
    };
}
