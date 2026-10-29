using System.Collections.Concurrent;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Covers the UNIQUE-constraint race recovery in <see cref="FileStoreService"/>: another
/// writer inserts the same file (or the same user/file read-status pair) between the
/// service's "does a row exist?" lookup and its insert.
/// </summary>
/// <remarks>
/// Runs against real SQLite because the in-memory provider does not enforce unique indexes.
/// The competing insert is injected from a <see cref="SaveChangesInterceptor"/>, i.e. exactly
/// inside the window being raced.
/// </remarks>
public sealed class FileStoreServiceUniqueRaceTests : IAsyncLifetime
{
    public enum Flag
    {
        Duplicate,
        Renamed,
        Normalized,
    }

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly string _library = Path.Join(Path.GetTempPath(), $"fss_unique_{Guid.NewGuid():N}");
    private readonly SaveHookInterceptor _interceptor = new();
    private readonly ListLogger _logger = new();

    private ServiceProvider _provider = null!;
    private IDbContextFactory<ComicMaintainerDbContext> _factory = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_library);
        await _connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddDbContextFactory<ComicMaintainerDbContext>(o => o.UseSqlite(_connection).AddInterceptors(_interceptor));
        _provider = services.BuildServiceProvider();
        _factory = _provider.GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>();

        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
        try
        {
            Directory.Delete(_library, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; a leftover temp directory must not fail the run.
        }
    }

    [Fact]
    public async Task AddFileAsync_RowInsertedConcurrently_UpdatesTheWinningRowAndKeepsItsState()
    {
        var path = WriteComic("Saga 001.cbz");
        var service = CreateService();
        _interceptor.BeforeNextSave = () => InsertFileRowAsync(path, renamedAndNormalized: true);

        await service.AddFileAsync(path);

        Assert.True(_logger.Contains(LogLevel.Debug, "File entity already exists (race condition)"));
        // Regression: the retry re-saved the context with the failed insert still tracked, so it
        // collided again and the update was lost with an Error log.
        Assert.True(_logger.Contains(LogLevel.Debug, "Updated existing file in database after retry"));
        Assert.False(_logger.Has(LogLevel.Error));
        var row = Assert.Single(await FileRowsAsync(path));
        Assert.True(row.IsRenamed);
        Assert.True(row.IsNormalized);
        Assert.Equal(new FileInfo(path).Length, row.FileSize);
        Assert.True(await service.FileExistsAsync(path));
    }

    [Theory]
    [InlineData(Flag.Duplicate)]
    [InlineData(Flag.Renamed)]
    [InlineData(Flag.Normalized)]
    public async Task MarkFlag_RowInsertedConcurrently_SetsTheFlagOnTheWinningRow(Flag flag)
    {
        var path = WriteComic("Saga 002.cbz");
        var service = CreateService();
        _interceptor.BeforeNextSave = () => InsertFileRowAsync(path, renamedAndNormalized: false);

        await (flag switch
        {
            Flag.Duplicate => service.MarkFileDuplicateAsync(path, true),
            Flag.Renamed => service.MarkFileRenamedAsync(path, true),
            _ => service.MarkFileNormalizedAsync(path, true),
        });

        Assert.True(_logger.Contains(LogLevel.Debug, "File entity already exists (race condition)"));
        // Regression: the retry re-saved the context with the failed insert still tracked, so it
        // collided again and the flag was lost with an Error log.
        Assert.True(_logger.Contains(LogLevel.Debug, "after retry"));
        Assert.False(_logger.Has(LogLevel.Error));
        var row = Assert.Single(await FileRowsAsync(path));
        Assert.Equal(flag == Flag.Duplicate, row.IsDuplicate);
        Assert.Equal(flag == Flag.Renamed, row.IsRenamed);
        Assert.Equal(flag == Flag.Normalized, row.IsNormalized);
    }

    [Fact]
    public async Task MarkFileReadAsync_StatusRowInsertedConcurrently_MarksTheSurvivingRowRead()
    {
        var path = Path.Join(_library, "Saga 003.cbz");
        var service = CreateService("alice");
        _interceptor.BeforeNextSave = () => InsertStatusRowAsync("alice", path, isRead: false, page: 7);

        await service.MarkFileReadAsync(path, read: true);

        var row = Assert.Single(await StatusRowsAsync("alice", path));
        Assert.True(row.IsRead);
        Assert.NotNull(row.LastReadDate);
        Assert.False(_logger.Has(LogLevel.Error));
    }

    [Fact]
    public async Task SaveReadingProgressAsync_StatusRowInsertedConcurrently_StoresThePageOnTheSurvivingRow()
    {
        var path = Path.Join(_library, "Saga 004.cbz");
        var service = CreateService("alice");
        _interceptor.BeforeNextSave = () => InsertStatusRowAsync("alice", path, isRead: false, page: 3);

        await service.SaveReadingProgressAsync(path, 9);

        var row = Assert.Single(await StatusRowsAsync("alice", path));
        Assert.Equal(9, row.CurrentPage);
        Assert.Equal(9, await service.GetReadingProgressAsync(path));
        Assert.False(_logger.Has(LogLevel.Error));
    }

    [Fact]
    public async Task MarkFileReadAsync_MarkingUnreadWhileStatusRowInsertedConcurrently_KeepsTheWinnersLastReadDate()
    {
        var path = Path.Join(_library, "Saga 005.cbz");
        var lastRead = new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        var service = CreateService("alice");
        _interceptor.BeforeNextSave = () => InsertStatusRowAsync("alice", path, isRead: true, page: 7, lastRead);

        await service.MarkFileReadAsync(path, read: false);

        var row = Assert.Single(await StatusRowsAsync("alice", path));
        Assert.False(row.IsRead);
        Assert.Equal(lastRead, row.LastReadDate);
        Assert.False(_logger.Has(LogLevel.Error));
    }

    [Fact]
    public async Task SaveReadingProgressAsync_MarkedReadConcurrently_KeepsTheFileRead()
    {
        // The web reader posts progress and mark-read together when the page it shows is the
        // last one, so for a comic opened for the first time both insert the first status row.
        var path = Path.Join(_library, "Saga 008.cbz");
        var service = CreateService("alice");
        _interceptor.BeforeNextSave = () => InsertStatusRowAsync("alice", path, isRead: true, page: 1);

        await service.SaveReadingProgressAsync(path, 9);

        // Regression: the losing request copied its whole row onto the winner, so its default
        // IsRead = false undid the mark-read.
        var row = Assert.Single(await StatusRowsAsync("alice", path));
        Assert.True(row.IsRead);
        Assert.Equal(9, row.CurrentPage);
        Assert.False(_logger.Has(LogLevel.Error));
    }

    [Fact]
    public async Task MarkFileReadAsync_ProgressSavedConcurrently_KeepsTheSavedPage()
    {
        var path = Path.Join(_library, "Saga 009.cbz");
        var service = CreateService("alice");
        _interceptor.BeforeNextSave = () => InsertStatusRowAsync("alice", path, isRead: false, page: 9);

        await service.MarkFileReadAsync(path, read: true);

        // Regression: as above, the losing request's default CurrentPage = 1 replaced the saved page.
        var row = Assert.Single(await StatusRowsAsync("alice", path));
        Assert.True(row.IsRead);
        Assert.Equal(9, row.CurrentPage);
        Assert.Equal(9, await service.GetReadingProgressAsync(path));
        Assert.False(_logger.Has(LogLevel.Error));
    }

    [Fact]
    public async Task MarkFileReadAsync_ConflictingStatusRowVanishesBeforeRecovery_LogsTheFailure()
    {
        var path = Path.Join(_library, "Saga 006.cbz");
        var service = CreateService("alice");
        _interceptor.BeforeNextSave = () => InsertStatusRowAsync("alice", path, isRead: true, page: 2);
        _interceptor.AfterNextFailure = () => DeleteStatusRowsAsync("alice", path);

        await service.MarkFileReadAsync(path, read: true);

        Assert.True(_logger.Contains(LogLevel.Error, "Error updating read status in database"));
        Assert.True((await StatusRowsAsync("alice", path)).Count <= 1);
    }

    [Fact]
    public async Task UpdateFilePathAsync_ConflictingTargetRowVanishesBeforeRecovery_LogsTheFailureWithoutDuplicatingRows()
    {
        var oldPath = WriteComic("Saga 007.cbz");
        var newPath = Path.Join(_library, "Saga 007 (renamed).cbz");
        var service = CreateService();
        await service.AddFileAsync(oldPath);
        File.Move(oldPath, newPath);
        _interceptor.BeforeNextSave = () => InsertFileRowAsync(newPath, renamedAndNormalized: false);
        _interceptor.AfterNextFailure = () => DeleteFileRowsAsync(newPath);

        await service.UpdateFilePathAsync(oldPath, newPath);

        Assert.True(_logger.Contains(LogLevel.Error, "Error updating file path in database"));
        Assert.False(_logger.Contains(LogLevel.Debug, "New path row was created concurrently"));
        var rows = (await FileRowsAsync(oldPath)).Concat(await FileRowsAsync(newPath)).ToList();
        Assert.Single(rows);
    }

    private FileStoreService CreateService(string? userId = null) =>
        new(
            new TestOptionsMonitor<AppSettings>(new AppSettings
            {
                WatchedDirectory = _library,
                DuplicateDirectory = string.Empty,
            }),
            _logger,
            _factory,
            userContext: new TestUserContextAccessor(userId));

    private string WriteComic(string name)
    {
        var path = Path.Join(_library, name);
        File.WriteAllText(path, "comic");
        return path;
    }

    private async Task InsertFileRowAsync(string path, bool renamedAndNormalized)
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.ComicFiles.Add(new ComicFileEntity
        {
            FilePath = path,
            FileName = Path.GetFileName(path),
            Directory = Path.GetDirectoryName(path)!,
            FileSize = 1,
            LastModified = DateTime.UtcNow,
            IsRenamed = renamedAndNormalized,
            IsNormalized = renamedAndNormalized,
            IsProcessed = renamedAndNormalized,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task InsertStatusRowAsync(string userId, string path, bool isRead, int page, DateTime? lastRead = null)
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.UserFileReadStatuses.Add(new UserFileReadStatusEntity
        {
            UserId = userId,
            FilePath = path,
            IsRead = isRead,
            CurrentPage = page,
            LastReadDate = lastRead,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task DeleteFileRowsAsync(string path)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.ComicFiles.Where(e => e.FilePath == path).ExecuteDeleteAsync();
    }

    private async Task DeleteStatusRowsAsync(string userId, string path)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.UserFileReadStatuses.Where(e => e.UserId == userId && e.FilePath == path).ExecuteDeleteAsync();
    }

    private async Task<List<ComicFileEntity>> FileRowsAsync(string path)
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.ComicFiles.AsNoTracking().Where(e => e.FilePath == path).ToListAsync();
    }

    private async Task<List<UserFileReadStatusEntity>> StatusRowsAsync(string userId, string path)
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.UserFileReadStatuses.AsNoTracking()
            .Where(e => e.UserId == userId && e.FilePath == path)
            .ToListAsync();
    }

    /// <summary>Runs one-shot callbacks immediately before the next save and after the next failed save.</summary>
    private sealed class SaveHookInterceptor : SaveChangesInterceptor
    {
        public Func<Task>? BeforeNextSave { get; set; }

        public Func<Task>? AfterNextFailure { get; set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var hook = BeforeNextSave;
            if (hook != null)
            {
                BeforeNextSave = null;
                await hook();
            }

            return await base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        public override async Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            var hook = AfterNextFailure;
            if (hook != null)
            {
                AfterNextFailure = null;
                await hook();
            }

            await base.SaveChangesFailedAsync(eventData, cancellationToken);
        }
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
