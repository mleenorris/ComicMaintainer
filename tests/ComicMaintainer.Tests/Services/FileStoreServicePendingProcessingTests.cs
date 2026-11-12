using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// The file watcher's durable record of the files it has queued for processing
/// (<c>ComicFiles.IsProcessingPending</c>), on a real SQLite database: it is kept with the
/// file's row through re-adds and moves, so a restart can pick the queued files up again.
/// </summary>
public sealed class FileStoreServicePendingProcessingTests : IAsyncLifetime
{
    private static readonly DateTime T0 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly string _library = Path.Join(Path.GetTempPath(), $"fss_pending_{Guid.NewGuid():N}");
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

    [Fact]
    public async Task SetProcessingPendingAsync_SetsAndClearsTheFlagOfATrackedFile()
    {
        var first = CreateComic("Saga", "Saga 001.cbz");
        var second = CreateComic("Saga", "Saga 002.cbz");
        var store = CreateService();
        await store.AddFileAsync(first);
        await store.AddFileAsync(second);

        await store.SetProcessingPendingAsync(second, true);
        await store.SetProcessingPendingAsync(first, true);

        Assert.Equal(new[] { first, second }, await store.GetProcessingPendingFilesAsync());

        await store.SetProcessingPendingAsync(first, false);

        Assert.Equal(new[] { second }, await store.GetProcessingPendingFilesAsync());
        Assert.False(await PendingInDatabaseAsync(first));
    }

    [Fact]
    public async Task SetProcessingPendingAsync_UntrackedFile_DoesNothing()
    {
        var store = CreateService();

        await store.SetProcessingPendingAsync(Path.Join(_library, "nowhere.cbz"), true);
        await store.SetProcessingPendingAsync("  ", true);

        Assert.Empty(await store.GetProcessingPendingFilesAsync());
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Empty(await db.ComicFiles.ToListAsync());
    }

    [Fact]
    public async Task PendingFlag_SurvivesReAddsAndStatusChangesOfTheRow()
    {
        var comic = CreateComic("Saga", "Saga 001.cbz");
        var store = CreateService();
        await store.AddFileAsync(comic);
        await store.SetProcessingPendingAsync(comic, true);

        // The watcher re-adds files it sees again, and the processor stamps the row.
        await store.AddFileAsync(comic);
        await store.MarkFileNormalizedAsync(comic, true);

        Assert.True(await PendingInDatabaseAsync(comic));
    }

    // The flag only remembers work for a restart: a database problem must not fail what the file watcher is doing.
    [Fact]
    public async Task SetProcessingPendingAsync_WhenTheDatabaseFails_DoesNotThrow()
    {
        var comic = CreateComic("Saga", "Saga 001.cbz");
        var store = CreateService();
        await store.AddFileAsync(comic);
        await DropFilesTableAsync();

        await store.SetProcessingPendingAsync(comic, true);
    }

    [Fact]
    public async Task GetProcessingPendingFilesAsync_WhenTheDatabaseFails_ReturnsNothing()
    {
        var store = CreateService();
        await DropFilesTableAsync();

        Assert.Empty(await store.GetProcessingPendingFilesAsync());
    }

    private async Task DropFilesTableAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys=OFF");
        await db.Database.ExecuteSqlRawAsync("DROP TABLE ComicFiles");
    }

    [Fact]
    public async Task PendingFlag_MovesWithTheFileWhenItOrItsDirectoryIsRenamed()
    {
        var comic = CreateComic("Saga", "Saga 001.cbz");
        var store = CreateService();
        await store.AddFileAsync(comic);
        await store.SetProcessingPendingAsync(comic, true);

        var renamed = Path.Join(_library, "Saga", "Saga - Chapter 0001.cbz");
        File.Move(comic, renamed);
        await store.UpdateFilePathAsync(comic, renamed);

        Assert.Equal(new[] { renamed }, await store.GetProcessingPendingFilesAsync());

        var newDirectory = Path.Join(_library, "Saga (2012)");
        Directory.Move(Path.Join(_library, "Saga"), newDirectory);
        await store.MoveDirectoryAsync(Path.Join(_library, "Saga"), newDirectory);

        Assert.Equal(new[] { Path.Join(newDirectory, "Saga - Chapter 0001.cbz") }, await store.GetProcessingPendingFilesAsync());
    }

    [Fact]
    public async Task Migration_QueuesUnprocessedDuplicatesForOneMorePass()
    {
        // Applies the migrations to a database of the previous release, so it holds rows the
        // way that release left them.
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var db = new ComicMaintainerDbContext(
            new DbContextOptionsBuilder<ComicMaintainerDbContext>().UseSqlite(connection).Options);
        var migrator = db.GetInfrastructure().GetRequiredService<IMigrator>();
        await migrator.MigrateAsync("20261027203049_AddEmailDeliverySentAtIndex");
        await InsertLegacyRowAsync(db, "/lib/Tower Of God/Tower Of God - Chapter 0001.cbz", isDuplicate: true, isProcessed: false);
        await InsertLegacyRowAsync(db, "/lib/Saga/Saga - Chapter 0001.cbz", isDuplicate: false, isProcessed: false);
        await InsertLegacyRowAsync(db, "/lib/Saga/Saga - Chapter 0002.cbz", isDuplicate: false, isProcessed: true);

        await db.Database.MigrateAsync();

        var pending = await db.ComicFiles.AsNoTracking()
            .Where(e => e.IsProcessingPending)
            .Select(e => e.FilePath)
            .ToListAsync();
        Assert.Equal(new[] { "/lib/Tower Of God/Tower Of God - Chapter 0001.cbz" }, pending);
    }

    private static Task InsertLegacyRowAsync(ComicMaintainerDbContext db, string path, bool isDuplicate, bool isProcessed) =>
        db.Database.ExecuteSqlAsync($"""
            INSERT INTO "ComicFiles" ("FilePath", "FileName", "Directory", "FileSize", "LastModified",
                "IsProcessed", "IsRenamed", "IsNormalized", "IsDuplicate", "IsRead", "CreatedAt", "UpdatedAt",
                "SeriesMetadataVersion", "MetadataVersion", "WrittenMetadataVersion", "MetadataSource")
            VALUES ({path}, {Path.GetFileName(path)}, {Path.GetDirectoryName(path)}, 10, {T0},
                {isProcessed}, {isProcessed}, {true}, {isDuplicate}, {false}, {T0}, {T0},
                0, 0, 0, 'Scanned')
            """);

    private string CreateComic(string folder, string fileName)
    {
        var directory = Path.Join(_library, folder);
        Directory.CreateDirectory(directory);
        var path = Path.Join(directory, fileName);
        File.WriteAllText(path, "comic");
        return path;
    }

    private async Task<bool> PendingInDatabaseAsync(string path)
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.ComicFiles.AsNoTracking().Where(e => e.FilePath == path).Select(e => e.IsProcessingPending).SingleAsync();
    }

    private FileStoreService CreateService() =>
        TestServices.CreateFileStore(
            new TestOptionsMonitor<AppSettings>(new AppSettings { WatchedDirectory = _library, DuplicateDirectory = string.Empty }),
            NullLogger<FileStoreService>.Instance,
            _factory);
}
