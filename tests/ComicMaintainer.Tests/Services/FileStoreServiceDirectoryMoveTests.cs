using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Reader.Models;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Moving or deleting a whole directory moves or removes every tracked row under it, with the
/// file state, user metadata edits and every user's read state, on a real SQLite database.
/// </summary>
public sealed class FileStoreServiceDirectoryMoveTests : IAsyncLifetime
{
    private static readonly DateTime T0 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly string _library = Path.Join(Path.GetTempPath(), $"fss_dirmove_{Guid.NewGuid():N}");
    private ServiceProvider _provider = null!;
    private IDbContextFactory<ComicMaintainerDbContext> _factory = null!;

    private string OldDir => Path.Join(_library, "One Piece");
    private string NewDir => Path.Join(_library, "One Piece (2023)");

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

    [Fact]
    public async Task MoveDirectoryAsync_MovesEveryRowWithItsStateUserEditsAndReadState()
    {
        var c1 = Path.Join(OldDir, "One Piece - Chapter 0001.cbz");
        var c2 = Path.Join(OldDir, "Extras", "One Piece - Chapter 0002.cbz");
        var sibling = Path.Join(_library, "One Piece2", "One Piece2 - Chapter 0001.cbz");
        var sameNameFile = Path.Join(_library, "One Piece.cbz");
        await SeedAsync(db =>
        {
            var edited = Row(c1, processed: true);
            edited.Metadata = new ComicMetadata { Series = "One Piece (User)", IsUserEdited = true, UserLockedFieldsMask = 5 };
            edited.MetadataVersion = 3;
            edited.MetadataSource = FileMetadataSource.UserEdit.ToString();
            db.ComicFiles.AddRange(edited, Row(c2, processed: false), Row(sibling, processed: true), Row(sameNameFile, processed: true));
            db.UserFileReadStatuses.AddRange(Status("alice", c1, isRead: true, page: 12), Status("bob", c2, isRead: false, page: 3), Status("alice", sibling, isRead: true, page: 1));
            db.ReadingProgresses.Add(Progress("alice", c1, current: 12, total: 20));
        });
        var service = CreateService();
        await service.InitializeFromDatabaseAsync();

        var moved = await service.MoveDirectoryAsync(OldDir, NewDir);

        Assert.Equal(2, moved);
        var newC1 = Path.Join(NewDir, "One Piece - Chapter 0001.cbz");
        var newC2 = Path.Join(NewDir, "Extras", "One Piece - Chapter 0002.cbz");
        await using var db = await _factory.CreateDbContextAsync();
        var rows = await db.ComicFiles.AsNoTracking().ToDictionaryAsync(r => r.FilePath);
        Assert.Equal(new[] { newC1, newC2, sameNameFile, sibling }.Order(StringComparer.Ordinal), rows.Keys.Order(StringComparer.Ordinal));

        var row1 = rows[newC1];
        Assert.True(row1.IsRenamed && row1.IsNormalized);
        Assert.Equal(NewDir, row1.Directory);
        Assert.Equal("One Piece - Chapter 0001.cbz", row1.FileName);
        Assert.True(row1.Metadata!.IsUserEdited);
        Assert.Equal(5, row1.Metadata.UserLockedFieldsMask);
        Assert.Equal("One Piece (User)", row1.Metadata.Series);
        Assert.Equal(3, row1.MetadataVersion);
        Assert.Equal(Path.Join(NewDir, "Extras"), rows[newC2].Directory);
        Assert.False(rows[newC2].IsRenamed);

        var statuses = await db.UserFileReadStatuses.AsNoTracking().ToListAsync();
        Assert.Contains(statuses, s => s.UserId == "alice" && s.FilePath == newC1 && s.IsRead && s.CurrentPage == 12);
        Assert.Contains(statuses, s => s.UserId == "bob" && s.FilePath == newC2);
        Assert.Contains(statuses, s => s.UserId == "alice" && s.FilePath == sibling);
        Assert.DoesNotContain(statuses, s => s.FilePath.StartsWith(OldDir + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        Assert.Equal(newC1, Assert.Single(await db.ReadingProgresses.AsNoTracking().ToListAsync()).ContentId);

        // The in-memory store and the per-user read state follow the move too.
        var alice = CreateService("alice");
        await alice.InitializeFromDatabaseAsync();
        Assert.True((await alice.GetAllFilesAsync()).Single(f => f.FilePath == newC1).IsRead);
        Assert.True(await service.IsFileProcessedAsync(newC1));
        Assert.False(await service.FileExistsAsync(c1));
    }

    [Fact]
    public async Task MoveDirectoryAsync_RowAlreadyAtTheDestination_MovedRowWinsAndAbsorbsIt()
    {
        var oldPath = Path.Join(OldDir, "One Piece - Chapter 0001.cbz");
        var newPath = Path.Join(NewDir, "One Piece - Chapter 0001.cbz");
        await SeedAsync(db =>
        {
            var original = Row(oldPath, processed: true);
            original.CreatedAt = T0;
            original.Metadata = new ComicMetadata { Series = "One Piece", IsUserEdited = true };
            var stub = Row(newPath, processed: false);
            stub.IsDuplicate = true;
            stub.CreatedAt = T0.AddDays(5);
            db.ComicFiles.AddRange(original, stub);
            db.UserFileReadStatuses.AddRange(Status("alice", oldPath, isRead: true, page: 9), Status("alice", newPath, isRead: false, page: 1));
        });
        var service = CreateService();
        await service.InitializeFromDatabaseAsync();

        Assert.Equal(1, await service.MoveDirectoryAsync(OldDir, NewDir));

        await using var db = await _factory.CreateDbContextAsync();
        var row = Assert.Single(await db.ComicFiles.AsNoTracking().ToListAsync());
        Assert.Equal(newPath, row.FilePath);
        Assert.True(row.IsProcessed);
        Assert.True(row.IsDuplicate);
        Assert.Equal(T0, row.CreatedAt);
        Assert.True(row.Metadata!.IsUserEdited);
        // As for a single rename, a read-status row already at the destination wins.
        var status = Assert.Single(await db.UserFileReadStatuses.AsNoTracking().ToListAsync());
        Assert.Equal(newPath, status.FilePath);
        Assert.Equal(1, status.CurrentPage);
    }

    [Fact]
    public async Task MoveDirectoryAsync_NothingTracked_ReturnsZero()
    {
        var service = CreateService();
        await service.InitializeFromDatabaseAsync();

        Assert.Equal(0, await service.MoveDirectoryAsync(OldDir, NewDir));
    }

    [Fact]
    public async Task MoveDirectoryAsync_DestinationOutsideTheLibrary_LeavesTheRows()
    {
        var path = Path.Join(OldDir, "One Piece - Chapter 0001.cbz");
        await SeedAsync(db => db.ComicFiles.Add(Row(path, processed: true)));
        var service = CreateService();
        await service.InitializeFromDatabaseAsync();

        Assert.Equal(0, await service.MoveDirectoryAsync(OldDir, Path.Join(Path.GetTempPath(), $"elsewhere_{Guid.NewGuid():N}")));

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(path, Assert.Single(await db.ComicFiles.AsNoTracking().ToListAsync()).FilePath);
    }

    [Fact]
    public async Task RemoveDirectoryAsync_RemovesEveryRowUnderTheDirectoryAndNothingElse()
    {
        var inside = Path.Join(OldDir, "One Piece - Chapter 0001.cbz");
        var nested = Path.Join(OldDir, "Extras", "Art.cbz");
        var sibling = Path.Join(_library, "One Piece2", "One Piece2 - Chapter 0001.cbz");
        await SeedAsync(db => db.ComicFiles.AddRange(Row(inside, true), Row(nested, false), Row(sibling, true)));
        var service = CreateService();
        await service.InitializeFromDatabaseAsync();

        Assert.Equal(2, await service.RemoveDirectoryAsync(OldDir));

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(sibling, Assert.Single(await db.ComicFiles.AsNoTracking().ToListAsync()).FilePath);
        Assert.False(await service.FileExistsAsync(inside));
        Assert.True(await service.FileExistsAsync(sibling));
    }

    [Fact]
    public async Task RemoveDirectoryAsync_PathOfAFileOrNothing_RemovesNothing()
    {
        var file = Path.Join(_library, "Saga.cbz");
        await SeedAsync(db => db.ComicFiles.Add(Row(file, true)));
        var service = CreateService();
        await service.InitializeFromDatabaseAsync();

        Assert.Equal(0, await service.RemoveDirectoryAsync(Path.Join(_library, "Saga")));
        Assert.Equal(0, await service.RemoveDirectoryAsync("  "));
        Assert.True(await service.FileExistsAsync(file));
    }

    private FileStoreService CreateService(string? userId = null) =>
        new(
            new TestOptionsMonitor<AppSettings>(new AppSettings { WatchedDirectory = _library, DuplicateDirectory = string.Empty }),
            NullLogger<FileStoreService>.Instance,
            _factory,
            userContext: new TestUserContextAccessor(userId));

    private async Task SeedAsync(Action<ComicMaintainerDbContext> seed)
    {
        await using var db = await _factory.CreateDbContextAsync();
        seed(db);
        await db.SaveChangesAsync();
    }

    private static ComicFileEntity Row(string path, bool processed) => new()
    {
        FilePath = path,
        FileName = Path.GetFileName(path),
        Directory = Path.GetDirectoryName(path)!,
        FileSize = 10,
        LastModified = T0,
        IsRenamed = processed,
        IsNormalized = processed,
        IsProcessed = processed,
        CreatedAt = T0.AddDays(1),
        UpdatedAt = T0.AddDays(1)
    };

    private static UserFileReadStatusEntity Status(string userId, string path, bool isRead, int page) => new()
    {
        UserId = userId,
        FilePath = path,
        IsRead = isRead,
        CurrentPage = page,
        LastReadDate = T0
    };

    private static ReadingProgressEntity Progress(string userId, string path, int current, int total) => new()
    {
        UserId = userId,
        ContentId = path,
        CurrentPage = current,
        TotalPages = total,
        PercentComplete = 100.0 * current / total,
        LastReadAt = T0
    };
}
