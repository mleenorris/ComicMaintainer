using System.Collections.Concurrent;
using ComicMaintainer.Core.Configuration;
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
/// Covers the status-flag writers of <see cref="FileStoreService"/> (duplicate / renamed /
/// normalized) for files the store has not seen yet, their behaviour during a database outage,
/// and <see cref="FileStoreService.ClearProcessedStatusAsync"/>.
/// </summary>
public sealed class FileStoreServiceStatusFlagsTests : IDisposable
{
    public enum Flag
    {
        Duplicate,
        Renamed,
        Normalized,
    }

    private readonly string _root = Path.Join(Path.GetTempPath(), $"fss_flags_{Guid.NewGuid():N}");
    private readonly string _library;
    private readonly string _outside;
    private readonly ServiceProvider _provider;
    private readonly IDbContextFactory<ComicMaintainerDbContext> _db;
    private readonly FlakyDbContextFactory _factory;
    private readonly ListLogger _logger = new();

    public FileStoreServiceStatusFlagsTests()
    {
        _library = Directory.CreateDirectory(Path.Join(_root, "library")).FullName;
        _outside = Directory.CreateDirectory(Path.Join(_root, "outside")).FullName;

        var dbName = $"fss_flags_{Guid.NewGuid()}";
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

    [Theory]
    [InlineData(Flag.Duplicate)]
    [InlineData(Flag.Renamed)]
    [InlineData(Flag.Normalized)]
    public async Task MarkFlag_FileOnDiskButUntracked_CreatesRowWithOnlyThatFlagSet(Flag flag)
    {
        var path = WriteComic(_library, "Saga 001.cbz");
        var service = CreateService();
        var before = service.ChangeVersion;

        await MarkAsync(service, flag, path, true);

        var row = await FindRowAsync(path);
        Assert.NotNull(row);
        Assert.Equal("Saga 001.cbz", row.FileName);
        Assert.Equal(_library, row.Directory);
        Assert.Equal(new FileInfo(path).Length, row.FileSize);
        Assert.Equal(flag == Flag.Duplicate, row.IsDuplicate);
        Assert.Equal(flag == Flag.Renamed, row.IsRenamed);
        Assert.Equal(flag == Flag.Normalized, row.IsNormalized);
        Assert.False(row.IsProcessed);
        Assert.Equal(nameof(FileMetadataSource.Scanned), row.MetadataSource);
        // Bumped for the in-memory change and again once the new row is persisted.
        Assert.True(service.ChangeVersion >= before + 2);
        Assert.False(_logger.Has(LogLevel.Error));
    }

    [Theory]
    [InlineData(Flag.Duplicate)]
    [InlineData(Flag.Renamed)]
    [InlineData(Flag.Normalized)]
    public async Task MarkFlag_FileMissingOrOutsideLibrary_DoesNotCreateRow(Flag flag)
    {
        var missing = Path.Join(_library, "Missing.cbz");
        var outside = WriteComic(_outside, "Stray.cbz");
        var service = CreateService();

        await MarkAsync(service, flag, missing, true);
        await MarkAsync(service, flag, outside, true);

        Assert.Null(await FindRowAsync(missing));
        Assert.Null(await FindRowAsync(outside));
        Assert.False(_logger.Has(LogLevel.Error));
    }

    [Theory]
    [InlineData(Flag.Duplicate)]
    [InlineData(Flag.Renamed)]
    [InlineData(Flag.Normalized)]
    public async Task MarkFlag_UntrackedFileNameWithConsecutiveDots_CreatesRowWithThatFlagSet(Flag flag)
    {
        // Regression: a ".." substring check rejected legitimate names such as "What If...?",
        // so no row was ever created for them and the flag was silently dropped.
        var path = WriteComic(_library, "What If... 001.cbz");
        var service = CreateService();

        await MarkAsync(service, flag, path, true);

        var row = await FindRowAsync(path);
        Assert.NotNull(row);
        Assert.True(Read(row, flag));
        Assert.False(_logger.Has(LogLevel.Warning));
    }

    [Theory]
    [InlineData(Flag.Duplicate)]
    [InlineData(Flag.Renamed)]
    [InlineData(Flag.Normalized)]
    public async Task MarkFlag_PathTraversingOutOfTheLibrary_DoesNotCreateRow(Flag flag)
    {
        var stray = WriteComic(_outside, "Stray.cbz");
        var traversal = Path.Join(_library, "..", "outside", "Stray.cbz");
        var service = CreateService();

        await MarkAsync(service, flag, traversal, true);

        Assert.Null(await FindRowAsync(traversal));
        Assert.Null(await FindRowAsync(stray));
        Assert.False(_logger.Has(LogLevel.Error));
    }

    [Fact]
    public async Task MarkFileDuplicateAsync_FileNotOnDiskYet_IsAppliedWhenTheFileIsAdded()
    {
        var path = Path.Join(_library, "Later.cbz");
        var service = CreateService();

        await service.MarkFileDuplicateAsync(path, true);
        Assert.Null(await FindRowAsync(path));

        File.WriteAllText(path, "comic");
        await service.AddFileAsync(path);

        Assert.True((await FindRowAsync(path))!.IsDuplicate);
        Assert.True(Assert.Single(await service.GetAllFilesAsync()).IsDuplicate);
    }

    [Theory]
    [InlineData(Flag.Duplicate)]
    [InlineData(Flag.Renamed)]
    [InlineData(Flag.Normalized)]
    public async Task MarkFlag_DatabaseUnavailable_LogsErrorAndKeepsInMemoryState(Flag flag)
    {
        var path = WriteComic(_library, "Saga 002.cbz");
        var service = CreateService();
        await service.AddFileAsync(path);
        _factory.FailAll();

        await MarkAsync(service, flag, path, true);

        Assert.True(_logger.Contains(LogLevel.Error, $"Error updating {flag.ToString().ToLowerInvariant()} status in database"));
        Assert.True(Read(Assert.Single(await service.GetAllFilesAsync()), flag));

        _factory.Heal();
        Assert.False(Read((await FindRowAsync(path))!, flag));
    }

    [Fact]
    public async Task ClearProcessedStatusAsync_NullInput_ReturnsZeroWithoutTouchingTheDatabase()
    {
        var service = CreateService();

        Assert.Equal(0, await service.ClearProcessedStatusAsync(null!));
        Assert.Equal(0, _factory.Created);
    }

    [Fact]
    public async Task ClearProcessedStatusAsync_FlaggedFile_ClearsEveryFlagAndBroadcasts()
    {
        var path = Path.Join(_library, "Done.cbz");
        await SeedAsync(Row(path, renamed: true, normalized: true, duplicate: true));
        var broadcaster = new Mock<IEventBroadcaster>();
        var service = CreateService(broadcaster.Object);
        await service.InitializeFromDatabaseAsync();
        var before = service.ChangeVersion;

        var cleared = await service.ClearProcessedStatusAsync(new[] { path, " " });

        Assert.Equal(1, cleared);
        broadcaster.Verify(b => b.BroadcastFileListUpdateAsync(), Times.Once);
        var row = await FindRowAsync(path);
        Assert.False(row!.IsRenamed || row.IsNormalized || row.IsProcessed || row.IsDuplicate);
        var file = Assert.Single(await service.GetAllFilesAsync());
        Assert.False(file.IsRenamed || file.IsNormalized || file.IsProcessed || file.IsDuplicate);
        Assert.True(service.ChangeVersion > before);
    }

    [Fact]
    public async Task ClearProcessedStatusAsync_BroadcastFails_StillReportsClearedCount()
    {
        var path = Path.Join(_library, "Done.cbz");
        await SeedAsync(Row(path, renamed: true));
        var broadcaster = new Mock<IEventBroadcaster>();
        broadcaster.Setup(b => b.BroadcastFileListUpdateAsync()).ThrowsAsync(new InvalidOperationException("hub down"));
        var service = CreateService(broadcaster.Object);

        var cleared = await service.ClearProcessedStatusAsync(new[] { path });

        Assert.Equal(1, cleared);
        Assert.False((await FindRowAsync(path))!.IsRenamed);
        Assert.True(_logger.Contains(LogLevel.Debug, "file list broadcast failed"));
    }

    [Fact]
    public async Task ClearProcessedStatusAsync_DatabaseUnavailable_ReturnsZeroAndLeavesSnapshotUntouched()
    {
        var path = Path.Join(_library, "Done.cbz");
        await SeedAsync(Row(path, renamed: true, normalized: true));
        var service = CreateService();
        await service.InitializeFromDatabaseAsync();
        _factory.FailAll();

        var cleared = await service.ClearProcessedStatusAsync(new[] { path });

        Assert.Equal(0, cleared);
        Assert.True(_logger.Contains(LogLevel.Error, "Error clearing processed status for 1 file(s)"));
        Assert.True(await service.IsFileRenamedAsync(path));
        Assert.True(await service.IsFileNormalizedAsync(path));
    }

    private FileStoreService CreateService(IEventBroadcaster? broadcaster = null) =>
        new(
            new TestOptionsMonitor<AppSettings>(new AppSettings
            {
                WatchedDirectory = _library,
                DuplicateDirectory = string.Empty,
            }),
            _logger,
            _factory,
            broadcaster);

    private static Task MarkAsync(FileStoreService service, Flag flag, string path, bool value) => flag switch
    {
        Flag.Duplicate => service.MarkFileDuplicateAsync(path, value),
        Flag.Renamed => service.MarkFileRenamedAsync(path, value),
        _ => service.MarkFileNormalizedAsync(path, value),
    };

    private static bool Read(ComicFileEntity row, Flag flag) => flag switch
    {
        Flag.Duplicate => row.IsDuplicate,
        Flag.Renamed => row.IsRenamed,
        _ => row.IsNormalized,
    };

    private static bool Read(ComicFile file, Flag flag) => flag switch
    {
        Flag.Duplicate => file.IsDuplicate,
        Flag.Renamed => file.IsRenamed,
        _ => file.IsNormalized,
    };

    private static string WriteComic(string directory, string name)
    {
        var path = Path.Join(directory, name);
        File.WriteAllText(path, "comic");
        return path;
    }

    private static ComicFileEntity Row(string path, bool renamed = false, bool normalized = false, bool duplicate = false) => new()
    {
        FilePath = path,
        FileName = Path.GetFileName(path),
        Directory = Path.GetDirectoryName(path)!,
        FileSize = 5,
        LastModified = DateTime.UtcNow,
        IsRenamed = renamed,
        IsNormalized = normalized,
        IsProcessed = renamed && normalized,
        IsDuplicate = duplicate,
    };

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

    /// <summary>Wraps a real factory and fails selected context creations on demand.</summary>
    private sealed class FlakyDbContextFactory : IDbContextFactory<ComicMaintainerDbContext>
    {
        private readonly IDbContextFactory<ComicMaintainerDbContext> _inner;
        private int _created;
        private volatile bool _failing;

        public FlakyDbContextFactory(IDbContextFactory<ComicMaintainerDbContext> inner) => _inner = inner;

        public int Created => Volatile.Read(ref _created);

        public void FailAll() => _failing = true;

        public void Heal() => _failing = false;

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

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _entries.Enqueue((logLevel, formatter(state, exception)));

        public bool Has(LogLevel level) => _entries.Any(e => e.Level == level);

        public bool Contains(LogLevel level, string fragment) =>
            _entries.Any(e => e.Level == level && e.Message.Contains(fragment, StringComparison.Ordinal));
    }
}
