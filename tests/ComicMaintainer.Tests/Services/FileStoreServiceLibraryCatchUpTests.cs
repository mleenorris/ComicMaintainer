using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// What the file watcher's catch-up relies on the store for, on a real SQLite database: the
/// durable record of the last complete library scan, and the stale-row cleanup waiting while
/// the watcher is working out which missing files were moved.
/// </summary>
public sealed class FileStoreServiceLibraryCatchUpTests : IAsyncLifetime
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(30);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly string _library = Path.Join(Path.GetTempPath(), $"fss_catchup_{Guid.NewGuid():N}");
    private ServiceProvider _provider = null!;
    private IDbContextFactory<ComicMaintainerDbContext> _factory = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_library);
        await _connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddDbContextFactory<ComicMaintainerDbContext>(o => o.UseSqlite(_connection));
        _provider = services.BuildServiceProvider();
        _factory = _provider.GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>();
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
        TestIO.TryDeleteDirectory(_library);
    }

    // Remembering the scan is bookkeeping for the next startup: failing it must not fail the scan that just finished.
    [Fact]
    public async Task RecordingAndForgettingAScan_WhenTheDatabaseFails_DoNotThrowAndReportNotScanned()
    {
        var store = CreateService();
        await store.RecordLibraryScannedAsync(_library);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await db.Database.ExecuteSqlRawAsync("DROP TABLE LibraryScans");
        }

        await store.RecordLibraryScannedAsync(_library);
        await store.ForgetLibraryScanAsync();

        Assert.False(await store.IsLibraryScannedAsync(_library));
    }

    [Fact]
    public async Task IsLibraryScanned_FalseUntilAScanIsRecorded()
    {
        var store = CreateService();

        Assert.False(await store.IsLibraryScannedAsync(_library));

        await store.RecordLibraryScannedAsync(_library);

        Assert.True(await store.IsLibraryScannedAsync(_library));
    }

    [Fact]
    public async Task IsLibraryScanned_CoversSubdirectoriesButNotParentsOrSiblings()
    {
        var store = CreateService();
        await store.RecordLibraryScannedAsync(_library);

        Assert.True(await store.IsLibraryScannedAsync(Path.Join(_library, "manga")));
        Assert.False(await store.IsLibraryScannedAsync(Path.GetDirectoryName(_library)!));
        Assert.False(await store.IsLibraryScannedAsync(_library + "2"));
    }

    [Fact]
    public async Task IsLibraryScanned_NarrowingTheDirectoryIsKnownButWideningIsNot()
    {
        var store = CreateService();
        var manga = Path.Join(_library, "manga");

        await store.RecordLibraryScannedAsync(manga);

        Assert.True(await store.IsLibraryScannedAsync(manga));
        Assert.False(await store.IsLibraryScannedAsync(_library));
    }

    [Fact]
    public async Task RecordLibraryScanned_ReplacesTheDirectoryRecordedBefore()
    {
        var store = CreateService();
        var other = Path.Join(_library, "other");

        await store.RecordLibraryScannedAsync(_library);
        await store.RecordLibraryScannedAsync(other);

        Assert.False(await store.IsLibraryScannedAsync(_library));
        Assert.True(await store.IsLibraryScannedAsync(other));
        Assert.Equal(1, await CountScanRecordsAsync());
    }

    [Fact]
    public async Task RecordLibraryScanned_SameDirectoryAgain_KeepsOneRecord()
    {
        var store = CreateService();

        await store.RecordLibraryScannedAsync(_library);
        await store.RecordLibraryScannedAsync(_library + Path.DirectorySeparatorChar);

        Assert.Equal(1, await CountScanRecordsAsync());
        Assert.True(await store.IsLibraryScannedAsync(_library));
    }

    [Fact]
    public async Task ForgetLibraryScan_LeavesNoRecord()
    {
        var store = CreateService();
        await store.RecordLibraryScannedAsync(_library);

        await store.ForgetLibraryScanAsync();

        Assert.False(await store.IsLibraryScannedAsync(_library));
        Assert.Equal(0, await CountScanRecordsAsync());
    }

    [Fact]
    public async Task IsLibraryScanned_UnreadableDatabase_IsNotTakenForScanned()
    {
        var store = CreateService();
        await store.RecordLibraryScannedAsync(_library);
        await _connection.CloseAsync();
        await _connection.DisposeAsync();

        Assert.False(await store.IsLibraryScannedAsync(_library));
    }

    [Fact]
    public async Task CleanupStaleEntries_WhileSuspended_WaitsAndPrunesOnlyAfterTheLeaseEnds()
    {
        var store = CreateService();
        var gone = await SeedGoneFileAsync(store);

        using var lease = store.SuspendStaleCleanup();

        var cleanup = store.CleanupStaleEntriesAsync(ConfirmedCleanup);

        await Task.Delay(100);
        Assert.False(cleanup.IsCompleted);
        Assert.True(await store.FileExistsAsync(gone));

        lease.Dispose();

        Assert.Equal(1, (await cleanup.WaitAsync(WaitTimeout)).RemovedCount);
        Assert.False(await store.FileExistsAsync(gone));
    }

    [Fact]
    public async Task CleanupStaleEntries_WaitsForEveryLease()
    {
        var store = CreateService();
        var gone = await SeedGoneFileAsync(store);
        using var first = store.SuspendStaleCleanup();
        using var second = store.SuspendStaleCleanup();

        var cleanup = store.CleanupStaleEntriesAsync(ConfirmedCleanup);
        first.Dispose();
        first.Dispose(); // Disposing twice ends one lease, not two.

        await Task.Delay(100);
        Assert.False(cleanup.IsCompleted);

        second.Dispose();

        Assert.Equal(1, (await cleanup.WaitAsync(WaitTimeout)).RemovedCount);
        Assert.False(await store.FileExistsAsync(gone));
    }

    [Fact]
    public async Task CleanupStaleEntries_CancelledWhileWaiting_PrunesNothing()
    {
        var store = CreateService();
        var gone = await SeedGoneFileAsync(store);
        using var lease = store.SuspendStaleCleanup();
        using var cts = new CancellationTokenSource();

        var cleanup = store.CleanupStaleEntriesAsync(ConfirmedCleanup, cts.Token);
        cts.Cancel();

        Assert.Equal(StaleCleanupStatus.Cancelled, (await cleanup.WaitAsync(WaitTimeout)).Status);
        lease.Dispose();
        Assert.True(await store.FileExistsAsync(gone));
    }

    [Fact]
    public async Task CleanupStaleEntries_NotSuspended_RunsAtOnce()
    {
        var store = CreateService();
        var gone = await SeedGoneFileAsync(store);

        Assert.Equal(1, (await store.CleanupStaleEntriesAsync(ConfirmedCleanup).WaitAsync(WaitTimeout)).RemovedCount);
        Assert.False(await store.FileExistsAsync(gone));
    }

    /// <summary>A confirmed manual run removes what is missing right now, without a grace period.</summary>
    private static readonly StaleCleanupOptions ConfirmedCleanup = new(IsManual: true, Confirmed: true);

    private FileStoreService CreateService() =>
        TestServices.CreateFileStore(
            new TestOptionsMonitor<AppSettings>(new AppSettings { WatchedDirectory = _library, DuplicateDirectory = string.Empty }),
            NullLogger<FileStoreService>.Instance,
            _factory);

    /// <summary>A tracked file that no longer exists on disk: what the cleanup prunes.</summary>
    private async Task<string> SeedGoneFileAsync(FileStoreService store)
    {
        var path = Path.Join(_library, "Saga 001.cbz");
        await File.WriteAllTextAsync(path, "comic");
        await store.AddFileAsync(path);
        File.Delete(path);
        return path;
    }

    private async Task<int> CountScanRecordsAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.LibraryScans.CountAsync();
    }
}
