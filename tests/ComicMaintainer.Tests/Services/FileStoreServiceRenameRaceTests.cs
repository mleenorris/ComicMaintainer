using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Covers the races in <see cref="FileStoreService.UpdateFilePathAsync"/> with concurrent
/// writers (the FileSystemWatcher) that insert the destination row or delete the source row.
/// </summary>
/// <remarks>
/// These run against real SQLite rather than the in-memory provider on purpose: the
/// behaviour under test is recovery from the unique index on <c>ComicFiles.FilePath</c>,
/// which the in-memory provider does not enforce, and from statements that match no row
/// once a concurrent writer has deleted it.
/// </remarks>
public class FileStoreServiceRenameRaceTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly string _testDirectory =
        Path.Combine(Path.GetTempPath(), $"filestore_rename_race_{Guid.NewGuid()}");
    private readonly BeforeSaveInterceptor _interceptor = new();
    private readonly Mock<ILogger<FileStoreService>> _logger = new();

    private ServiceProvider _provider = null!;
    private IDbContextFactory<ComicMaintainerDbContext> _factory = null!;
    private FileStoreService _service = null!;

    private string OldPath => Path.Combine(_testDirectory, "Chapter 100.cbz");
    private string NewPath => Path.Combine(_testDirectory, "Monarch - Chapter 0100.cbz");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_testDirectory);
        File.WriteAllText(OldPath, "content");

        await _connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddDbContextFactory<ComicMaintainerDbContext>(opt =>
            opt.UseSqlite(_connection).AddInterceptors(_interceptor));
        _provider = services.BuildServiceProvider();

        _factory = _provider.GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
        }

        _service = new FileStoreService(
            new TestOptionsMonitor<AppSettings>(new AppSettings { WatchedDirectory = _testDirectory }),
            _logger.Object,
            _factory,
            userContext: new TestUserContextAccessor("user-a"));
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
        try
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; a leftover temp directory must not fail the test run.
        }
    }

    [Fact]
    public async Task UpdateFilePathAsync_NewPathRowInsertedDuringSave_MergesInsteadOfFailing()
    {
        // Regression: the destination row is looked up before the rename is saved, so a
        // concurrent writer (the FileSystemWatcher observes the move as a Created event and
        // inserts a stub row) can slip a row in between the two and make the rename violate
        // the unique index on ComicFiles.FilePath. That surfaced as a DbUpdateException,
        // left the stale old-path row behind and skipped the read-status move.
        await _service.AddFileAsync(OldPath);
        await _service.MarkFileNormalizedAsync(OldPath, true);
        await _service.MarkFileReadAsync(OldPath, true);
        await _service.ApplyUserMetadataEditAsync(
            OldPath,
            new ComicMetadata { Series = "Monarch", Issue = "100" },
            ComicMetadataFieldFlags.Series | ComicMetadataFieldFlags.Issue);

        File.Move(OldPath, NewPath);

        _interceptor.BeforeNextSave = InsertWatcherStubRowAsync;

        // Act
        await _service.UpdateFilePathAsync(OldPath, NewPath);

        // The hook is one-shot, so a null value proves the race was actually injected.
        Assert.Null(_interceptor.BeforeNextSave);

        await using var db = await _factory.CreateDbContextAsync();

        // The stale old-path row is gone and the surviving row carries the processing state
        // from the authoritative old row rather than the stub's defaults.
        Assert.False(await db.ComicFiles.AnyAsync(e => e.FilePath == OldPath));
        var surviving = Assert.Single(await db.ComicFiles.Where(e => e.FilePath == NewPath).ToListAsync());
        Assert.True(surviving.IsNormalized);
        Assert.Equal("Monarch", surviving.Metadata?.Series);

        // Per-user read state still follows the file.
        Assert.False(await db.UserFileReadStatuses.AnyAsync(e => e.FilePath == OldPath));
        Assert.True(await db.UserFileReadStatuses
            .AnyAsync(e => e.FilePath == NewPath && e.UserId == "user-a" && e.IsRead));

        // The collision is an expected concurrency outcome, so it must not be logged as an
        // error (Error-level logs are what raise an automated error report).
        _logger.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
    }

    [Fact]
    public async Task UpdateFilePathAsync_OldPathRowDeletedDuringSave_RestoresRowAtNewPath()
    {
        // Regression (#834): the watcher can report a move as a Deleted event for the old
        // path, and its RemoveFileAsync can delete the old-path row between the lookup and
        // the save of the in-place rename. The rename then matched no row, which surfaced as
        // a DbUpdateConcurrencyException logged at Error, left the moved file without a row
        // (losing its processing state) and skipped the read-status move.
        var originalRow = await TrackFileAndMoveItAsync();

        _interceptor.BeforeNextSave = () => _service.RemoveFileAsync(OldPath);

        // Act
        await _service.UpdateFilePathAsync(OldPath, NewPath);

        // The hook is one-shot, so a null value proves the race was actually injected.
        Assert.Null(_interceptor.BeforeNextSave);
        await AssertMovedWithStateAsync(originalRow);
    }

    [Fact]
    public async Task UpdateFilePathAsync_OldPathRowDeletedThenNewPathRowInserted_MergesOntoInsertedRow()
    {
        // An unpaired move is reported as Deleted (old path) plus Created (new path), so the
        // row restored at the new path can in turn collide with the stub row the Created
        // handler inserts there. The state must then be merged onto that row instead.
        var originalRow = await TrackFileAndMoveItAsync();

        _interceptor.BeforeNextSave = async () =>
        {
            await _service.RemoveFileAsync(OldPath);
            _interceptor.BeforeNextSave = InsertWatcherStubRowAsync;
        };

        // Act
        await _service.UpdateFilePathAsync(OldPath, NewPath);

        Assert.Null(_interceptor.BeforeNextSave);
        await AssertMovedWithStateAsync(originalRow);
    }

    [Fact]
    public async Task UpdateFilePathAsync_OldPathRowWithoutMetadataDeletedDuringSave_KeepsLastWriteAt()
    {
        // The merge only copies LastWriteAt alongside metadata, so a row restored without it
        // would lose the stamp for good when the moved file has no metadata yet.
        await _service.AddFileAsync(OldPath);
        await _service.MarkFileNormalizedAsync(OldPath, true);
        var lastWriteAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        await using (var seed = await _factory.CreateDbContextAsync())
        {
            var row = await seed.ComicFiles.SingleAsync(e => e.FilePath == OldPath);
            Assert.Null(row.Metadata);
            row.LastWriteAt = lastWriteAt;
            await seed.SaveChangesAsync();
        }

        File.Move(OldPath, NewPath);

        _interceptor.BeforeNextSave = () => _service.RemoveFileAsync(OldPath);

        // Act
        await _service.UpdateFilePathAsync(OldPath, NewPath);

        Assert.Null(_interceptor.BeforeNextSave);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.False(await db.ComicFiles.AnyAsync(e => e.FilePath == OldPath));
        var moved = Assert.Single(await db.ComicFiles.Where(e => e.FilePath == NewPath).ToListAsync());
        Assert.True(moved.IsNormalized);
        Assert.Equal(lastWriteAt, moved.LastWriteAt);
    }

    [Fact]
    public async Task UpdateFilePathAsync_OldPathRowDeletedDuringMerge_KeepsMergedRow()
    {
        // With a stub row already at the new path, the old row's state is merged onto the
        // stub and the old row deleted in the same save. If a concurrent writer deletes the
        // old row first, that delete matches no row; it must not take the merge down with it.
        var originalRow = await TrackFileAndMoveItAsync();
        await InsertWatcherStubRowAsync();

        _interceptor.BeforeNextSave = () => _service.RemoveFileAsync(OldPath);

        // Act
        await _service.UpdateFilePathAsync(OldPath, NewPath);

        Assert.Null(_interceptor.BeforeNextSave);
        await AssertMovedWithStateAsync(originalRow);
    }

    /// <summary>
    /// Tracks the file at <see cref="OldPath"/> with processing state, edited metadata and a
    /// read mark, then moves it to <see cref="NewPath"/> on disk the way the processor does
    /// before updating the store. Returns the row's original persisted state.
    /// </summary>
    private async Task<ComicFileEntity> TrackFileAndMoveItAsync()
    {
        await _service.AddFileAsync(OldPath);
        await _service.MarkFileNormalizedAsync(OldPath, true);
        await _service.MarkFileReadAsync(OldPath, true);
        await _service.ApplyUserMetadataEditAsync(
            OldPath,
            new ComicMetadata { Series = "Monarch", Issue = "100" },
            ComicMetadataFieldFlags.Series | ComicMetadataFieldFlags.Issue);

        File.Move(OldPath, NewPath);

        await using var db = await _factory.CreateDbContextAsync();
        var row = await db.ComicFiles.SingleAsync(e => e.FilePath == OldPath);
        row.SeriesMetadataVersion = 7;
        row.MetadataVersion = 8;
        row.WrittenMetadataVersion = 6;
        row.LastDbEditAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        row.LastWriteAt = new DateTime(2026, 1, 2, 3, 4, 6, DateTimeKind.Utc);
        await db.SaveChangesAsync();
        return row;
    }

    /// <summary>
    /// Asserts that the file's row, processing state and read state all followed it to
    /// <see cref="NewPath"/>, and that the race was not reported as an error.
    /// </summary>
    private async Task AssertMovedWithStateAsync(ComicFileEntity originalRow)
    {
        await using var db = await _factory.CreateDbContextAsync();

        Assert.False(await db.ComicFiles.AnyAsync(e => e.FilePath == OldPath));
        var row = Assert.Single(await db.ComicFiles.Where(e => e.FilePath == NewPath).ToListAsync());
        Assert.True(row.IsNormalized);
        Assert.Equal("Monarch", row.Metadata?.Series);
        Assert.Equal(FileMetadataSource.UserEdit.ToString(), row.MetadataSource);
        Assert.Equal(originalRow.CreatedAt, row.CreatedAt);
        Assert.Equal(originalRow.SeriesMetadataVersion, row.SeriesMetadataVersion);
        Assert.Equal(originalRow.MetadataVersion, row.MetadataVersion);
        Assert.Equal(originalRow.WrittenMetadataVersion, row.WrittenMetadataVersion);
        Assert.Equal(originalRow.LastDbEditAt, row.LastDbEditAt);
        Assert.Equal(originalRow.LastWriteAt, row.LastWriteAt);

        Assert.False(await db.UserFileReadStatuses.AnyAsync(e => e.FilePath == OldPath));
        Assert.True(await db.UserFileReadStatuses
            .AnyAsync(e => e.FilePath == NewPath && e.UserId == "user-a" && e.IsRead));

        // What the library shows: one entry, at the new path, still normalized and read.
        var listed = Assert.Single(await _service.GetAllFilesAsync());
        Assert.Equal(NewPath, listed.FilePath);
        Assert.True(listed.IsNormalized);
        Assert.True(listed.IsRead);

        // Error-level logs are what raise an automated error report.
        _logger.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
    }

    /// <summary>
    /// Inserts the default stub row the watcher would create for the move target.
    /// </summary>
    private async Task InsertWatcherStubRowAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.ComicFiles.Add(new ComicFileEntity
        {
            FilePath = NewPath,
            FileName = Path.GetFileName(NewPath),
            Directory = _testDirectory,
            FileSize = new FileInfo(NewPath).Length,
            LastModified = File.GetLastWriteTime(NewPath),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Runs a one-shot callback immediately before the next <c>SaveChangesAsync</c>, which
    /// is the only window in which the race under test can occur.
    /// </summary>
    private sealed class BeforeSaveInterceptor : SaveChangesInterceptor
    {
        public Func<Task>? BeforeNextSave { get; set; }

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
    }
}
