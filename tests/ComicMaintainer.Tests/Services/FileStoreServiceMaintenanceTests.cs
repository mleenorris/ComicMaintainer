using System.Collections.Concurrent;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Covers the maintenance paths of <see cref="FileStoreService"/>: start-up load, metadata
/// backfill bookkeeping, stale-entry cleanup (cancellation and failures) and file-stat
/// refreshes.
/// </summary>
public sealed class FileStoreServiceMaintenanceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"fss_maint_{Guid.NewGuid():N}");
    private readonly string _library;
    private readonly ServiceProvider _provider;
    private readonly IDbContextFactory<ComicMaintainerDbContext> _db;
    private readonly FlakyDbContextFactory _factory;
    private readonly ListLogger _logger = new();

    public FileStoreServiceMaintenanceTests()
    {
        _library = Directory.CreateDirectory(Path.Combine(_root, "library")).FullName;

        var dbName = $"fss_maint_{Guid.NewGuid()}";
        var services = new ServiceCollection();
        services.AddDbContextFactory<ComicMaintainerDbContext>(o => o.UseInMemoryDatabase(dbName));
        _provider = services.BuildServiceProvider();
        _db = _provider.GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>();
        _factory = new FlakyDbContextFactory(_db);
    }

    public void Dispose()
    {
        _provider.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; a leftover temp directory must not fail the run.
        }
    }

    [Fact]
    public async Task InitializeFromDatabaseAsync_DatabaseUnavailable_LogsErrorAndStartsEmpty()
    {
        await SeedAsync(Row(Path.Combine(_library, "Saga 001.cbz")));
        var service = CreateService();
        _factory.FailAll();

        await service.InitializeFromDatabaseAsync();

        Assert.True(_logger.Contains(LogLevel.Error, "Error initializing file store from database"));
        Assert.Empty(await service.GetAllFilesAsync());
    }

    // ---- MarkFileBackfilledAsync --------------------------------------------------------

    [Fact]
    public async Task MarkFileBackfilledAsync_UnknownFile_ChangesNothing()
    {
        var service = CreateService();
        var before = service.ChangeVersion;

        await service.MarkFileBackfilledAsync(Path.Combine(_library, "Missing.cbz"), 3);

        Assert.Equal(before, service.ChangeVersion);
        Assert.Empty(await service.GetAllFilesAsync());
    }

    [Fact]
    public async Task MarkFileBackfilledAsync_LookupSourcedRow_StampsWrittenVersionAndMarksScanned()
    {
        var path = Path.Combine(_library, "Saga 002.cbz");
        await SeedAsync(Row(path, e =>
        {
            e.MetadataVersion = 4;
            e.WrittenMetadataVersion = 1;
            e.MetadataSource = nameof(FileMetadataSource.ExternalLookup);
        }));
        var service = CreateService();
        await service.InitializeFromDatabaseAsync();
        var before = service.ChangeVersion;

        await service.MarkFileBackfilledAsync(path, 4);

        var row = await FindRowAsync(path);
        Assert.Equal(4, row!.WrittenMetadataVersion);
        Assert.NotNull(row.LastWriteAt);
        Assert.Equal(nameof(FileMetadataSource.Scanned), row.MetadataSource);
        var file = Assert.Single(await service.GetAllFilesAsync());
        Assert.Equal(4, file.WrittenMetadataVersion);
        Assert.Equal(FileMetadataSource.Scanned, file.MetadataSource);
        Assert.True(service.ChangeVersion > before);
    }

    [Fact]
    public async Task MarkFileBackfilledAsync_UserEditedRow_KeepsUserEditSource()
    {
        var path = Path.Combine(_library, "Saga 003.cbz");
        await SeedAsync(Row(path, e =>
        {
            e.MetadataVersion = 2;
            e.MetadataSource = "useredit";
        }));
        var service = CreateService();

        await service.MarkFileBackfilledAsync(path, 2);

        var row = await FindRowAsync(path);
        Assert.Equal(2, row!.WrittenMetadataVersion);
        Assert.Equal("useredit", row.MetadataSource);
        Assert.Equal(FileMetadataSource.UserEdit, Assert.Single(await service.GetAllFilesAsync()).MetadataSource);
    }

    // ---- MarkFilesNeedingBackfillAsync --------------------------------------------------

    [Fact]
    public async Task MarkFilesNeedingBackfillAsync_NoUsablePaths_ReturnsZeroWithoutQuerying()
    {
        var service = CreateService();

        Assert.Equal(0, await service.MarkFilesNeedingBackfillAsync(null!));
        Assert.Equal(0, await service.MarkFilesNeedingBackfillAsync(new[] { "", "  " }));
        Assert.Equal(0, _factory.Created);
    }

    [Fact]
    public async Task MarkFilesNeedingBackfillAsync_DatabaseUnavailable_ReturnsZeroAndWarns()
    {
        var service = CreateService();
        _factory.FailAll();

        var marked = await service.MarkFilesNeedingBackfillAsync(new[] { Path.Combine(_library, "Saga 004.cbz") });

        Assert.Equal(0, marked);
        Assert.True(_logger.Contains(LogLevel.Warning, "Failed to mark 1 file(s) as needing metadata backfill"));
    }

    // ---- CleanupStaleEntriesAsync -------------------------------------------------------

    [Fact]
    public async Task CleanupStaleEntriesAsync_AlreadyCancelled_RemovesNothing()
    {
        var stale = Path.Combine(_library, "Gone.cbz");
        await SeedAsync(Row(stale));
        var service = CreateService();

        var removed = await service.CleanupStaleEntriesAsync(new CancellationToken(canceled: true));

        Assert.Equal(0, removed);
        Assert.True(_logger.Contains(LogLevel.Warning, "Cleanup of stale entries was cancelled"));
        Assert.NotNull(await FindRowAsync(stale));
    }

    [Fact]
    public async Task CleanupStaleEntriesAsync_CancelledMidScan_LeavesUnvisitedEntriesAlone()
    {
        var first = Path.Combine(_library, "Gone 1.cbz");
        var second = Path.Combine(_library, "Gone 2.cbz");
        await SeedAsync(Row(first), Row(second));
        var service = CreateService();
        await service.InitializeFromDatabaseAsync();
        using var cts = new CancellationTokenSource();
        string? visited = null;
        _logger.OnLog = (_, message) =>
        {
            if (visited == null && message.StartsWith("Marking stale entry for removal: ", StringComparison.Ordinal))
            {
                visited = message["Marking stale entry for removal: ".Length..];
                cts.Cancel();
            }
        };

        var removed = await service.CleanupStaleEntriesAsync(cts.Token);

        Assert.NotNull(visited);
        var unvisited = visited == first ? second : first;
        Assert.Equal(0, removed);
        Assert.True(_logger.Contains(LogLevel.Warning, "Cleanup of stale entries was cancelled"));
        Assert.NotNull(await FindRowAsync(unvisited));
        Assert.True(await service.FileExistsAsync(unvisited));
    }

    [Fact]
    public async Task CleanupStaleEntriesAsync_DatabaseUnavailable_ReturnsZeroAndLogsError()
    {
        var service = CreateService();
        _factory.FailAll();

        Assert.Equal(0, await service.CleanupStaleEntriesAsync());
        Assert.True(_logger.Contains(LogLevel.Error, "Error during cleanup of stale database entries"));
    }

    // ---- RefreshFileStatsAsync ----------------------------------------------------------

    [Fact]
    public async Task RefreshFileStatsAsync_NothingOnDisk_ReturnsZeroWithoutQuerying()
    {
        var service = CreateService();
        var missing = Path.Combine(_library, "Missing.cbz");

        Assert.Equal(0, await service.RefreshFileStatsAsync(null!));
        Assert.Equal(0, await service.RefreshFileStatsAsync(new[] { "", "  ", missing, missing }));
        Assert.Equal(0, _factory.Created);
    }

    [Fact]
    public async Task RefreshFileStatsAsync_InvalidPath_IsSkipped()
    {
        var service = CreateService();

        Assert.Equal(0, await service.RefreshFileStatsAsync(new[] { Path.Combine(_library, "Bad\0Name.cbz") }));
        Assert.True(_logger.Contains(LogLevel.Debug, "RefreshFileStats: stat failed"));
    }

    [Fact]
    public async Task RefreshFileStatsAsync_PersistFails_StillRefreshesTheSnapshot()
    {
        var path = Path.Combine(_library, "Saga 005.cbz");
        File.WriteAllText(path, "comic");
        var service = CreateService();
        await service.AddFileAsync(path);
        File.AppendAllText(path, " grew");
        var length = new FileInfo(path).Length;
        _factory.FailAll();

        var changed = await service.RefreshFileStatsAsync(new[] { path, path });

        Assert.Equal(0, changed);
        Assert.True(_logger.Contains(LogLevel.Warning, "RefreshFileStats: failed to persist file stats for 1 file(s)"));
        Assert.Equal(length, Assert.Single(await service.GetAllFilesAsync()).FileSize);
    }

    // ---- helpers ------------------------------------------------------------------------

    private FileStoreService CreateService() =>
        new(
            new TestOptionsMonitor<AppSettings>(new AppSettings
            {
                WatchedDirectory = _library,
                DuplicateDirectory = string.Empty,
            }),
            _logger,
            _factory);

    private static ComicFileEntity Row(string path, Action<ComicFileEntity>? configure = null)
    {
        var row = new ComicFileEntity
        {
            FilePath = path,
            FileName = Path.GetFileName(path),
            Directory = Path.GetDirectoryName(path)!,
            FileSize = 5,
            LastModified = DateTime.UtcNow,
        };
        configure?.Invoke(row);
        return row;
    }

    private async Task SeedAsync(params ComicFileEntity[] rows)
    {
        await using var db = await _db.CreateDbContextAsync();
        db.ComicFiles.AddRange(rows);
        await db.SaveChangesAsync();
    }

    private async Task<ComicFileEntity?> FindRowAsync(string path)
    {
        await using var db = await _db.CreateDbContextAsync();
        return await db.ComicFiles.AsNoTracking().SingleOrDefaultAsync(e => e.FilePath == path);
    }

    /// <summary>Wraps a real factory and fails every context creation once told to.</summary>
    private sealed class FlakyDbContextFactory : IDbContextFactory<ComicMaintainerDbContext>
    {
        private readonly IDbContextFactory<ComicMaintainerDbContext> _inner;
        private int _created;
        private volatile bool _failing;

        public FlakyDbContextFactory(IDbContextFactory<ComicMaintainerDbContext> inner) => _inner = inner;

        public int Created => Volatile.Read(ref _created);

        public void FailAll() => _failing = true;

        public ComicMaintainerDbContext CreateDbContext()
        {
            Interlocked.Increment(ref _created);
            if (_failing)
            {
                throw new InvalidOperationException("Simulated database outage");
            }

            return _inner.CreateDbContext();
        }

        public Task<ComicMaintainerDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class ListLogger : ILogger<FileStoreService>
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

        /// <summary>Invoked synchronously after each entry is recorded.</summary>
        public Action<LogLevel, string>? OnLog { get; set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            _entries.Enqueue((logLevel, message));
            OnLog?.Invoke(logLevel, message);
        }

        public bool Contains(LogLevel level, string fragment) =>
            _entries.Any(e => e.Level == level && e.Message.Contains(fragment, StringComparison.Ordinal));
    }
}
