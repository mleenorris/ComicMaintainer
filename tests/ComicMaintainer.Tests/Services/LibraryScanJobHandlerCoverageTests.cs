using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Additional coverage for <see cref="LibraryScanJobHandler"/>: unavailable watched directory,
/// per-file failures that must not abort the scan, queue failures, job outcomes, option
/// combinations and cancellation while waiting for a spawned job.
/// </summary>
public class LibraryScanJobHandlerCoverageTests : IDisposable
{
    private const string ModifiedOnlyOptions =
        """{"scanForNew":true,"reconcileDeletions":true,"retagStale":false,"reprocessModified":true}""";

    private readonly string _watchDir;
    private readonly AppSettings _settings;
    private readonly Mock<IFileStoreService> _fileStore = new();
    private readonly Mock<IComicProcessorService> _processor = new();
    private readonly Mock<ISeriesMetadataCacheService> _cache = new();

    public LibraryScanJobHandlerCoverageTests()
    {
        // A unique scratch library inside the test output folder, removed in Dispose.
        _watchDir = Path.Combine(AppContext.BaseDirectory, "lib-scan-cov-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_watchDir);
        _settings = new AppSettings { WatchedDirectory = _watchDir };
        _cache.Setup(c => c.NormalizeKey(It.IsAny<string?>()))
            .Returns<string?>(s => (s ?? string.Empty).ToLowerInvariant().Replace(' ', '-'));
        SetupTracked();
        _fileStore
            .Setup(f => f.GetFilesWithStaleSeriesMetadataAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_watchDir, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private LibraryScanJobHandler CreateHandler() => new(
        new TestOptionsMonitor<AppSettings>(_settings),
        _fileStore.Object,
        _processor.Object,
        NullLogger<LibraryScanJobHandler>.Instance,
        _cache.Object);

    private string WriteComic(string folder, string name)
    {
        var dir = Path.Combine(_watchDir, folder);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, new byte[] { 0x50, 0x4B, 0x03, 0x04 });
        return path;
    }

    /// <summary>A store row whose stats match the file on disk unless <paramref name="size"/> overrides them.</summary>
    private static ComicFile Tracked(string path, long? size = null)
    {
        var info = new FileInfo(path);
        return new ComicFile
        {
            FilePath = path,
            FileSize = size ?? (info.Exists ? info.Length : 0),
            LastModified = info.Exists ? info.LastWriteTime : default,
        };
    }

    private void SetupTracked(params ComicFile[] files) =>
        _fileStore.Setup(f => f.GetAllFilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(files);

    private static ProcessingJob Finished(JobStatus status, int processed, int failed) => new()
    {
        JobId = Guid.NewGuid(),
        Status = status,
        ProcessedFiles = processed,
        FailedFiles = failed,
    };

    private Guid SetupAddedJob(ProcessingJob? job, Action<List<string>>? capture = null)
    {
        var jobId = Guid.NewGuid();
        _processor.Setup(p => p.NormalizeAndRenameFilesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<string>, CancellationToken>((paths, _) => capture?.Invoke(paths.ToList()))
            .ReturnsAsync(jobId);
        _processor.Setup(p => p.GetJob(jobId)).Returns(job);
        return jobId;
    }

    private Guid SetupNormalizeJob(ProcessingJob? job, Action<List<string>>? capture = null)
    {
        var jobId = Guid.NewGuid();
        _processor.Setup(p => p.NormalizeFilesAsync(It.IsAny<IEnumerable<string>>(), true, It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<string>, bool, CancellationToken>((paths, _, _) => capture?.Invoke(paths.ToList()))
            .ReturnsAsync(jobId);
        _processor.Setup(p => p.GetJob(jobId)).Returns(job);
        return jobId;
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ExecuteAsync_WatchedDirectoryBlank_ReportsItIsUnavailable(string watchedDirectory)
    {
        _settings.WatchedDirectory = watchedDirectory;

        var summary = await CreateHandler().ExecuteAsync(null, CancellationToken.None);

        Assert.Equal($"Watched directory not available: '{watchedDirectory}'", summary);
        _fileStore.Verify(f => f.GetAllFilesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WatchedDirectoryMissing_ReportsItIsUnavailable()
    {
        var missing = Path.Combine(_watchDir, "does-not-exist");
        _settings.WatchedDirectory = missing;

        var summary = await CreateHandler().ExecuteAsync(null, CancellationToken.None);

        Assert.Equal($"Watched directory not available: '{missing}'", summary);
        _fileStore.Verify(f => f.GetAllFilesAsync(It.IsAny<CancellationToken>()), Times.Never);
        _fileStore.Verify(f => f.AddFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_AddingOneFileFails_StillAddsAndProcessesTheOthers()
    {
        var failing = WriteComic("Series", "c001.cbz");
        var added = WriteComic("Series", "c002.cbz");
        WriteComic("Series", "notes.txt");
        _fileStore.Setup(f => f.AddFileAsync(failing, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("database is locked"));
        List<string>? queued = null;
        SetupAddedJob(Finished(JobStatus.Completed, 1, 0), paths => queued = paths);

        var summary = await CreateHandler().ExecuteAsync(null, CancellationToken.None);

        Assert.Equal("Library scan: +1 added, -0 removed, 0 stale retagged. [added: 1/1 ok, 0 failed]", summary);
        Assert.Equal(new[] { added }, queued);
        // notes.txt is not a comic archive, so only the two .cbz files were offered to the store.
        _fileStore.Verify(f => f.AddFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ExecuteAsync_RemovingOneGhostFails_KeepsItTrackedForTheStalePass()
    {
        var stuck = Path.Combine(_watchDir, "Ghosts", "c001.cbz");
        var removable = Path.Combine(_watchDir, "Ghosts", "c002.cbz");
        SetupTracked(Tracked(stuck), Tracked(removable));
        _fileStore.Setup(f => f.RemoveFileAsync(stuck, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("database is locked"));
        _cache.Setup(c => c.GetAsync("ghosts", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesMetadataCacheRecord { NormalizedKey = "ghosts", MetadataVersion = 3 });
        List<string>? staleCandidates = null;
        _fileStore.Setup(f => f.GetFilesWithStaleSeriesMetadataAsync(It.IsAny<IEnumerable<string>>(), 3, It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<string>, int, CancellationToken>((paths, _, _) => staleCandidates = paths.ToList())
            .ReturnsAsync(Array.Empty<string>());

        var summary = await CreateHandler().ExecuteAsync(null, CancellationToken.None);

        Assert.Equal("Library scan: +0 added, -1 removed, 0 stale retagged.", summary);
        _fileStore.Verify(f => f.RemoveFileAsync(stuck, It.IsAny<CancellationToken>()), Times.Once);
        _fileStore.Verify(f => f.RemoveFileAsync(removable, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(new[] { stuck }, staleCandidates);
    }

    [Fact]
    public async Task ExecuteAsync_StalePassFails_OtherPassesStillRun()
    {
        var existing = WriteComic("Series", "c001.cbz");
        var fresh = WriteComic("Series", "c002.cbz");
        SetupTracked(Tracked(existing));
        _cache.Setup(c => c.GetAsync("series", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("cache offline"));
        SetupAddedJob(Finished(JobStatus.Completed, 1, 0));

        var summary = await CreateHandler().ExecuteAsync(null, CancellationToken.None);

        Assert.Equal("Library scan: +1 added, -0 removed, 0 stale retagged. [added: 1/1 ok, 0 failed]", summary);
        _fileStore.Verify(f => f.AddFileAsync(fresh, It.IsAny<CancellationToken>()), Times.Once);
        _fileStore.Verify(f => f.AddFileAsync(existing, It.IsAny<CancellationToken>()), Times.Never);
        _processor.Verify(p => p.NormalizeFilesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_RefreshingStoredStatsFails_StillReprocessesTheModifiedFile()
    {
        var path = WriteComic("Series", "c001.cbz");
        SetupTracked(Tracked(path, size: 999));
        _fileStore.Setup(f => f.RefreshFileStatsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("database is locked"));
        List<string>? queued = null;
        SetupNormalizeJob(Finished(JobStatus.Completed, 1, 0), paths => queued = paths);

        var summary = await CreateHandler().ExecuteAsync(ModifiedOnlyOptions, CancellationToken.None);

        Assert.Equal(
            "Library scan: +0 added, -0 removed, 0 stale retagged, 1 modified reprocessed. [modified: 1/1 ok, 0 failed]",
            summary);
        Assert.Equal(new[] { path }, queued);
        _fileStore.Verify(f => f.RefreshFileStatsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_QueueingAddedFilesFails_ReportsTheQueueFailure()
    {
        WriteComic("Series", "c001.cbz");
        _processor.Setup(p => p.NormalizeAndRenameFilesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("queue full"));

        var summary = await CreateHandler().ExecuteAsync(null, CancellationToken.None);

        Assert.Equal("Library scan: +1 added, -0 removed, 0 stale retagged. [added: queue failed]", summary);
        _processor.Verify(p => p.GetJob(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_QueueingStaleFilesFails_ReportsTheQueueFailure()
    {
        var path = WriteComic("Series", "c001.cbz");
        SetupTracked(Tracked(path));
        _cache.Setup(c => c.GetAsync("series", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesMetadataCacheRecord { NormalizedKey = "series", MetadataVersion = 2 });
        _fileStore.Setup(f => f.GetFilesWithStaleSeriesMetadataAsync(It.IsAny<IEnumerable<string>>(), 2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { path });
        _processor.Setup(p => p.NormalizeFilesAsync(It.IsAny<IEnumerable<string>>(), true, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("queue full"));

        var summary = await CreateHandler().ExecuteAsync(null, CancellationToken.None);

        Assert.Equal("Library scan: +0 added, -0 removed, 1 stale retagged. [stale: queue failed]", summary);
    }

    [Fact]
    public async Task ExecuteAsync_QueueingModifiedFilesFails_ReportsTheQueueFailure()
    {
        var path = WriteComic("Series", "c001.cbz");
        SetupTracked(Tracked(path, size: 999));
        _processor.Setup(p => p.NormalizeFilesAsync(It.IsAny<IEnumerable<string>>(), true, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("queue full"));

        var summary = await CreateHandler().ExecuteAsync(ModifiedOnlyOptions, CancellationToken.None);

        Assert.Equal(
            "Library scan: +0 added, -0 removed, 0 stale retagged, 1 modified reprocessed. [modified: queue failed]",
            summary);
        // The change was never handled, so it must stay visible to the next scan (#864).
        _fileStore.Verify(f => f.RefreshFileStatsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_ModifiedFileFailsToRenormalize_IsRetriedOnTheNextScan()
    {
        // Regression test for #864: a failed re-normalize used to be recorded as handled, so later
        // scans never retried the file. The store re-syncs a row from disk when asked to refresh it.
        var path = WriteComic("Series", "c001.cbz");
        var row = Tracked(path, size: 999);
        SetupTracked(row);
        _fileStore.Setup(f => f.RefreshFileStatsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<string>, CancellationToken>((paths, _) =>
            {
                if (!paths.Contains(path)) return;
                var info = new FileInfo(path);
                row.FileSize = info.Length;
                row.LastModified = info.LastWriteTime;
            })
            .ReturnsAsync(1);
        var queued = new List<List<string>>();
        var failed = Finished(JobStatus.Completed, 0, 1);
        failed.Errors[path] = "Normalize failed";
        SetupNormalizeJob(failed, queued.Add);

        var first = await CreateHandler().ExecuteAsync(ModifiedOnlyOptions, CancellationToken.None);

        SetupNormalizeJob(Finished(JobStatus.Completed, 1, 0), queued.Add);
        var second = await CreateHandler().ExecuteAsync(ModifiedOnlyOptions, CancellationToken.None);
        var third = await CreateHandler().ExecuteAsync(ModifiedOnlyOptions, CancellationToken.None);

        Assert.Equal(
            "Library scan: +0 added, -0 removed, 0 stale retagged, 1 modified reprocessed. [modified: 0/1 ok, 1 failed]",
            first);
        Assert.Equal(
            "Library scan: +0 added, -0 removed, 0 stale retagged, 1 modified reprocessed. [modified: 1/1 ok, 0 failed]",
            second);
        // Once the retry succeeds the change is absorbed and later scans leave the file alone.
        Assert.Equal("Library scan: +0 added, -0 removed, 0 stale retagged, 0 modified reprocessed.", third);
        Assert.Collection(queued,
            paths => Assert.Equal(new[] { path }, paths),
            paths => Assert.Equal(new[] { path }, paths));
    }

    [Fact]
    public async Task ExecuteAsync_SomeModifiedFilesFailToRenormalize_RefreshesOnlyTheOthers()
    {
        var failing = WriteComic("Series", "c001.cbz");
        var renormalized = WriteComic("Series", "c002.cbz");
        SetupTracked(Tracked(failing, size: 999), Tracked(renormalized, size: 999));
        var job = Finished(JobStatus.Completed, 1, 1);
        job.Errors[failing] = "Normalize failed";
        SetupNormalizeJob(job);
        List<string>? refreshed = null;
        _fileStore.Setup(f => f.RefreshFileStatsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<string>, CancellationToken>((paths, _) => refreshed = paths.ToList())
            .ReturnsAsync(1);

        var summary = await CreateHandler().ExecuteAsync(ModifiedOnlyOptions, CancellationToken.None);

        Assert.Equal(
            "Library scan: +0 added, -0 removed, 0 stale retagged, 2 modified reprocessed. [modified: 1/2 ok, 1 failed]",
            summary);
        Assert.Equal(new[] { renormalized }, refreshed);
        _fileStore.Verify(f => f.RefreshFileStatsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(JobStatus.Failed)]
    [InlineData(JobStatus.Cancelled)]
    public async Task ExecuteAsync_ModifiedFilesJobEndsUnsuccessfully_RefreshesNoStoredStats(JobStatus finalStatus)
    {
        var first = WriteComic("Series", "c001.cbz");
        var second = WriteComic("Series", "c002.cbz");
        SetupTracked(Tracked(first, size: 999), Tracked(second, size: 999));
        // One file went through before the job stopped, but the job doesn't say which one.
        SetupNormalizeJob(Finished(finalStatus, 1, 0));

        var summary = await CreateHandler().ExecuteAsync(ModifiedOnlyOptions, CancellationToken.None);

        Assert.Equal(
            "Library scan: +0 added, -0 removed, 0 stale retagged, 2 modified reprocessed. [modified: 1/2 ok, 0 failed]",
            summary);
        _fileStore.Verify(f => f.RefreshFileStatsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_ModifiedFilesJobRecordEvicted_RefreshesNoStoredStats()
    {
        var path = WriteComic("Series", "c001.cbz");
        SetupTracked(Tracked(path, size: 999));
        SetupNormalizeJob(job: null);

        var summary = await CreateHandler().ExecuteAsync(ModifiedOnlyOptions, CancellationToken.None);

        Assert.Equal(
            "Library scan: +0 added, -0 removed, 0 stale retagged, 1 modified reprocessed. [modified: queued (job record evicted)]",
            summary);
        _fileStore.Verify(f => f.RefreshFileStatsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(JobStatus.Failed)]
    [InlineData(JobStatus.Cancelled)]
    public async Task ExecuteAsync_SpawnedJobEndsUnsuccessfully_ReportsItsCounts(JobStatus finalStatus)
    {
        WriteComic("Series", "c001.cbz");
        SetupAddedJob(Finished(finalStatus, 0, 1));

        var summary = await CreateHandler().ExecuteAsync(null, CancellationToken.None);

        Assert.Equal("Library scan: +1 added, -0 removed, 0 stale retagged. [added: 0/1 ok, 1 failed]", summary);
    }

    [Fact]
    public async Task ExecuteAsync_SpawnedJobRecordEvicted_ReportsItAsQueued()
    {
        WriteComic("Series", "c001.cbz");
        SetupAddedJob(job: null);

        var summary = await CreateHandler().ExecuteAsync(null, CancellationToken.None);

        Assert.Equal("Library scan: +1 added, -0 removed, 0 stale retagged. [added: queued (job record evicted)]", summary);
    }

    [Fact]
    public async Task ExecuteAsync_CancelledWhileWaitingForASpawnedJob_Throws()
    {
        WriteComic("Series", "c001.cbz");
        using var cts = new CancellationTokenSource();
        var jobId = Guid.NewGuid();
        _processor.Setup(p => p.NormalizeAndRenameFilesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(jobId);
        _processor.Setup(p => p.GetJob(jobId))
            .Callback(() => cts.Cancel())
            .Returns(new ProcessingJob { JobId = jobId, Status = JobStatus.Running });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateHandler().ExecuteAsync(null, cts.Token));

        _processor.Verify(p => p.GetJob(jobId), Times.Once);
        _processor.Verify(p => p.NormalizeFilesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("   ")]
    public async Task ExecuteAsync_UnparseableOrNullOptions_FallBackToTheDefaults(string optionsJson)
    {
        var ghost = Path.Combine(_watchDir, "Ghosts", "gone.cbz");
        var modified = WriteComic("Series", "c001.cbz");
        var fresh = WriteComic("Series", "c002.cbz");
        SetupTracked(Tracked(ghost), Tracked(modified, size: 999));
        List<string>? queued = null;
        SetupAddedJob(Finished(JobStatus.Completed, 1, 0), paths => queued = paths);

        var summary = await CreateHandler().ExecuteAsync(optionsJson, CancellationToken.None);

        // Defaults: add new files, reconcile deletions and retag stale files; modified-file reprocessing stays off.
        Assert.Equal("Library scan: +1 added, -1 removed, 0 stale retagged. [added: 1/1 ok, 0 failed]", summary);
        Assert.Equal(new[] { fresh }, queued);
        _fileStore.Verify(f => f.RemoveFileAsync(ghost, It.IsAny<CancellationToken>()), Times.Once);
        _cache.Verify(c => c.GetAsync("series", It.IsAny<CancellationToken>()), Times.Once);
        _processor.Verify(p => p.NormalizeFilesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_OnlyStaleRetagEnabled_DoesNotWalkTheDirectory()
    {
        var ghost = Path.Combine(_watchDir, "Ghosts", "gone.cbz");
        WriteComic("Series", "c001.cbz");
        SetupTracked(Tracked(ghost));

        var summary = await CreateHandler().ExecuteAsync(
            """{"scanForNew":false,"reconcileDeletions":false,"retagStale":true,"reprocessModified":false}""",
            CancellationToken.None);

        Assert.Equal("Library scan: +0 added, -0 removed, 0 stale retagged.", summary);
        _fileStore.Verify(f => f.AddFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _fileStore.Verify(f => f.RemoveFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _cache.Verify(c => c.GetAsync("ghosts", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_OnlyReprocessModifiedEnabled_WalksTheDiskWithoutAddingOrRemoving()
    {
        var ghost = Path.Combine(_watchDir, "Ghosts", "gone.cbz");
        var modified = WriteComic("Series", "c001.cbz");
        WriteComic("Series", "c002.cbz");
        SetupTracked(Tracked(ghost), Tracked(modified, size: 999));
        List<string>? queued = null;
        SetupNormalizeJob(Finished(JobStatus.Completed, 1, 0), paths => queued = paths);

        var summary = await CreateHandler().ExecuteAsync(
            """{"scanForNew":false,"reconcileDeletions":false,"retagStale":false,"reprocessModified":true}""",
            CancellationToken.None);

        Assert.Equal(
            "Library scan: +0 added, -0 removed, 0 stale retagged, 1 modified reprocessed. [modified: 1/1 ok, 0 failed]",
            summary);
        Assert.Equal(new[] { modified }, queued);
        _fileStore.Verify(f => f.AddFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _fileStore.Verify(f => f.RemoveFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_TrackedSnapshotUnavailable_TreatsEveryDiskFileAsNew()
    {
        _fileStore.Setup(f => f.GetAllFilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<ComicFile>)null!);
        var fresh = WriteComic("Series", "c001.cbz");
        SetupAddedJob(Finished(JobStatus.Completed, 1, 0));

        var summary = await CreateHandler().ExecuteAsync(null, CancellationToken.None);

        Assert.Equal("Library scan: +1 added, -0 removed, 0 stale retagged. [added: 1/1 ok, 0 failed]", summary);
        _fileStore.Verify(f => f.AddFileAsync(fresh, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_TrackedFileDeletedMidScan_IsNotTreatedAsModified()
    {
        var vanishing = WriteComic("Series", "c001.cbz");
        var fresh = WriteComic("Series", "c002.cbz");
        // The stored size differs, so the file would count as modified if it still existed.
        SetupTracked(Tracked(vanishing, size: 999));
        // New files are added after the directory walk; the tracked file disappears at that point.
        _fileStore.Setup(f => f.AddFileAsync(fresh, It.IsAny<CancellationToken>()))
            .Callback(() => File.Delete(vanishing))
            .Returns(Task.CompletedTask);
        SetupAddedJob(Finished(JobStatus.Completed, 1, 0));

        var summary = await CreateHandler().ExecuteAsync(ModifiedOnlyOptions, CancellationToken.None);

        Assert.Equal(
            "Library scan: +1 added, -0 removed, 0 stale retagged, 0 modified reprocessed. [added: 1/1 ok, 0 failed]",
            summary);
        _processor.Verify(p => p.NormalizeFilesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        _fileStore.Verify(f => f.RefreshFileStatsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
