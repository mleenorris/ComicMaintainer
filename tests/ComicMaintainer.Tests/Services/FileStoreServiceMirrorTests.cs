using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Covers how <see cref="FileStoreService"/> keeps its in-memory copy in step with the database
/// when another writer commits to the same row while a write is in flight. EF writes only the
/// columns that changed, so the copy has to come from the committed row, not from the entity as
/// it was loaded. Real SQLite, with the concurrent writer injected around <c>SaveChanges</c>.
/// </summary>
public sealed class FileStoreServiceMirrorTests : IAsyncLifetime
{
    private const string Library = "/library";
    private const string FilePath = "/library/Saga/Saga 001.cbz";
    private static readonly DateTime T0 = new(2024, 1, 1, 10, 0, 0);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ConcurrentWriter _writer = new();
    private ServiceProvider _provider = null!;
    private IDbContextFactory<ComicMaintainerDbContext> _factory = null!;
    private FailingFactory _failing = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddDbContextFactory<ComicMaintainerDbContext>(o => o.UseSqlite(_connection).AddInterceptors(_writer));
        _provider = services.BuildServiceProvider();
        _factory = _provider.GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>();
        _failing = new FailingFactory(_factory);
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task StatusWrite_KeepsAColumnAnotherWriterCommittedMeanwhile()
    {
        var service = await SeedAndCreateServiceAsync();
        // A stat refresh commits after this write has loaded its row and before it saves.
        _writer.BeforeSave = () => Execute($"UPDATE ComicFiles SET FileSize = 999 WHERE FilePath = '{FilePath}'");

        await service.MarkFileNormalizedAsync(FilePath, true);

        var file = Assert.Single(await service.GetAllFilesAsync());
        Assert.True(file.IsNormalized);
        Assert.Equal(999, file.FileSize);
    }

    [Fact]
    public async Task StatsRefresh_KeepsAFlagAnotherWriterCommittedMeanwhile()
    {
        var service = await SeedAndCreateServiceAsync();
        _writer.BeforeSave = () => Execute($"UPDATE ComicFiles SET IsRenamed = 1 WHERE FilePath = '{FilePath}'");

        await service.SetFileSeriesMetadataVersionAsync(FilePath, 7);

        var file = Assert.Single(await service.GetAllFilesAsync());
        Assert.Equal(7, file.SeriesMetadataVersion);
        Assert.True(file.IsRenamed);
    }

    [Fact]
    public async Task Removal_KeepsARowAnotherWriterAddedBackMeanwhile()
    {
        var service = await SeedAndCreateServiceAsync();
        // The watcher re-adds the file right after the delete committed.
        _writer.AfterSave = () => Execute(
            "INSERT INTO ComicFiles (FilePath, FileName, Directory, FileSize, LastModified, IsProcessed, IsRenamed, IsNormalized, IsDuplicate, IsRead, CreatedAt, UpdatedAt, SeriesMetadataVersion, MetadataVersion, WrittenMetadataVersion, MetadataSource) "
            + $"VALUES ('{FilePath}', 'Saga 001.cbz', '/library/Saga', 5, '2024-01-01 10:00:00', 0, 0, 0, 0, 0, '2024-01-01 10:00:00', '2024-01-01 10:00:00', 0, 0, 0, 'Scanned')");

        await service.RemoveFileAsync(FilePath);

        Assert.True(await service.FileExistsAsync(FilePath));
    }

    [Fact]
    public async Task WriteOfARowAnotherWriterDeletedMeanwhile_DropsItFromMemory()
    {
        var service = await SeedAndCreateServiceAsync();
        _writer.AfterSave = () => Execute($"DELETE FROM ComicFiles WHERE FilePath = '{FilePath}'");

        await service.MarkFileRenamedAsync(FilePath, true);

        Assert.False(await service.FileExistsAsync(FilePath));
    }

    [Fact]
    public async Task ReReadFails_FallsBackToTheValuesThatWereSaved()
    {
        var service = await SeedAndCreateServiceAsync();
        _writer.AfterSave = _failing.FailFromNowOn;

        await service.MarkFileNormalizedAsync(FilePath, true);

        Assert.True(await service.IsFileNormalizedAsync(FilePath));
    }

    private async Task<FileStoreService> SeedAndCreateServiceAsync()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.ComicFiles.Add(new ComicFileEntity
            {
                FilePath = FilePath,
                FileName = Path.GetFileName(FilePath),
                Directory = "/library/Saga",
                FileSize = 100,
                LastModified = T0,
            });
            await db.SaveChangesAsync();
        }

        var service = new FileStoreService(
            new TestOptionsMonitor<AppSettings>(new AppSettings { WatchedDirectory = Library, DuplicateDirectory = string.Empty }),
            NullLogger<FileStoreService>.Instance,
            _failing);
        await service.InitializeFromDatabaseAsync();
        return service;
    }

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>Runs the supplied action once around the next <c>SaveChanges</c>.</summary>
    private sealed class ConcurrentWriter : SaveChangesInterceptor
    {
        public Action? BeforeSave { get; set; }

        public Action? AfterSave { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var action = BeforeSave;
            BeforeSave = null;
            action?.Invoke();
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            var action = AfterSave;
            AfterSave = null;
            action?.Invoke();
            return base.SavedChangesAsync(eventData, result, cancellationToken);
        }
    }

    /// <summary>Hands out real contexts until told to fail, as a database outage would.</summary>
    private sealed class FailingFactory : IDbContextFactory<ComicMaintainerDbContext>
    {
        private readonly IDbContextFactory<ComicMaintainerDbContext> _inner;
        private volatile bool _failing;

        public FailingFactory(IDbContextFactory<ComicMaintainerDbContext> inner) => _inner = inner;

        public void FailFromNowOn() => _failing = true;

        public ComicMaintainerDbContext CreateDbContext() =>
            _failing ? throw new InvalidOperationException("Simulated database outage") : _inner.CreateDbContext();

        public Task<ComicMaintainerDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
