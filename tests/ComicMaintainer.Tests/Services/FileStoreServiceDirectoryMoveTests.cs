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
        var versionBefore = service.ChangeVersion;

        Assert.Equal(2, await service.RemoveDirectoryAsync(OldDir));

        // Caches keyed on the change version (series library snapshot, library ETag) refresh.
        Assert.NotEqual(versionBefore, service.ChangeVersion);
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
        var versionBefore = service.ChangeVersion;

        Assert.Equal(0, await service.RemoveDirectoryAsync(Path.Join(_library, "Saga")));
        Assert.Equal(0, await service.RemoveDirectoryAsync("  "));
        Assert.True(await service.FileExistsAsync(file));
        Assert.Equal(versionBefore, service.ChangeVersion);
    }

    [Fact]
    public async Task MoveDirectoryAsync_RowAtTheDestinationHasTheOnlyMetadataAndTheOlderDate_MovedRowTakesThem()
    {
        var oldPath = Path.Join(OldDir, "One Piece - Chapter 0001.cbz");
        var newPath = Path.Join(NewDir, "One Piece - Chapter 0001.cbz");
        var editedAt = T0.AddDays(2);
        await SeedAsync(db =>
        {
            var moved = Row(oldPath, processed: false);
            moved.CreatedAt = T0.AddDays(5);
            var atDestination = Row(newPath, processed: true);
            atDestination.CreatedAt = T0;
            atDestination.IsProcessingPending = true;
            atDestination.SeriesMetadataVersion = 9;
            atDestination.Metadata = new ComicMetadata { Series = "One Piece", IsUserEdited = true };
            atDestination.LastDbEditAt = editedAt;
            atDestination.LastWriteAt = editedAt;
            atDestination.MetadataSource = FileMetadataSource.UserEdit.ToString();
            db.ComicFiles.AddRange(moved, atDestination);
        });
        var service = CreateService();
        await service.InitializeFromDatabaseAsync();

        Assert.Equal(1, await service.MoveDirectoryAsync(OldDir, NewDir));

        await using var db = await _factory.CreateDbContextAsync();
        var row = Assert.Single(await db.ComicFiles.AsNoTracking().ToListAsync());
        Assert.Equal(newPath, row.FilePath);
        Assert.Equal(T0, row.CreatedAt);
        Assert.Equal("One Piece", row.Metadata!.Series);
        Assert.True(row.Metadata.IsUserEdited);
        Assert.Equal(editedAt, row.LastDbEditAt);
        Assert.Equal(editedAt, row.LastWriteAt);
        Assert.Equal(FileMetadataSource.UserEdit.ToString(), row.MetadataSource);
        Assert.Equal(9, row.SeriesMetadataVersion);
        Assert.True(row.IsRenamed && row.IsNormalized && row.IsProcessed, "The processed state of either row counts");
        Assert.True(row.IsProcessingPending);
    }

    [Fact]
    public async Task MoveDirectoryAsync_ProgressAlreadyAtTheDestination_MergesToTheFurthestPosition()
    {
        var oldPath = Path.Join(OldDir, "One Piece - Chapter 0001.cbz");
        var newPath = Path.Join(NewDir, "One Piece - Chapter 0001.cbz");
        var completedEarlier = T0.AddDays(1);
        var completedLater = T0.AddDays(2);
        await SeedAsync(db =>
        {
            db.ComicFiles.Add(Row(oldPath, processed: true));

            var aliceMoved = Progress("alice", oldPath, current: 12, total: 20);
            aliceMoved.LastReadAt = T0.AddHours(1);
            aliceMoved.CompletedAt = completedLater;
            var aliceThere = Progress("alice", newPath, current: 5, total: 24);
            aliceThere.LastReadAt = T0.AddHours(9);
            aliceThere.CompletedAt = completedEarlier;

            var bobMoved = Progress("bob", oldPath, current: 3, total: 20);
            bobMoved.LastReadAt = T0.AddHours(8);
            var bobThere = Progress("bob", newPath, current: 8, total: 20);
            bobThere.LastReadAt = T0.AddHours(2);
            bobThere.CompletedAt = completedEarlier;

            // Nobody else's row at the destination: carol's moves.
            db.ReadingProgresses.AddRange(aliceMoved, aliceThere, bobMoved, bobThere, Progress("carol", oldPath, current: 1, total: 20));
        });
        var service = CreateService();
        await service.InitializeFromDatabaseAsync();

        Assert.Equal(1, await service.MoveDirectoryAsync(OldDir, NewDir));

        await using var db = await _factory.CreateDbContextAsync();
        var progress = await db.ReadingProgresses.AsNoTracking().ToDictionaryAsync(p => p.UserId);
        Assert.Equal(3, progress.Count);
        Assert.All(progress.Values, p => Assert.Equal(newPath, p.ContentId));

        var alice = progress["alice"];
        Assert.Equal(12, alice.CurrentPage);
        Assert.Equal(24, alice.TotalPages);
        Assert.Equal(60.0, alice.PercentComplete, precision: 3);
        Assert.Equal(completedLater, alice.CompletedAt);
        Assert.Equal(T0.AddHours(9), alice.LastReadAt);

        // Only one side has a completion date: it is kept.
        var bob = progress["bob"];
        Assert.Equal(8, bob.CurrentPage);
        Assert.Equal(completedEarlier, bob.CompletedAt);
        Assert.Equal(T0.AddHours(8), bob.LastReadAt);

        Assert.Equal(1, progress["carol"].CurrentPage);
    }

    [Fact]
    public async Task MoveDirectoryAsync_SameDirectoryAndUnusablePaths_DoNothing()
    {
        var path = Path.Join(OldDir, "One Piece - Chapter 0001.cbz");
        await SeedAsync(db => db.ComicFiles.Add(Row(path, processed: true)));
        var service = CreateService();
        await service.InitializeFromDatabaseAsync();

        Assert.Equal(0, await service.MoveDirectoryAsync(OldDir, OldDir + Path.DirectorySeparatorChar));
        Assert.Equal(0, await service.MoveDirectoryAsync(OldDir, "  "));
        Assert.Equal(0, await service.MoveDirectoryAsync("bad\0path", NewDir));
        Assert.Equal(0, await service.MoveDirectoryAsync(Path.GetPathRoot(_library)!, NewDir));

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(path, Assert.Single(await db.ComicFiles.AsNoTracking().ToListAsync()).FilePath);
        Assert.True(await service.FileExistsAsync(path));
    }

    [Fact]
    public async Task MoveDirectoryAsync_WhenTheDatabaseFails_ReportsNothingMovedAndKeepsTheInMemoryPaths()
    {
        var path = Path.Join(OldDir, "One Piece - Chapter 0001.cbz");
        await SeedAsync(db => db.ComicFiles.Add(Row(path, processed: true)));
        var service = CreateService();
        await service.InitializeFromDatabaseAsync();
        await DropFilesTableAsync();

        Assert.Equal(0, await service.MoveDirectoryAsync(OldDir, NewDir));

        Assert.True(await service.FileExistsAsync(path));
        Assert.False(await service.FileExistsAsync(Path.Join(NewDir, Path.GetFileName(path))));
    }

    [Fact]
    public async Task RemoveDirectoryAsync_WhenTheDatabaseFails_ReportsNothingRemovedAndKeepsTheInMemoryPaths()
    {
        var path = Path.Join(OldDir, "One Piece - Chapter 0001.cbz");
        await SeedAsync(db => db.ComicFiles.Add(Row(path, processed: true)));
        var service = CreateService();
        await service.InitializeFromDatabaseAsync();
        await DropFilesTableAsync();

        Assert.Equal(0, await service.RemoveDirectoryAsync(OldDir));

        Assert.True(await service.FileExistsAsync(path));
    }

    private async Task DropFilesTableAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys=OFF");
        await db.Database.ExecuteSqlRawAsync("DROP TABLE ComicFiles");
    }

    private FileStoreService CreateService(string? userId = null) =>
        TestServices.CreateFileStore(
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
