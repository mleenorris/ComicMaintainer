using System.IO.Compression;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Notifications;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// <see cref="FileCoverWarmupQueue"/> generates the covers of new and changed files in the
/// background, one at a time, and the archive-metadata scan and the processor queue those files.
/// </summary>
public sealed class FileCoverWarmupQueueTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), $"cover_warmup_{Guid.NewGuid():N}");
    private readonly Mock<IFileCoverCacheService> _covers = new();
    private readonly Mock<IFileCoverWarmupQueue> _queued = new();

    public FileCoverWarmupQueueTests() => Directory.CreateDirectory(_root);

    public void Dispose() => TestIO.TryDeleteDirectory(_root);

    private FileCoverWarmupQueue CreateQueue() => new(_covers.Object, NullLogger<FileCoverWarmupQueue>.Instance);

    [Fact]
    public async Task QueuedCovers_AreGeneratedOneAtATime()
    {
        var running = 0;
        var mostRunning = 0;
        var done = new TaskCompletionSource();
        var warmed = new List<string>();
        _covers
            .Setup(c => c.WarmUpAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string path, CancellationToken _) =>
            {
                mostRunning = Math.Max(mostRunning, Interlocked.Increment(ref running));
                await Task.Delay(20);
                Interlocked.Decrement(ref running);
                lock (warmed)
                {
                    warmed.Add(path);
                    if (warmed.Count == 3)
                    {
                        done.TrySetResult();
                    }
                }
            });
        var queue = CreateQueue();
        using var stopping = new CancellationTokenSource();

        queue.Enqueue("/library/a.cbz");
        queue.Enqueue("/library/b.cbz");
        queue.Enqueue("/library/c.cbz");
        var run = queue.RunAsync(stopping.Token);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await stopping.CancelAsync();
        await run;

        Assert.Equal(new[] { "/library/a.cbz", "/library/b.cbz", "/library/c.cbz" }, warmed);
        Assert.Equal(1, mostRunning);
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public void AFileAlreadyWaiting_IsNotQueuedTwice_AndBlankPathsAreIgnored()
    {
        var queue = CreateQueue();

        queue.Enqueue("/library/a.cbz");
        queue.Enqueue("/library/a.cbz");
        queue.Enqueue(" ");

        Assert.Equal(1, queue.PendingCount);
    }

    [Fact]
    public void AFullQueue_TakesNoMore()
    {
        var queue = CreateQueue();

        for (var i = 0; i < FileCoverWarmupQueue.MaxPending + 5; i++)
        {
            queue.Enqueue($"/library/{i}.cbz");
        }

        Assert.Equal(FileCoverWarmupQueue.MaxPending, queue.PendingCount);
    }

    [Fact]
    public async Task ACoverThatFails_IsSkippedAndTheRestAreStillGenerated()
    {
        var second = new TaskCompletionSource();
        _covers
            .Setup(c => c.WarmUpAsync("/library/bad.cbz", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("unreadable"));
        _covers
            .Setup(c => c.WarmUpAsync("/library/good.cbz", It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                second.TrySetResult();
                return Task.CompletedTask;
            });
        var queue = CreateQueue();
        using var stopping = new CancellationTokenSource();

        queue.Enqueue("/library/bad.cbz");
        queue.Enqueue("/library/good.cbz");
        var run = queue.RunAsync(stopping.Token);
        await second.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await stopping.CancelAsync();
        await run;

        Assert.Equal(2, queue.WarmedCount);
    }

    [Fact]
    public async Task Stopping_EndsTheRunInTheMiddleOfACover()
    {
        var started = new TaskCompletionSource();
        _covers
            .Setup(c => c.WarmUpAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, CancellationToken token) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            });
        var queue = CreateQueue();
        using var stopping = new CancellationTokenSource();

        queue.Enqueue("/library/a.cbz");
        queue.Enqueue("/library/b.cbz");
        var run = queue.RunAsync(stopping.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await stopping.CancelAsync();

        await run.WaitAsync(TimeSpan.FromSeconds(10));
        _covers.Verify(c => c.WarmUpAsync("/library/b.cbz", It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---- what queues covers --------------------------------------------------------------

    [Fact]
    public async Task TheArchiveMetadataScan_QueuesTheFilesItRecords()
    {
        var fileStore = new Mock<IFileStoreService>();
        var processor = new Mock<IComicProcessorService>();
        var recorded = new ArchiveMetadataScanCandidate(1, "/library/new.cbz", 10, DateTime.UtcNow);
        var changedMeanwhile = new ArchiveMetadataScanCandidate(2, "/library/moving.cbz", 10, DateTime.UtcNow);
        fileStore
            .Setup(f => f.GetFilesNeedingMetadataScanAsync(0, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { recorded, changedMeanwhile });
        fileStore
            .Setup(f => f.RecordScannedArchiveMetadataAsync(It.IsAny<ArchiveMetadataScanCandidate>(), It.IsAny<ComicMetadata>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ArchiveMetadataScanCandidate c, ComicMetadata _, CancellationToken _) => c == recorded);
        processor
            .Setup(p => p.GetMetadataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComicMetadata { Series = "Saga" });
        var scanner = new ArchiveMetadataScanService(fileStore.Object, processor.Object, NullLogger<ArchiveMetadataScanService>.Instance, _queued.Object);

        await scanner.ScanAsync(CancellationToken.None);

        _queued.Verify(q => q.Enqueue("/library/new.cbz"), Times.Once);
        _queued.Verify(q => q.Enqueue("/library/moving.cbz"), Times.Never);
    }

    [Fact]
    public async Task ProcessingThatRenamesAFile_QueuesTheCoverAtItsNewPath()
    {
        var file = CreateArchive("Saga", "Saga 1.cbz");
        using var processor = CreateProcessor(rename: true);

        Assert.True(await processor.ProcessFileAsync(file));

        _queued.Verify(q => q.Enqueue(Path.Join(_root, "Saga", "Saga - Chapter 0001.cbz")), Times.Once);
        _queued.Verify(q => q.Enqueue(file), Times.Never);
    }

    [Fact]
    public async Task ProcessingThatLeavesAFileAsItIs_QueuesNothing()
    {
        var file = CreateArchive("Saga", "Saga 1.cbz");
        using var processor = CreateProcessor(rename: false);

        await processor.ProcessFileAsync(file);

        _queued.Verify(q => q.Enqueue(It.IsAny<string>()), Times.Never);
    }

    private ComicProcessorService CreateProcessor(bool rename) =>
        TestServices.CreateComicProcessor(
            new TestOptionsMonitor<AppSettings>(new AppSettings
            {
                WatchedDirectory = _root,
                DuplicateDirectory = Path.Join(_root, "duplicates"),
                TempFileDirectory = Path.Join(_root, "temp"),
                FilenameFormat = "{series} - Chapter {issue}",
                IssueNumberPadding = 4,
                WatcherEnableRename = rename,
                WatcherEnableNormalize = rename,
            }),
            Mock.Of<ILogger<ComicProcessorService>>(),
            Mock.Of<IFileStoreService>(),
            Mock.Of<IProcessingHistoryService>(),
            externalSeriesMetadata: Mock.Of<IExternalSeriesMetadataService>(),
            notifications: Mock.Of<INotificationPublisher>(),
            coverWarmup: _queued.Object);

    private string CreateArchive(string folder, string fileName)
    {
        var directory = Path.Join(_root, folder);
        Directory.CreateDirectory(directory);
        var path = Path.Join(directory, fileName);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(archive.CreateEntry("ComicInfo.xml").Open()))
        {
            writer.Write("<?xml version=\"1.0\"?><ComicInfo><Series>Saga</Series><Number>1</Number><Title>Chapter 1</Title></ComicInfo>");
        }

        using (var writer = new StreamWriter(archive.CreateEntry("page001.jpg").Open()))
        {
            writer.Write("page");
        }

        return path;
    }
}
