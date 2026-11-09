using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using Microsoft.Data.Sqlite;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Covers the guards of <see cref="FileStoreService.CleanupStaleEntriesAsync"/> against real
/// SQLite and a real library folder: a missing, empty or unmarked library never loses rows,
/// a scheduled run only deletes a file that stayed missing across two runs, a run that would
/// remove more than the configured share removes nothing without confirmation, and a file
/// that comes back keeps its row and everything stored on it.
/// </summary>
public sealed class FileStoreServiceStaleCleanupTests : IAsyncLifetime
{
    private readonly string _root = Path.Join(Path.GetTempPath(), $"fss_stale_{Guid.NewGuid():N}");
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly Mock<IEventBroadcaster> _broadcaster = new();
    private readonly BeforeDeleteInterceptor _beforeDelete = new();
    private readonly AppSettings _settings = new();
    private string _library = null!;
    private ServiceProvider _provider = null!;
    private IDbContextFactory<ComicMaintainerDbContext> _factory = null!;

    public async Task InitializeAsync()
    {
        _library = Directory.CreateDirectory(Path.Join(_root, "library")).FullName;
        _settings.WatchedDirectory = _library;
        _settings.DuplicateDirectory = string.Empty;

        await _connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddDbContextFactory<ComicMaintainerDbContext>(o => o.UseSqlite(_connection).AddInterceptors(_beforeDelete));
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
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; a leftover temp directory must not fail the run.
        }
    }

    [Fact]
    public async Task MissingWatchedDirectory_RemovesAndStampsNothingEvenWhenConfirmed()
    {
        var paths = await TrackAsync(present: 0, missing: 3);
        Directory.Delete(_library, recursive: true);
        var service = await CreateServiceAsync();

        var result = await service.CleanupStaleEntriesAsync(new StaleCleanupOptions(IsManual: true, Confirmed: true));

        Assert.Equal(StaleCleanupStatus.WatchedDirectoryUnavailable, result.Status);
        Assert.False(result.RequiresConfirmation);
        Assert.Equal(0, result.RemovedCount);
        await AssertRowsAsync(paths, missingSinceSet: false);
    }

    [Fact]
    public async Task EmptyWatchedDirectory_ScheduledRun_RemovesAndStampsNothing()
    {
        // What an unmounted share looks like: the mount point exists but is empty.
        var paths = await TrackAsync(present: 0, missing: 50);
        var service = await CreateServiceAsync();

        var result = await service.CleanupStaleEntriesAsync(StaleCleanupOptions.Scheduled);

        Assert.Equal(StaleCleanupStatus.WatchedDirectoryEmpty, result.Status);
        Assert.Equal(50, result.TrackedCount);
        Assert.Equal(0, result.RemovedCount);
        await AssertRowsAsync(paths, missingSinceSet: false);
        Assert.Equal(50, (await service.GetAllFilesAsync()).Count());
    }

    [Fact]
    public async Task EmptyWatchedDirectory_ManualRun_AsksForConfirmationAndConfirmedRemovesEverything()
    {
        var paths = await TrackAsync(present: 0, missing: 3);
        var service = await CreateServiceAsync();

        var unconfirmed = await service.CleanupStaleEntriesAsync(StaleCleanupOptions.Manual);

        Assert.Equal(StaleCleanupStatus.WatchedDirectoryEmpty, unconfirmed.Status);
        Assert.True(unconfirmed.RequiresConfirmation);
        await AssertRowsAsync(paths, missingSinceSet: false);

        var confirmed = await service.CleanupStaleEntriesAsync(new StaleCleanupOptions(IsManual: true, Confirmed: true));

        Assert.Equal(StaleCleanupStatus.Completed, confirmed.Status);
        Assert.Equal(3, confirmed.RemovedCount);
        Assert.Empty(await RowsAsync());
        Assert.Empty(await service.GetAllFilesAsync());
    }

    [Fact]
    public async Task MissingSentinel_RemovesNothingEvenWhenConfirmed_AndAPresentSentinelLetsTheRunProceed()
    {
        var paths = await TrackAsync(present: 10, missing: 1);
        _settings.StaleCleanupSentinelFile = ".library-mounted";
        var service = await CreateServiceAsync();

        var refused = await service.CleanupStaleEntriesAsync(new StaleCleanupOptions(IsManual: true, Confirmed: true));

        Assert.Equal(StaleCleanupStatus.SentinelMissing, refused.Status);
        Assert.False(refused.RequiresConfirmation);
        Assert.Equal(11, (await RowsAsync()).Count);

        await File.WriteAllTextAsync(Path.Join(_library, ".library-mounted"), string.Empty);
        var result = await service.CleanupStaleEntriesAsync(new StaleCleanupOptions(IsManual: true, Confirmed: true));

        Assert.Equal(StaleCleanupStatus.Completed, result.Status);
        Assert.Equal(1, result.RemovedCount);
        Assert.DoesNotContain(paths.Missing[0], (await RowsAsync()).Keys);
    }

    [Fact]
    public async Task ScheduledRun_OnlyStampsAFileTheFirstTimeItIsMissing()
    {
        var paths = await TrackAsync(present: 10, missing: 1);
        var service = await CreateServiceAsync();

        var result = await service.CleanupStaleEntriesAsync(StaleCleanupOptions.Scheduled);

        Assert.Equal(StaleCleanupStatus.Completed, result.Status);
        Assert.Equal(0, result.RemovedCount);
        Assert.Equal(1, result.MissingCount);
        Assert.Equal(1, result.NewlyMissingCount);
        var rows = await RowsAsync();
        Assert.NotNull(rows[paths.Missing[0]].MissingSince);
        Assert.All(paths.Present, path => Assert.Null(rows[path].MissingSince));
        Assert.True(await service.FileExistsAsync(paths.Missing[0]));
        _broadcaster.Verify(b => b.BroadcastFileListUpdateAsync(), Times.Never());
    }

    [Fact]
    public async Task ScheduledRun_RemovesAFileOnlyOnceItHasBeenMissingForTheGracePeriod()
    {
        _settings.StaleCleanupMissingGraceHours = 6;
        var paths = await TrackAsync(present: 10, missing: 2);
        var service = await CreateServiceAsync();
        var gone = paths.Missing[0];
        var recent = paths.Missing[1];
        await SetMissingSinceAsync(gone, DateTime.UtcNow.AddHours(-7));
        await SetMissingSinceAsync(recent, DateTime.UtcNow.AddHours(-1));

        var result = await service.CleanupStaleEntriesAsync(StaleCleanupOptions.Scheduled);

        Assert.Equal(StaleCleanupStatus.Completed, result.Status);
        Assert.Equal(1, result.RemovedCount);
        var rows = await RowsAsync();
        Assert.DoesNotContain(gone, rows.Keys);
        Assert.NotNull(rows[recent].MissingSince);
        Assert.False(await service.FileExistsAsync(gone));
        Assert.True(await service.FileExistsAsync(recent));
        _broadcaster.Verify(b => b.BroadcastFileListUpdateAsync(), Times.Once());
    }

    [Fact]
    public async Task FileThatComesBack_KeepsItsRowAndStoredStateAndLosesTheStamp()
    {
        var paths = await TrackAsync(present: 10, missing: 1);
        var service = await CreateServiceAsync();
        var file = paths.Missing[0];
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var row = await db.ComicFiles.SingleAsync(e => e.FilePath == file);
            row.IsRenamed = true;
            row.IsNormalized = true;
            row.MissingSince = DateTime.UtcNow.AddDays(-2);
            await db.SaveChangesAsync();
        }

        await File.WriteAllTextAsync(file, "back again");
        var result = await service.CleanupStaleEntriesAsync(StaleCleanupOptions.Scheduled);

        Assert.Equal(StaleCleanupStatus.Completed, result.Status);
        Assert.Equal(0, result.RemovedCount);
        Assert.Equal(1, result.RestoredCount);
        var restored = (await RowsAsync())[file];
        Assert.Null(restored.MissingSince);
        Assert.True(restored.IsRenamed);
        Assert.True(restored.IsNormalized);
    }

    [Fact]
    public async Task ReAddingAFile_ClearsItsMissingStamp()
    {
        var paths = await TrackAsync(present: 1, missing: 1);
        var service = await CreateServiceAsync();
        var file = paths.Missing[0];
        await SetMissingSinceAsync(file, DateTime.UtcNow.AddDays(-2));
        await File.WriteAllTextAsync(file, "back again");

        await service.AddFileAsync(file);

        Assert.Null((await RowsAsync())[file].MissingSince);
    }

    [Fact]
    public async Task RunOverTheRemovalShare_RemovesNothingAndWarns_ManualConfirmationRemovesThem()
    {
        // 30 of 100 files are gone (a partly mounted library); the guard allows 10%.
        _settings.StaleCleanupMaxRemovalPercent = 10;
        _settings.StaleCleanupMissingGraceHours = 1;
        var paths = await TrackAsync(present: 70, missing: 30);
        foreach (var path in paths.Missing)
        {
            await SetMissingSinceAsync(path, DateTime.UtcNow.AddDays(-1));
        }
        var service = await CreateServiceAsync();

        var scheduled = await service.CleanupStaleEntriesAsync(StaleCleanupOptions.Scheduled);

        Assert.Equal(StaleCleanupStatus.ThresholdExceeded, scheduled.Status);
        Assert.Equal(30, scheduled.RemovableCount);
        Assert.Equal(0, scheduled.RemovedCount);
        Assert.Equal(100, (await RowsAsync()).Count);

        var manual = await service.CleanupStaleEntriesAsync(StaleCleanupOptions.Manual);
        Assert.Equal(StaleCleanupStatus.ThresholdExceeded, manual.Status);
        Assert.True(manual.RequiresConfirmation);
        Assert.Equal(100, (await RowsAsync()).Count);

        var confirmed = await service.CleanupStaleEntriesAsync(new StaleCleanupOptions(IsManual: true, Confirmed: true));
        Assert.Equal(StaleCleanupStatus.Completed, confirmed.Status);
        Assert.Equal(30, confirmed.RemovedCount);
        Assert.Equal(70, (await RowsAsync()).Count);
        Assert.Equal(70, (await service.GetAllFilesAsync()).Count());
    }

    [Fact]
    public async Task SmallRemovals_AreAllowedWhateverTheShare()
    {
        // 2 of 4 files is 50%, but a handful of removals never needs confirmation.
        _settings.StaleCleanupMaxRemovalPercent = 10;
        _settings.StaleCleanupMissingGraceHours = 1;
        var paths = await TrackAsync(present: 2, missing: 2);
        foreach (var path in paths.Missing)
        {
            await SetMissingSinceAsync(path, DateTime.UtcNow.AddDays(-1));
        }
        var service = await CreateServiceAsync();

        var result = await service.CleanupStaleEntriesAsync(StaleCleanupOptions.Manual);

        Assert.Equal(StaleCleanupStatus.Completed, result.Status);
        Assert.Equal(2, result.RemovedCount);
    }

    [Fact]
    public async Task ManualRun_AFileJustFoundMissing_AsksForConfirmationAndRemovesNothing()
    {
        // A small sub-share offline when the administrator clicks "Clean Database Now": well
        // under the removal share, so only the two-run rule protects these rows.
        var paths = await TrackAsync(present: 20, missing: 1);
        var service = await CreateServiceAsync();

        var result = await service.CleanupStaleEntriesAsync(StaleCleanupOptions.Manual);

        Assert.Equal(StaleCleanupStatus.ConfirmationRequired, result.Status);
        Assert.True(result.RequiresConfirmation);
        Assert.Equal(0, result.RemovedCount);
        Assert.Equal(1, result.MissingCount);
        Assert.Equal(0, result.RemovableCount);
        Assert.Contains("No rows were removed", result.Message);
        Assert.NotNull((await RowsAsync())[paths.Missing[0]].MissingSince);
        Assert.True(await service.FileExistsAsync(paths.Missing[0]));
        _broadcaster.Verify(b => b.BroadcastFileListUpdateAsync(), Times.Never());
    }

    [Fact]
    public async Task ManualRun_Confirmed_RemovesWhatIsMissingNowWithoutWaitingForASecondRun()
    {
        var paths = await TrackAsync(present: 20, missing: 1);
        var service = await CreateServiceAsync();

        var result = await service.CleanupStaleEntriesAsync(new StaleCleanupOptions(IsManual: true, Confirmed: true));

        Assert.Equal(StaleCleanupStatus.Completed, result.Status);
        Assert.Equal(1, result.RemovedCount);
        Assert.DoesNotContain(paths.Missing[0], (await RowsAsync()).Keys);
        Assert.False(await service.FileExistsAsync(paths.Missing[0]));
    }

    [Fact]
    public async Task ManualRun_AFileMissingForTheGracePeriod_IsRemovedWithoutConfirmation()
    {
        _settings.StaleCleanupMissingGraceHours = 6;
        var paths = await TrackAsync(present: 20, missing: 1);
        await SetMissingSinceAsync(paths.Missing[0], DateTime.UtcNow.AddHours(-7));
        var service = await CreateServiceAsync();

        var result = await service.CleanupStaleEntriesAsync(StaleCleanupOptions.Manual);

        Assert.Equal(StaleCleanupStatus.Completed, result.Status);
        Assert.False(result.RequiresConfirmation);
        Assert.Equal(1, result.RemovedCount);
        Assert.DoesNotContain(paths.Missing[0], (await RowsAsync()).Keys);
    }

    [Fact]
    public async Task ManualRun_SomeFilesStillWithinTheGracePeriod_RemovesNothingUntilConfirmed()
    {
        // Declining the prompt must leave everything in place, so the run holds back the
        // file past its grace period as well.
        _settings.StaleCleanupMissingGraceHours = 6;
        var paths = await TrackAsync(present: 20, missing: 2);
        var pastGrace = paths.Missing[0];
        var recent = paths.Missing[1];
        await SetMissingSinceAsync(pastGrace, DateTime.UtcNow.AddHours(-7));
        await SetMissingSinceAsync(recent, DateTime.UtcNow.AddHours(-1));
        var service = await CreateServiceAsync();

        var unconfirmed = await service.CleanupStaleEntriesAsync(StaleCleanupOptions.Manual);

        Assert.Equal(StaleCleanupStatus.ConfirmationRequired, unconfirmed.Status);
        Assert.Equal(0, unconfirmed.RemovedCount);
        Assert.Equal(22, (await RowsAsync()).Count);

        var confirmed = await service.CleanupStaleEntriesAsync(new StaleCleanupOptions(IsManual: true, Confirmed: true));

        Assert.Equal(StaleCleanupStatus.Completed, confirmed.Status);
        Assert.Equal(2, confirmed.RemovedCount);
    }

    [Fact]
    public async Task GraceOfZero_StillTakesASecondRun()
    {
        _settings.StaleCleanupMissingGraceHours = 0;
        var paths = await TrackAsync(present: 20, missing: 1);
        var service = await CreateServiceAsync();

        var first = await service.CleanupStaleEntriesAsync(StaleCleanupOptions.Scheduled);

        Assert.Equal(0, first.RemovedCount);
        Assert.NotNull((await RowsAsync())[paths.Missing[0]].MissingSince);

        var second = await service.CleanupStaleEntriesAsync(StaleCleanupOptions.Scheduled);

        Assert.Equal(1, second.RemovedCount);
        Assert.DoesNotContain(paths.Missing[0], (await RowsAsync()).Keys);
    }

    [Fact]
    public async Task FilesInAnUnavailableDuplicateDirectory_AreLeftUnchecked()
    {
        await TrackAsync(present: 5, missing: 0);
        var duplicates = Path.Join(_root, "duplicates");
        _settings.DuplicateDirectory = duplicates;
        var duplicate = Path.Join(duplicates, "Saga", "Saga 001.cbz");
        await InsertRowsAsync(duplicate);
        var service = await CreateServiceAsync();

        var result = await service.CleanupStaleEntriesAsync(StaleCleanupOptions.Manual);

        Assert.Equal(StaleCleanupStatus.Completed, result.Status);
        Assert.Equal(1, result.UncheckedCount);
        Assert.Equal(0, result.RemovedCount);
        var row = (await RowsAsync())[duplicate];
        Assert.Null(row.MissingSince);
    }

    [Fact]
    public async Task FilesInAnEmptyDuplicateDirectory_ScheduledRun_AreLeftUnchecked()
    {
        // A separately mounted duplicate share that is unmounted shows up as an empty
        // directory. Even a row stamped long ago must survive that.
        _settings.StaleCleanupMissingGraceHours = 1;
        await TrackAsync(present: 5, missing: 0);
        var duplicates = Directory.CreateDirectory(Path.Join(_root, "duplicates")).FullName;
        _settings.DuplicateDirectory = duplicates;
        var stamped = Path.Join(duplicates, "Saga", "Saga 001.cbz");
        var unstamped = Path.Join(duplicates, "Saga", "Saga 002.cbz");
        await InsertRowsAsync(stamped, unstamped);
        await SetMissingSinceAsync(stamped, DateTime.UtcNow.AddDays(-1));
        var service = await CreateServiceAsync();

        var result = await service.CleanupStaleEntriesAsync(StaleCleanupOptions.Scheduled);

        Assert.Equal(StaleCleanupStatus.Completed, result.Status);
        Assert.Equal(2, result.UncheckedCount);
        Assert.Equal(0, result.RemovedCount);
        var rows = await RowsAsync();
        Assert.Contains(stamped, rows.Keys);
        Assert.Null(rows[unstamped].MissingSince);
    }

    [Fact]
    public async Task FilesInAnEmptyDuplicateDirectory_ManualRun_AsksForConfirmationAndConfirmedRemovesThem()
    {
        await TrackAsync(present: 5, missing: 0);
        var duplicates = Directory.CreateDirectory(Path.Join(_root, "duplicates")).FullName;
        _settings.DuplicateDirectory = duplicates;
        var duplicate = Path.Join(duplicates, "Saga", "Saga 001.cbz");
        await InsertRowsAsync(duplicate);
        var service = await CreateServiceAsync();

        var unconfirmed = await service.CleanupStaleEntriesAsync(StaleCleanupOptions.Manual);

        Assert.Equal(StaleCleanupStatus.ConfirmationRequired, unconfirmed.Status);
        Assert.Contains("duplicate directory", unconfirmed.Message);
        Assert.Contains(duplicate, (await RowsAsync()).Keys);

        var confirmed = await service.CleanupStaleEntriesAsync(new StaleCleanupOptions(IsManual: true, Confirmed: true));

        Assert.Equal(StaleCleanupStatus.Completed, confirmed.Status);
        Assert.Equal(1, confirmed.RemovedCount);
        Assert.DoesNotContain(duplicate, (await RowsAsync()).Keys);
    }

    [Fact]
    public async Task FilesInAPopulatedDuplicateDirectory_AreCheckedLikeAnyOther()
    {
        await TrackAsync(present: 5, missing: 0);
        var duplicates = Directory.CreateDirectory(Path.Join(_root, "duplicates")).FullName;
        _settings.DuplicateDirectory = duplicates;
        var present = Path.Join(duplicates, "Saga 001.cbz");
        await File.WriteAllTextAsync(present, "comic");
        var gone = Path.Join(duplicates, "Saga 002.cbz");
        await InsertRowsAsync(present, gone);
        var service = await CreateServiceAsync();

        var result = await service.CleanupStaleEntriesAsync(StaleCleanupOptions.Scheduled);

        Assert.Equal(0, result.UncheckedCount);
        Assert.Equal(1, result.NewlyMissingCount);
        var rows = await RowsAsync();
        Assert.Null(rows[present].MissingSince);
        Assert.NotNull(rows[gone].MissingSince);
    }

    [Fact]
    public async Task CancelledRun_ChangesNothing()
    {
        var paths = await TrackAsync(present: 5, missing: 2);
        var service = await CreateServiceAsync();

        var result = await service.CleanupStaleEntriesAsync(StaleCleanupOptions.Manual, new CancellationToken(canceled: true));

        Assert.Equal(StaleCleanupStatus.Cancelled, result.Status);
        await AssertRowsAsync(paths, missingSinceSet: false);
    }

    [Fact]
    public async Task ScheduledRun_KeepsARowWhoseFileWasReAddedWhileTheDiskWasBeingChecked()
    {
        _settings.StaleCleanupMissingGraceHours = 1;
        var paths = await TrackAsync(present: 10, missing: 2);
        foreach (var path in paths.Missing)
        {
            await SetMissingSinceAsync(path, DateTime.UtcNow.AddDays(-1));
        }
        var service = await CreateServiceAsync();
        var returned = paths.Missing[0];
        // The watcher re-adds the file (clearing its stamp) just before the delete runs.
        _beforeDelete.Action = command =>
        {
            using var clear = command.Connection!.CreateCommand();
            clear.Transaction = command.Transaction;
            clear.CommandText = "UPDATE ComicFiles SET MissingSince = NULL WHERE FilePath = $path";
            clear.Parameters.Add(new SqliteParameter("$path", returned));
            clear.ExecuteNonQuery();
        };

        var result = await service.CleanupStaleEntriesAsync(StaleCleanupOptions.Scheduled);

        Assert.Equal(StaleCleanupStatus.Completed, result.Status);
        Assert.Equal(1, result.RemovedCount);
        var rows = await RowsAsync();
        Assert.Contains(returned, rows.Keys);
        Assert.DoesNotContain(paths.Missing[1], rows.Keys);
        // The in-memory copy agrees with the database.
        Assert.True(await service.FileExistsAsync(returned));
        Assert.False(await service.FileExistsAsync(paths.Missing[1]));
    }

    /// <summary>Runs <see cref="Action"/> once, just before the first DELETE from ComicFiles.</summary>
    private sealed class BeforeDeleteInterceptor : DbCommandInterceptor
    {
        public Action<DbCommand>? Action { get; set; }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Action is { } action && command.CommandText.TrimStart().StartsWith("DELETE FROM \"ComicFiles\"", StringComparison.Ordinal))
            {
                Action = null;
                action(command);
            }
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed record TrackedPaths(IReadOnlyList<string> Present, IReadOnlyList<string> Missing);

    /// <summary>Tracks <paramref name="present"/> files that exist and <paramref name="missing"/> that do not.</summary>
    private async Task<TrackedPaths> TrackAsync(int present, int missing)
    {
        var presentPaths = new List<string>();
        var missingPaths = new List<string>();
        var folder = Directory.CreateDirectory(Path.Join(_library, "Saga")).FullName;
        for (var i = 0; i < present; i++)
        {
            var path = Path.Join(folder, $"Saga {i + 1:000}.cbz");
            await File.WriteAllTextAsync(path, "comic");
            presentPaths.Add(path);
        }
        for (var i = 0; i < missing; i++)
        {
            missingPaths.Add(Path.Join(folder, $"Gone {i + 1:000}.cbz"));
        }

        await InsertRowsAsync([.. presentPaths, .. missingPaths]);
        return new TrackedPaths(presentPaths, missingPaths);
    }

    private async Task InsertRowsAsync(params string[] paths)
    {
        await using var db = await _factory.CreateDbContextAsync();
        foreach (var path in paths)
        {
            db.ComicFiles.Add(new ComicFileEntity
            {
                FilePath = path,
                FileName = Path.GetFileName(path),
                Directory = Path.GetDirectoryName(path)!,
            });
        }
        await db.SaveChangesAsync();
    }

    private async Task SetMissingSinceAsync(string path, DateTime missingSince)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.ComicFiles.Where(e => e.FilePath == path)
            .ExecuteUpdateAsync(set => set.SetProperty(e => e.MissingSince, missingSince));
    }

    private async Task<Dictionary<string, ComicFileEntity>> RowsAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.ComicFiles.AsNoTracking().ToDictionaryAsync(e => e.FilePath, StringComparer.Ordinal);
    }

    private async Task AssertRowsAsync(TrackedPaths paths, bool missingSinceSet)
    {
        var rows = await RowsAsync();
        Assert.Equal(paths.Present.Count + paths.Missing.Count, rows.Count);
        Assert.All(rows.Values, row => Assert.Equal(missingSinceSet, row.MissingSince is not null));
    }

    private async Task<FileStoreService> CreateServiceAsync()
    {
        var service = TestServices.CreateFileStore(
            new TestOptionsMonitor<AppSettings>(_settings),
            NullLogger<FileStoreService>.Instance,
            _factory,
            _broadcaster.Object);
        await service.InitializeFromDatabaseAsync();
        return service;
    }
}
