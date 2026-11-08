using System.IO.Compression;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Moq;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Once a file has moved on disk, its file-store row follows it even if the job is cancelled at
/// that moment, so the row never keeps pointing at a path that no longer exists.
/// </summary>
public sealed class ComicProcessorServiceRenameCancellationTests : IDisposable
{
    private static readonly TimeSpan JobTimeout = TimeSpan.FromSeconds(30);

    private readonly string _root = Path.Join(Path.GetTempPath(), $"rename_cancel_tests_{Guid.NewGuid():N}");
    private readonly Mock<IFileStoreService> _fileStore = new();
    private readonly Mock<IProcessingHistoryService> _history = new();
    private readonly List<ProcessingHistoryEntry> _historyEntries = new();
    private readonly AppSettings _settings;

    public ComicProcessorServiceRenameCancellationTests()
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

        // Like the real store and history service, the mocks observe the token they are given.
        _fileStore.Setup(s => s.MarkFileRenamedAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns<string, bool, CancellationToken>((_, _, token) => Observe(token));
        _fileStore.Setup(s => s.MarkFileNormalizedAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns<string, bool, CancellationToken>((_, _, token) => Observe(token));
        _history.Setup(h => h.AddHistoryEntryAsync(It.IsAny<ProcessingHistoryEntry>(), It.IsAny<CancellationToken>()))
            .Returns<ProcessingHistoryEntry, CancellationToken>((entry, token) =>
            {
                token.ThrowIfCancellationRequested();
                lock (_historyEntries)
                {
                    _historyEntries.Add(entry);
                }

                return Task.CompletedTask;
            });
    }

    public void Dispose() => TestIO.TryDeleteDirectory(_root);

    [Fact]
    public async Task ProcessFileAsync_CancelledRightAfterTheMove_StillMovesTheRowAndMarksTheFileRenamed()
    {
        var file = CreateArchive("Saga", "Saga 1.cbz", "Saga", "1");
        var renamed = Path.Join(_root, "Saga", "Saga - Chapter 0001.cbz");
        using var cts = new CancellationTokenSource();
        CancelWhenTheRowIsMoved(() => cts.Cancel());
        using var service = CreateService();

        var result = await service.ProcessFileAsync(file, forceReprocess: false, cts.Token);

        Assert.True(result);
        Assert.True(File.Exists(renamed));
        _fileStore.Verify(s => s.UpdateFilePathAsync(file, renamed, It.Is<CancellationToken>(t => !t.CanBeCanceled), It.IsAny<bool>()), Times.Once);
        _fileStore.Verify(s => s.MarkFileRenamedAsync(renamed, true, It.IsAny<CancellationToken>()), Times.Once);
        _fileStore.Verify(s => s.MarkFileNormalizedAsync(renamed, true, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains(_historyEntries, e => e.Action == "Rename" && e.Success && e.FilePath == renamed);
    }

    [Fact]
    public async Task ProcessFileAsync_RowCannotFollowTheRename_StillReportsTheRename()
    {
        // The file has moved, so a failure to record it must not report the rename as failed;
        // the watcher's rename event and the next scan repoint the row.
        var file = CreateArchive("Saga", "Saga 3.cbz", "Saga", "3");
        var renamed = Path.Join(_root, "Saga", "Saga - Chapter 0003.cbz");
        _fileStore
            .Setup(s => s.UpdateFilePathAsync(file, renamed, It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ThrowsAsync(new InvalidOperationException("database is locked"));
        using var service = CreateService();

        var result = await service.ProcessFileAsync(file, forceReprocess: false, CancellationToken.None);

        Assert.True(result);
        Assert.True(File.Exists(renamed));
        _fileStore.Verify(s => s.MarkFileRenamedAsync(renamed, true, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains(_historyEntries, e => e.Action == "Rename" && e.Success && e.FilePath == renamed);
    }

    [Fact]
    public async Task RenameFilesAsync_JobCancelledRightAfterTheMove_StillRecordsTheRename()
    {
        var file = CreateArchive("Saga", "Saga 2.cbz", "Saga", "2");
        var renamed = Path.Join(_root, "Saga", "Saga - Chapter 0002.cbz");
        using var service = CreateService();
        var jobId = Guid.Empty;
        var jobStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancelWhenTheRowIsMoved(() =>
        {
            jobStarted.Task.Wait(JobTimeout);
            service.CancelJob(jobId);
        });

        jobId = await service.RenameFilesAsync(new[] { file });
        jobStarted.SetResult();
        var job = await WaitForJobAsync(service, jobId);

        Assert.Equal(1, job.ProcessedFiles);
        Assert.Equal(0, job.FailedFiles);
        Assert.True(File.Exists(renamed));
        _fileStore.Verify(s => s.UpdateFilePathAsync(file, renamed, It.Is<CancellationToken>(t => !t.CanBeCanceled), It.IsAny<bool>()), Times.Once);
        _fileStore.Verify(s => s.MarkFileRenamedAsync(renamed, true, It.IsAny<CancellationToken>()), Times.Once);
        _fileStore.Verify(s => s.MarkFileRenamedAsync(file, false, It.IsAny<CancellationToken>()), Times.Never);
        Assert.Contains(_historyEntries, e => e.Action == "Rename" && e.Success && e.FilePath == renamed);
    }

    [Fact]
    public async Task ProcessFileAsync_CancelledBeforeTheMove_LeavesTheFileWhereItIs()
    {
        var file = CreateArchive("Saga", "Saga 3.cbz", "Saga", "3");
        using var cts = new CancellationTokenSource();
        // The normalize phase finishes (the file is already normalized), then the job is cancelled.
        _fileStore.Setup(s => s.MarkFileNormalizedAsync(file, true, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                cts.Cancel();
                return Task.CompletedTask;
            });
        // Nothing else observes the token before the rename, so only the check ahead of the
        // move can stop it.
        _history.Setup(h => h.AddHistoryEntryAsync(It.IsAny<ProcessingHistoryEntry>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        using var service = CreateService();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ProcessFileAsync(file, forceReprocess: false, cts.Token));

        Assert.True(File.Exists(file));
        Assert.False(File.Exists(Path.Join(_root, "Saga", "Saga - Chapter 0003.cbz")));
        _fileStore.Verify(s => s.UpdateFilePathAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Never);
    }

    private void CancelWhenTheRowIsMoved(Action cancel)
    {
        _fileStore.Setup(s => s.UpdateFilePathAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Returns<string, string, CancellationToken, bool>((_, _, token, _) =>
            {
                cancel();
                return Observe(token);
            });
    }

    private static Task Observe(CancellationToken token) =>
        token.IsCancellationRequested ? Task.FromCanceled(token) : Task.CompletedTask;

    private ComicProcessorService CreateService() =>
        new(
            new TestOptionsMonitor<AppSettings>(_settings),
            Mock.Of<ILogger<ComicProcessorService>>(),
            _fileStore.Object,
            _history.Object,
            externalSeriesMetadata: Mock.Of<IExternalSeriesMetadataService>());

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

    private static async Task<ProcessingJob> WaitForJobAsync(ComicProcessorService service, Guid jobId)
    {
        var deadline = DateTime.UtcNow + JobTimeout;
        while (true)
        {
            var job = service.GetJob(jobId);
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
