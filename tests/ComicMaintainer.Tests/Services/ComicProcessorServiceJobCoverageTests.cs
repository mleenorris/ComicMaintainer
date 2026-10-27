using System.Collections.Concurrent;
using System.IO.Compression;
using System.Xml.Linq;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Covers batch job orchestration, job-state queries, cancellation and disposal of
/// <see cref="ComicProcessorService"/>.
/// </summary>
public class ComicProcessorServiceJobCoverageTests : IDisposable
{
    private readonly string _root;
    private readonly string _library;
    private readonly AppSettings _settings;
    private readonly HookLogger _logger = new();
    private readonly Mock<IFileStoreService> _fileStore = new();
    private readonly Mock<IProcessingHistoryService> _history = new();
    private readonly Mock<IEventBroadcaster> _broadcaster = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<string>> _jobSignals = new();
    private readonly List<ComicProcessorService> _services = new();

    public ComicProcessorServiceJobCoverageTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "cps-job-cov-" + Guid.NewGuid().ToString("N"));
        _library = Path.Combine(_root, "library");
        Directory.CreateDirectory(_library);

        _settings = new AppSettings
        {
            WatchedDirectory = _library,
            DuplicateDirectory = Path.Combine(_root, "duplicates"),
            TempFileDirectory = Path.Combine(_root, "temp"),
            FilenameFormat = "{series} - Chapter {issue}",
            IssueNumberPadding = 4,
        };

        _history
            .Setup(h => h.AddHistoryEntryAsync(It.IsAny<ProcessingHistoryEntry>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _broadcaster
            .Setup(b => b.BroadcastJobUpdateAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()))
            .Returns<Guid, string, int, int, int, int>((jobId, status, _, _, _, _) =>
            {
                if (status is "completed" or "cancelled" or "failed")
                {
                    JobSignal(jobId).TrySetResult(status);
                }

                return Task.CompletedTask;
            });
    }

    public void Dispose()
    {
        foreach (var service in _services)
        {
            service.Dispose();
        }

        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task UpdateMetadataAsync_Batch_UpdatesExistingFilesAndReportsMissingOnes()
    {
        var service = CreateService();
        var ok = WriteCbz("Series/ok.cbz", Xml("Old", "1", "Chapter 1"));
        var missing = Path.Combine(_library, "Series", "missing.cbz");

        var jobId = await service.UpdateMetadataAsync(new[] { ok, missing }, new ComicMetadata { Series = "Updated" });
        var job = await WaitForJobAsync(service, jobId);

        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.Equal(1, job.ProcessedFiles);
        Assert.Equal(1, job.FailedFiles);
        Assert.Equal("Update metadata failed", job.Errors[missing]);
        Assert.Equal("Updated", ReadComicInfo(ok)!.Element("Series")?.Value);
    }

    [Fact]
    public async Task RemoveMetadataFromFilesAsync_Batch_ClearsProcessedStatusOnlyForRemovedFiles()
    {
        var service = CreateService();
        var ok = WriteCbz("Series/ok.cbz", Xml("Old", "1", "Chapter 1"));
        var missing = Path.Combine(_library, "Series", "missing.cbz");

        var jobId = await service.RemoveMetadataFromFilesAsync(new[] { ok, missing });
        var job = await WaitForJobAsync(service, jobId);

        Assert.Equal(1, job.ProcessedFiles);
        Assert.Equal(1, job.FailedFiles);
        Assert.Equal("Remove metadata failed", job.Errors[missing]);
        Assert.Null(ReadComicInfo(ok));
        Assert.Contains("page001.jpg", EntryNames(ok));
        _fileStore.Verify(
            f => f.ClearProcessedStatusAsync(It.Is<IEnumerable<string>>(p => p.Single() == ok), It.IsAny<CancellationToken>()),
            Times.Once);
        _fileStore.Verify(
            f => f.ClearProcessedStatusAsync(It.Is<IEnumerable<string>>(p => p.Contains(missing)), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RemoveMetadataFromFilesAsync_ClearingProcessedStatusFails_StillCountsFileAsProcessed()
    {
        _fileStore
            .Setup(f => f.ClearProcessedStatusAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));
        var service = CreateService();
        var ok = WriteCbz("Series/ok.cbz", Xml("Old", "1", "Chapter 1"));

        var jobId = await service.RemoveMetadataFromFilesAsync(new[] { ok });
        var job = await WaitForJobAsync(service, jobId);

        Assert.Equal(1, job.ProcessedFiles);
        Assert.Equal(0, job.FailedFiles);
        Assert.Null(ReadComicInfo(ok));
        Assert.True(_logger.Has(LogLevel.Warning, "RemoveMetadataFromFilesAsync: failed to clear processed status for {FilePath}"));
    }

    [Fact]
    public async Task NormalizeAndRenameFilesAsync_NormalizesThenRenames_AndReportsFilesThatCannotBeNormalized()
    {
        var service = CreateService();
        var ok = WriteCbz("Series/source.cbz", Xml("Series", "5", title: null));
        var missing = Path.Combine(_library, "Series", "missing.cbz");
        var expected = Path.Combine(_library, "Series", "Series - Chapter 0005.cbz");

        var jobId = await service.NormalizeAndRenameFilesAsync(new[] { ok, missing });
        var job = await WaitForJobAsync(service, jobId);

        Assert.Equal(1, job.ProcessedFiles);
        Assert.Equal(1, job.FailedFiles);
        Assert.Equal("Normalize and rename failed", job.Errors[missing]);
        Assert.False(File.Exists(ok));
        Assert.True(File.Exists(expected));
        Assert.Equal("Chapter 5", ReadComicInfo(expected)!.Element("Title")?.Value);
    }

    [Fact]
    public async Task RunCustomBatchJobAsync_SkipsNullAndBlankItems()
    {
        var service = CreateService();
        var seen = new ConcurrentQueue<string>();

        var jobId = await service.RunCustomBatchJobAsync(
            "CustomOp",
            new[] { "a", null!, string.Empty, "   ", "b" },
            (item, _) =>
            {
                seen.Enqueue(item);
                return Task.FromResult(true);
            },
            "Custom failed");
        var job = await WaitForJobAsync(service, jobId);

        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.Equal(2, job.TotalFiles);
        Assert.Equal(new[] { "a", "b" }, job.Files);
        Assert.Equal(new[] { "a", "b" }, seen.OrderBy(s => s, StringComparer.Ordinal));
        var warning = Assert.Single(_logger.Entries, e =>
            e.Level == LogLevel.Warning
            && e.Template == "{OperationName}: Skipping {SkippedCount} null or empty file path(s) in job {JobId}");
        Assert.Equal(3, warning.Properties["SkippedCount"]);
        Assert.Equal("CustomOp", warning.Properties["OperationName"]);
    }

    [Fact]
    public async Task RunCustomBatchJobAsync_NullItemCollection_CompletesAsEmptyJob()
    {
        var service = CreateService();
        var invoked = false;

        var jobId = await service.RunCustomBatchJobAsync(
            "CustomOp",
            null!,
            (_, _) =>
            {
                invoked = true;
                return Task.FromResult(true);
            },
            "Custom failed");
        var job = await WaitForJobAsync(service, jobId);

        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.Equal(0, job.TotalFiles);
        Assert.Empty(job.Files);
        Assert.False(invoked);
    }

    [Fact]
    public void GetAllJobs_ReturnsRestoredJobsNewestFirst()
    {
        var service = CreateService();
        var older = new ProcessingJob { JobId = Guid.NewGuid(), Status = JobStatus.Completed, StartTime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        var newest = new ProcessingJob { JobId = Guid.NewGuid(), Status = JobStatus.Interrupted, StartTime = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc) };
        var middle = new ProcessingJob { JobId = Guid.NewGuid(), Status = JobStatus.Failed, StartTime = new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc) };

        service.RestoreJobs(new[] { older, newest, middle });

        Assert.Equal(new[] { newest.JobId, middle.JobId, older.JobId }, service.GetAllJobs().Select(j => j.JobId));
    }

    [Fact]
    public async Task DeleteJobAsync_UnknownJob_ReturnsFalse()
    {
        var service = CreateService();

        Assert.False(await service.DeleteJobAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task DeleteJobAsync_ActiveJob_IsRefusedAndKept()
    {
        var service = CreateService();
        var running = new ProcessingJob { JobId = Guid.NewGuid(), Status = JobStatus.Running, StartTime = DateTime.UtcNow };
        service.RestoreJobs(new[] { running });

        var deleted = await service.DeleteJobAsync(running.JobId);

        Assert.False(deleted);
        Assert.True(_logger.Has(LogLevel.Warning, "Cannot delete active job: {JobId}"));
        Assert.NotNull(service.GetJob(running.JobId));
    }

    [Fact]
    public void CancelJob_UnknownJob_ReturnsFalseWithWarning()
    {
        var service = CreateService();

        Assert.False(service.CancelJob(Guid.NewGuid()));
        Assert.True(_logger.Has(LogLevel.Warning, "Cannot cancel job {JobId}: no active cancellation token found"));
    }

    [Fact]
    public async Task CancelJob_RunningJob_EndsCancelled()
    {
        var service = CreateService();
        var (jobId, itemToken) = await StartBlockedJobAsync(service);

        Assert.True(service.CancelJob(jobId));
        var job = await WaitForJobAsync(service, jobId);

        Assert.True(itemToken.IsCancellationRequested);
        Assert.Equal(JobStatus.Cancelled, job.Status);
        Assert.NotNull(job.EndTime);
        Assert.Equal(0, job.ProcessedFiles);
    }

    [Fact]
    public async Task CancelJob_JobFinishesBetweenLookupAndCancel_ReturnsFalseInsteadOfThrowing()
    {
        // Regression test for #862: the job disposed its token source after CancelJob had looked
        // it up, so Cancel() threw ObjectDisposedException and the API answered 500.
        var service = CreateService();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (jobId, jobToken) = await StartHeldJobAsync(service, release.Task);
        _logger.OnLog = entry =>
        {
            if (entry.Template == "Cancelling job: {JobId}")
            {
                // CancelJob holds the token source now; let the job finish and dispose it first.
                release.TrySetResult();
                WaitUntilDisposed(jobToken);
            }
        };

        var cancelled = service.CancelJob(jobId);

        Assert.False(cancelled);
        Assert.True(_logger.Has(LogLevel.Warning, "Cannot cancel job {JobId}: no active cancellation token found"));
        var job = await WaitForJobAsync(service, jobId);
        Assert.Equal(JobStatus.Completed, job.Status);
    }

    [Fact]
    public async Task RunCustomBatchJobAsync_CallerTokenAlreadyCancelled_EndsCancelledWithoutRunningAnything()
    {
        var service = CreateService();
        using var callerCts = new CancellationTokenSource();
        callerCts.Cancel();
        var itemInvoked = false;
        var postLoopInvoked = false;

        var jobId = await service.RunCustomBatchJobAsync(
            "CustomOp",
            new[] { "only-item" },
            (_, _) =>
            {
                itemInvoked = true;
                return Task.FromResult(true);
            },
            "Custom failed",
            _ =>
            {
                postLoopInvoked = true;
                return Task.CompletedTask;
            },
            callerCts.Token);
        var job = await WaitForJobAsync(service, jobId);

        Assert.Equal(JobStatus.Cancelled, job.Status);
        Assert.NotNull(job.EndTime);
        Assert.Equal(0, job.ProcessedFiles + job.FailedFiles);
        Assert.False(itemInvoked);
        Assert.False(postLoopInvoked);
        _broadcaster.Verify(
            b => b.BroadcastJobUpdateAsync(jobId, "running", It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()),
            Times.Never);
    }

    [Fact]
    public async Task Dispose_WithRunningJob_CancelsItAndIsIdempotent()
    {
        var service = CreateService();
        var (jobId, itemToken) = await StartBlockedJobAsync(service);

        service.Dispose();

        Assert.True(itemToken.IsCancellationRequested);
        var job = await WaitForJobAsync(service, jobId);
        Assert.Equal(JobStatus.Cancelled, job.Status);

        service.Dispose();
    }

    [Fact]
    public async Task Dispose_WithoutManagedDisposal_LeavesRunningJobsAlone()
    {
        var service = new UnmanagedDisposeService(
            new TestOptionsMonitor<AppSettings>(_settings), _logger, _fileStore.Object, _history.Object, _broadcaster.Object);
        _services.Add(service);
        var (jobId, itemToken) = await StartBlockedJobAsync(service);

        service.DisposeUnmanagedOnly();

        Assert.False(itemToken.IsCancellationRequested);
        Assert.True(service.CancelJob(jobId));
        var job = await WaitForJobAsync(service, jobId);
        Assert.Equal(JobStatus.Cancelled, job.Status);
    }

    [Fact]
    public async Task Dispose_JobFinishesWhileJobsAreBeingCancelled_DoesNotThrowAndCancelsTheRest()
    {
        // Regression test for #862: Dispose cancels every token source it listed, and a job that
        // finished in the meantime has already disposed its own.
        var service = CreateService();
        var (blockedJobId, blockedItemToken) = await StartBlockedJobAsync(service);
        var releases = new[]
        {
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var heldJobIds = new Guid[2];
        var heldJobTokens = new CancellationToken[2];
        var cancelledHeldJobs = new ConcurrentQueue<int>();

        // Dispose lists the jobs in no particular order, so whichever held job it cancels first
        // finishes the other one before Dispose gets to it.
        void FinishOtherHeldJob(int cancelled)
        {
            cancelledHeldJobs.Enqueue(cancelled);
            var other = 1 - cancelled;
            releases[other].TrySetResult();
            WaitUntilDisposed(heldJobTokens[other]);
        }

        for (var i = 0; i < 2; i++)
        {
            var index = i;
            (heldJobIds[i], heldJobTokens[i]) = await StartHeldJobAsync(
                service, releases[i].Task, onCancelled: () => FinishOtherHeldJob(index));
        }

        var error = Record.Exception(() => service.Dispose());

        Assert.Null(error);
        var cancelledFirst = Assert.Single(cancelledHeldJobs);
        Assert.Equal(JobStatus.Cancelled, (await WaitForJobAsync(service, heldJobIds[cancelledFirst])).Status);
        Assert.Equal(JobStatus.Completed, (await WaitForJobAsync(service, heldJobIds[1 - cancelledFirst])).Status);
        Assert.True(blockedItemToken.IsCancellationRequested);
        Assert.Equal(JobStatus.Cancelled, (await WaitForJobAsync(service, blockedJobId)).Status);
    }

    [Fact]
    public async Task JobStateStore_PersistsStatusTransitionsThroughCompletion()
    {
        var saved = new ConcurrentQueue<JobStatus>();
        var completedSaved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new Mock<IJobStateStore>();
        store.Setup(s => s.SaveAsync(It.IsAny<ProcessingJob>(), It.IsAny<CancellationToken>()))
            .Callback<ProcessingJob, CancellationToken>((job, _) =>
            {
                saved.Enqueue(job.Status);
                if (job.Status == JobStatus.Completed)
                {
                    completedSaved.TrySetResult();
                }
            })
            .Returns(Task.CompletedTask);
        var service = CreateService(store.Object);

        var jobId = await service.RunCustomBatchJobAsync(
            "CustomOp",
            new[] { "first", "second" },
            (_, _) => Task.FromResult(true),
            "Custom failed");
        await completedSaved.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var statuses = saved.ToList();
        Assert.Equal(JobStatus.Queued, statuses.First());
        Assert.Contains(JobStatus.Running, statuses);
        Assert.Equal(JobStatus.Completed, statuses.Last());
        Assert.Single(statuses, s => s == JobStatus.Completed);
        Assert.Equal(JobStatus.Completed, service.GetJob(jobId)!.Status);
    }

    private async Task<(Guid JobId, CancellationToken ItemToken)> StartBlockedJobAsync(ComicProcessorService service)
    {
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var jobId = await service.RunCustomBatchJobAsync(
            "BlockingOp",
            new[] { "only-item" },
            async (_, token) =>
            {
                started.TrySetResult(token);
                await Task.Delay(Timeout.Infinite, token);
                return true;
            },
            "Blocking failed");
        var itemToken = await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        return (jobId, itemToken);
    }

    /// <summary>
    /// Starts a job that waits in its post-loop hook until <paramref name="release"/> completes or
    /// the job is cancelled, and returns the job's own cancellation token.
    /// </summary>
    private async Task<(Guid JobId, CancellationToken JobToken)> StartHeldJobAsync(
        ComicProcessorService service,
        Task release,
        Action? onCancelled = null)
    {
        var held = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var jobId = await service.RunCustomBatchJobAsync(
            "HeldOp",
            new[] { "only-item" },
            (_, _) => Task.FromResult(true),
            "Held failed",
            async token =>
            {
                if (onCancelled != null)
                {
                    token.Register(onCancelled);
                }

                held.TrySetResult(token);
                await release.WaitAsync(token);
            });
        var jobToken = await held.Task.WaitAsync(TimeSpan.FromSeconds(30));
        return (jobId, jobToken);
    }

    /// <summary>
    /// Blocks until the job has disposed its token source, the last thing its finally block does.
    /// </summary>
    private static void WaitUntilDisposed(CancellationToken jobToken)
    {
        // Reading WaitHandle is how a token reports that its source has been disposed.
        var disposed = SpinWait.SpinUntil(
            () =>
            {
                try
                {
                    _ = jobToken.WaitHandle;
                    return false;
                }
                catch (ObjectDisposedException)
                {
                    return true;
                }
            },
            TimeSpan.FromSeconds(30));
        Assert.True(disposed, "The job never disposed its cancellation token source.");
    }

    private ComicProcessorService CreateService(IJobStateStore? jobStateStore = null)
    {
        var service = new ComicProcessorService(
            new TestOptionsMonitor<AppSettings>(_settings),
            _logger,
            _fileStore.Object,
            _history.Object,
            eventBroadcaster: _broadcaster.Object,
            jobStateStore: jobStateStore);
        _services.Add(service);
        return service;
    }

    private TaskCompletionSource<string> JobSignal(Guid jobId) =>
        _jobSignals.GetOrAdd(jobId, _ => new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously));

    private async Task<ProcessingJob> WaitForJobAsync(ComicProcessorService service, Guid jobId)
    {
        await JobSignal(jobId).Task.WaitAsync(TimeSpan.FromSeconds(30));
        return service.GetJob(jobId)!;
    }

    private string WriteCbz(string relativePath, string? comicInfoXml)
    {
        var path = Path.Combine(_library, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            if (comicInfoXml != null)
            {
                var info = zip.CreateEntry("ComicInfo.xml");
                using var writer = new StreamWriter(info.Open());
                writer.Write(comicInfoXml);
            }

            var page = zip.CreateEntry("page001.jpg");
            using var stream = page.Open();
            stream.Write(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 });
        }

        return path;
    }

    private static string Xml(string? series, string? number, string? title)
    {
        var root = new XElement("ComicInfo");
        if (series != null)
        {
            root.Add(new XElement("Series", series));
        }

        if (number != null)
        {
            root.Add(new XElement("Number", number));
        }

        if (title != null)
        {
            root.Add(new XElement("Title", title));
        }

        return root.ToString();
    }

    private static XElement? ReadComicInfo(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry("ComicInfo.xml");
        if (entry == null)
        {
            return null;
        }

        using var stream = entry.Open();
        return XElement.Load(stream);
    }

    private static List<string> EntryNames(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        return zip.Entries.Select(e => e.FullName).ToList();
    }

    private sealed class UnmanagedDisposeService : ComicProcessorService
    {
        public UnmanagedDisposeService(
            IOptionsMonitor<AppSettings> settings,
            ILogger<ComicProcessorService> logger,
            IFileStoreService fileStore,
            IProcessingHistoryService historyService,
            IEventBroadcaster eventBroadcaster)
            : base(settings, logger, fileStore, historyService, eventBroadcaster)
        {
        }

        public void DisposeUnmanagedOnly() => Dispose(false);
    }

    private sealed record LogEntry(LogLevel Level, string Template, IReadOnlyDictionary<string, object?> Properties, Exception? Exception);

    private sealed class HookLogger : ILogger<ComicProcessorService>
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();

        /// <summary>
        /// Runs inside each log call, on the logging thread, so a test can pause the service at
        /// the point where it logs.
        /// </summary>
        public Action<LogEntry>? OnLog { get; set; }

        public bool Has(LogLevel level, string template) =>
            Entries.Any(e => e.Level == level && e.Template == template);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NoopScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var template = string.Empty;
            var properties = new Dictionary<string, object?>();
            if (state is IReadOnlyList<KeyValuePair<string, object?>> pairs)
            {
                foreach (var pair in pairs)
                {
                    if (pair.Key == "{OriginalFormat}")
                    {
                        template = pair.Value?.ToString() ?? string.Empty;
                    }
                    else
                    {
                        properties[pair.Key] = pair.Value;
                    }
                }
            }

            var entry = new LogEntry(logLevel, template, properties, exception);
            Entries.Enqueue(entry);
            OnLog?.Invoke(entry);
        }

        private sealed class NoopScope : IDisposable
        {
            public static readonly NoopScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
