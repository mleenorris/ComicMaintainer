using System.Collections.Concurrent;
using System.Reflection;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Covers what the job registry owns: waiting for a job, keeping job state, persisting it and
/// forgetting finished jobs under the shared retention policy. The per-item pipeline is covered
/// through <see cref="ComicProcessorService"/> in ComicProcessorServiceJobCoverageTests.
/// </summary>
public sealed class BatchJobRegistryTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly Mock<IJobStateStore> _store = new();
    private readonly List<ProcessingJob> _saved = new();

    public BatchJobRegistryTests()
    {
        _store
            .Setup(s => s.SaveAsync(It.IsAny<ProcessingJob>(), It.IsAny<CancellationToken>()))
            .Callback<ProcessingJob, CancellationToken>((job, _) =>
            {
                lock (_saved)
                {
                    _saved.Add(job);
                }
            })
            .Returns(Task.CompletedTask);
    }

    private static BatchJobRegistry CreateRegistry(IJobStateStore? store = null, IEventBroadcaster? broadcaster = null) =>
        new(
            new TestOptionsMonitor<AppSettings>(new AppSettings()),
            NullLogger<BatchJobRegistry>.Instance,
            store,
            broadcaster);

    private static BatchJobDefinition Definition(
        Func<string, CancellationToken, Task<BatchItemResult>>? item = null,
        Func<IBatchJobReport, CancellationToken, Task>? postLoop = null) =>
        new(
            "TestJob",
            "testing",
            item ?? ((_, _) => Task.FromResult<BatchItemResult>(true)),
            "Item failed",
            postLoop);

    private static ProcessingJob FinishedJob(DateTime startTime, JobStatus status = JobStatus.Completed) =>
        new() { JobId = Guid.NewGuid(), Status = status, StartTime = startTime };

    private static IEnumerable<ProcessingJob> FinishedJobs(int count, TimeSpan age) =>
        Enumerable.Range(0, count).Select(i => FinishedJob(DateTime.UtcNow - age - TimeSpan.FromMinutes(i)));

    private static async Task RunToCompletionAsync(BatchJobRegistry registry, params string[] items)
    {
        var jobId = registry.StartJob(Definition(), items);
        await registry.WaitForCompletionAsync(jobId).WaitAsync(Timeout);
    }

    // ---- waiting for a job ---------------------------------------------------------------

    [Fact]
    public async Task WaitForCompletion_UnknownJob_ReturnsNull()
    {
        Assert.Null(await CreateRegistry().WaitForCompletionAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task WaitForCompletion_FinishesWithTheJobAndReturnsItsFinalState()
    {
        var registry = CreateRegistry();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var jobId = registry.StartJob(
            Definition(async (item, _) =>
            {
                await release.Task;
                return item == "good" ? true : new BatchItemResult(false, "bad item");
            }),
            new[] { "good", "bad" });

        var waiting = registry.WaitForCompletionAsync(jobId);

        await Task.Delay(50);
        Assert.False(waiting.IsCompleted);

        release.SetResult();
        var job = await waiting.WaitAsync(Timeout);

        Assert.NotNull(job);
        Assert.Equal(JobStatus.Completed, job!.Status);
        Assert.Equal(1, job.ProcessedFiles);
        Assert.Equal(1, job.FailedFiles);
        Assert.Equal("bad item", job.Errors["bad"]);
        Assert.NotNull(job.EndTime);
    }

    [Fact]
    public async Task WaitForCompletion_JobAlreadyFinished_ReturnsItsState()
    {
        var registry = CreateRegistry();
        var jobId = registry.StartJob(Definition(), new[] { "a" });
        await registry.WaitForCompletionAsync(jobId).WaitAsync(Timeout);

        var job = await registry.WaitForCompletionAsync(jobId).WaitAsync(Timeout);

        Assert.Equal(JobStatus.Completed, job!.Status);
    }

    [Fact]
    public async Task WaitForCompletion_Cancelled_ThrowsAndLeavesTheJobRunning()
    {
        var registry = CreateRegistry();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var jobId = registry.StartJob(Definition(async (_, _) =>
        {
            await release.Task;
            return true;
        }), new[] { "a" });
        using var cts = new CancellationTokenSource();

        var waiting = registry.WaitForCompletionAsync(jobId, cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        // Only the waiter was cancelled; the job still ends on its own.
        release.SetResult();
        var job = await registry.WaitForCompletionAsync(jobId).WaitAsync(Timeout);
        Assert.Equal(JobStatus.Completed, job!.Status);
    }

    [Fact]
    public async Task WaitForCompletion_TokenAlreadyCancelled_ThrowsEvenThoughTheJobHasFinished()
    {
        var registry = CreateRegistry();
        var jobId = registry.StartJob(Definition(), new[] { "a" });
        await registry.WaitForCompletionAsync(jobId).WaitAsync(Timeout);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => registry.WaitForCompletionAsync(jobId, cts.Token));
    }

    [Theory]
    [InlineData(JobStatus.Interrupted)]
    [InlineData(JobStatus.Running)]
    public async Task WaitForCompletion_RestoredJob_ReturnsAtOnceBecauseNothingWillRunIt(JobStatus status)
    {
        var registry = CreateRegistry();
        var restored = new ProcessingJob { JobId = Guid.NewGuid(), Status = status };
        registry.RestoreJobs(new[] { restored });

        var job = await registry.WaitForCompletionAsync(restored.JobId).WaitAsync(Timeout);

        Assert.Equal(status, job!.Status);
    }

    [Fact]
    public async Task WaitForCompletion_IsReleasedOnlyAfterTheFinalStateIsStored()
    {
        var saveGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _store
            .Setup(s => s.SaveAsync(It.Is<ProcessingJob>(j => j.IsTerminal), It.IsAny<CancellationToken>()))
            .Returns(saveGate.Task);
        var registry = CreateRegistry(_store.Object);
        var jobId = registry.StartJob(Definition(), new[] { "a" });

        var waiting = registry.WaitForCompletionAsync(jobId);

        await Task.Delay(100);
        Assert.False(waiting.IsCompleted);

        saveGate.SetResult();
        await waiting.WaitAsync(Timeout);
        Assert.Equal(JobStatus.Completed, registry.GetJob(jobId)!.Status);
    }

    [Fact]
    public async Task WaitForCompletion_FinalBroadcastFails_StillReleasesAndKeepsTheOutcome()
    {
        var broadcaster = new Mock<IEventBroadcaster>();
        broadcaster
            .Setup(b => b.BroadcastJobUpdateAsync(It.IsAny<Guid>(), "completed", It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()))
            .ThrowsAsync(new InvalidOperationException("broadcast failed"));
        var registry = CreateRegistry(_store.Object, broadcaster.Object);

        var jobId = registry.StartJob(Definition(), new[] { "a" });
        var job = await registry.WaitForCompletionAsync(jobId).WaitAsync(Timeout);

        Assert.Equal(JobStatus.Completed, job!.Status);
        // Otherwise a restart would report the finished job as interrupted.
        _store.Verify(
            s => s.SaveAsync(It.Is<ProcessingJob>(j => j.JobId == jobId && j.Status == JobStatus.Completed), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ItemThrowing_FailsTheJobAndReleasesWaiters()
    {
        var registry = CreateRegistry();

        var jobId = registry.StartJob(
            Definition((_, _) => throw new InvalidOperationException("item failed unexpectedly")),
            new[] { "a" });
        var job = await registry.WaitForCompletionAsync(jobId).WaitAsync(Timeout);

        Assert.Equal(JobStatus.Failed, job!.Status);
        Assert.NotNull(job.EndTime);
    }

    [Fact]
    public async Task PostLoopThrowing_FailsTheJobAfterItsItemsRan()
    {
        var registry = CreateRegistry();

        var jobId = registry.StartJob(
            Definition(postLoop: (_, _) => throw new InvalidOperationException("cleanup failed")),
            new[] { "a" });
        var job = await registry.WaitForCompletionAsync(jobId).WaitAsync(Timeout);

        Assert.Equal(JobStatus.Failed, job!.Status);
        Assert.Equal(1, job.ProcessedFiles);
    }

    [Fact]
    public async Task ItemThrowingOutOfMemory_MarksTheJobFailedAndReleasesWaiters()
    {
        var registry = CreateRegistry();

        var jobId = registry.StartJob(
            Definition((_, _) => throw new OutOfMemoryException()),
            new[] { "a" });
        var job = await registry.WaitForCompletionAsync(jobId).WaitAsync(Timeout);

        // A runner that has gone must not leave the job reported as active forever.
        Assert.Equal(JobStatus.Failed, job!.Status);
        Assert.Null(registry.GetActiveJob());
    }

    // ---- reading job state ----------------------------------------------------------------

    [Fact]
    public void StartJob_NullDefinition_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => CreateRegistry().StartJob(null!, new[] { "a" }));
    }

    [Fact]
    public void GetJob_UnknownJob_ReturnsNull()
    {
        Assert.Null(CreateRegistry().GetJob(Guid.NewGuid()));
    }

    [Fact]
    public async Task GetJob_ReturnsACopyTheCallerCannotChangeTheJobThrough()
    {
        var registry = CreateRegistry();
        var jobId = registry.StartJob(Definition(), new[] { "a" });
        await registry.WaitForCompletionAsync(jobId).WaitAsync(Timeout);

        var copy = registry.GetJob(jobId)!;
        copy.Files.Add("injected");
        copy.Errors["injected"] = "injected";

        var job = registry.GetJob(jobId)!;
        Assert.Equal(new[] { "a" }, job.Files);
        Assert.Empty(job.Errors);
    }

    [Fact]
    public async Task GetActiveJob_ReportsARunningJobAndNotAFinishedOne()
    {
        var registry = CreateRegistry();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        registry.RestoreJobs(new[] { FinishedJob(DateTime.UtcNow) });
        var jobId = registry.StartJob(Definition(async (_, _) =>
        {
            started.TrySetResult();
            await release.Task;
            return true;
        }), new[] { "a", "b" });
        await started.Task.WaitAsync(Timeout);

        var active = registry.GetActiveJob();

        Assert.NotNull(active);
        Assert.Equal(jobId, active!.JobId);
        Assert.False(active.IsTerminal);
        Assert.Equal(2, active.TotalFiles);

        release.SetResult();
        await registry.WaitForCompletionAsync(jobId).WaitAsync(Timeout);
        Assert.Null(registry.GetActiveJob());
    }

    [Fact]
    public void GetJobSummaries_ListsNewestFirst()
    {
        var registry = CreateRegistry();
        var older = FinishedJob(DateTime.UtcNow.AddHours(-2));
        var newest = FinishedJob(DateTime.UtcNow);
        var middle = FinishedJob(DateTime.UtcNow.AddHours(-1));
        registry.RestoreJobs(new[] { older, newest, middle });

        Assert.Equal(new[] { newest.JobId, middle.JobId, older.JobId }, registry.GetJobSummaries().Select(j => j.JobId));
    }

    [Fact]
    public void RestoreJobs_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => CreateRegistry().RestoreJobs(null!));
    }

    // ---- persistence ----------------------------------------------------------------------

    [Fact]
    public async Task ProgressBetweenStatusChanges_IsThrottled()
    {
        var registry = CreateRegistry(_store.Object);

        await RunToCompletionAsync(registry, Enumerable.Range(0, 50).Select(i => $"item{i}").ToArray());

        // One write per status change, however many items there were. (A slow machine may fit
        // one interval write into the run, so allow for it.)
        lock (_saved)
        {
            Assert.InRange(_saved.Count, 3, 4);
            Assert.Equal(JobStatus.Queued, _saved[0].Status);
            Assert.Equal(JobStatus.Completed, _saved[^1].Status);
        }
    }

    [Fact]
    public async Task PersistedSnapshots_ShareOneFileList()
    {
        var registry = CreateRegistry(_store.Object);

        await RunToCompletionAsync(registry, "a", "b");

        // The list is fixed at creation and can run to tens of thousands of paths.
        lock (_saved)
        {
            Assert.True(_saved.Count >= 2);
            Assert.All(_saved, job => Assert.Same(_saved[0].Files, job.Files));
        }
    }

    [Fact]
    public async Task PersistenceFailure_DoesNotFailTheJob()
    {
        _store
            .Setup(s => s.SaveAsync(It.IsAny<ProcessingJob>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database unavailable"));
        var registry = CreateRegistry(_store.Object);

        var jobId = registry.StartJob(Definition(), new[] { "a" });
        var job = await registry.WaitForCompletionAsync(jobId).WaitAsync(Timeout);

        Assert.Equal(JobStatus.Completed, job!.Status);
    }

    [Fact]
    public async Task DeletingAFinishedJob_BeforeItsFinalWrite_KeepsItFromBeingStored()
    {
        var broadcaster = new Mock<IEventBroadcaster>();
        var finishedAnnounced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAnnouncement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        broadcaster
            .Setup(b => b.BroadcastJobUpdateAsync(It.IsAny<Guid>(), "completed", It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()))
            .Returns(async () =>
            {
                finishedAnnounced.TrySetResult();
                await releaseAnnouncement.Task;
            });
        var registry = CreateRegistry(_store.Object, broadcaster.Object);
        var jobId = registry.StartJob(Definition(), new[] { "a" });
        await finishedAnnounced.Task.WaitAsync(Timeout);

        Assert.True(await registry.DeleteJobAsync(jobId));
        releaseAnnouncement.SetResult();

        // Nothing left to wait on through the registry, so let the runner finish its publish.
        await Task.Delay(100);
        _store.Verify(s => s.DeleteAsync(jobId, It.IsAny<CancellationToken>()), Times.Once);
        lock (_saved)
        {
            Assert.DoesNotContain(_saved, j => j.Status == JobStatus.Completed);
        }
    }

    [Fact]
    public async Task DeleteJob_StoreFails_KeepsTheJobSoDeletingCanBeRetried()
    {
        var job = FinishedJob(DateTime.UtcNow);
        var registry = CreateRegistry(_store.Object);
        registry.RestoreJobs(new[] { job });
        _store
            .Setup(s => s.DeleteAsync(job.JobId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database unavailable"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.DeleteJobAsync(job.JobId));
        Assert.NotNull(registry.GetJob(job.JobId));

        _store
            .Setup(s => s.DeleteAsync(job.JobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        Assert.True(await registry.DeleteJobAsync(job.JobId));
        Assert.Null(registry.GetJob(job.JobId));
    }

    [Fact]
    public void AllPerJobStateLivesInOneDictionary()
    {
        // The parallel per-job dictionaries this replaced leaked entries: a delete removed only
        // some of them. With one, forgetting a job cannot leave part of it behind.
        var dictionaries = typeof(BatchJobRegistry)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(field => field.FieldType.IsGenericType
                && field.FieldType.GetGenericTypeDefinition() == typeof(ConcurrentDictionary<,>));

        Assert.Single(dictionaries);
    }

    // ---- retention ------------------------------------------------------------------------

    [Fact]
    public async Task Retention_ForgetsOldFinishedJobsBeyondTheMostRecent()
    {
        var registry = CreateRegistry(_store.Object);
        var old = FinishedJobs(BatchJobRetention.KeepMostRecent + 10, BatchJobRetention.MaxAge + TimeSpan.FromDays(1)).ToList();
        registry.RestoreJobs(old);

        await RunToCompletionAsync(registry, "a");

        var kept = registry.GetJobSummaries();
        Assert.Equal(BatchJobRetention.KeepMostRecent, kept.Count);
        // The most recent old jobs stayed, so recent history is never empty.
        Assert.Contains(kept, j => j.JobId == old[0].JobId);
        Assert.Null(registry.GetJob(old[^1].JobId));
        _store.Verify(
            s => s.PruneAsync(
                It.Is<DateTime>(cutoff => DateTime.UtcNow - cutoff >= BatchJobRetention.MaxAge - TimeSpan.FromMinutes(1)),
                BatchJobRetention.KeepMostRecent,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Retention_KeepsOldJobsWithinTheMostRecentCount()
    {
        var registry = CreateRegistry(_store.Object);
        registry.RestoreJobs(FinishedJobs(10, BatchJobRetention.MaxAge + TimeSpan.FromDays(1)).ToList());

        await RunToCompletionAsync(registry, "a");

        Assert.Equal(11, registry.GetJobSummaries().Count);
        _store.Verify(s => s.PruneAsync(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Retention_KeepsJobsYoungerThanTheCutoffHoweverMany()
    {
        var registry = CreateRegistry();
        registry.RestoreJobs(FinishedJobs(BatchJobRetention.KeepMostRecent + 10, TimeSpan.FromHours(1)).ToList());

        await RunToCompletionAsync(registry, "a");

        Assert.Equal(BatchJobRetention.KeepMostRecent + 11, registry.GetJobSummaries().Count);
    }

    [Fact]
    public async Task Retention_NeverForgetsAJobStillInFlight()
    {
        var registry = CreateRegistry();
        var inFlight = new ProcessingJob
        {
            JobId = Guid.NewGuid(),
            Status = JobStatus.Running,
            StartTime = DateTime.UtcNow - BatchJobRetention.MaxAge - TimeSpan.FromDays(30)
        };
        registry.RestoreJobs(FinishedJobs(BatchJobRetention.KeepMostRecent + 10, BatchJobRetention.MaxAge + TimeSpan.FromDays(1)).ToList());
        registry.RestoreJobs(new[] { inFlight });

        await RunToCompletionAsync(registry, "a");

        Assert.NotNull(registry.GetJob(inFlight.JobId));
    }

    [Fact]
    public async Task Retention_PruneFailure_DoesNotAffectAnything()
    {
        _store
            .Setup(s => s.PruneAsync(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database unavailable"));
        var registry = CreateRegistry(_store.Object);
        registry.RestoreJobs(FinishedJobs(BatchJobRetention.KeepMostRecent + 1, BatchJobRetention.MaxAge + TimeSpan.FromDays(1)).ToList());

        var jobId = registry.StartJob(Definition(), new[] { "a" });
        var job = await registry.WaitForCompletionAsync(jobId).WaitAsync(Timeout);

        Assert.Equal(JobStatus.Completed, job!.Status);
    }

    // ---- cancellation -----------------------------------------------------------------------

    [Fact]
    public async Task CancelActiveJobs_StopsEveryRunningJobAndCountsThem()
    {
        var registry = CreateRegistry();
        var started = new CountdownEvent(2);
        var finished = FinishedJob(DateTime.UtcNow);
        registry.RestoreJobs(new[] { finished });

        Func<string, CancellationToken, Task<BatchItemResult>> hold = async (_, token) =>
        {
            started.Signal();
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token);
            return true;
        };
        var first = registry.StartJob(Definition(hold), new[] { "a" });
        var second = registry.StartJob(Definition(hold), new[] { "b" });
        Assert.True(started.Wait(Timeout));

        Assert.Equal(2, registry.CancelActiveJobs());

        Assert.Equal(JobStatus.Cancelled, (await registry.WaitForCompletionAsync(first).WaitAsync(Timeout))!.Status);
        Assert.Equal(JobStatus.Cancelled, (await registry.WaitForCompletionAsync(second).WaitAsync(Timeout))!.Status);
        Assert.Equal(JobStatus.Completed, registry.GetJob(finished.JobId)!.Status);
    }

    [Fact]
    public async Task Dispose_CancelsRunningJobs()
    {
        var registry = CreateRegistry();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var jobId = registry.StartJob(Definition(async (_, token) =>
        {
            started.TrySetResult();
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token);
            return true;
        }), new[] { "a" });
        await started.Task.WaitAsync(Timeout);

        registry.Dispose();

        var job = await registry.WaitForCompletionAsync(jobId).WaitAsync(Timeout);
        Assert.Equal(JobStatus.Cancelled, job!.Status);
    }

    [Fact]
    public async Task ThroughTheProcessor_DisposingCancelsItsJobs()
    {
        var registry = new Mock<IBatchJobRegistry>();
        var processor = new ComicProcessorService(
            new TestOptionsMonitor<AppSettings>(new AppSettings()),
            NullLogger<ComicProcessorService>.Instance,
            Mock.Of<IFileStoreService>(),
            Mock.Of<IProcessingHistoryService>(),
            jobRegistry: registry.Object);

        processor.Dispose();

        registry.Verify(r => r.CancelActiveJobs(), Times.Once);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task HandlerWaitsForTheJobThroughTheRegistry()
    {
        // The handlers wait on the job's completion rather than polling for it.
        var registry = CreateRegistry();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var jobId = registry.StartJob(Definition(async (_, _) =>
        {
            await release.Task;
            return true;
        }), new[] { "/library/c002.cbz" });

        var fileStore = new Mock<IFileStoreService>();
        fileStore
            .Setup(f => f.GetAllFilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ComicFile> { new() { FilePath = "/library/c002.cbz", IsRenamed = false } });
        var processor = new Mock<IComicProcessorService>();
        processor
            .Setup(p => p.RenameFilesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(jobId);
        var handler = new FileNamingAuditJobHandler(fileStore.Object, processor.Object, registry, NullLogger<FileNamingAuditJobHandler>.Instance);

        var run = handler.ExecuteAsync("""{"autoCorrect":true}""", CancellationToken.None);
        await Task.Delay(100);
        Assert.False(run.IsCompleted);

        release.SetResult();
        var summary = await run.WaitAsync(Timeout);

        Assert.Equal("Evaluated 1/1 file(s): 1 processed, 0 failed (status: Completed).", summary);
    }
}
