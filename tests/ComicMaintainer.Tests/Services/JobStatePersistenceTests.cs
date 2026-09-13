using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using ComicMaintainer.WebApi.Services;
using Microsoft.Extensions.Logging;
using Moq;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Covers persistence of batch job state and the startup reconciliation that reports jobs
/// orphaned by a restart.
/// </summary>
public class JobStatePersistenceTests : IDisposable
{
    private readonly string _testDirectory =
        Path.Combine(Path.GetTempPath(), $"jobstate_{Guid.NewGuid()}");

    private readonly Mock<IFileStoreService> _fileStore = new();
    private readonly Mock<IProcessingHistoryService> _history = new();
    private readonly Mock<IJobStateStore> _jobStateStore = new();

    public JobStatePersistenceTests()
    {
        Directory.CreateDirectory(_testDirectory);
        _fileStore
            .Setup(f => f.GetFilteredFilesAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ComicFile>());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort cleanup.
        }
        GC.SuppressFinalize(this);
    }

    private ComicProcessorService CreateProcessor()
    {
        return new ComicProcessorService(
            new TestOptionsMonitor<AppSettings>(new AppSettings { WatchedDirectory = _testDirectory }),
            new Mock<ILogger<ComicProcessorService>>().Object,
            _fileStore.Object,
            _history.Object,
            jobStateStore: _jobStateStore.Object);
    }

    private static async Task<ProcessingJob?> WaitForTerminalAsync(
        IComicProcessorService processor,
        Guid jobId,
        int timeoutMs = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var job = processor.GetJob(jobId);
            if (job != null && job.IsTerminal)
            {
                return job;
            }
            await Task.Delay(25);
        }

        return processor.GetJob(jobId);
    }

    [Fact]
    public async Task BatchJob_PersistsCreationAndTerminalState()
    {
        var processor = CreateProcessor();
        var saved = new List<ProcessingJob>();
        _jobStateStore
            .Setup(s => s.SaveAsync(It.IsAny<ProcessingJob>(), It.IsAny<CancellationToken>()))
            .Callback<ProcessingJob, CancellationToken>((j, _) => saved.Add(j))
            .Returns(Task.CompletedTask);

        var jobId = await processor.ProcessFilesAsync(new List<string>());
        var job = await WaitForTerminalAsync(processor, jobId);

        Assert.NotNull(job);
        Assert.Equal(JobStatus.Completed, job!.Status);

        // A restart must be able to see both that the job existed and how it ended.
        Assert.Contains(saved, j => j.Status == JobStatus.Running);
        Assert.Contains(saved, j => j.Status == JobStatus.Completed);
    }

    [Fact]
    public async Task BatchJob_RecordsOperationNameForPostRestartDisplay()
    {
        var processor = CreateProcessor();
        var saved = new List<ProcessingJob>();
        _jobStateStore
            .Setup(s => s.SaveAsync(It.IsAny<ProcessingJob>(), It.IsAny<CancellationToken>()))
            .Callback<ProcessingJob, CancellationToken>((j, _) => saved.Add(j))
            .Returns(Task.CompletedTask);

        var jobId = await processor.ProcessFilesAsync(new List<string>());
        await WaitForTerminalAsync(processor, jobId);

        Assert.NotEmpty(saved);
        Assert.All(saved, j => Assert.False(string.IsNullOrWhiteSpace(j.OperationName)));
    }

    [Fact]
    public async Task BatchJob_SucceedsEvenWhenPersistenceFails()
    {
        var processor = CreateProcessor();
        _jobStateStore
            .Setup(s => s.SaveAsync(It.IsAny<ProcessingJob>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database unavailable"));

        var jobId = await processor.ProcessFilesAsync(new List<string>());
        var job = await WaitForTerminalAsync(processor, jobId);

        // Job history is observability; losing it must never fail the user's batch.
        Assert.NotNull(job);
        Assert.Equal(JobStatus.Completed, job!.Status);
    }

    [Fact]
    public async Task BatchJob_NeverPersistsANonTerminalStateAfterTheTerminalOne()
    {
        // Status writes are fire-and-forget, so for a job that finishes almost immediately an
        // early "queued"/"running" write can still be in flight. If one landed last, the next
        // restart would report a job that actually completed as interrupted.
        var processor = CreateProcessor();
        var saved = new List<JobStatus>();
        _jobStateStore
            .Setup(s => s.SaveAsync(It.IsAny<ProcessingJob>(), It.IsAny<CancellationToken>()))
            .Callback<ProcessingJob, CancellationToken>((j, _) =>
            {
                lock (saved)
                {
                    saved.Add(j.Status);
                }
            })
            .Returns(Task.CompletedTask);

        var jobId = await processor.ProcessFilesAsync(new List<string>());
        await WaitForTerminalAsync(processor, jobId);

        // Give any straggling fire-and-forget write a chance to land.
        await Task.Delay(250);

        lock (saved)
        {
            Assert.NotEmpty(saved);
            Assert.Equal(JobStatus.Completed, saved[^1]);
            Assert.Single(saved, s => s == JobStatus.Completed);
        }
    }

    [Fact]
    public void RestoreJobs_MakesPreRestartJobsVisibleAgain()
    {
        var processor = CreateProcessor();
        var jobId = Guid.NewGuid();

        processor.RestoreJobs(new[]
        {
            new ProcessingJob
            {
                JobId = jobId,
                Status = JobStatus.Interrupted,
                OperationName = "ProcessAll",
                TotalFiles = 10,
                ProcessedFiles = 4,
                StartTime = DateTime.UtcNow.AddMinutes(-5)
            }
        });

        var job = processor.GetJob(jobId);

        Assert.NotNull(job);
        Assert.Equal(JobStatus.Interrupted, job!.Status);
        Assert.Equal(4, job.ProcessedFiles);
    }

    [Fact]
    public void RestoreJobs_DoesNotOverwriteAJobAlreadyInMemory()
    {
        var processor = CreateProcessor();
        var jobId = Guid.NewGuid();

        processor.RestoreJobs(new[]
        {
            new ProcessingJob { JobId = jobId, Status = JobStatus.Completed, TotalFiles = 3 }
        });
        processor.RestoreJobs(new[]
        {
            new ProcessingJob { JobId = jobId, Status = JobStatus.Interrupted, TotalFiles = 99 }
        });

        var job = processor.GetJob(jobId);

        Assert.NotNull(job);
        Assert.Equal(JobStatus.Completed, job!.Status);
        Assert.Equal(3, job.TotalFiles);
    }

    [Fact]
    public void RestoredInterruptedJob_IsNotReportedAsActive()
    {
        var processor = CreateProcessor();

        processor.RestoreJobs(new[]
        {
            new ProcessingJob { JobId = Guid.NewGuid(), Status = JobStatus.Interrupted }
        });

        // Otherwise the UI would show a permanently stalled progress bar.
        Assert.Null(processor.GetActiveJob());
    }

    [Fact]
    public async Task DeleteJob_RemovesInterruptedJobFromMemoryAndStorage()
    {
        var processor = CreateProcessor();
        var jobId = Guid.NewGuid();
        _jobStateStore
            .Setup(s => s.DeleteAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        processor.RestoreJobs(new[]
        {
            new ProcessingJob { JobId = jobId, Status = JobStatus.Interrupted }
        });

        // An interrupted job is terminal, so the user must be able to dismiss it.
        Assert.True(await processor.DeleteJobAsync(jobId));
        Assert.Null(processor.GetJob(jobId));
    }

    [Fact]
    public async Task ReconciliationService_MarksInterruptedJobsAndRestoresThem()
    {
        var interrupted = new ProcessingJob
        {
            JobId = Guid.NewGuid(),
            Status = JobStatus.Interrupted,
            OperationName = "RenameAll",
            TotalFiles = 8,
            ProcessedFiles = 3
        };

        _jobStateStore
            .Setup(s => s.MarkInterruptedAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { interrupted });
        _jobStateStore
            .Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { interrupted });
        _jobStateStore
            .Setup(s => s.PruneAsync(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        var processor = CreateProcessor();
        var service = new JobStateReconciliationHostedService(
            _jobStateStore.Object,
            processor,
            new Mock<ILogger<JobStateReconciliationHostedService>>().Object);

        await service.StartAsync(CancellationToken.None);

        _jobStateStore.Verify(s => s.MarkInterruptedAsync(It.IsAny<CancellationToken>()), Times.Once);

        var restored = processor.GetJob(interrupted.JobId);
        Assert.NotNull(restored);
        Assert.Equal(JobStatus.Interrupted, restored!.Status);
        Assert.Equal(3, restored.ProcessedFiles);
    }

    [Fact]
    public async Task ReconciliationService_PrunesOldJobRecords()
    {
        var sequence = new MockSequence();
        _jobStateStore.InSequence(sequence)
            .Setup(s => s.MarkInterruptedAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProcessingJob>());
        _jobStateStore.InSequence(sequence)
            .Setup(s => s.PruneAsync(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(2);
        _jobStateStore.InSequence(sequence)
            .Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProcessingJob>());

        var service = new JobStateReconciliationHostedService(
            _jobStateStore.Object,
            CreateProcessor(),
            new Mock<ILogger<JobStateReconciliationHostedService>>().Object);

        await service.StartAsync(CancellationToken.None);

        _jobStateStore.Verify(
            s => s.PruneAsync(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ReconciliationService_DoesNotBlockStartupWhenStorageFails()
    {
        _jobStateStore
            .Setup(s => s.MarkInterruptedAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database unavailable"));

        var service = new JobStateReconciliationHostedService(
            _jobStateStore.Object,
            CreateProcessor(),
            new Mock<ILogger<JobStateReconciliationHostedService>>().Object);

        // The application is perfectly usable without job history, so startup must continue.
        await service.StartAsync(CancellationToken.None);
    }
}
