using System.Data.Common;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Reader.Models;
using ComicMaintainer.Core.Reader.Services;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ComicMaintainer.Tests.Reader;

/// <summary>
/// <see cref="ReadingProgressService.RecordPageAsync"/>, the reader's per-page
/// save: the resume page (<c>UserFileReadStatuses</c>) and the durable progress
/// (<c>ReadingProgresses</c>) in one context and one write transaction.
/// </summary>
/// <remarks>
/// Runs against real SQLite: the in-memory provider neither enforces unique
/// indexes nor uses transactions.
/// </remarks>
public sealed class ReadingProgressRecordPageTests : IAsyncLifetime
{
    private const string FilePath = "/comics/Saga/Saga 001.cbz";

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly CountingInterceptor _counter = new();
    private ServiceProvider _provider = null!;
    private IDbContextFactory<ComicMaintainerDbContext> _factory = null!;
    private ReadingProgressService _service = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddDbContextFactory<ComicMaintainerDbContext>(o => o.UseSqlite(_connection).AddInterceptors(_counter));
        _provider = services.BuildServiceProvider();
        _factory = _provider.GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
        }

        _service = new ReadingProgressService(_factory, NullLogger<ReadingProgressService>.Instance);
        _counter.Reset();
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task FirstPage_CreatesBothRowsInOneWriteTransaction()
    {
        var before = DateTime.UtcNow;

        await _service.RecordPageAsync("alice", FilePath, page: 5, totalPages: 20);

        // One commit and two lookups, where the reader used to make two commits
        // and three lookups through three contexts.
        Assert.Equal(1, _counter.Commits);
        Assert.Equal(2, _counter.Selects);

        var status = Assert.Single(await StatusRowsAsync());
        Assert.Equal(5, status.CurrentPage);
        Assert.False(status.IsRead);
        Assert.True(status.LastReadDate >= before);

        var progress = Assert.Single(await ProgressRowsAsync());
        Assert.Equal(5, progress.CurrentPage);
        Assert.Equal(20, progress.TotalPages);
        Assert.Equal(25d, progress.PercentComplete);
        Assert.True(progress.LastReadAt >= before);
        Assert.Null(progress.CompletedAt);
    }

    [Fact]
    public async Task LaterPage_UpdatesBothRowsAndKeepsWhatItDoesNotOwn()
    {
        await SeedAsync(isRead: true, statusPage: 2, progressPage: 2, totalPages: 10,
            mode: ReaderMode.Longstrip, direction: ReadingDirection.RightToLeft);
        _counter.Reset();

        await _service.RecordPageAsync("alice", FilePath, page: 6, totalPages: 12);

        Assert.Equal(1, _counter.Commits);
        var status = Assert.Single(await StatusRowsAsync());
        Assert.Equal(6, status.CurrentPage);
        Assert.True(status.IsRead);
        var progress = Assert.Single(await ProgressRowsAsync());
        Assert.Equal(6, progress.CurrentPage);
        Assert.Equal(12, progress.TotalPages);
        Assert.Equal(50d, progress.PercentComplete);
        Assert.Equal((int)ReaderMode.Longstrip, progress.LastReaderMode);
        Assert.Equal((int)ReadingDirection.RightToLeft, progress.ReadingDirection);
    }

    [Theory]
    [InlineData(30, 30)]
    [InlineData(99, 30)]
    public async Task LastPageOrBeyond_IsClampedAndCompletes(int page, int expected)
    {
        await _service.RecordPageAsync("alice", FilePath, page, totalPages: 30);

        Assert.Equal(expected, Assert.Single(await StatusRowsAsync()).CurrentPage);
        var progress = Assert.Single(await ProgressRowsAsync());
        Assert.Equal(expected, progress.CurrentPage);
        Assert.Equal(100d, progress.PercentComplete);
        Assert.NotNull(progress.CompletedAt);
    }

    [Fact]
    public async Task UnknownPageCount_RecordsOnlyTheResumePage()
    {
        await _service.RecordPageAsync("alice", FilePath, page: 4, totalPages: 0);

        Assert.Equal(4, Assert.Single(await StatusRowsAsync()).CurrentPage);
        Assert.Empty(await ProgressRowsAsync());
    }

    [Fact]
    public async Task ProgressIsPerUser()
    {
        await _service.RecordPageAsync("alice", FilePath, page: 4, totalPages: 10);
        await _service.RecordPageAsync("bob", FilePath, page: 9, totalPages: 10);

        Assert.Equal(4, Assert.Single(await StatusRowsAsync("alice")).CurrentPage);
        Assert.Equal(9, Assert.Single(await StatusRowsAsync("bob")).CurrentPage);
    }

    [Fact]
    public async Task TheResumePage_IsWhatTheFileStoreReadsBack()
    {
        await _service.RecordPageAsync("alice", FilePath, page: 7, totalPages: 10);

        var fileStore = new FileStoreService(
            new TestOptionsMonitor<AppSettings>(new AppSettings { WatchedDirectory = "/comics", DuplicateDirectory = string.Empty }),
            NullLogger<FileStoreService>.Instance,
            _factory,
            userContext: new TestUserContextAccessor("alice"));

        Assert.Equal(7, await fileStore.GetReadingProgressAsync(FilePath));
    }

    [Fact]
    public async Task RowsInsertedConcurrently_AreUpdatedInsteadOfDuplicated()
    {
        // Another request (mark-read, a second tab) inserts both rows between this
        // save's lookups and its insert.
        _counter.BeforeNextSave = () => SeedAsync(isRead: true, statusPage: 3, progressPage: 3, totalPages: 10,
            mode: ReaderMode.SinglePage, direction: ReadingDirection.LeftToRight);

        await _service.RecordPageAsync("alice", FilePath, page: 8, totalPages: 10);

        var status = Assert.Single(await StatusRowsAsync());
        Assert.Equal(8, status.CurrentPage);
        Assert.True(status.IsRead);
        var progress = Assert.Single(await ProgressRowsAsync());
        Assert.Equal(8, progress.CurrentPage);
    }

    [Theory]
    [InlineData(null, FilePath, 1)]
    [InlineData("", FilePath, 1)]
    [InlineData("alice", "", 1)]
    [InlineData("alice", FilePath, 0)]
    public async Task InvalidArguments_AreRejected(string? userId, string filePath, int page) =>
        await Assert.ThrowsAnyAsync<ArgumentException>(() => _service.RecordPageAsync(userId!, filePath, page, totalPages: 10));

    // ---- helpers ----------------------------------------------------------------------

    private async Task SeedAsync(bool isRead, int statusPage, int progressPage, int totalPages, ReaderMode mode, ReadingDirection direction)
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.UserFileReadStatuses.Add(new UserFileReadStatusEntity
        {
            UserId = "alice",
            FilePath = FilePath,
            IsRead = isRead,
            CurrentPage = statusPage,
        });
        db.ReadingProgresses.Add(new ReadingProgressEntity
        {
            UserId = "alice",
            ContentId = FilePath,
            CurrentPage = progressPage,
            TotalPages = totalPages,
            LastReaderMode = (int)mode,
            ReadingDirection = (int)direction,
        });
        await db.SaveChangesAsync();
    }

    private async Task<List<UserFileReadStatusEntity>> StatusRowsAsync(string userId = "alice")
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.UserFileReadStatuses.AsNoTracking().Where(s => s.UserId == userId && s.FilePath == FilePath).ToListAsync();
    }

    private async Task<List<ReadingProgressEntity>> ProgressRowsAsync(string userId = "alice")
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.ReadingProgresses.AsNoTracking().Where(p => p.UserId == userId && p.ContentId == FilePath).ToListAsync();
    }

    /// <summary>
    /// Counts committed transactions and SELECT commands, and can run a hook just
    /// before the next <c>SaveChanges</c> (inside the window a concurrent insert races).
    /// </summary>
    private sealed class CountingInterceptor : DbCommandInterceptor, IDbTransactionInterceptor, ISaveChangesInterceptor
    {
        private int _commits;
        private int _selects;

        public int Commits => Volatile.Read(ref _commits);

        public int Selects => Volatile.Read(ref _selects);

        public Func<Task>? BeforeNextSave { get; set; }

        public void Reset()
        {
            Interlocked.Exchange(ref _commits, 0);
            Interlocked.Exchange(ref _selects, 0);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref _selects);
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _commits);
            return Task.CompletedTask;
        }

        public async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var hook = BeforeNextSave;
            if (hook is not null)
            {
                BeforeNextSave = null;
                await hook();
            }

            return result;
        }
    }
}
