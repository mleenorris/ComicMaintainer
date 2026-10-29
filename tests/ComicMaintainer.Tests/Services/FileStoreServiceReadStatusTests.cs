using System.Collections.Concurrent;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Covers the per-user read-state paths of <see cref="FileStoreService"/>: the cached
/// read-path set, repointing read status and reader progress on a move when the destination
/// already has rows, bulk marking, and reading-progress persistence without a user or a
/// working database.
/// </summary>
public sealed class FileStoreServiceReadStatusTests : IDisposable
{
    private static readonly DateTime T0 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly string _library = Path.Join(Path.GetTempPath(), $"fss_read_{Guid.NewGuid():N}");
    private readonly ServiceProvider _provider;
    private readonly IDbContextFactory<ComicMaintainerDbContext> _db;
    private readonly FlakyDbContextFactory _factory;
    private readonly ListLogger _logger = new();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    public FileStoreServiceReadStatusTests()
    {
        var dbName = $"fss_read_{Guid.NewGuid()}";
        var services = new ServiceCollection();
        services.AddDbContextFactory<ComicMaintainerDbContext>(o => o.UseInMemoryDatabase(dbName));
        _provider = services.BuildServiceProvider();
        _db = _provider.GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>();
        _factory = new FlakyDbContextFactory(_db);
    }

    public void Dispose()
    {
        _cache.Dispose();
        _provider.Dispose();
    }

    [Fact]
    public async Task GetAllFilesAsync_SecondCallWithoutChanges_ServesReadPathsFromCache()
    {
        var path = Path.Join(_library, "Saga 001.cbz");
        await SeedAsync(db =>
        {
            db.ComicFiles.Add(FileRow(path));
            db.UserFileReadStatuses.Add(Status("alice", path, isRead: true));
        });
        var service = CreateService("alice", _cache);
        await service.InitializeFromDatabaseAsync();

        Assert.True(Assert.Single(await service.GetAllFilesAsync()).IsRead);
        var contextsAfterFirstCall = _factory.Created;

        Assert.True(Assert.Single(await service.GetAllFilesAsync()).IsRead);
        Assert.Equal(contextsAfterFirstCall, _factory.Created);
    }

    [Fact]
    public async Task UpdateFilePathAsync_DestinationHasUserRows_DestinationWinsAndProgressIsMerged()
    {
        var oldPath = Path.Join(_library, "Chapter 7.cbz");
        var newPath = Path.Join(_library, "Saga - Chapter 007.cbz");
        await SeedAsync(db =>
        {
            db.ComicFiles.Add(FileRow(oldPath));
            db.UserFileReadStatuses.AddRange(
                Status("alice", oldPath, isRead: true, page: 9),
                Status("alice", newPath, isRead: false, page: 7),
                Status("bob", oldPath, isRead: true, page: 3));
            db.ReadingProgresses.AddRange(
                // alice: only the destination is finished; the source got further.
                Progress("alice", oldPath, current: 12, total: 20, percent: 60, lastRead: T0.AddDays(1), completed: null),
                Progress("alice", newPath, current: 5, total: 24, percent: 20, lastRead: T0, completed: T0.AddDays(2)),
                // bob: both finished, the destination more recently.
                Progress("bob", oldPath, current: 3, total: 10, percent: 30, lastRead: T0, completed: T0.AddDays(1)),
                Progress("bob", newPath, current: 4, total: 10, percent: 40, lastRead: T0.AddDays(3), completed: T0.AddDays(4)),
                // carol: both finished, the source more recently.
                Progress("carol", oldPath, current: 8, total: 10, percent: 80, lastRead: T0.AddDays(5), completed: T0.AddDays(6)),
                Progress("carol", newPath, current: 2, total: 10, percent: 20, lastRead: T0.AddDays(1), completed: T0.AddDays(2)),
                // dave: only the source finished.
                Progress("dave", oldPath, current: 10, total: 10, percent: 100, lastRead: T0, completed: T0.AddDays(3)),
                Progress("dave", newPath, current: 1, total: 10, percent: 10, lastRead: T0, completed: null),
                // erin: nothing at the destination yet.
                Progress("erin", oldPath, current: 6, total: 10, percent: 60, lastRead: T0, completed: null));
        });
        var service = CreateService("alice", _cache);
        await service.InitializeFromDatabaseAsync();

        await service.UpdateFilePathAsync(oldPath, newPath);

        await using var db = await _db.CreateDbContextAsync();
        var statuses = await db.UserFileReadStatuses.AsNoTracking().ToListAsync();
        Assert.DoesNotContain(statuses, s => s.FilePath == oldPath);
        var alice = Assert.Single(statuses, s => s.UserId == "alice");
        Assert.Equal(newPath, alice.FilePath);
        Assert.False(alice.IsRead);
        Assert.Equal(7, alice.CurrentPage);
        var bob = Assert.Single(statuses, s => s.UserId == "bob");
        Assert.Equal(newPath, bob.FilePath);
        Assert.True(bob.IsRead);

        var progress = (await db.ReadingProgresses.AsNoTracking().ToListAsync()).ToDictionary(p => p.UserId);
        Assert.Equal(5, progress.Count);
        Assert.All(progress.Values, p => Assert.Equal(newPath, p.ContentId));

        Assert.Equal(24, progress["alice"].TotalPages);
        Assert.Equal(12, progress["alice"].CurrentPage);
        Assert.Equal(60, progress["alice"].PercentComplete);
        Assert.Equal(T0.AddDays(2), progress["alice"].CompletedAt);
        Assert.Equal(T0.AddDays(1), progress["alice"].LastReadAt);

        Assert.Equal(T0.AddDays(4), progress["bob"].CompletedAt);
        Assert.Equal(T0.AddDays(3), progress["bob"].LastReadAt);
        Assert.Equal(40, progress["bob"].PercentComplete);

        Assert.Equal(T0.AddDays(6), progress["carol"].CompletedAt);
        Assert.Equal(T0.AddDays(5), progress["carol"].LastReadAt);
        Assert.Equal(8, progress["carol"].CurrentPage);

        Assert.Equal(T0.AddDays(3), progress["dave"].CompletedAt);
        Assert.Equal(10, progress["dave"].CurrentPage);

        Assert.Equal(6, progress["erin"].CurrentPage);
        Assert.Null(progress["erin"].CompletedAt);
    }

    [Fact]
    public async Task UpdateFilePathAsync_SamePath_LeavesEveryUsersReadStatusInPlace()
    {
        var path = Path.Join(_library, "Saga 001.cbz");
        await SeedAsync(db =>
        {
            db.ComicFiles.Add(FileRow(path));
            db.UserFileReadStatuses.AddRange(
                Status("alice", path, isRead: true, page: 4),
                Status("bob", path, isRead: false, page: 9));
        });
        var service = CreateService("alice");
        await service.InitializeFromDatabaseAsync();

        // Only invariants are asserted: a same-path update also deletes the file's own
        // ComicFiles row (reported as a production bug), which is deliberately not pinned here.
        await service.UpdateFilePathAsync(path, path);

        var statuses = await AllStatusesAsync();
        Assert.Equal(2, statuses.Count);
        Assert.All(statuses, s => Assert.Equal(path, s.FilePath));
        Assert.True(Assert.Single(statuses, s => s.UserId == "alice").IsRead);
        Assert.Equal(9, Assert.Single(statuses, s => s.UserId == "bob").CurrentPage);
        Assert.True(await service.FileExistsAsync(path));
        Assert.False(_logger.Contains(LogLevel.Error, string.Empty));
    }

    [Fact]
    public async Task MarkFilesReadAsync_NoUserInScope_WritesNothing()
    {
        var service = CreateService(userId: null);

        await service.MarkFilesReadAsync(new[] { Path.Join(_library, "Saga 001.cbz") }, read: true);

        Assert.Equal(0, _factory.Created);
        Assert.Empty(await AllStatusesAsync());
    }

    [Fact]
    public async Task MarkFilesReadAsync_ExistingRows_UpdatesInPlaceAndOnlyStampsLastReadWhenMarkingRead()
    {
        var unread = Path.Join(_library, "Saga 001.cbz");
        var read = Path.Join(_library, "Saga 002.cbz");
        await SeedAsync(db => db.UserFileReadStatuses.AddRange(
            Status("alice", unread, isRead: false, lastRead: T0),
            Status("alice", read, isRead: true, lastRead: T0)));
        var service = CreateService("alice");

        await service.MarkFilesReadAsync(new[] { unread }, read: true);
        await service.MarkFilesReadAsync(new[] { read }, read: false);

        var rows = (await AllStatusesAsync()).ToDictionary(s => s.FilePath);
        Assert.Equal(2, rows.Count);
        Assert.True(rows[unread].IsRead);
        Assert.True(rows[unread].LastReadDate > T0);
        Assert.False(rows[read].IsRead);
        Assert.Equal(T0, rows[read].LastReadDate);
    }

    [Fact]
    public async Task MarkFilesReadAsync_LazySequenceMarkedUnread_CreatesRowsWithoutLastReadDate()
    {
        var first = Path.Join(_library, "Saga 003.cbz");
        var second = Path.Join(_library, "Saga 004.cbz");
        var service = CreateService("alice");

        await service.MarkFilesReadAsync(Lazily(first, second), read: false);

        var rows = await AllStatusesAsync();
        Assert.Equal(new[] { first, second }, rows.Select(r => r.FilePath).OrderBy(p => p, StringComparer.Ordinal));
        Assert.All(rows, r =>
        {
            Assert.Equal("alice", r.UserId);
            Assert.False(r.IsRead);
            Assert.Null(r.LastReadDate);
        });
    }

    [Fact]
    public async Task MarkFilesReadAsync_ExactlyOneFullChunk_MarksEveryFile()
    {
        var paths = Enumerable.Range(0, 500).Select(i => Path.Join(_library, $"Issue {i:000}.cbz")).ToList();
        var service = CreateService("alice");

        await service.MarkFilesReadAsync(paths, read: true);

        var rows = await AllStatusesAsync();
        Assert.Equal(500, rows.Count);
        Assert.All(rows, r => Assert.True(r.IsRead));
    }

    [Fact]
    public async Task MarkFilesReadAsync_BulkUpdateFails_FallsBackToPerFileUpdates()
    {
        var first = Path.Join(_library, "Saga 005.cbz");
        var second = Path.Join(_library, "Saga 006.cbz");
        var service = CreateService("alice");
        _factory.FailNth(1);

        await service.MarkFilesReadAsync(new[] { first, second }, read: true);

        Assert.True(_logger.Contains(LogLevel.Error, "falling back to per-file updates"));
        var rows = await AllStatusesAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.True(r.IsRead));
    }

    [Fact]
    public async Task SaveReadingProgressAsync_PageBelowOne_IsRejected()
    {
        var service = CreateService("alice");

        await service.SaveReadingProgressAsync(Path.Join(_library, "Saga 001.cbz"), 0);

        Assert.True(_logger.Contains(LogLevel.Warning, "Invalid page number 0"));
        Assert.Equal(0, _factory.Created);
    }

    [Fact]
    public async Task SaveReadingProgressAsync_NoUserInScope_WritesNothing()
    {
        var service = CreateService(userId: null);

        await service.SaveReadingProgressAsync(Path.Join(_library, "Saga 001.cbz"), 4);

        Assert.Equal(0, _factory.Created);
        Assert.Empty(await AllStatusesAsync());
    }

    [Fact]
    public async Task SaveReadingProgressAsync_DatabaseUnavailable_LogsError()
    {
        var service = CreateService("alice");
        _factory.FailAll();

        await service.SaveReadingProgressAsync(Path.Join(_library, "Saga 001.cbz"), 4);

        Assert.True(_logger.Contains(LogLevel.Error, "Error saving reading progress"));
    }

    [Fact]
    public async Task GetReadingProgressAsync_NoUserInScope_ReturnsFirstPage()
    {
        var service = CreateService(userId: null);

        Assert.Equal(1, await service.GetReadingProgressAsync(Path.Join(_library, "Saga 001.cbz")));
        Assert.Equal(0, _factory.Created);
    }

    [Fact]
    public async Task GetReadingProgressAsync_DatabaseUnavailable_ReturnsFirstPage()
    {
        var path = Path.Join(_library, "Saga 001.cbz");
        await SeedAsync(db => db.UserFileReadStatuses.Add(Status("alice", path, isRead: false, page: 9)));
        var service = CreateService("alice");
        _factory.FailAll();

        Assert.Equal(1, await service.GetReadingProgressAsync(path));
        Assert.True(_logger.Contains(LogLevel.Error, "Error getting reading progress"));
    }

    private FileStoreService CreateService(string? userId, IMemoryCache? cache = null) =>
        new(
            new TestOptionsMonitor<AppSettings>(new AppSettings
            {
                WatchedDirectory = _library,
                DuplicateDirectory = string.Empty,
            }),
            _logger,
            _factory,
            eventBroadcaster: null,
            memoryCache: cache,
            userContext: new TestUserContextAccessor(userId));

    private static IEnumerable<string> Lazily(params string[] paths)
    {
        foreach (var path in paths)
        {
            yield return path;
        }
    }

    private static ComicFileEntity FileRow(string path) => new()
    {
        FilePath = path,
        FileName = Path.GetFileName(path),
        Directory = Path.GetDirectoryName(path)!,
        FileSize = 5,
        LastModified = T0,
    };

    private static UserFileReadStatusEntity Status(string userId, string path, bool isRead, int page = 1, DateTime? lastRead = null) => new()
    {
        UserId = userId,
        FilePath = path,
        IsRead = isRead,
        CurrentPage = page,
        LastReadDate = lastRead,
        CreatedAt = T0,
        UpdatedAt = T0,
    };

    private static ReadingProgressEntity Progress(
        string userId,
        string contentId,
        int current,
        int total,
        double percent,
        DateTime lastRead,
        DateTime? completed) => new()
    {
        UserId = userId,
        ContentId = contentId,
        CurrentPage = current,
        TotalPages = total,
        PercentComplete = percent,
        LastReadAt = lastRead,
        CompletedAt = completed,
    };

    private async Task SeedAsync(Action<ComicMaintainerDbContext> seed)
    {
        await using var db = await _db.CreateDbContextAsync();
        seed(db);
        await db.SaveChangesAsync();
    }

    private async Task<List<UserFileReadStatusEntity>> AllStatusesAsync()
    {
        await using var db = await _db.CreateDbContextAsync();
        return await db.UserFileReadStatuses.AsNoTracking().ToListAsync();
    }

    /// <summary>Wraps a real factory and fails selected context creations on demand.</summary>
    private sealed class FlakyDbContextFactory : IDbContextFactory<ComicMaintainerDbContext>
    {
        private readonly IDbContextFactory<ComicMaintainerDbContext> _inner;
        private int _created;
        private Func<int, bool>? _shouldFail;

        public FlakyDbContextFactory(IDbContextFactory<ComicMaintainerDbContext> inner) => _inner = inner;

        public int Created => Volatile.Read(ref _created);

        public void FailAll() => _shouldFail = _ => true;

        /// <summary>Fails only the <paramref name="nth"/> (1-based) context requested from now on.</summary>
        public void FailNth(int nth)
        {
            var target = Created + nth;
            _shouldFail = ordinal => ordinal == target;
        }

        public ComicMaintainerDbContext CreateDbContext()
        {
            var ordinal = Interlocked.Increment(ref _created);
            if (_shouldFail?.Invoke(ordinal) == true)
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

        public bool Contains(LogLevel level, string fragment) =>
            _entries.Any(e => e.Level == level && e.Message.Contains(fragment, StringComparison.Ordinal));
    }
}
