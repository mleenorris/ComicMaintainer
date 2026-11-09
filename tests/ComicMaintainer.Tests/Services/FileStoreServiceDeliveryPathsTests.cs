using System.Text.Json;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Core.Utilities;
using ComicMaintainer.Tests.Helpers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// A delivery keeps the path it was queued for, and a condensed one the path of every issue.
/// When a library file or folder moves those paths follow it, so a re-send finds the file (and
/// never sends whatever later took its name), on a real SQLite database.
/// </summary>
public sealed class FileStoreServiceDeliveryPathsTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly string _library = Path.Join(Path.GetTempPath(), $"fss_delivery_{Guid.NewGuid():N}");
    private ServiceProvider _provider = null!;
    private IDbContextFactory<ComicMaintainerDbContext> _factory = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_library);
        await _connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddDbContextFactory<ComicMaintainerDbContext>(o => o.UseSqlite(_connection));
        _provider = services.BuildServiceProvider();
        _factory = _provider.GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>();
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
        TestIO.TryDeleteDirectory(_library);
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("sent")]
    [InlineData("failed")]
    [InlineData("cancelled")]
    public async Task UpdateFilePathAsync_RepointsTheDeliveryWhateverItsStatus(string status)
    {
        var (oldPath, newPath) = await TrackedMoveAsync("Saga 001.cbz", "Saga - Chapter 0001.cbz");
        await SeedDeliveryAsync(oldPath, status);

        await Service().UpdateFilePathAsync(oldPath, newPath);

        var delivery = Assert.Single(await DeliveriesAsync());
        Assert.Equal(newPath, delivery.FilePath);
        Assert.Equal(status, delivery.Status);
    }

    [Fact]
    public async Task UpdateFilePathAsync_LeavesTheDeliveriesOfOtherFilesAlone()
    {
        var (oldPath, newPath) = await TrackedMoveAsync("Saga 001.cbz", "Saga - Chapter 0001.cbz");
        // Same name with a longer stem, and with a different extension.
        var similar = Path.Join(_library, "Saga 0010.cbz");
        var otherExtension = Path.Join(_library, "Saga 001.cbz.bak");
        await SeedDeliveryAsync(similar);
        await SeedDeliveryAsync(otherExtension);

        await Service().UpdateFilePathAsync(oldPath, newPath);

        Assert.Equal(new[] { similar, otherExtension }, (await DeliveriesAsync()).Select(d => d.FilePath));
    }

    [Fact]
    public async Task UpdateFilePathAsync_RepointsEveryPlaceTheFileAppearsInACondensedDelivery()
    {
        var (oldPath, newPath) = await TrackedMoveAsync("Saga 001.cbz", "Saga - Chapter 0001.cbz");
        var second = Path.Join(_library, "Saga 002.cbz");
        await SeedDeliveryAsync(oldPath, condensedMembers: new[] { oldPath, second });
        await SeedDeliveryAsync(second, condensedMembers: new[] { second, oldPath });

        await Service().UpdateFilePathAsync(oldPath, newPath);

        var deliveries = await DeliveriesAsync();
        Assert.Equal(newPath, deliveries[0].FilePath);
        Assert.Equal(new[] { newPath, second }, CondensedFilePathList.Parse(deliveries[0].CondensedFilePaths));
        Assert.Equal(second, deliveries[1].FilePath);
        Assert.Equal(new[] { second, newPath }, CondensedFilePathList.Parse(deliveries[1].CondensedFilePaths));
    }

    /// <summary>
    /// The member list is JSON with the serializer's default escaping, which turns these
    /// characters into \uXXXX, so looking for the plain path would find nothing.
    /// </summary>
    [Theory]
    [InlineData("Bob's Saga 001.cbz")]
    [InlineData("Cafe & Co 001.cbz")]
    [InlineData("ワンピース 001.cbz")]
    public async Task UpdateFilePathAsync_RepointsCondensedMembersWhoseNamesAreEscapedInJson(string name)
    {
        var (oldPath, newPath) = await TrackedMoveAsync(name, "Renamed.cbz");
        var second = Path.Join(_library, "Other 002.cbz");
        await SeedDeliveryAsync(oldPath, condensedMembers: new[] { oldPath, second });

        await Service().UpdateFilePathAsync(oldPath, newPath);

        var delivery = Assert.Single(await DeliveriesAsync());
        Assert.Equal(new[] { newPath, second }, CondensedFilePathList.Parse(delivery.CondensedFilePaths));
    }

    [Fact]
    public async Task UpdateFilePathAsync_NeitherStoreKnowsTheOldPath_StillRepointsTheHistory()
    {
        var oldPath = Path.Join(_library, "Saga 001.cbz");
        var newPath = Path.Join(_library, "Saga - Chapter 0001.cbz");
        var other = Path.Join(_library, "Saga 002.cbz");
        File.WriteAllText(newPath, "comic");
        await SeedDeliveryAsync(oldPath);
        await SeedDeliveryAsync(other, condensedMembers: [oldPath, other]);

        await Service().UpdateFilePathAsync(oldPath, newPath);

        var deliveries = await DeliveriesAsync();
        Assert.Equal(newPath, deliveries[0].FilePath);
        Assert.Equal(new[] { newPath, other }, CondensedFilePathList.Parse(deliveries[1].CondensedFilePaths));
        await using var db = await _factory.CreateDbContextAsync();
        Assert.True(await db.ComicFiles.AnyAsync(f => f.FilePath == newPath));
    }

    [Fact]
    public async Task UpdateFilePathAsync_ComparesACondensedMemberExactly()
    {
        var (oldPath, newPath) = await TrackedMoveAsync("Saga 001.cbz", "Saga - Chapter 0001.cbz");
        var other = Path.Join(_library, "Saga 002.cbz");
        var recased = Path.Join(_library, "SAGA 001.cbz");
        await SeedDeliveryAsync(other, condensedMembers: [recased, other]);

        await Service().UpdateFilePathAsync(oldPath, newPath);

        var delivery = Assert.Single(await DeliveriesAsync());
        Assert.Equal(new[] { recased, other }, CondensedFilePathList.Parse(delivery.CondensedFilePaths));
    }

    [Fact]
    public async Task UpdateFilePathAsync_LeavesACondensedListThatCannotBeReadAlone()
    {
        var (oldPath, newPath) = await TrackedMoveAsync("Saga 001.cbz", "Saga - Chapter 0001.cbz");
        var unreadable = $"not json {oldPath}";
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.ComicEmailDeliveries.Add(NewDelivery(oldPath, "sent", unreadable));
            await db.SaveChangesAsync();
        }

        await Service().UpdateFilePathAsync(oldPath, newPath);

        var delivery = Assert.Single(await DeliveriesAsync());
        Assert.Equal(newPath, delivery.FilePath);
        Assert.Equal(unreadable, delivery.CondensedFilePaths);
    }

    [Fact]
    public async Task MoveDirectoryAsync_RepointsDeliveriesUnderTheDirectoryOnly()
    {
        var oldDir = Path.Join(_library, "One Piece");
        var newDir = Path.Join(_library, "One Piece (2023)");
        var first = Path.Join(oldDir, "One Piece - Chapter 0001.cbz");
        var nested = Path.Join(oldDir, "Extras", "One Piece - Chapter 0002.cbz");
        var sibling = Path.Join(_library, "One Piece2", "One Piece2 - Chapter 0001.cbz");
        await SeedRowsAsync(first, nested, sibling);
        await SeedDeliveryAsync(first, "failed");
        await SeedDeliveryAsync(sibling);
        await SeedDeliveryAsync(first, "sent", new[] { first, nested, sibling });
        var service = Service();
        await service.InitializeFromDatabaseAsync();

        await service.MoveDirectoryAsync(oldDir, newDir);

        var newFirst = Path.Join(newDir, "One Piece - Chapter 0001.cbz");
        var newNested = Path.Join(newDir, "Extras", "One Piece - Chapter 0002.cbz");
        var deliveries = await DeliveriesAsync();
        Assert.Equal(newFirst, deliveries[0].FilePath);
        Assert.Equal(sibling, deliveries[1].FilePath);
        Assert.Equal(newFirst, deliveries[2].FilePath);
        Assert.Equal(new[] { newFirst, newNested, sibling }, CondensedFilePathList.Parse(deliveries[2].CondensedFilePaths));
    }

    [Fact]
    public async Task MoveDirectoryAsync_LeavesACondensedDeliveryWithNothingUnderTheDirectoryUntouched()
    {
        var oldDir = Path.Join(_library, "One Piece");
        var newDir = Path.Join(_library, "One Piece (2023)");
        var tracked = Path.Join(oldDir, "One Piece - Chapter 0001.cbz");
        var other = Path.Join(_library, "Saga", "Saga 001.cbz");
        await SeedRowsAsync(tracked);
        var unchanged = JsonSerializer.Serialize(new[] { other, other + ".2" });
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.ComicEmailDeliveries.Add(NewDelivery(other, "sent", unchanged));
            await db.SaveChangesAsync();
        }

        var service = Service();
        await service.InitializeFromDatabaseAsync();

        await service.MoveDirectoryAsync(oldDir, newDir);

        Assert.Equal(unchanged, Assert.Single(await DeliveriesAsync()).CondensedFilePaths);
    }

    private FileStoreService Service() => TestServices.CreateFileStore(
        new TestOptionsMonitor<AppSettings>(new AppSettings { WatchedDirectory = _library, DuplicateDirectory = string.Empty }),
        NullLogger<FileStoreService>.Instance,
        _factory);

    private async Task<(string OldPath, string NewPath)> TrackedMoveAsync(string oldName, string newName)
    {
        var oldPath = Path.Join(_library, oldName);
        var newPath = Path.Join(_library, newName);
        File.WriteAllText(newPath, "comic");
        await SeedRowsAsync(oldPath);
        return (oldPath, newPath);
    }

    private async Task SeedRowsAsync(params string[] paths)
    {
        await using var db = await _factory.CreateDbContextAsync();
        foreach (var path in paths)
        {
            db.ComicFiles.Add(new ComicFileEntity
            {
                FilePath = path,
                FileName = Path.GetFileName(path),
                Directory = Path.GetDirectoryName(path)!,
            });
        }

        await db.SaveChangesAsync();
    }

    private async Task SeedDeliveryAsync(string filePath, string status = "sent", string[]? condensedMembers = null)
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.ComicEmailDeliveries.Add(NewDelivery(
            filePath,
            status,
            condensedMembers is null ? null : JsonSerializer.Serialize(condensedMembers)));
        await db.SaveChangesAsync();
    }

    private static ComicEmailDeliveryEntity NewDelivery(string filePath, string status, string? condensed) => new()
    {
        FilePath = filePath,
        DeviceId = 1,
        DeviceName = "Kindle",
        DeviceEmail = "reader@example.com",
        DeliveryFormat = "original",
        Status = status,
        Source = "manual",
        CondensedFilePaths = condensed,
    };

    private async Task<List<ComicEmailDeliveryEntity>> DeliveriesAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.ComicEmailDeliveries.AsNoTracking().OrderBy(d => d.Id).ToListAsync();
    }
}
