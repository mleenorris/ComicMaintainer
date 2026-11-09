using System.IO.Compression;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Notifications;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using ComicMaintainer.WebApi.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ComicMaintainer.Tests.Notifications;

/// <summary>Where notification events are raised: file processing, scheduled jobs and the Suwayomi import.</summary>
public sealed class ProcessingNotificationHookTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), $"notify_hooks_{Guid.NewGuid():N}");
    private readonly Mock<IFileStoreService> _fileStore = new();
    private readonly Mock<IProcessingHistoryService> _history = new();
    private readonly Mock<INotificationPublisher> _notifications = new();
    private readonly AppSettings _settings;

    public ProcessingNotificationHookTests()
    {
        Directory.CreateDirectory(_root);
        _settings = new AppSettings
        {
            WatchedDirectory = _root,
            DuplicateDirectory = Path.Join(_root, "duplicates"),
            TempFileDirectory = Path.Join(_root, "temp"),
            FilenameFormat = "{series} - Chapter {issue}",
            IssueNumberPadding = 4,
            WatcherEnableRename = true,
            WatcherEnableNormalize = true
        };
    }

    public void Dispose() => TestIO.TryDeleteDirectory(_root);

    private ComicProcessorService CreateService(BatchJobRegistry? jobs = null) =>
        TestServices.CreateComicProcessor(
            new TestOptionsMonitor<AppSettings>(_settings),
            Mock.Of<ILogger<ComicProcessorService>>(),
            _fileStore.Object,
            _history.Object,
            externalSeriesMetadata: Mock.Of<IExternalSeriesMetadataService>(),
            jobRegistry: jobs,
            notifications: _notifications.Object);

    private string CreateArchive(string folder, string fileName, string series, string number)
    {
        var directory = Path.Join(_root, folder);
        Directory.CreateDirectory(directory);
        var path = Path.Join(directory, fileName);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(archive.CreateEntry("ComicInfo.xml").Open()))
        {
            writer.Write($"<?xml version=\"1.0\"?><ComicInfo><Series>{series}</Series><Number>{number}</Number><Title>Chapter {number}</Title></ComicInfo>");
        }

        using (var writer = new StreamWriter(archive.CreateEntry("page001.jpg").Open()))
        {
            writer.Write("page");
        }

        return path;
    }

    private void VerifyNoNewChapter() =>
        _notifications.Verify(n => n.NewChapter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Never);

    [Fact]
    public async Task ProcessingANewFile_AnnouncesAChapterAtItsNewPath()
    {
        var file = CreateArchive("Saga", "Saga 1.cbz", "Saga", "1");
        using var service = CreateService();

        Assert.True(await service.ProcessFileAsync(file));

        _notifications.Verify(n => n.NewChapter("Saga", "1", Path.Join(_root, "Saga", "Saga - Chapter 0001.cbz")), Times.Once);
    }

    [Fact]
    public async Task AFileChangedByProcessing_RaisesLibraryChanged()
    {
        var file = CreateArchive("Saga", "Saga 1.cbz", "Saga", "1");
        using var service = CreateService();

        await service.ProcessFileAsync(file);

        _notifications.Verify(n => n.LibraryChanged(), Times.AtLeastOnce);
    }

    [Fact]
    public async Task AForcedReprocess_IsNotANewChapter()
    {
        var file = CreateArchive("Saga", "Saga - Chapter 0001.cbz", "Saga", "1");
        using var service = CreateService();

        await service.ProcessFileAsync(file, forceReprocess: true);

        VerifyNoNewChapter();
    }

    [Fact]
    public async Task AFileAlreadyProcessed_IsNotANewChapter()
    {
        var file = CreateArchive("Saga", "Saga - Chapter 0001.cbz", "Saga", "1");
        _fileStore.Setup(s => s.IsFileProcessedAsync(file, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        using var service = CreateService();

        await service.ProcessFileAsync(file);

        VerifyNoNewChapter();
    }

    [Fact]
    public async Task AFileThatCouldNotBeProcessed_IsNotANewChapter()
    {
        var directory = Path.Join(_root, "Broken");
        Directory.CreateDirectory(directory);
        var corrupt = Path.Join(directory, "Broken - Chapter 0001.cbz");
        await File.WriteAllBytesAsync(corrupt, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        using var service = CreateService();

        await service.ProcessFileAsync(corrupt);

        VerifyNoNewChapter();
    }

    [Fact]
    public async Task AChapterWithoutTags_IsAnnouncedByItsFolderAndFileName()
    {
        // Both phases off: nothing is read from the archive, but it still counts as processed.
        _settings.WatcherEnableNormalize = false;
        _settings.WatcherEnableRename = false;
        var directory = Path.Join(_root, "Loose Folder");
        Directory.CreateDirectory(directory);
        var file = Path.Join(directory, "chapter one.cbz");
        using (var archive = ZipFile.Open(file, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry("page.jpg").Open()))
        {
            writer.Write("page");
        }

        using var service = CreateService();

        await service.ProcessFileAsync(file);

        _notifications.Verify(n => n.NewChapter("Loose Folder", "chapter one", file), Times.Once);
    }

    private async Task RunJobAsync(BatchJobRegistry jobs, Guid jobId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (jobs.GetJob(jobId) is { Status: JobStatus.Queued or JobStatus.Running })
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Job {jobId} did not finish.");
            }

            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task DeletingAFile_RaisesLibraryChanged()
    {
        var jobs = TestServices.CreateBatchJobRegistry(new TestOptionsMonitor<AppSettings>(_settings), NullLogger<BatchJobRegistry>.Instance);
        var file = CreateArchive("Saga", "Saga 2.cbz", "Saga", "2");
        using var service = CreateService(jobs);

        var jobId = await service.DeleteFilesAsync(new[] { file });
        await RunJobAsync(jobs, jobId);

        Assert.False(File.Exists(file));
        _notifications.Verify(n => n.LibraryChanged(), Times.Once);
    }

    [Fact]
    public async Task RemovingMetadata_RaisesLibraryChanged()
    {
        var file = CreateArchive("Saga", "Saga 3.cbz", "Saga", "3");
        using var service = CreateService();

        await service.RemoveMetadataAsync(file);

        _notifications.Verify(n => n.LibraryChanged(), Times.Once);
    }
}

public sealed class ScheduledJobNotificationHookTests : IDisposable
{
    private readonly SqliteTestDbFactory _db = new();
    private readonly Mock<INotificationPublisher> _notifications = new();

    public void Dispose() => _db.Dispose();

    private ScheduledJobsHostedService CreateHosted(IScheduledJobHandler handler)
    {
        var service = new ScheduledJobService(_db, new[] { handler }, NullLogger<ScheduledJobService>.Instance);
        using var db = _db.CreateDbContext();
        db.ScheduledJobs.Add(new ScheduledJobEntity { JobKey = handler.JobKey, Enabled = false, IntervalMinutes = 60 });
        db.SaveChanges();
        return new ScheduledJobsHostedService(service, NullLogger<ScheduledJobsHostedService>.Instance, _notifications.Object);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Timed out.");
            }

            await Task.Delay(20);
        }
    }

    private string LastStatus(string jobKey)
    {
        using var db = _db.CreateDbContext();
        return db.ScheduledJobs.Single(j => j.JobKey == jobKey).LastStatus;
    }

    [Fact]
    public async Task AFailedJob_RaisesAFailureEvent()
    {
        var handler = new Handler("failing", (_, _) => throw new InvalidOperationException("no disk"));
        using var hosted = CreateHosted(handler);
        await hosted.StartAsync(default);

        Assert.True(await hosted.RunNowAsync("failing"));
        await WaitForAsync(() => LastStatus("failing") == ScheduledJobStatus.Failed.ToString());

        _notifications.Verify(n => n.ScheduledJobFailed("failing", "Display failing", "no disk"), Times.Once);
        _notifications.Verify(n => n.ScheduledJobFindings(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        await hosted.StopAsync(default);
    }

    [Fact]
    public async Task AJobWithFindings_RaisesAFindingsEventWithItsSummary()
    {
        var handler = new FindingsHandler("audit", new ScheduledJobResult("2 problems", 2));
        using var hosted = CreateHosted(handler);
        await hosted.StartAsync(default);

        await hosted.RunNowAsync("audit");
        await WaitForAsync(() => LastStatus("audit") == ScheduledJobStatus.Success.ToString());

        _notifications.Verify(n => n.ScheduledJobFindings("audit", "Display audit", "2 problems"), Times.Once);
        await hosted.StopAsync(default);
    }

    [Fact]
    public async Task AJobWithNothingToReport_RaisesNothing()
    {
        var handler = new FindingsHandler("clean", new ScheduledJobResult("all good"));
        using var hosted = CreateHosted(handler);
        await hosted.StartAsync(default);

        await hosted.RunNowAsync("clean");
        await WaitForAsync(() => LastStatus("clean") == ScheduledJobStatus.Success.ToString());

        _notifications.Verify(n => n.ScheduledJobFindings(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _notifications.Verify(n => n.ScheduledJobFailed(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        await hosted.StopAsync(default);
    }

    [Fact]
    public async Task AJobThatDoesNotReportFindings_RunsAsBefore()
    {
        var handler = new Handler("plain", (_, _) => Task.FromResult("done"));
        using var hosted = CreateHosted(handler);
        await hosted.StartAsync(default);

        await hosted.RunNowAsync("plain");
        await WaitForAsync(() => LastStatus("plain") == ScheduledJobStatus.Success.ToString());

        _notifications.Verify(n => n.ScheduledJobFindings(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        await hosted.StopAsync(default);
    }

    private class Handler(string key, Func<string?, CancellationToken, Task<string>> run) : IScheduledJobHandler
    {
        public string JobKey => key;
        public string DisplayName => $"Display {key}";
        public string Description => string.Empty;
        public ScheduledJobDefaults Defaults => new(false, 60);
        public Task<string> ExecuteAsync(string? optionsJson, CancellationToken cancellationToken) => run(optionsJson, cancellationToken);
    }

    private sealed class FindingsHandler(string key, ScheduledJobResult result) : Handler(key, (_, _) => Task.FromResult(result.Summary)), IScheduledJobFindingsHandler
    {
        public Task<ScheduledJobResult> ExecuteWithFindingsAsync(string? optionsJson, CancellationToken cancellationToken) => Task.FromResult(result);
    }
}

public sealed class JobFindingsTests
{
    private readonly Mock<IFileStoreService> _fileStore = new();
    private readonly Mock<IComicProcessorService> _processor = new();
    private readonly Mock<IBatchJobRegistry> _jobs = new();

    private void TrackedFiles(params ComicFile[] files) =>
        _fileStore.Setup(f => f.GetAllFilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(files);

    private FileNamingAuditJobHandler FileNaming() =>
        new(_fileStore.Object, _processor.Object, _jobs.Object, NullLogger<FileNamingAuditJobHandler>.Instance);

    [Fact]
    public async Task FileNamingAudit_ReportOnly_ReportsTheFilesFound()
    {
        TrackedFiles(new ComicFile { FilePath = "/a/x.cbz", IsRenamed = false }, new ComicFile { FilePath = "/a/y.cbz", IsRenamed = false });

        var result = await FileNaming().ExecuteWithFindingsAsync(null, default);

        Assert.Equal(2, result.Findings);
        Assert.True(result.HasFindings);
    }

    [Fact]
    public async Task FileNamingAudit_NothingToRename_HasNoFindings()
    {
        TrackedFiles(new ComicFile { FilePath = "/a/x.cbz", IsRenamed = true });

        Assert.False((await FileNaming().ExecuteWithFindingsAsync(null, default)).HasFindings);
    }

    [Fact]
    public async Task FileNamingAudit_AutoCorrect_ReportsOnlyWhatItCouldNotFix()
    {
        TrackedFiles(new ComicFile { FilePath = "/a/x.cbz", IsRenamed = false }, new ComicFile { FilePath = "/a/y.cbz", IsRenamed = false });
        var jobId = Guid.NewGuid();
        _processor.Setup(p => p.RenameFilesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(jobId);
        _jobs.Setup(j => j.WaitForCompletionAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessingJob { JobId = jobId, Status = JobStatus.Completed, ProcessedFiles = 1, FailedFiles = 1 });

        var result = await FileNaming().ExecuteWithFindingsAsync("""{"autoCorrect":true}""", default);

        Assert.Equal(1, result.Findings);
    }

    [Fact]
    public async Task FileNamingAudit_AutoCorrect_JobRecordGone_HasNoFindings()
    {
        TrackedFiles(new ComicFile { FilePath = "/a/x.cbz", IsRenamed = false });
        var jobId = Guid.NewGuid();
        _processor.Setup(p => p.RenameFilesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(jobId);
        _jobs.Setup(j => j.WaitForCompletionAsync(jobId, It.IsAny<CancellationToken>())).ReturnsAsync((ProcessingJob?)null);

        Assert.False((await FileNaming().ExecuteWithFindingsAsync("""{"autoCorrect":true}""", default)).HasFindings);
    }

    [Fact]
    public async Task MetadataAudit_ReportsItsFindings_AndNoneWhenClean()
    {
        using var db = new SqliteTestDbFactory();
        var resolver = new Mock<ISeriesNameResolver>();
        resolver.Setup(r => r.ResolveAsync(It.IsAny<string>(), It.IsAny<ComicMetadata?>(), false, It.IsAny<CancellationToken>())).ReturnsAsync(new SeriesNameResolution { ResolvedSeries = "S" });

        var handler = new MetadataAuditJobHandler(_fileStore.Object, _processor.Object, _jobs.Object, db, NullLogger<MetadataAuditJobHandler>.Instance, resolver.Object);

        TrackedFiles(new ComicFile { FilePath = "/a/x.cbz" });
        _processor.Setup(p => p.GetMetadataAsync("/a/x.cbz", It.IsAny<CancellationToken>())).ReturnsAsync((ComicMetadata?)null);
        Assert.Equal(1, (await handler.ExecuteWithFindingsAsync(null, default)).Findings);

        TrackedFiles();
        Assert.False((await handler.ExecuteWithFindingsAsync(null, default)).HasFindings);
    }

    [Fact]
    public async Task MetadataBackfill_ReportsFailures_NotSuccesses()
    {
        var resolver = new Mock<ISeriesNameResolver>();
        resolver.Setup(r => r.ResolveAsync(It.IsAny<string>(), It.IsAny<ComicMetadata?>(), false, It.IsAny<CancellationToken>())).ReturnsAsync(new SeriesNameResolution { ResolvedSeries = "S" });
        var handler = new MetadataBackfillJobHandler(_fileStore.Object, _processor.Object, resolver.Object, NullLogger<MetadataBackfillJobHandler>.Instance);

        var file = new ComicFile { FilePath = "/a/x.cbz", MetadataVersion = 2, WrittenMetadataVersion = 1, Metadata = new ComicMetadata { Series = "S", Issue = "1" } };
        _fileStore.Setup(f => f.GetFilesNeedingBackfillAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new[] { file });
        _processor.Setup(p => p.WriteMetadataAsync(file.FilePath, It.IsAny<ComicMetadata>(), It.IsAny<ComicMetadataFieldFlags>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MetadataWriteResult.Failed(file.FilePath));

        Assert.Equal(1, (await handler.ExecuteWithFindingsAsync(null, default)).Findings);

        _processor.Setup(p => p.WriteMetadataAsync(file.FilePath, It.IsAny<ComicMetadata>(), It.IsAny<ComicMetadataFieldFlags>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MetadataWriteResult(MetadataWriteOutcome.Written, file.FilePath));

        Assert.False((await handler.ExecuteWithFindingsAsync(null, default)).HasFindings);
    }
}

public sealed class SuwayomiNotificationHookTests
{
    private readonly Mock<ISuwayomiDownloadImportService> _importer = new();
    private readonly Mock<INotificationPublisher> _notifications = new();

    private Task<string> RunAsync(SuwayomiImportResult result)
    {
        _importer.Setup(i => i.ImportAsync(It.IsAny<CancellationToken>())).ReturnsAsync(result);
        return new SuwayomiDownloadImportJobHandler(_importer.Object, _notifications.Object).ExecuteAsync(null, default);
    }

    [Fact]
    public async Task AChapterThatCouldNotBeMoved_RaisesAFailure()
    {
        var summary = await RunAsync(new SuwayomiImportResult { Failed = 2 });

        _notifications.Verify(n => n.SuwayomiImportFailed(summary), Times.Once);
    }

    [Fact]
    public async Task APermissionProblem_RaisesAFailure()
    {
        var result = new SuwayomiImportResult { Failed = 1, PermissionDenied = true };

        var summary = await RunAsync(result);

        _notifications.Verify(n => n.SuwayomiImportFailed(summary), Times.Once);
    }

    [Fact]
    public async Task AFolderThatCouldNotBeRead_RaisesAFailure()
    {
        await RunAsync(SuwayomiImportResult.Skipped("The Suwayomi downloads folder could not be read"));

        _notifications.Verify(n => n.SuwayomiImportFailed(It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task ASuccessfulImport_RaisesNoFailure_ButReportsTheChange()
    {
        var result = new SuwayomiImportResult();
        result.Imported.Add(new SuwayomiImportedChapter("/d/s/c.cbz", "/lib/s/c.cbz"));

        await RunAsync(result);

        _notifications.Verify(n => n.SuwayomiImportFailed(It.IsAny<string>()), Times.Never);
        _notifications.Verify(n => n.LibraryChanged(), Times.Once);
    }

    [Fact]
    public async Task ANothingToDoRun_RaisesNothing()
    {
        await RunAsync(new SuwayomiImportResult { StillDownloading = 1 });

        _notifications.Verify(n => n.SuwayomiImportFailed(It.IsAny<string>()), Times.Never);
        _notifications.Verify(n => n.LibraryChanged(), Times.Never);
    }

    [Fact]
    public async Task NotConfigured_RaisesNoFailure()
    {
        await RunAsync(SuwayomiImportResult.Skipped("No folder", notConfigured: true));

        _notifications.Verify(n => n.SuwayomiImportFailed(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task AnotherRunInProgress_RaisesNoFailure()
    {
        await RunAsync(SuwayomiImportResult.Skipped("Another import is running", alreadyRunning: true));

        _notifications.Verify(n => n.SuwayomiImportFailed(It.IsAny<string>()), Times.Never);
    }

}
