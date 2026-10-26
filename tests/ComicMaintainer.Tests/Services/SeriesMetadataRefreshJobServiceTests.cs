using System.Collections.Concurrent;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using Microsoft.Extensions.Logging;
using Moq;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Tests for <see cref="SeriesMetadataRefreshJobService"/>. Jobs run on a
/// background task, so every test synchronizes on an observable side effect of
/// the job's final step (the stale-sibling cleanup call or a log entry written
/// by the retag step) instead of sleeping.
/// </summary>
public class SeriesMetadataRefreshJobServiceTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(30);

    private readonly Mock<ISeriesMetadataCacheService> _cache = new();
    private readonly Mock<IFileStoreService> _fileStore = new();
    private readonly Mock<IComicProcessorService> _processor = new();
    private readonly Mock<IEventBroadcaster> _broadcaster = new();
    private readonly RecordingLogger _logger = new();
    private readonly SemaphoreSlim _cleanupCalls = new(0);
    private readonly ConcurrentQueue<BroadcastCall> _broadcasts = new();

    public SeriesMetadataRefreshJobServiceTests()
    {
        SetupCleanup(removed: 0);
        _broadcaster
            .Setup(b => b.BroadcastJobUpdateAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()))
            .Callback<Guid, string, int, int, int, int>((id, status, processed, total, success, errors) =>
                _broadcasts.Enqueue(new BroadcastCall(id, status, processed, total, success, errors)))
            .Returns(Task.CompletedTask);
    }

    private SeriesMetadataRefreshJobService CreateService(
        bool withBroadcaster = true,
        bool withFileStore = true,
        bool withProcessor = true) => new(
            _cache.Object,
            _logger,
            withBroadcaster ? _broadcaster.Object : null,
            withFileStore ? _fileStore.Object : null,
            withProcessor ? _processor.Object : null);

    private void SetupCleanup(int removed) =>
        _cache.Setup(c => c.CleanupStaleSiblingRecordsAsync(It.IsAny<CancellationToken>()))
            .Callback(() => _cleanupCalls.Release())
            .ReturnsAsync(removed);

    private void SetupRefresh(string title, string? lookupStatus, string? source = null, string? canonical = null,
        List<string>? aliases = null, List<string>? userAliases = null) =>
        _cache.Setup(c => c.RefreshAsync(title, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesMetadataCacheRecord
            {
                CanonicalTitle = canonical ?? title,
                LookupStatus = lookupStatus,
                Source = source,
                Aliases = aliases!,
                UserAliases = userAliases!,
            });

    private void SetupTrackedFiles(params ComicFile[] files) =>
        _fileStore.Setup(f => f.GetAllFilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(files);

    private static ComicFile TrackedFile(string path, string? series) => new()
    {
        FilePath = path,
        Metadata = series is null ? null : new ComicMetadata { Series = series },
    };

    /// <summary>Waits until the job reaches its stale-sibling cleanup step, which runs after the
    /// job's terminal status, end time and final broadcast have been recorded.</summary>
    private async Task<MetadataRefreshJob> WaitForCleanupAsync(SeriesMetadataRefreshJobService service, Guid jobId)
    {
        Assert.True(await _cleanupCalls.WaitAsync(WaitTimeout), "Timed out waiting for the refresh job to finish.");
        var job = service.GetJob(jobId);
        Assert.NotNull(job);
        return job!;
    }

    [Fact]
    public async Task StartAsync_TrimsDeduplicatesAndDropsBlankTitles()
    {
        SetupRefresh("Alpha", "success");
        SetupRefresh("Beta", "success");
        var service = CreateService(withFileStore: false, withProcessor: false);

        var jobId = await service.StartAsync(new[] { " Alpha ", "alpha", "", "   ", null!, "Beta", "BETA " });

        var started = service.GetJob(jobId);
        Assert.NotNull(started);
        Assert.Equal(jobId, started!.JobId);
        Assert.Equal(new[] { "Alpha", "Beta" }, started.Series);
        Assert.Equal(2, started.TotalSeries);

        var job = await WaitForCleanupAsync(service, jobId);
        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.Equal(2, job.ProcessedSeries);
        Assert.Equal(2, job.Successes);
        _cache.Verify(c => c.RefreshAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        _cache.Verify(c => c.RefreshAsync("Alpha", It.IsAny<CancellationToken>()), Times.Once);
        _cache.Verify(c => c.RefreshAsync("Beta", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StartAsync_NullTitles_CompletesEmptyJobWithoutRetag()
    {
        var service = CreateService();

        var jobId = await service.StartAsync(null!);
        var job = await WaitForCleanupAsync(service, jobId);

        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.Empty(job.Series);
        Assert.Equal(0, job.TotalSeries);
        Assert.Equal(0, job.ProcessedSeries);
        Assert.NotNull(job.EndTime);
        _cache.Verify(c => c.RefreshAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _fileStore.Verify(f => f.GetAllFilesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunJob_CountsEachLookupStatusAndRecordsRecentOutcomes()
    {
        SetupRefresh("Alpha", "success", source: "AniList");
        SetupRefresh("Beta", "manual_match", source: "ComicVine");
        SetupRefresh("Gamma", "not_found");
        SetupRefresh("Delta", "error", source: "MangaDex");
        SetupRefresh("Epsilon", lookupStatus: null);
        var service = CreateService(withFileStore: false, withProcessor: false);

        var jobId = await service.StartAsync(new[] { "Alpha", "Beta", "Gamma", "Delta", "Epsilon" });
        var job = await WaitForCleanupAsync(service, jobId);

        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.Equal(5, job.TotalSeries);
        Assert.Equal(5, job.ProcessedSeries);
        Assert.Equal(2, job.Successes);
        Assert.Equal(1, job.NotFound);
        Assert.Equal(2, job.Failures);
        Assert.Equal("Epsilon", job.CurrentSeries);
        Assert.NotNull(job.EndTime);
        Assert.True(job.EndTime >= job.StartTime);
        Assert.Equal(
            new[]
            {
                ("Alpha", "success", (string?)"AniList"),
                ("Beta", "manual_match", "ComicVine"),
                ("Gamma", "not_found", null),
                ("Delta", "error", "MangaDex"),
                ("Epsilon", "error", null),
            },
            job.RecentResults.Select(r => (r.SeriesTitle, r.Status, r.Source)).ToArray());
    }

    [Fact]
    public async Task RunJob_RefreshThrows_CountsFailureAndContinuesWithRemainingSeries()
    {
        _cache.Setup(c => c.RefreshAsync("Broken", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("provider exploded"));
        SetupRefresh("Healthy", "success", source: "AniList");
        var service = CreateService(withFileStore: false, withProcessor: false);

        var jobId = await service.StartAsync(new[] { "Broken", "Healthy" });
        var job = await WaitForCleanupAsync(service, jobId);

        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.Equal(2, job.ProcessedSeries);
        Assert.Equal(1, job.Failures);
        Assert.Equal(1, job.Successes);
        Assert.Equal(("Broken", "error", (string?)null), (job.RecentResults[0].SeriesTitle, job.RecentResults[0].Status, job.RecentResults[0].Source));
        Assert.Equal(("Healthy", "success", (string?)"AniList"), (job.RecentResults[1].SeriesTitle, job.RecentResults[1].Status, job.RecentResults[1].Source));
        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Warning
            && e.Message.Contains("Metadata refresh failed for Broken")
            && e.Exception is InvalidOperationException);
    }

    [Fact]
    public async Task RunJob_RecentResults_KeepsOnlyTheTwentyMostRecentOutcomes()
    {
        var titles = Enumerable.Range(0, 25).Select(i => $"Series {i:00}").ToArray();
        _cache.Setup(c => c.RefreshAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesMetadataCacheRecord { LookupStatus = "success" });
        var service = CreateService(withFileStore: false, withProcessor: false);

        var jobId = await service.StartAsync(titles);
        var job = await WaitForCleanupAsync(service, jobId);

        Assert.Equal(25, job.ProcessedSeries);
        Assert.Equal(25, job.Successes);
        Assert.Equal(titles.Skip(5), job.RecentResults.Select(r => r.SeriesTitle));
    }

    [Fact]
    public async Task RunJob_BroadcastsStartPerSeriesProgressAndFinalStatus()
    {
        SetupRefresh("Alpha", "success");
        SetupRefresh("Beta", "not_found");
        _cache.Setup(c => c.RefreshAsync("Gamma", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        var service = CreateService(withFileStore: false, withProcessor: false);

        var jobId = await service.StartAsync(new[] { "Alpha", "Beta", "Gamma" });
        await WaitForCleanupAsync(service, jobId);

        Assert.Equal(
            new[]
            {
                new BroadcastCall(jobId, "running", 0, 3, 0, 0),
                new BroadcastCall(jobId, "running", 1, 3, 1, 0),
                new BroadcastCall(jobId, "running", 2, 3, 1, 0),
                new BroadcastCall(jobId, "running", 3, 3, 1, 1),
                new BroadcastCall(jobId, "completed", 3, 3, 1, 1),
            },
            _broadcasts.ToArray());
    }

    [Fact]
    public async Task RunJob_WithoutBroadcaster_StillCompletes()
    {
        SetupRefresh("Alpha", "success");
        var service = CreateService(withBroadcaster: false, withFileStore: false, withProcessor: false);

        var jobId = await service.StartAsync(new[] { "Alpha" });
        var job = await WaitForCleanupAsync(service, jobId);

        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.Equal(1, job.Successes);
        _broadcaster.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RunJob_BroadcasterThrowsSynchronously_FailureIsSwallowedAndJobCompletes()
    {
        _broadcaster
            .Setup(b => b.BroadcastJobUpdateAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()))
            .Throws(new InvalidOperationException("hub unavailable"));
        SetupRefresh("Alpha", "success");
        var service = CreateService(withFileStore: false, withProcessor: false);

        var jobId = await service.StartAsync(new[] { "Alpha" });
        var job = await WaitForCleanupAsync(service, jobId);

        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.Equal(1, job.Successes);
        // Start, one per-series progress update and the final update all failed and were swallowed.
        Assert.Equal(3, _logger.Entries.Count(e => e.Level == LogLevel.Debug
            && e.Message.Contains("Failed to broadcast metadata refresh job update")
            && e.Exception is InvalidOperationException));
    }

    [Fact]
    public async Task RunJob_ProgressBroadcastFaultsMidRun_JobStillReachesATerminalStateWithEndTime()
    {
        // The broadcaster returns a faulted task (rather than throwing) for the first
        // per-series progress update. Whatever the job does with that failure, it must
        // not be left looking "running" forever and the cleanup step must still run.
        _broadcaster
            .Setup(b => b.BroadcastJobUpdateAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()))
            .Returns<Guid, string, int, int, int, int>((_, status, processed, _, _, _) =>
                status == "running" && processed == 1
                    ? Task.FromException(new InvalidOperationException("hub send failed"))
                    : Task.CompletedTask);
        SetupRefresh("Alpha", "success");
        SetupRefresh("Beta", "success");
        var service = CreateService(withFileStore: false, withProcessor: false);

        var jobId = await service.StartAsync(new[] { "Alpha", "Beta" });
        var job = await WaitForCleanupAsync(service, jobId);

        Assert.True(job.Status is JobStatus.Completed or JobStatus.Failed, $"Unexpected status {job.Status}");
        Assert.NotNull(job.EndTime);
        Assert.True(job.ProcessedSeries >= 1);
    }

    [Fact]
    public async Task RunJob_TokenAlreadyCancelled_CancelsWithoutRefreshingOrRetagging()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var service = CreateService();

        var jobId = await service.StartAsync(new[] { "Alpha", "Beta" }, cts.Token);
        var job = await WaitForCleanupAsync(service, jobId);

        Assert.Equal(JobStatus.Cancelled, job.Status);
        Assert.Equal(0, job.ProcessedSeries);
        Assert.Null(job.CurrentSeries);
        Assert.NotNull(job.EndTime);
        Assert.Equal("cancelled", _broadcasts.Last().Status);
        _cache.Verify(c => c.RefreshAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _fileStore.Verify(f => f.GetAllFilesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunJob_CancelledMidRun_StopsBeforeTheNextSeriesAndSkipsRetag()
    {
        using var cts = new CancellationTokenSource();
        _cache.Setup(c => c.RefreshAsync("Alpha", It.IsAny<CancellationToken>()))
            .Callback(() => cts.Cancel())
            .ReturnsAsync(new SeriesMetadataCacheRecord { CanonicalTitle = "Alpha", LookupStatus = "success" });
        SetupTrackedFiles(TrackedFile("/library/Alpha/c001.cbz", "Alpha"));
        var service = CreateService();

        var jobId = await service.StartAsync(new[] { "Alpha", "Beta", "Gamma" }, cts.Token);
        var job = await WaitForCleanupAsync(service, jobId);

        Assert.Equal(JobStatus.Cancelled, job.Status);
        Assert.Equal(1, job.ProcessedSeries);
        Assert.Equal(1, job.Successes);
        Assert.Equal("Alpha", Assert.Single(job.RecentResults).SeriesTitle);
        _cache.Verify(c => c.RefreshAsync("Beta", It.IsAny<CancellationToken>()), Times.Never);
        _cache.Verify(c => c.RefreshAsync("Gamma", It.IsAny<CancellationToken>()), Times.Never);
        _processor.Verify(p => p.NormalizeFilesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunJob_RefreshObservesCancellation_MarksJobCancelledWithoutRecordingAnOutcome()
    {
        using var cts = new CancellationTokenSource();
        _cache.Setup(c => c.RefreshAsync("Alpha", It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                cts.Cancel();
                return Task.FromException<SeriesMetadataCacheRecord>(new OperationCanceledException(cts.Token));
            });
        var service = CreateService();

        var jobId = await service.StartAsync(new[] { "Alpha", "Beta" }, cts.Token);
        var job = await WaitForCleanupAsync(service, jobId);

        Assert.Equal(JobStatus.Cancelled, job.Status);
        Assert.Equal(0, job.ProcessedSeries);
        Assert.Equal(0, job.Failures);
        Assert.Empty(job.RecentResults);
        Assert.Equal("Alpha", job.CurrentSeries);
        _cache.Verify(c => c.RefreshAsync("Beta", It.IsAny<CancellationToken>()), Times.Never);
        _fileStore.Verify(f => f.GetAllFilesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunJob_CleanupRemovesStaleRecords_LogsTheRemovedCount()
    {
        SetupCleanup(removed: 3);
        SetupRefresh("Alpha", "success");
        var service = CreateService(withFileStore: false, withProcessor: false);

        var jobId = await service.StartAsync(new[] { "Alpha" });

        await _logger.WaitForAsync(e => e.Level == LogLevel.Information
            && e.Message.Contains($"Removed 3 stale series-metadata cache record(s) after refresh job {jobId}"));
        Assert.Equal(JobStatus.Completed, service.GetJob(jobId)!.Status);
    }

    [Fact]
    public async Task RunJob_CleanupFails_IsLoggedAndRetagStillRuns()
    {
        _cache.Setup(c => c.CleanupStaleSiblingRecordsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database is locked"));
        SetupRefresh("Alpha", "success");
        SetupTrackedFiles(TrackedFile("/library/Alpha/c001.cbz", "Alpha"));
        var normalizeJobId = Guid.NewGuid();
        _processor.Setup(p => p.NormalizeFilesAsync(It.IsAny<IEnumerable<string>>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(normalizeJobId);
        var service = CreateService();

        var jobId = await service.StartAsync(new[] { "Alpha" });

        await _logger.WaitForAsync(e => e.Message.Contains($"queued normalization job {normalizeJobId}"));
        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Warning
            && e.Message.Contains($"Failed to clean up stale series-metadata cache records after refresh job {jobId}")
            && e.Exception is InvalidOperationException);
        Assert.Equal(JobStatus.Completed, service.GetJob(jobId)!.Status);
        _processor.Verify(p => p.NormalizeFilesAsync(
            It.Is<IEnumerable<string>>(paths => paths.SequenceEqual(new[] { "/library/Alpha/c001.cbz" })),
            true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunJob_Completed_ForceNormalizesFilesTaggedWithTheTitleCanonicalTitleOrAnyAlias()
    {
        SetupRefresh(
            "One Piece",
            "success",
            canonical: "One Piece (Official)",
            aliases: new List<string> { "Wan Pisu", " " },
            userAliases: new List<string> { "OP", "" });
        SetupTrackedFiles(
            TrackedFile("/library/a.cbz", "one piece"),
            TrackedFile("/library/b.cbz", "One Piece (Official)"),
            TrackedFile("/library/c.cbz", "WAN PISU"),
            TrackedFile("/library/d.cbz", "OP"),
            TrackedFile("/LIBRARY/A.CBZ", "One Piece"),
            TrackedFile("/library/naruto.cbz", "Naruto"),
            TrackedFile("/library/no-metadata.cbz", null),
            TrackedFile("/library/blank-series.cbz", "   "),
            new ComicFile { FilePath = "", Metadata = new ComicMetadata { Series = "OP" } });
        List<string>? normalized = null;
        var normalizeJobId = Guid.NewGuid();
        _processor.Setup(p => p.NormalizeFilesAsync(It.IsAny<IEnumerable<string>>(), true, It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<string>, bool, CancellationToken>((paths, _, _) => normalized = paths.ToList())
            .ReturnsAsync(normalizeJobId);
        var service = CreateService();

        var jobId = await service.StartAsync(new[] { "One Piece" });

        await _logger.WaitForAsync(e => e.Level == LogLevel.Information
            && e.Message.Contains($"Metadata refresh job {jobId} queued normalization job {normalizeJobId} for 4 files"));
        Assert.Equal(
            new[] { "/library/a.cbz", "/library/b.cbz", "/library/c.cbz", "/library/d.cbz" },
            normalized);
        _processor.Verify(p => p.NormalizeFilesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunJob_ErrorOutcomeStillRetagsFilesMatchingTheRequestedTitle()
    {
        // Even when the lookup failed the requested title is kept in the retag set, so
        // files whose <Series> still holds that title are re-normalized from the cache.
        SetupRefresh("Alpha", "error", aliases: new List<string>(), userAliases: new List<string>());
        SetupTrackedFiles(TrackedFile("/library/Alpha/c001.cbz", "Alpha"));
        var normalizeJobId = Guid.NewGuid();
        _processor.Setup(p => p.NormalizeFilesAsync(It.IsAny<IEnumerable<string>>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(normalizeJobId);
        var service = CreateService();

        var jobId = await service.StartAsync(new[] { "Alpha" });

        await _logger.WaitForAsync(e => e.Message.Contains($"queued normalization job {normalizeJobId} for 1 files"));
        var job = service.GetJob(jobId)!;
        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.Equal(1, job.Failures);
    }

    [Fact]
    public async Task RunJob_NoTrackedFileMatches_DoesNotQueueNormalization()
    {
        SetupRefresh("Alpha", "success");
        SetupTrackedFiles(TrackedFile("/library/Beta/c001.cbz", "Beta"), TrackedFile("/library/none.cbz", null));
        var service = CreateService();

        var jobId = await service.StartAsync(new[] { "Alpha" });

        await _logger.WaitForAsync(e => e.Level == LogLevel.Information
            && e.Message.Contains($"Metadata refresh job {jobId} completed; no tracked files matched")
            && e.Message.Contains("nothing to retag"));
        _processor.Verify(p => p.NormalizeFilesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        _processor.Verify(p => p.NormalizeFilesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunJob_RetagFails_IsLoggedAndJobStaysCompleted()
    {
        SetupRefresh("Alpha", "success");
        _fileStore.Setup(f => f.GetAllFilesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("store offline"));
        var service = CreateService();

        var jobId = await service.StartAsync(new[] { "Alpha" });

        await _logger.WaitForAsync(e => e.Level == LogLevel.Warning
            && e.Message.Contains($"Failed to queue per-file retag after metadata refresh job {jobId}")
            && e.Exception is IOException);
        var job = service.GetJob(jobId)!;
        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.Equal(1, job.Successes);
        Assert.Equal(0, job.Failures);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task RunJob_WithoutFileStoreOrProcessor_SkipsRetag(bool withFileStore, bool withProcessor)
    {
        SetupRefresh("Alpha", "success");
        SetupTrackedFiles(TrackedFile("/library/Alpha/c001.cbz", "Alpha"));
        var service = CreateService(withFileStore: withFileStore, withProcessor: withProcessor);

        var jobId = await service.StartAsync(new[] { "Alpha" });
        var job = await WaitForCleanupAsync(service, jobId);

        Assert.Equal(JobStatus.Completed, job.Status);
        _fileStore.Verify(f => f.GetAllFilesAsync(It.IsAny<CancellationToken>()), Times.Never);
        _processor.Verify(p => p.NormalizeFilesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunJob_EveryRefreshThrows_NothingToRetag()
    {
        _cache.Setup(c => c.RefreshAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("provider down"));
        SetupTrackedFiles(TrackedFile("/library/Alpha/c001.cbz", "Alpha"));
        var service = CreateService();

        var jobId = await service.StartAsync(new[] { "Alpha", "Beta" });
        var job = await WaitForCleanupAsync(service, jobId);

        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.Equal(2, job.Failures);
        Assert.All(job.RecentResults, r => Assert.Equal("error", r.Status));
        _fileStore.Verify(f => f.GetAllFilesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void GetJob_UnknownId_ReturnsNull()
    {
        Assert.Null(CreateService().GetJob(Guid.NewGuid()));
    }

    [Fact]
    public async Task GetJob_ReturnsASnapshotThatDoesNotExposeInternalState()
    {
        SetupRefresh("Alpha", "success", source: "AniList");
        var service = CreateService(withFileStore: false, withProcessor: false);
        var jobId = await service.StartAsync(new[] { "Alpha" });
        await WaitForCleanupAsync(service, jobId);

        var snapshot = service.GetJob(jobId)!;
        snapshot.Status = JobStatus.Failed;
        snapshot.Series.Add("Injected");
        snapshot.RecentResults[0].Status = "tampered";
        snapshot.RecentResults.Add(new MetadataRefreshOutcome { SeriesTitle = "Injected" });

        var fresh = service.GetJob(jobId)!;
        Assert.NotSame(snapshot, fresh);
        Assert.Equal(JobStatus.Completed, fresh.Status);
        Assert.Equal(new[] { "Alpha" }, fresh.Series);
        var outcome = Assert.Single(fresh.RecentResults);
        Assert.Equal("success", outcome.Status);
        Assert.Equal("AniList", outcome.Source);
    }

    [Fact]
    public async Task GetAllJobs_ReturnsEveryStartedJob()
    {
        var service = CreateService();
        Assert.Empty(service.GetAllJobs());

        var first = await service.StartAsync(Array.Empty<string>());
        var second = await service.StartAsync(Array.Empty<string>());
        Assert.True(await _cleanupCalls.WaitAsync(WaitTimeout));
        Assert.True(await _cleanupCalls.WaitAsync(WaitTimeout));

        var jobs = service.GetAllJobs().ToList();
        Assert.Equal(2, jobs.Count);
        Assert.Equal(new[] { first, second }.OrderBy(id => id), jobs.Select(j => j.JobId).OrderBy(id => id));
        Assert.All(jobs, j => Assert.Equal(JobStatus.Completed, j.Status));
    }

    private sealed record BroadcastCall(Guid JobId, string Status, int Processed, int Total, int Success, int Errors);

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    /// <summary>Thread-safe logger that records entries and lets a test await a specific one.</summary>
    private sealed class RecordingLogger : ILogger<SeriesMetadataRefreshJobService>
    {
        private readonly object _gate = new();
        private readonly List<LogEntry> _entries = new();
        private readonly List<(Func<LogEntry, bool> Predicate, TaskCompletionSource Signal)> _waiters = new();

        public IReadOnlyList<LogEntry> Entries
        {
            get { lock (_gate) return _entries.ToList(); }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var entry = new LogEntry(logLevel, formatter(state, exception), exception);
            lock (_gate)
            {
                _entries.Add(entry);
                foreach (var waiter in _waiters.Where(w => w.Predicate(entry)).ToList())
                {
                    waiter.Signal.TrySetResult();
                    _waiters.Remove(waiter);
                }
            }
        }

        public Task WaitForAsync(Func<LogEntry, bool> predicate)
        {
            lock (_gate)
            {
                if (_entries.Any(predicate))
                {
                    return Task.CompletedTask;
                }
                var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((predicate, signal));
                return signal.Task.WaitAsync(WaitTimeout);
            }
        }
    }
}
