using System.Collections.Concurrent;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Covers the write paths of <see cref="FileStoreService"/> (add / remove / move, series
/// metadata stamps and user metadata edits): path validation, database outages, concurrent
/// deletes, merging a moved file onto an existing destination row, and broadcast failures.
/// </summary>
public sealed class FileStoreServiceWritesTests : IDisposable
{
    private static readonly DateTime Jan2020 = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"fss_writes_{Guid.NewGuid():N}");
    private readonly string _library;
    private readonly string _dupes;
    private readonly string _outside;
    private readonly ServiceProvider _provider;
    private readonly IDbContextFactory<ComicMaintainerDbContext> _db;
    private readonly FlakyDbContextFactory _factory;
    private readonly BeforeSaveInterceptor _interceptor = new();
    private readonly ListLogger _logger = new();
    private readonly Mock<IEventBroadcaster> _broadcaster = new();

    public FileStoreServiceWritesTests()
    {
        _library = Directory.CreateDirectory(Path.Combine(_root, "library")).FullName;
        _dupes = Directory.CreateDirectory(Path.Combine(_root, "dupes")).FullName;
        _outside = Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;

        var dbName = $"fss_writes_{Guid.NewGuid()}";
        var services = new ServiceCollection();
        services.AddDbContextFactory<ComicMaintainerDbContext>(o =>
            o.UseInMemoryDatabase(dbName).AddInterceptors(_interceptor));
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

    // ---- AddFileAsync -------------------------------------------------------------------

    [Fact]
    public async Task AddFileAsync_PathOutsideLibrary_IsRejected()
    {
        var stray = WriteComic(_outside, "Stray.cbz");
        var service = CreateService();

        await service.AddFileAsync(stray);

        Assert.False(await service.FileExistsAsync(stray));
        Assert.Null(await FindRowAsync(stray));
        Assert.True(_logger.Contains(LogLevel.Warning, "Attempted to add file outside allowed directories"));
    }

    [Theory]
    [InlineData("", "Batman... Again 001.cbz")]
    [InlineData("", "What If..Vol 1.cbz")]
    [InlineData("...And Justice for All", "Issue 001.cbz")]
    public async Task AddFileAsync_PathWithConsecutiveDotsInsideLibrary_IsTracked(string folder, string name)
    {
        // Regression: a ".." substring check ran after Path.GetFullPath had already collapsed
        // every real ".." segment, so it only ever rejected legitimate names such as
        // "What If...?" and those comics never made it into the library.
        var path = WriteComic(Directory.CreateDirectory(Path.Combine(_library, folder)).FullName, name);
        var service = CreateService();

        await service.AddFileAsync(path);

        Assert.True(await service.FileExistsAsync(path));
        Assert.NotNull(await FindRowAsync(path));
        Assert.False(_logger.Has(LogLevel.Warning));
    }

    [Fact]
    public async Task AddFileAsync_PathTraversingOutOfTheLibrary_IsRejected()
    {
        WriteComic(_outside, "Stray.cbz");
        var traversal = Path.Combine(_library, "..", "outside", "Stray.cbz");
        var service = CreateService();

        await service.AddFileAsync(traversal);

        Assert.Empty(await service.GetAllFilesAsync());
        Assert.Empty(await AllRowsAsync());
        Assert.True(_logger.Contains(LogLevel.Warning, "Attempted to add file outside allowed directories"));
    }

    [Fact]
    public async Task AddFileAsync_EmptyPath_IsRejectedWithoutThrowing()
    {
        var service = CreateService();

        await service.AddFileAsync(string.Empty);

        Assert.Empty(await service.GetAllFilesAsync());
        Assert.Equal(0, _factory.Created);
        Assert.True(_logger.Contains(LogLevel.Warning, "Error validating path"));
    }

    [Fact]
    public async Task AddFileAsync_DuplicateDirectoryConfigured_AcceptsFilesThereButNotElsewhere()
    {
        var inDupes = WriteComic(_dupes, "Copy.cbz");
        var stray = WriteComic(_outside, "Stray.cbz");
        var service = CreateService(duplicateDirectory: _dupes);

        await service.AddFileAsync(inDupes);
        await service.AddFileAsync(stray);

        Assert.True(await service.FileExistsAsync(inDupes));
        Assert.NotNull(await FindRowAsync(inDupes));
        Assert.False(await service.FileExistsAsync(stray));
    }

    [Theory]
    [InlineData("library2")]
    [InlineData("library-old")]
    [InlineData("dupes2")]
    public async Task AddFileAsync_SiblingFolderSharingARootsPrefix_IsRejected(string sibling)
    {
        // Regression: containment was a bare prefix test, so "<root>2/x.cbz" counted as being
        // inside "<root>".
        var stray = WriteComic(Directory.CreateDirectory(Path.Combine(_root, sibling)).FullName, "Stray.cbz");
        var service = CreateService(duplicateDirectory: _dupes);

        await service.AddFileAsync(stray);

        Assert.False(await service.FileExistsAsync(stray));
        Assert.Null(await FindRowAsync(stray));
        Assert.True(_logger.Contains(LogLevel.Warning, "Attempted to add file outside allowed directories"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddFileAsync_LibraryConfiguredWithOrWithoutTrailingSeparator_TracksOnlyFilesInside(bool trailingSeparator)
    {
        var inside = WriteComic(_library, "Saga 016.cbz");
        var sibling = WriteComic(Directory.CreateDirectory(_library + "2").FullName, "Saga 016.cbz");
        var service = CreateService(watchedDirectory: trailingSeparator ? _library + Path.DirectorySeparatorChar : _library);

        await service.AddFileAsync(inside);
        await service.AddFileAsync(sibling);

        Assert.Equal(inside, Assert.Single(await service.GetAllFilesAsync()).FilePath);
        Assert.Equal(inside, Assert.Single(await AllRowsAsync()).FilePath);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("", "/")]
    [InlineData("/", "")]
    [InlineData("/", "/")]
    public async Task AddFileAsync_PathIsTheLibraryRootItself_IsNotReportedAsOutside(string rootSuffix, string pathSuffix)
    {
        var service = CreateService(watchedDirectory: _library + rootSuffix);

        await service.AddFileAsync(_library + pathSuffix);

        // The root is a directory, so there is nothing to track, but it is not outside the library.
        Assert.Empty(await service.GetAllFilesAsync());
        Assert.False(_logger.Has(LogLevel.Warning));
    }

    [Fact]
    public async Task AddFileAsync_FolderDifferingFromTheLibraryOnlyInCase_IsRejectedOnCaseSensitiveFileSystems()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // Regression: containment ignored case even where "/comics" and "/COMICS" are two
        // different directories.
        var stray = WriteComic(Directory.CreateDirectory(Path.Combine(_root, "LIBRARY")).FullName, "Stray.cbz");
        var service = CreateService();

        await service.AddFileAsync(stray);

        Assert.False(await service.FileExistsAsync(stray));
        Assert.Null(await FindRowAsync(stray));
        Assert.True(_logger.Contains(LogLevel.Warning, "Attempted to add file outside allowed directories"));
    }

    [Fact]
    public async Task AddFileAsync_StatusReadFails_KeepsMetadataFromSnapshot()
    {
        var path = WriteComic(_library, "Saga 003.cbz");
        var edited = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        await SeedAsync(Row(path, configure: e =>
        {
            e.Metadata = new ComicMetadata { Series = "Saga", Issue = "3" };
            e.SeriesMetadataVersion = 5;
            e.MetadataVersion = 4;
            e.WrittenMetadataVersion = 2;
            e.MetadataSource = nameof(FileMetadataSource.UserEdit);
            e.LastDbEditAt = edited;
        }));
        var service = CreateService();
        await service.InitializeFromDatabaseAsync();
        _factory.FailNth(1); // the status read fails; the write that follows succeeds

        await service.AddFileAsync(path);

        Assert.True(_logger.Contains(LogLevel.Error, "Error reading file status from database"));
        var file = Assert.Single(await service.GetAllFilesAsync());
        Assert.Equal("Saga", file.Metadata?.Series);
        Assert.Equal("3", file.Metadata?.Issue);
        Assert.Equal(5, file.SeriesMetadataVersion);
        Assert.Equal(4, file.MetadataVersion);
        Assert.Equal(2, file.WrittenMetadataVersion);
        Assert.Equal(FileMetadataSource.UserEdit, file.MetadataSource);
        Assert.Equal(edited, file.LastDbEditAt);
        Assert.Equal("Saga", (await FindRowAsync(path))!.Metadata?.Series);
    }

    [Fact]
    public async Task AddFileAsync_PersistFails_LogsErrorButStillTracksAndBroadcasts()
    {
        var path = WriteComic(_library, "Saga 004.cbz");
        var service = CreateService(broadcaster: _broadcaster.Object);
        _factory.FailNth(2); // the status read succeeds; the write fails

        await service.AddFileAsync(path);

        Assert.True(_logger.Contains(LogLevel.Error, "Error persisting file to database"));
        Assert.True(await service.FileExistsAsync(path));
        Assert.Null(await FindRowAsync(path));
        _broadcaster.Verify(b => b.BroadcastFileListUpdateAsync(), Times.Once);
    }

    [Fact]
    public async Task AddFileAsync_BroadcastFails_DoesNotPropagate()
    {
        var path = WriteComic(_library, "Saga 005.cbz");
        _broadcaster.Setup(b => b.BroadcastFileListUpdateAsync()).ThrowsAsync(new InvalidOperationException("hub down"));
        var service = CreateService(broadcaster: _broadcaster.Object);

        await service.AddFileAsync(path);

        Assert.NotNull(await FindRowAsync(path));
        Assert.True(_logger.Contains(LogLevel.Warning, "Failed to broadcast file list update"));
    }

    // ---- RemoveFileAsync ----------------------------------------------------------------

    [Fact]
    public async Task RemoveFileAsync_RowAlreadyGone_StillBroadcasts()
    {
        var service = CreateService(broadcaster: _broadcaster.Object);

        await service.RemoveFileAsync(Path.Combine(_library, "Ghost.cbz"));

        _broadcaster.Verify(b => b.BroadcastFileListUpdateAsync(), Times.Once);
        Assert.True(_logger.Contains(LogLevel.Debug, "File already removed from database"));
        Assert.False(_logger.Has(LogLevel.Error));
    }

    [Fact]
    public async Task RemoveFileAsync_RowDeletedConcurrently_IsTreatedAsAlreadyRemoved()
    {
        var path = Path.Combine(_library, "Saga 006.cbz");
        await SeedAsync(Row(path));
        var service = CreateService();
        await service.InitializeFromDatabaseAsync();
        _interceptor.BeforeNextSave = () => DeleteRowAsync(path);

        await service.RemoveFileAsync(path);

        Assert.True(_logger.Contains(LogLevel.Debug, "File already removed from database (concurrency)"));
        Assert.False(_logger.Has(LogLevel.Error));
        Assert.False(await service.FileExistsAsync(path));
        Assert.Null(await FindRowAsync(path));
    }

    [Fact]
    public async Task RemoveFileAsync_DatabaseUnavailable_StillUntracksFile()
    {
        var path = Path.Combine(_library, "Saga 007.cbz");
        await SeedAsync(Row(path));
        var service = CreateService();
        await service.InitializeFromDatabaseAsync();
        _factory.FailAll();

        await service.RemoveFileAsync(path);

        Assert.True(_logger.Contains(LogLevel.Error, "Error removing file from database"));
        Assert.False(await service.FileExistsAsync(path));
    }

    [Fact]
    public async Task RemoveFileAsync_BroadcastFails_DoesNotPropagate()
    {
        var path = Path.Combine(_library, "Saga 008.cbz");
        await SeedAsync(Row(path));
        _broadcaster.Setup(b => b.BroadcastFileListUpdateAsync()).ThrowsAsync(new InvalidOperationException("hub down"));
        var service = CreateService(broadcaster: _broadcaster.Object);

        await service.RemoveFileAsync(path);

        Assert.Null(await FindRowAsync(path));
        Assert.True(_logger.Contains(LogLevel.Warning, "Failed to broadcast file list update"));
    }

    // ---- UpdateFilePathAsync ------------------------------------------------------------

    [Fact]
    public async Task UpdateFilePathAsync_TargetOutsideLibrary_LeavesEntryUntouched()
    {
        var oldPath = Path.Combine(_library, "Old.cbz");
        await SeedAsync(Row(oldPath));
        var service = CreateService();
        await service.InitializeFromDatabaseAsync();
        var target = Path.Combine(_outside, "New.cbz");

        await service.UpdateFilePathAsync(oldPath, target);

        Assert.True(await service.FileExistsAsync(oldPath));
        Assert.False(await service.FileExistsAsync(target));
        Assert.NotNull(await FindRowAsync(oldPath));
        Assert.True(_logger.Contains(LogLevel.Warning, "outside allowed directories"));
    }

    [Fact]
    public async Task UpdateFilePathAsync_TargetTraversingOutOfTheLibrary_LeavesEntryUntouched()
    {
        var oldPath = Path.Combine(_library, "Old.cbz");
        await SeedAsync(Row(oldPath));
        var service = CreateService();
        await service.InitializeFromDatabaseAsync();
        var target = Path.Combine(_library, "..", "outside", "New.cbz");

        await service.UpdateFilePathAsync(oldPath, target);

        Assert.Equal(oldPath, Assert.Single(await service.GetAllFilesAsync()).FilePath);
        Assert.Equal(oldPath, Assert.Single(await AllRowsAsync()).FilePath);
        Assert.True(_logger.Contains(LogLevel.Warning, "outside allowed directories"));
    }

    [Fact]
    public async Task UpdateFilePathAsync_TargetNameWithConsecutiveDots_MovesTheEntry()
    {
        var oldPath = Path.Combine(_library, "What If 001.cbz");
        await SeedAsync(Row(oldPath, renamed: true, normalized: true));
        var service = CreateService();
        await service.InitializeFromDatabaseAsync();
        var newPath = WriteComic(_library, "What If... 001.cbz");

        await service.UpdateFilePathAsync(oldPath, newPath);

        var file = Assert.Single(await service.GetAllFilesAsync());
        Assert.Equal(newPath, file.FilePath);
        Assert.True(file.IsProcessed);
        var row = Assert.Single(await AllRowsAsync());
        Assert.Equal(newPath, row.FilePath);
        Assert.True(row.IsRenamed && row.IsNormalized);
        Assert.False(_logger.Has(LogLevel.Warning));
    }

    [Fact]
    public async Task UpdateFilePathAsync_TargetNotOnDisk_KeepsKnownSizeAndTimestamp()
    {
        var oldPath = Path.Combine(_library, "Old.cbz");
        var modified = new DateTime(2023, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        await SeedAsync(Row(oldPath, configure: e =>
        {
            e.FileSize = 1234;
            e.LastModified = modified;
        }));
        var service = CreateService();
        await service.InitializeFromDatabaseAsync();
        var newDirectory = Path.Combine(_library, "Saga");
        var newPath = Path.Combine(newDirectory, "Saga 001.cbz");

        await service.UpdateFilePathAsync(oldPath, newPath);

        var file = Assert.Single(await service.GetAllFilesAsync());
        Assert.Equal(newPath, file.FilePath);
        Assert.Equal("Saga 001.cbz", file.FileName);
        Assert.Equal(newDirectory, file.Directory);
        Assert.Equal(1234, file.FileSize);
        Assert.Equal(modified, file.LastModified);

        Assert.Null(await FindRowAsync(oldPath));
        var row = await FindRowAsync(newPath);
        Assert.NotNull(row);
        Assert.Equal("Saga 001.cbz", row.FileName);
        Assert.Equal(newDirectory, row.Directory);
        Assert.Equal(1234, row.FileSize);
        Assert.Equal(modified, row.LastModified);
    }

    [Fact]
    public async Task UpdateFilePathAsync_SameOldAndNewPath_IsANoOpThatKeepsTheEntry()
    {
        // Regression: the destination lookup found the source row itself, so the "merge onto
        // the existing destination row" path deleted the only row for the file.
        var path = WriteComic(_library, "Saga 017.cbz");
        await SeedAsync(Row(path, renamed: true, normalized: true, configure: e =>
        {
            e.Metadata = new ComicMetadata { Series = "Saga", Issue = "17" };
            e.CreatedAt = Jan2020;
        }));
        var service = CreateService(broadcaster: _broadcaster.Object);
        await service.InitializeFromDatabaseAsync();
        var before = service.ChangeVersion;

        await service.UpdateFilePathAsync(path, path);

        Assert.Equal(before, service.ChangeVersion);
        _broadcaster.Verify(b => b.BroadcastFileListUpdateAsync(), Times.Never);
        var row = Assert.Single(await AllRowsAsync());
        Assert.Equal(path, row.FilePath);
        Assert.True(row.IsRenamed && row.IsNormalized);
        Assert.Equal("Saga", row.Metadata?.Series);
        Assert.Equal(Jan2020, row.CreatedAt);
        var file = Assert.Single(await service.GetAllFilesAsync());
        Assert.Equal(path, file.FilePath);
        Assert.True(file.IsProcessed);
        Assert.False(_logger.Has(LogLevel.Error));
    }

    [Fact]
    public async Task UpdateFilePathAsync_DuplicateFile_MovesDuplicateMarkerToNewPath()
    {
        var oldPath = Path.Combine(_library, "Dupe.cbz");
        await SeedAsync(Row(oldPath, duplicate: true));
        var service = CreateService();
        await service.InitializeFromDatabaseAsync();
        var newPath = WriteComic(_library, "Dupe (2).cbz");

        await service.UpdateFilePathAsync(oldPath, newPath);

        Assert.True(Assert.Single(await service.GetAllFilesAsync()).IsDuplicate);
        Assert.Equal(1, (await service.GetFileCountsAsync()).duplicates);

        // The duplicate marker itself moved: a row recreated for the new path picks it up...
        await DeleteRowAsync(newPath);
        await service.AddFileAsync(newPath);
        Assert.True((await FindRowAsync(newPath))!.IsDuplicate);

        // ...while a file reappearing at the old path no longer carries it.
        WriteComic(_library, "Dupe.cbz");
        await service.AddFileAsync(oldPath);
        Assert.False((await FindRowAsync(oldPath))!.IsDuplicate);
    }

    [Fact]
    public async Task UpdateFilePathAsync_StubRowAtTarget_ReceivesTheOldRowsState()
    {
        var oldPath = Path.Combine(_library, "Chapter 7.cbz");
        var newPath = WriteComic(_library, "Saga - Chapter 007.cbz");
        var edited = new DateTime(2024, 2, 2, 0, 0, 0, DateTimeKind.Utc);
        var written = new DateTime(2024, 3, 3, 0, 0, 0, DateTimeKind.Utc);
        await SeedAsync(
            Row(oldPath, renamed: true, normalized: true, duplicate: true, configure: e =>
            {
                e.SeriesMetadataVersion = 5;
                e.MetadataVersion = 4;
                e.WrittenMetadataVersion = 3;
                e.Metadata = new ComicMetadata { Series = "Saga", Issue = "7" };
                e.LastDbEditAt = edited;
                e.LastWriteAt = written;
                e.MetadataSource = nameof(FileMetadataSource.UserEdit);
                e.CreatedAt = Jan2020;
            }),
            Row(newPath, configure: e => e.CreatedAt = DateTime.UtcNow));
        var service = CreateService();
        await service.InitializeFromDatabaseAsync();

        await service.UpdateFilePathAsync(oldPath, newPath);

        var row = Assert.Single(await AllRowsAsync());
        Assert.Equal(newPath, row.FilePath);
        Assert.True(row.IsRenamed);
        Assert.True(row.IsNormalized);
        Assert.True(row.IsDuplicate);
        Assert.True(row.IsProcessed);
        Assert.Equal(5, row.SeriesMetadataVersion);
        Assert.Equal(4, row.MetadataVersion);
        Assert.Equal(3, row.WrittenMetadataVersion);
        Assert.Equal("Saga", row.Metadata?.Series);
        Assert.Equal("7", row.Metadata?.Issue);
        Assert.Equal(edited, row.LastDbEditAt);
        Assert.Equal(written, row.LastWriteAt);
        Assert.Equal(nameof(FileMetadataSource.UserEdit), row.MetadataSource);
        Assert.Equal(Jan2020, row.CreatedAt);
    }

    [Fact]
    public async Task UpdateFilePathAsync_TargetRowAlreadyComplete_KeepsTheTargetsState()
    {
        var oldPath = Path.Combine(_library, "Chapter 8.cbz");
        var newPath = WriteComic(_library, "Saga - Chapter 008.cbz");
        await SeedAsync(
            Row(oldPath, configure: e =>
            {
                e.SeriesMetadataVersion = 1;
                e.MetadataVersion = 1;
                e.WrittenMetadataVersion = 1;
                e.Metadata = new ComicMetadata { Series = "Stale" };
                e.CreatedAt = Jan2020.AddYears(1);
            }),
            Row(newPath, renamed: true, normalized: true, duplicate: true, configure: e =>
            {
                e.SeriesMetadataVersion = 9;
                e.MetadataVersion = 8;
                e.WrittenMetadataVersion = 7;
                e.Metadata = new ComicMetadata { Series = "Current" };
                e.CreatedAt = Jan2020;
            }));
        var service = CreateService();
        await service.InitializeFromDatabaseAsync();

        await service.UpdateFilePathAsync(oldPath, newPath);

        var row = Assert.Single(await AllRowsAsync());
        Assert.Equal(newPath, row.FilePath);
        Assert.True(row.IsRenamed && row.IsNormalized && row.IsDuplicate && row.IsProcessed);
        Assert.Equal(9, row.SeriesMetadataVersion);
        Assert.Equal(8, row.MetadataVersion);
        Assert.Equal(7, row.WrittenMetadataVersion);
        Assert.Equal("Current", row.Metadata?.Series);
        Assert.Equal(Jan2020, row.CreatedAt);
    }

    [Fact]
    public async Task UpdateFilePathAsync_NeitherRowHasState_MergedRowStaysUnprocessed()
    {
        var oldPath = Path.Combine(_library, "Chapter 9.cbz");
        var newPath = WriteComic(_library, "Saga - Chapter 009.cbz");
        await SeedAsync(
            Row(oldPath, configure: e => e.CreatedAt = Jan2020.AddYears(2)),
            Row(newPath, configure: e => e.CreatedAt = Jan2020));
        var service = CreateService();
        await service.InitializeFromDatabaseAsync();

        await service.UpdateFilePathAsync(oldPath, newPath);

        var row = Assert.Single(await AllRowsAsync());
        Assert.Equal(newPath, row.FilePath);
        Assert.False(row.IsRenamed || row.IsNormalized || row.IsDuplicate || row.IsProcessed);
        Assert.Equal(0, row.SeriesMetadataVersion);
        Assert.Equal(0, row.MetadataVersion);
        Assert.Equal(0, row.WrittenMetadataVersion);
        Assert.Null(row.Metadata);
        Assert.Equal(Jan2020, row.CreatedAt);
    }

    [Fact]
    public async Task UpdateFilePathAsync_OldPathMissingFromDatabase_PersistsTheNewPath()
    {
        var oldPath = WriteComic(_library, "Old.cbz");
        var service = CreateService(broadcaster: _broadcaster.Object);
        await service.AddFileAsync(oldPath);
        await DeleteRowAsync(oldPath);
        var newPath = Path.Combine(_library, "New.cbz");
        File.Move(oldPath, newPath);
        _broadcaster.Invocations.Clear();

        await service.UpdateFilePathAsync(oldPath, newPath);

        Assert.NotNull(await FindRowAsync(newPath));
        Assert.False(await service.FileExistsAsync(oldPath));
        Assert.True(await service.FileExistsAsync(newPath));
        _broadcaster.Verify(b => b.BroadcastFileListUpdateAsync(), Times.Once);
    }

    [Fact]
    public async Task UpdateFilePathAsync_DatabaseUnavailable_MovesSnapshotAndStillBroadcasts()
    {
        var oldPath = Path.Combine(_library, "Old.cbz");
        await SeedAsync(Row(oldPath, renamed: true));
        var service = CreateService(broadcaster: _broadcaster.Object);
        await service.InitializeFromDatabaseAsync();
        var newPath = WriteComic(_library, "New.cbz");
        _factory.FailAll();

        await service.UpdateFilePathAsync(oldPath, newPath);

        Assert.True(_logger.Contains(LogLevel.Error, "Error updating file path in database"));
        Assert.False(await service.FileExistsAsync(oldPath));
        Assert.True(await service.IsFileRenamedAsync(newPath));
        _broadcaster.Verify(b => b.BroadcastFileListUpdateAsync(), Times.Once);
    }

    [Fact]
    public async Task UpdateFilePathAsync_BroadcastFails_DoesNotPropagate()
    {
        var oldPath = Path.Combine(_library, "Old.cbz");
        await SeedAsync(Row(oldPath));
        _broadcaster.Setup(b => b.BroadcastFileListUpdateAsync()).ThrowsAsync(new InvalidOperationException("hub down"));
        var service = CreateService(broadcaster: _broadcaster.Object);
        await service.InitializeFromDatabaseAsync();
        var newPath = WriteComic(_library, "New.cbz");

        await service.UpdateFilePathAsync(oldPath, newPath);

        Assert.NotNull(await FindRowAsync(newPath));
        Assert.True(_logger.Contains(LogLevel.Warning, "Failed to broadcast file list update after path update"));
    }

    [Fact]
    public async Task UpdateFilePathAsync_BroadcastDisabled_DoesNotNotifyClients()
    {
        var oldPath = Path.Combine(_library, "Old.cbz");
        await SeedAsync(Row(oldPath));
        var service = CreateService(broadcaster: _broadcaster.Object);
        await service.InitializeFromDatabaseAsync();
        var newPath = WriteComic(_library, "New.cbz");

        await service.UpdateFilePathAsync(oldPath, newPath, broadcastUpdate: false);

        Assert.NotNull(await FindRowAsync(newPath));
        _broadcaster.Verify(b => b.BroadcastFileListUpdateAsync(), Times.Never);
    }

    // ---- SetFileSeriesMetadataVersionAsync ----------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SetFileSeriesMetadataVersionAsync_BlankPath_DoesNothing(string path)
    {
        var service = CreateService();
        var before = service.ChangeVersion;

        await service.SetFileSeriesMetadataVersionAsync(path, 3);

        Assert.Equal(0, _factory.Created);
        Assert.Equal(before, service.ChangeVersion);
    }

    [Fact]
    public async Task SetFileSeriesMetadataVersionAsync_UntrackedOrUnchanged_DoesNotWrite()
    {
        var path = Path.Combine(_library, "Saga 009.cbz");
        await SeedAsync(Row(path, configure: e =>
        {
            e.SeriesMetadataVersion = 4;
            e.UpdatedAt = Jan2020;
        }));
        var service = CreateService();
        var before = service.ChangeVersion;

        await service.SetFileSeriesMetadataVersionAsync(Path.Combine(_library, "Untracked.cbz"), 4);
        await service.SetFileSeriesMetadataVersionAsync(path, 4);

        Assert.Equal(before, service.ChangeVersion);
        Assert.Equal(Jan2020, (await FindRowAsync(path))!.UpdatedAt);
    }

    [Fact]
    public async Task SetFileSeriesMetadataVersionAsync_NewVersion_UpdatesRowAndSnapshot()
    {
        var path = Path.Combine(_library, "Saga 010.cbz");
        await SeedAsync(Row(path, configure: e => e.SeriesMetadataVersion = 1));
        var service = CreateService();
        await service.InitializeFromDatabaseAsync();
        var before = service.ChangeVersion;

        await service.SetFileSeriesMetadataVersionAsync(path, 6);

        Assert.Equal(6, (await FindRowAsync(path))!.SeriesMetadataVersion);
        Assert.Equal(6, Assert.Single(await service.GetAllFilesAsync()).SeriesMetadataVersion);
        Assert.True(service.ChangeVersion > before);
    }

    [Fact]
    public async Task SetFileSeriesMetadataVersionAsync_RowNotInSnapshot_UpdatesDatabaseOnly()
    {
        var path = Path.Combine(_library, "Saga 011.cbz");
        await SeedAsync(Row(path));
        var service = CreateService();

        await service.SetFileSeriesMetadataVersionAsync(path, 2);

        Assert.Equal(2, (await FindRowAsync(path))!.SeriesMetadataVersion);
        Assert.Empty(await service.GetAllFilesAsync());
    }

    [Fact]
    public async Task SetFileSeriesMetadataVersionAsync_DatabaseUnavailable_LogsWarning()
    {
        var service = CreateService();
        _factory.FailAll();

        await service.SetFileSeriesMetadataVersionAsync(Path.Combine(_library, "Saga 012.cbz"), 2);

        Assert.True(_logger.Contains(LogLevel.Warning, "Failed to stamp series metadata version"));
    }

    // ---- ApplyUserMetadataEditAsync -----------------------------------------------------

    [Fact]
    public async Task ApplyUserMetadataEditAsync_UntrackedFile_Throws()
    {
        var service = CreateService();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ApplyUserMetadataEditAsync(Path.Combine(_library, "Nope.cbz"), new ComicMetadata(), ComicMetadataFieldFlags.None));

        Assert.Contains("not tracked", ex.Message);
    }

    [Fact]
    public async Task ApplyUserMetadataEditAsync_FullPatch_CopiesEveryFieldAndLocksRequestedFields()
    {
        var path = Path.Combine(_library, "Saga 013.cbz");
        await SeedAsync(Row(path));
        var service = CreateService(broadcaster: _broadcaster.Object);
        var before = service.ChangeVersion;
        var patch = new ComicMetadata
        {
            Series = "Saga",
            Title = "Chapter Thirteen",
            Issue = "13",
            Volume = "2",
            Publisher = "Image",
            Year = 2013,
            Summary = "Space opera.",
            Authors = { "Brian K. Vaughan", "Fiona Staples" },
            Tags = { "space" },
        };

        await service.ApplyUserMetadataEditAsync(path, patch, ComicMetadataFieldFlags.Series | ComicMetadataFieldFlags.Title);

        var row = await FindRowAsync(path);
        var metadata = row!.Metadata!;
        Assert.Equal("Saga", metadata.Series);
        Assert.Equal("Chapter Thirteen", metadata.Title);
        Assert.Equal("13", metadata.Issue);
        Assert.Equal("2", metadata.Volume);
        Assert.Equal("Image", metadata.Publisher);
        Assert.Equal(2013, metadata.Year);
        Assert.Equal("Space opera.", metadata.Summary);
        Assert.Equal(new[] { "Brian K. Vaughan", "Fiona Staples" }, metadata.Authors);
        Assert.Equal(new[] { "space" }, metadata.Tags);
        Assert.True(metadata.IsUserEdited);
        Assert.Equal((long)(ComicMetadataFieldFlags.Series | ComicMetadataFieldFlags.Title), metadata.UserLockedFieldsMask);
        Assert.Equal(1, row.MetadataVersion);
        Assert.Equal(nameof(FileMetadataSource.UserEdit), row.MetadataSource);
        Assert.NotNull(row.LastDbEditAt);

        var file = Assert.Single(await service.GetAllFilesAsync());
        Assert.Equal("Chapter Thirteen", file.Metadata?.Title);
        Assert.Equal(FileMetadataSource.UserEdit, file.MetadataSource);
        Assert.True(service.ChangeVersion > before);
        _broadcaster.Verify(b => b.BroadcastFileListUpdateAsync(), Times.Once);
    }

    [Fact]
    public async Task ApplyUserMetadataEditAsync_EmptyPatch_KeepsExistingValuesAndCapsVersion()
    {
        var path = Path.Combine(_library, "Saga 014.cbz");
        await SeedAsync(Row(path, configure: e =>
        {
            e.MetadataVersion = int.MaxValue;
            e.Metadata = new ComicMetadata
            {
                Series = "Saga",
                Title = "Fourteen",
                Issue = "14",
                Volume = "3",
                Publisher = "Image",
                Year = 2014,
                Summary = "Kept.",
                Authors = { "Brian K. Vaughan" },
                Tags = { "space" },
                UserLockedFieldsMask = (long)ComicMetadataFieldFlags.Series,
            };
        }));
        var service = CreateService();

        await service.ApplyUserMetadataEditAsync(path, new ComicMetadata(), ComicMetadataFieldFlags.Tags);

        var row = await FindRowAsync(path);
        var metadata = row!.Metadata!;
        Assert.Equal("Saga", metadata.Series);
        Assert.Equal("Fourteen", metadata.Title);
        Assert.Equal("14", metadata.Issue);
        Assert.Equal("3", metadata.Volume);
        Assert.Equal("Image", metadata.Publisher);
        Assert.Equal(2014, metadata.Year);
        Assert.Equal("Kept.", metadata.Summary);
        Assert.Equal(new[] { "Brian K. Vaughan" }, metadata.Authors);
        Assert.Equal(new[] { "space" }, metadata.Tags);
        Assert.True(metadata.IsUserEdited);
        Assert.Equal((long)(ComicMetadataFieldFlags.Series | ComicMetadataFieldFlags.Tags), metadata.UserLockedFieldsMask);
        Assert.Equal(int.MaxValue, row.MetadataVersion);
    }

    [Fact]
    public async Task ApplyUserMetadataEditAsync_BroadcastFails_StillPersistsTheEdit()
    {
        var path = Path.Combine(_library, "Saga 015.cbz");
        await SeedAsync(Row(path));
        _broadcaster.Setup(b => b.BroadcastFileListUpdateAsync()).ThrowsAsync(new InvalidOperationException("hub down"));
        var service = CreateService(broadcaster: _broadcaster.Object);

        await service.ApplyUserMetadataEditAsync(path, new ComicMetadata { Series = "Saga" }, ComicMetadataFieldFlags.None);

        Assert.Equal("Saga", (await FindRowAsync(path))!.Metadata?.Series);
        Assert.True(_logger.Contains(LogLevel.Warning, "Failed to broadcast file list update"));
    }

    // ---- helpers ------------------------------------------------------------------------

    private FileStoreService CreateService(
        string duplicateDirectory = "",
        IEventBroadcaster? broadcaster = null,
        string? watchedDirectory = null) =>
        new(
            new TestOptionsMonitor<AppSettings>(new AppSettings
            {
                WatchedDirectory = watchedDirectory ?? _library,
                DuplicateDirectory = duplicateDirectory,
            }),
            _logger,
            _factory,
            broadcaster);

    private static string WriteComic(string directory, string name)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, "comic");
        return path;
    }

    private static ComicFileEntity Row(
        string path,
        bool renamed = false,
        bool normalized = false,
        bool duplicate = false,
        Action<ComicFileEntity>? configure = null)
    {
        var row = new ComicFileEntity
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
        configure?.Invoke(row);
        return row;
    }

    private async Task SeedAsync(params ComicFileEntity[] rows)
    {
        await using var db = await _db.CreateDbContextAsync();
        db.ComicFiles.AddRange(rows);
        await db.SaveChangesAsync();
    }

    private async Task DeleteRowAsync(string path)
    {
        await using var db = await _db.CreateDbContextAsync();
        db.ComicFiles.Remove(await db.ComicFiles.SingleAsync(e => e.FilePath == path));
        await db.SaveChangesAsync();
    }

    private async Task<ComicFileEntity?> FindRowAsync(string path)
    {
        await using var db = await _db.CreateDbContextAsync();
        return await db.ComicFiles.AsNoTracking().SingleOrDefaultAsync(e => e.FilePath == path);
    }

    private async Task<List<ComicFileEntity>> AllRowsAsync()
    {
        await using var db = await _db.CreateDbContextAsync();
        return await db.ComicFiles.AsNoTracking().ToListAsync();
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

    /// <summary>Runs a one-shot callback immediately before the next save.</summary>
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
