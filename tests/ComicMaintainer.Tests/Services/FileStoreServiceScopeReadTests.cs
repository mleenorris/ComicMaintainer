using System.Data.Common;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Covers <see cref="FileStoreService.MarkScopeReadAsync"/> against real SQLite: the whole
/// library, a folder or a list of files is marked read/unread for one user with set-based
/// statements in one transaction, reading progress follows, other users are untouched, and a
/// cancellation part-way changes nothing.
/// </summary>
public sealed class FileStoreServiceScopeReadTests : IAsyncLifetime
{
    private const string Library = "/library";
    private static readonly string SagaOne = $"{Library}/Saga/Saga 001.cbz";
    private static readonly string SagaTwo = $"{Library}/Saga/Saga 002.cbz";
    private static readonly string Batman = $"{Library}/Batman/Batman 001.cbz";
    private static readonly DateTime T0 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly CancelAfterCommandsInterceptor _interceptor = new();
    private readonly TestUserContextAccessor _user = new("alice");
    private ServiceProvider _provider = null!;
    private IDbContextFactory<ComicMaintainerDbContext> _factory = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddDbContextFactory<ComicMaintainerDbContext>(o => o.UseSqlite(_connection).AddInterceptors(_interceptor));
        _provider = services.BuildServiceProvider();
        _factory = _provider.GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>();

        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        foreach (var path in new[] { SagaOne, SagaTwo, Batman })
        {
            db.ComicFiles.Add(new ComicFileEntity
            {
                FilePath = path,
                FileName = Path.GetFileName(path),
                Directory = path[..path.LastIndexOf('/')],
            });
        }

        db.UserFileReadStatuses.AddRange(
            Status("alice", SagaOne, isRead: false, page: 4),
            Status("bob", SagaTwo, isRead: true, page: 9));
        db.ReadingProgresses.Add(new ReadingProgressEntity
        {
            UserId = "alice",
            ContentId = SagaOne,
            CurrentPage = 4,
            TotalPages = 20,
            PercentComplete = 20,
            LastReadAt = T0,
        });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task Library_MarksEveryTrackedFileReadForTheUserAndSyncsReadingProgress()
    {
        var service = CreateService();
        await service.InitializeFromDatabaseAsync();
        Assert.Empty(await service.GetFilteredFilesAsync("read"));
        var versionBefore = service.GetReadStateVersion("alice");

        var count = await service.MarkScopeReadAsync(FileReadScope.Library, read: true);

        Assert.Equal(3, count);
        var alice = await StatusesAsync("alice");
        Assert.Equal(new[] { Batman, SagaOne, SagaTwo }, alice.Keys.Order(StringComparer.Ordinal));
        Assert.All(alice.Values, s => Assert.True(s.IsRead));
        Assert.All(alice.Values, s => Assert.NotNull(s.LastReadDate));
        // The resume page follows the finished reading progress.
        Assert.Equal(20, alice[SagaOne].CurrentPage);
        Assert.Equal(1, alice[Batman].CurrentPage);

        var progress = await ProgressAsync("alice", SagaOne);
        Assert.Equal(20, progress.CurrentPage);
        Assert.Equal(100, progress.PercentComplete);
        Assert.NotNull(progress.CompletedAt);

        // Bob's state is his own.
        var bob = await StatusesAsync("bob");
        Assert.Equal(SagaTwo, Assert.Single(bob).Key);
        // The cached read set was invalidated, so the library shows the change at once.
        Assert.True(service.GetReadStateVersion("alice") > versionBefore);
        Assert.Equal(3, (await service.GetFilteredFilesAsync("read")).Count());
    }

    [Fact]
    public async Task Library_Unread_ClearsExistingRowsWithoutInsertingNewOnesAndResetsProgress()
    {
        var service = CreateService();
        await service.InitializeFromDatabaseAsync();
        await service.MarkScopeReadAsync(FileReadScope.Library, read: true);
        _user.UserId = "bob";

        var count = await service.MarkScopeReadAsync(FileReadScope.Library, read: false);

        Assert.Equal(3, count);
        // A missing row already means unread: nothing was inserted for Bob.
        var bob = await StatusesAsync("bob");
        Assert.False(Assert.Single(bob).Value.IsRead);
        // Alice is untouched by Bob's change.
        Assert.All((await StatusesAsync("alice")).Values, s => Assert.True(s.IsRead));

        _user.UserId = "alice";
        await service.MarkScopeReadAsync(FileReadScope.Library, read: false);
        var progress = await ProgressAsync("alice", SagaOne);
        Assert.Equal(1, progress.CurrentPage);
        Assert.Equal(0, progress.PercentComplete);
        Assert.Null(progress.CompletedAt);
        Assert.Equal(1, (await StatusesAsync("alice"))[SagaOne].CurrentPage);
        Assert.Empty(await service.GetFilteredFilesAsync("read"));
    }

    [Fact]
    public async Task Folder_MarksOnlyTheFilesInThatFolder()
    {
        var service = CreateService();

        var count = await service.MarkScopeReadAsync(FileReadScope.Folder("Saga"), read: true);

        Assert.Equal(2, count);
        var alice = await StatusesAsync("alice");
        Assert.True(alice[SagaOne].IsRead);
        Assert.True(alice[SagaTwo].IsRead);
        Assert.False(alice.ContainsKey(Batman));
    }

    [Fact]
    public async Task Files_MarksOnlyTrackedFilesAmongThePaths()
    {
        var service = CreateService();

        var count = await service.MarkScopeReadAsync(
            FileReadScope.Files(new[] { Batman, $"{Library}/Untracked.cbz" }), read: true);

        Assert.Equal(1, count);
        var alice = await StatusesAsync("alice");
        Assert.True(alice[Batman].IsRead);
        Assert.False(alice[SagaOne].IsRead);
        Assert.False(alice.ContainsKey($"{Library}/Untracked.cbz"));
    }

    [Fact]
    public async Task CancelledPartWay_RollsTheWholeChangeBackAndPropagates()
    {
        var service = CreateService();
        using var cancellation = new CancellationTokenSource();
        // Let the first update statement run, then cancel before the rest.
        _interceptor.CancelAfter(cancellation, nonQueries: 1);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.MarkScopeReadAsync(FileReadScope.Library, read: true, cancellation.Token));

        var alice = await StatusesAsync("alice");
        Assert.False(Assert.Single(alice).Value.IsRead);
        Assert.Equal(4, (await ProgressAsync("alice", SagaOne)).CurrentPage);
    }

    [Fact]
    public async Task NoUserInScope_ChangesNothing()
    {
        _user.UserId = null;
        var service = CreateService();

        Assert.Equal(0, await service.MarkScopeReadAsync(FileReadScope.Library, read: true));
        Assert.Single(await StatusesAsync("alice"));
    }

    private FileStoreService CreateService() => new(
        new TestOptionsMonitor<AppSettings>(new AppSettings { WatchedDirectory = Library }),
        NullLogger<FileStoreService>.Instance,
        _factory,
        userContext: _user);

    private static UserFileReadStatusEntity Status(string userId, string path, bool isRead, int page) => new()
    {
        UserId = userId,
        FilePath = path,
        IsRead = isRead,
        CurrentPage = page,
        CreatedAt = T0,
        UpdatedAt = T0,
    };

    private async Task<Dictionary<string, UserFileReadStatusEntity>> StatusesAsync(string userId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.UserFileReadStatuses.AsNoTracking()
            .Where(s => s.UserId == userId)
            .ToDictionaryAsync(s => s.FilePath, StringComparer.Ordinal);
    }

    private async Task<ReadingProgressEntity> ProgressAsync(string userId, string path)
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.ReadingProgresses.AsNoTracking().SingleAsync(p => p.UserId == userId && p.ContentId == path);
    }

    /// <summary>Cancels a token once a given number of non-query commands have executed.</summary>
    private sealed class CancelAfterCommandsInterceptor : DbCommandInterceptor
    {
        private CancellationTokenSource? _cancellation;
        private int _remaining;

        public void CancelAfter(CancellationTokenSource cancellation, int nonQueries)
        {
            _cancellation = cancellation;
            _remaining = nonQueries;
        }

        public override ValueTask<int> NonQueryExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            if (_cancellation is not null && Interlocked.Decrement(ref _remaining) == 0)
            {
                _cancellation.Cancel();
                _cancellation = null;
            }

            return base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
        }
    }
}
