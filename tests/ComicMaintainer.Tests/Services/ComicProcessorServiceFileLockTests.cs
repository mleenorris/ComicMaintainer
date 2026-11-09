using Microsoft.Extensions.Logging.Abstractions;
using System.IO.Compression;
using System.Xml.Linq;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Operations on one archive (watcher processing, batch jobs, metadata edits, renames, cover
/// embeds) are serialized by the shared per-file lock, and every archive operation shares the
/// MaxWorkers processing slots.
/// </summary>
public sealed class ComicProcessorServiceFileLockTests : IDisposable
{
    // Long enough for an operation that is not blocked to finish on a busy machine; a blocked
    // operation never finishes, however long the test waits.
    private static readonly TimeSpan BlockedWait = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(30);

    private readonly string _root = Path.Join(Path.GetTempPath(), $"processor_lock_tests_{Guid.NewGuid():N}");
    private readonly Mock<IFileStoreService> _fileStore = new();
    private readonly Mock<IExternalSeriesMetadataService> _external = new();
    private readonly AppSettings _settings;

    public ComicProcessorServiceFileLockTests()
    {
        Directory.CreateDirectory(_root);
        _settings = new AppSettings
        {
            WatchedDirectory = _root,
            DuplicateDirectory = Path.Join(_root, "duplicates"),
            TempFileDirectory = Path.Join(_root, "temp"),
            FilenameFormat = "{series} - Chapter {issue}",
            IssueNumberPadding = 4,
            WatcherEnableRename = false,
            WatcherEnableNormalize = true
        };
    }

    public void Dispose() => TestIO.TryDeleteDirectory(_root);

    [Fact]
    public async Task UpdateMetadataAsync_WhileProcessingIsResolvingTheSeries_WaitsAndWinsOverTheStaleNormalize()
    {
        // The normalize reads the file, then waits on a slow provider lookup. A user edit saved
        // meanwhile must not be overwritten by metadata derived from the state read before it.
        var file = CreateArchive("Batman", "Batman - Chapter 1.cbz", series: "Batman", number: "1", title: "Old title");
        var lookupGate = BlockExternalLookups();
        using var service = CreateService();

        var processing = service.ProcessFileAsync(file);
        await WaitUntilAsync(() => _external.Invocations.Any(i => i.Method.Name == nameof(IExternalSeriesMetadataService.LookupSeriesAsync)));

        var edit = service.UpdateMetadataAsync(file, new ComicMetadata { Series = "Batman (User)", Issue = "1", Title = "User title" });
        await Task.Delay(BlockedWait);

        Assert.False(edit.IsCompleted);
        Assert.Equal("Old title", ReadComicInfo(file, "Title"));

        lookupGate.SetResult();
        await processing.WaitAsync(CompletionTimeout);
        Assert.True(await edit.WaitAsync(CompletionTimeout));

        Assert.Equal("Batman (User)", ReadComicInfo(file, "Series"));
        Assert.Equal("User title", ReadComicInfo(file, "Title"));
    }

    [Fact]
    public async Task UpdateMetadataAsync_WhileABatchJobNormalizesTheFile_WaitsForTheJobsWrite()
    {
        var file = CreateArchive("Saga", "Saga - Chapter 3.cbz", series: "Saga", number: "3", title: "Old title");
        var lookupGate = BlockExternalLookups();
        using var service = CreateService();

        var jobId = await service.NormalizeFilesAsync(new[] { file }, forceReprocess: true);
        await WaitUntilAsync(() => _external.Invocations.Any(i => i.Method.Name == nameof(IExternalSeriesMetadataService.LookupSeriesAsync)));

        var edit = service.UpdateMetadataAsync(file, new ComicMetadata { Series = "Saga (User)", Issue = "3", Title = "User title" });
        await Task.Delay(BlockedWait);

        Assert.False(edit.IsCompleted);

        lookupGate.SetResult();
        Assert.True(await edit.WaitAsync(CompletionTimeout));
        var job = await WaitForJobAsync(service, jobId);

        Assert.Equal(1, job.ProcessedFiles);
        Assert.Equal("Saga (User)", ReadComicInfo(file, "Series"));
    }

    [Fact]
    public async Task ProcessFileAsync_SamePathInAnotherCase_WaitsForTheRunningOperation()
    {
        var file = CreateArchive("Saga", "Saga - Chapter 1.cbz", series: "Saga", number: "1", title: "Old title");
        var lookupGate = BlockExternalLookups();
        using var service = CreateService();

        var first = service.ProcessFileAsync(file);
        await WaitUntilAsync(() => _external.Invocations.Any(i => i.Method.Name == nameof(IExternalSeriesMetadataService.LookupSeriesAsync)));

        var second = service.RemoveMetadataAsync(file.ToUpperInvariant());
        await Task.Delay(BlockedWait);

        Assert.False(second.IsCompleted);

        lookupGate.SetResult();
        await first.WaitAsync(CompletionTimeout);
        await second.WaitAsync(CompletionTimeout);
    }

    [Fact]
    public async Task BatchJobs_ShareTheMaxWorkersSlotsWithEachOther()
    {
        // Two jobs used to run MaxWorkers files each; now every archive operation takes one of
        // the same MaxWorkers slots.
        _settings.MaxWorkers = 1;
        var first = CreateArchive("A", "A - Chapter 1.cbz", series: "A", number: "1", title: "Chapter 1");
        var second = CreateArchive("B", "B - Chapter 1.cbz", series: "B", number: "1", title: "Chapter 1");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _fileStore.Setup(s => s.IsFileProcessedAsync(first, It.IsAny<CancellationToken>()))
            .Returns(async () => { await gate.Task; return false; });
        using var service = CreateService();

        var firstJob = await service.ProcessFilesAsync(new[] { first });
        await WaitUntilAsync(() => Invoked(nameof(IFileStoreService.IsFileProcessedAsync), first));
        var secondJob = await service.ProcessFilesAsync(new[] { second });
        await Task.Delay(BlockedWait);

        Assert.False(Invoked(nameof(IFileStoreService.IsFileProcessedAsync), second));

        gate.SetResult();
        await WaitForJobAsync(service, firstJob);
        await WaitForJobAsync(service, secondJob);
        Assert.True(Invoked(nameof(IFileStoreService.IsFileProcessedAsync), second));
    }

    [Fact]
    public async Task DeleteFilesAsync_WhileTheFileIsBeingProcessed_DeletesItOnlyAfterwards()
    {
        var file = CreateArchive("Saga", "Saga - Chapter 2.cbz", series: "Saga", number: "2", title: "Old title");
        var lookupGate = BlockExternalLookups();
        using var service = CreateService();

        var processing = service.ProcessFileAsync(file);
        await WaitUntilAsync(() => _external.Invocations.Any(i => i.Method.Name == nameof(IExternalSeriesMetadataService.LookupSeriesAsync)));

        var deleteJob = await service.DeleteFilesAsync(new[] { file });
        await Task.Delay(BlockedWait);

        Assert.True(File.Exists(file));
        Assert.False(Invoked(nameof(IFileStoreService.RemoveFileAsync), file));

        lookupGate.SetResult();
        await processing.WaitAsync(CompletionTimeout);
        var job = await WaitForJobAsync(service, deleteJob);

        Assert.Equal(1, job.ProcessedFiles);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task SeriesArchiveCoverWriter_WaitsForTheSharedLockOfTheArchive()
    {
        var file = CreateArchive("Saga", "Saga - Chapter 1.cbz", series: "Saga", number: "1", title: "Chapter 1");
        var cover = Path.Join(_root, "cover-source.jpg");
        await File.WriteAllBytesAsync(cover, new byte[] { 1, 2, 3 });
        var fileLock = new FileOperationLock();
        var library = new Mock<ISeriesLibraryService>();
        library.Setup(l => l.GetFirstIssueFilePathForNormalizedKeyAsync("saga", It.IsAny<CancellationToken>()))
            .ReturnsAsync(file);
        var writer = new SeriesArchiveCoverWriter(
            library.Object,
            Mock.Of<IFileWatcherService>(),
            new TestOptionsMonitor<AppSettings>(new AppSettings { WriteCoverToFirstArchive = true }),
            Mock.Of<ILogger<SeriesArchiveCoverWriter>>(),
            fileLock);

        var held = await fileLock.AcquireAsync(file);
        var write = writer.WriteAsync("saga", cover, "image/jpeg");
        await Task.Delay(BlockedWait);

        Assert.False(write.IsCompleted);
        Assert.DoesNotContain("cover.jpg", ListEntries(file));

        held.Dispose();
        await write.WaitAsync(CompletionTimeout);
        Assert.Contains("cover.jpg", ListEntries(file));
    }

    private BatchJobRegistry? _jobRegistryInstance;

    private BatchJobRegistry Jobs => _jobRegistryInstance ??= TestServices.CreateBatchJobRegistry(
        new TestOptionsMonitor<AppSettings>(_settings),
        NullLogger<BatchJobRegistry>.Instance);

    private ComicProcessorService CreateService() =>
        TestServices.CreateComicProcessor(
            new TestOptionsMonitor<AppSettings>(_settings),
            Mock.Of<ILogger<ComicProcessorService>>(),
            _fileStore.Object,
            Mock.Of<IProcessingHistoryService>(),
            externalSeriesMetadata: _external.Object,
            jobRegistry: Jobs);

    private TaskCompletionSource BlockExternalLookups()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _external.Setup(e => e.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await gate.Task;
                return (ExternalSeriesMetadata?)null;
            });
        return gate;
    }

    private bool Invoked(string methodName, string path) =>
        _fileStore.Invocations.Any(i => i.Method.Name == methodName && i.Arguments.Contains(path));

    private string CreateArchive(string folder, string fileName, string series, string number, string title)
    {
        var directory = Path.Join(_root, folder);
        Directory.CreateDirectory(directory);
        var path = Path.Join(directory, fileName);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(archive.CreateEntry("ComicInfo.xml").Open()))
        {
            writer.Write($"<?xml version=\"1.0\"?><ComicInfo><Series>{series}</Series><Number>{number}</Number><Title>{title}</Title></ComicInfo>");
        }

        using (var writer = new StreamWriter(archive.CreateEntry("page001.jpg").Open()))
        {
            writer.Write("page");
        }

        return path;
    }

    private static string? ReadComicInfo(string path, string element)
    {
        using var archive = ZipFile.OpenRead(path);
        var entry = archive.Entries.First(e => e.FullName.Equals("ComicInfo.xml", StringComparison.OrdinalIgnoreCase));
        using var stream = entry.Open();
        return XDocument.Load(stream).Root?.Element(element)?.Value;
    }

    private static List<string> ListEntries(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        return archive.Entries.Select(e => e.FullName).ToList();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + CompletionTimeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The expected operation did not start.");
            }

            await Task.Delay(10);
        }
    }

    private async Task<ProcessingJob> WaitForJobAsync(ComicProcessorService service, Guid jobId)
    {
        var deadline = DateTime.UtcNow + CompletionTimeout;
        while (true)
        {
            var job = Jobs.GetJob(jobId);
            if (job is { Status: not (JobStatus.Queued or JobStatus.Running) })
            {
                return job;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Job {jobId} did not finish.");
            }

            await Task.Delay(20);
        }
    }
}
