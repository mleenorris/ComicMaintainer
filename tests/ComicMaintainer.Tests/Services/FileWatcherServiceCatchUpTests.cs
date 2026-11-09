using System.Collections.Concurrent;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// The watcher catches up with what it could not see: files that arrived while the application
/// was down or the watcher was stopped, files whose processing was still queued when it stopped,
/// and files moved meanwhile. It does so without processing files that are processed already or
/// were deliberately left unprocessed, and without processing a collection it has never
/// finished scanning.
/// </summary>
/// <remarks>
/// Driven by real file-system events in a unique temp directory, mostly against a mocked store
/// and processor; the tests named EndToEnd and the cleanup race run the real store on a
/// temporary SQLite database across restarts. Every wait polls for a recorded call or log entry,
/// bounded by <see cref="WaitTimeout"/>, so the assertions that something did not happen are
/// made once a later step that follows it has been observed.
/// </remarks>
public sealed class FileWatcherServiceCatchUpTests : IDisposable
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(60);

    private readonly string _root = Path.Join(Path.GetTempPath(), $"fws_catchup_{Guid.NewGuid():N}");
    private readonly string _library;
    private readonly string _staging;
    private readonly Mock<IFileStoreService> _store = new();
    private readonly Mock<IComicProcessorService> _processor = new();
    private readonly Mock<IEventBroadcaster> _broadcaster = new();
    private readonly ConcurrentQueue<string> _logs = new();

    /// <summary>Store and processor calls in the order they were made.</summary>
    private readonly ConcurrentQueue<string> _calls = new();

    private readonly List<FileWatcherService> _services = new();
    private readonly List<ServiceProvider> _providers = new();
    private List<ComicFile> _tracked = new();
    private List<string> _pending = new();

    /// <summary>Whether the store has a record of a complete scan of the library.</summary>
    private bool _scanned = true;
    private volatile Action<string>? _onLog;

    public FileWatcherServiceCatchUpTests()
    {
        _library = Path.Join(_root, "library");
        _staging = Path.Join(_root, "staging");
        Directory.CreateDirectory(_library);
        Directory.CreateDirectory(_staging);

        _store.Setup(s => s.GetAllFilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _tracked.ToList());
        _store.Setup(s => s.GetProcessingPendingFilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _pending.ToList());
        _store.Setup(s => s.IsLibraryScannedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _scanned);
        _store.Setup(s => s.RecordLibraryScannedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((directory, _) =>
            {
                _calls.Enqueue($"scanned {directory}");
                return Task.CompletedTask;
            });
        _store.Setup(s => s.ForgetLibraryScanAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(_ =>
            {
                _calls.Enqueue("forget scan");
                return Task.CompletedTask;
            });
        _store.Setup(s => s.AddFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((path, _) =>
            {
                _calls.Enqueue($"add {path}");
                return Task.CompletedTask;
            });
        _store.Setup(s => s.SetProcessingPendingAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns<string, bool, CancellationToken>((path, pending, _) =>
            {
                _calls.Enqueue($"pending {pending} {path}");
                return Task.CompletedTask;
            });
        _store.Setup(s => s.UpdateFilePathAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Returns<string, string, CancellationToken, bool>((oldPath, newPath, _, _) =>
            {
                _calls.Enqueue($"move {oldPath} -> {newPath}");
                return Task.CompletedTask;
            });
        _processor.Setup(p => p.ProcessFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((path, _) =>
            {
                _calls.Enqueue($"process {path}");
                return Task.FromResult(true);
            });
    }

    public void Dispose()
    {
        _onLog = null;
        foreach (var service in _services)
        {
            service.Dispose();
        }

        foreach (var provider in _providers)
        {
            provider.Dispose();
        }

        TestIO.TryDeleteDirectory(_root);
    }

    [Fact]
    public async Task Startup_TrackedLibrary_QueuesTheFilesThatArrivedWhileItWasDown()
    {
        var known = CreateComic("Saga", "Saga - Chapter 0001.cbz");
        var arrived = CreateComic("Saga", "Saga 002.cbz");
        _tracked = [Tracked(known, processed: false)];

        await StartWatchingAsync();

        await WaitForCallAsync($"process {arrived}");
        await WaitForLogAsync("Queued 1 file(s) that arrived and 0 file(s) that were still queued");
        Assert.Contains($"add {arrived}", _calls);
        Assert.Contains($"pending True {arrived}", _calls);
        // A tracked file is not queued again, processed or not.
        Assert.DoesNotContain(_calls, call => call.EndsWith(known, StringComparison.Ordinal));
        _store.Verify(s => s.IsFileProcessedAsync(known, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Startup_LibraryNeverScanned_AddsItsFilesWithoutProcessingThem()
    {
        var first = CreateComic("Saga", "Saga 001.cbz");
        var second = CreateComic("Monster", "Monster 001.cbz");
        // Rows of another library (an earlier watched directory) do not make this one known.
        _tracked = [Tracked(Path.Join(_root, "old-library", "Saga", "Saga 001.cbz"), processed: true)];
        _scanned = false;

        await StartWatchingAsync();

        await WaitForLogAsync($"{_library} has not been scanned completely before, so its 2 existing files were added without processing them");
        Assert.Contains($"add {first}", _calls);
        Assert.Contains($"add {second}", _calls);
        Assert.DoesNotContain(_calls, call => call.StartsWith("pending", StringComparison.Ordinal) || call.StartsWith("process", StringComparison.Ordinal));
        _store.Verify(s => s.IsFileProcessedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Startup_FirstScanWasInterrupted_TheRowsItLeftDoNotMakeTheRestArrivals()
    {
        // The earlier scan got as far as adding this file before the application stopped.
        var added = CreateComic("Saga", "Saga 001.cbz");
        var notAddedYet = CreateComic("Saga", "Saga 002.cbz");
        _tracked = [Tracked(added, processed: false)];
        _scanned = false;

        await StartWatchingAsync();

        await WaitForLogAsync("has not been scanned completely before, so its 1 existing files were added without processing them");
        Assert.Contains($"add {notAddedYet}", _calls);
        Assert.Contains($"scanned {_library}", _calls);
        Assert.DoesNotContain(_calls, call => call.StartsWith("pending", StringComparison.Ordinal) || call.StartsWith("process", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Scan_EndedPartWayByStopOrDispose_AddsNoMoreAndIsNotRecordedAsComplete(bool dispose)
    {
        CreateComic("Saga", "Saga 001.cbz");
        CreateComic("Saga", "Saga 002.cbz");
        var service = TestServices.CreateFileWatcher(
            new TestOptionsMonitor<AppSettings>(Settings()), new QueueLogger(_logs, () => null), _store.Object, _processor.Object);
        _services.Add(service);
        StopServiceOnFirstAdd(service, dispose);

        await service.StartAsync();

        await WaitForLogAsync("Incremental scan completed");
        Assert.Single(_calls, call => call.StartsWith("add", StringComparison.Ordinal));
        Assert.DoesNotContain(_calls, call => call.StartsWith("scanned", StringComparison.Ordinal));
        Assert.DoesNotContain(_calls, call => call.StartsWith("process", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartScan_EndedByStopOrDispose_DoesNotAddOrQueueTheRest(bool dispose)
    {
        var service = await StartWatchingAsync();
        await service.StopAsync();
        CreateComic("Saga", "Saga 001.cbz");
        CreateComic("Saga", "Saga 002.cbz");
        _logs.Clear();
        _calls.Clear();
        StopServiceOnFirstAdd(service, dispose);

        await service.StartAsync();

        await WaitForLogAsync("Incremental scan completed");
        Assert.Single(_calls, call => call.StartsWith("add", StringComparison.Ordinal));
        Assert.DoesNotContain(_calls, call => call.StartsWith("process", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Scans_RunOneAtATime_SoARestartWaitsForTheScanItEnded()
    {
        CreateComic("Saga", "Saga 001.cbz");
        var addEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var addRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _store.Setup(s => s.AddFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>(async (path, _) =>
            {
                addEntered.TrySetResult();
                await addRelease.Task;
            });
        var service = TestServices.CreateFileWatcher(
            new TestOptionsMonitor<AppSettings>(Settings()), new QueueLogger(_logs, () => null), _store.Object, _processor.Object);
        _services.Add(service);

        await service.StartAsync();
        await addEntered.Task.WaitAsync(WaitTimeout);
        await service.StopAsync();
        await service.StartAsync();

        // Long enough for a second scan that did not wait to have logged its start.
        await Task.Delay(100);
        addRelease.SetResult();

        await WaitUntilAsync(() => Task.FromResult(_logs.Count(m => m.Contains("Incremental scan completed", StringComparison.Ordinal)) == 2), "both scans to finish");
        var scanLogs = _logs.Where(m => m.Contains("incremental scan", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Equal(4, scanLogs.Count);
        Assert.Contains("Starting", scanLogs[0], StringComparison.Ordinal);
        Assert.Contains("completed", scanLogs[1], StringComparison.Ordinal);
        Assert.Contains("Starting", scanLogs[2], StringComparison.Ordinal);
        Assert.Contains("completed", scanLogs[3], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Startup_NoneOfTheTrackedFilesLeft_TreatsWhatIsFoundAsAnotherCollection()
    {
        // Another volume mounted at the same path, with the old library's rows still around.
        var found = CreateComic("Other", "Other 001.cbz");
        _tracked = [Tracked(Path.Join(_library, "Saga", "Saga 001.cbz"), processed: true)];

        await StartWatchingAsync();

        await WaitForLogAsync($"None of the files tracked under {_library} were found there, so its 1 files were added as a different collection");
        Assert.Contains($"add {found}", _calls);
        Assert.DoesNotContain(_calls, call => call.StartsWith("pending", StringComparison.Ordinal) || call.StartsWith("process", StringComparison.Ordinal));
        // The new collection is tracked now, so later arrivals to it are arrivals.
        Assert.Contains($"scanned {_library}", _calls);
    }

    [Fact]
    public async Task Startup_LibraryMissingFromDisk_ForgetsTheScanSoItsReturnIsNotAnArrival()
    {
        _tracked = [Tracked(Path.Join(_library, "Saga", "Saga 001.cbz"), processed: true)];

        await StartWatchingAsync();

        Assert.Contains("forget scan", _calls);
        Assert.DoesNotContain(_calls, call => call.StartsWith("scanned", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Startup_EmptyLibrary_IsRecordedSoFilesArrivingLaterAreProcessed()
    {
        _scanned = false;

        await StartWatchingAsync();

        Assert.Contains($"scanned {_library}", _calls);
    }

    [Fact]
    public async Task Startup_QueuesTheFilesThatWereStillQueuedWhenTheWatcherStopped()
    {
        var queued = CreateComic("Saga", "Saga 003.cbz");
        var other = CreateComic("Saga", "Saga - Chapter 0001.cbz");
        var gone = Path.Join(_library, "Saga", "Saga 004.cbz");
        var elsewhere = Path.Join(_root, "old-library", "Saga 005.cbz");
        _tracked = [Tracked(queued, processed: false), Tracked(other, processed: false), Tracked(gone, processed: false)];
        _pending = [elsewhere, gone, queued];

        await StartWatchingAsync();

        await WaitForCallAsync($"process {queued}");
        await WaitForLogAsync("Queued 0 file(s) that arrived and 1 file(s) that were still queued");
        // A queued file that no longer exists is no longer queued.
        Assert.Contains($"pending False {gone}", _calls);
        Assert.DoesNotContain(_calls, call => call.EndsWith(other, StringComparison.Ordinal) || call.EndsWith(elsewhere, StringComparison.Ordinal));
        Assert.DoesNotContain($"process {gone}", _calls);
    }

    [Fact]
    public async Task Startup_TrackedFileMovedWhileDown_KeepsItsRowInsteadOfComingBackAsNew()
    {
        var oldPath = Path.Join(_library, "One Piece", "One Piece - Chapter 0001.cbz");
        var newPath = CreateComic("One Piece (1997)", "One Piece - Chapter 0001.cbz");
        var info = new FileInfo(newPath);
        _tracked =
        [
            new ComicFile
            {
                FilePath = oldPath,
                FileName = info.Name,
                FileSize = info.Length,
                LastModified = info.LastWriteTime,
                IsRenamed = true,
                IsNormalized = true,
                IsProcessed = true,
            },
        ];

        await StartWatchingAsync();

        await WaitForLogAsync("Followed 1 tracked file(s) moved while their move was not seen");
        Assert.Contains($"move {oldPath} -> {newPath}", _calls);
        Assert.DoesNotContain($"add {newPath}", _calls);
        Assert.DoesNotContain($"process {newPath}", _calls);
    }

    // A row that cannot be moved must not stop the rest of the catch-up.
    [Fact]
    public async Task Startup_MovingOneTrackedFileFails_LogsItAndStillMovesTheOthers()
    {
        var oldFirst = Path.Join(_library, "One Piece", "One Piece - Chapter 0001.cbz");
        var oldSecond = Path.Join(_library, "One Piece", "One Piece - Chapter 0002.cbz");
        var newFirst = CreateComic("One Piece (1997)", "One Piece - Chapter 0001.cbz");
        var newSecond = CreateComic("One Piece (1997)", "One Piece - Chapter 0002.cbz");
        _tracked = [MovedFrom(oldFirst, newFirst), MovedFrom(oldSecond, newSecond)];
        _store.Setup(s => s.UpdateFilePathAsync(oldFirst, newFirst, It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ThrowsAsync(new InvalidOperationException("database is locked"));

        await StartWatchingAsync();

        await WaitForLogAsync("Followed 1 tracked file(s) moved while their move was not seen");
        Assert.Contains($"move {oldSecond} -> {newSecond}", _calls);
        Assert.Contains(_logs, message => message.Contains("Error moving tracked file", StringComparison.Ordinal));
    }

    // The summary reports what happened: nothing was followed when every move failed.
    [Fact]
    public async Task Startup_NoTrackedFileCouldBeMoved_DoesNotReportAnyAsFollowed()
    {
        var oldPath = Path.Join(_library, "One Piece", "One Piece - Chapter 0001.cbz");
        var newPath = CreateComic("One Piece (1997)", "One Piece - Chapter 0001.cbz");
        _tracked = [MovedFrom(oldPath, newPath)];
        _store.Setup(s => s.UpdateFilePathAsync(oldPath, newPath, It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ThrowsAsync(new InvalidOperationException("database is locked"));

        await StartWatchingAsync();

        Assert.Contains(_logs, message => message.Contains("Error moving tracked file", StringComparison.Ordinal));
        Assert.DoesNotContain(_logs, message => message.Contains("Followed", StringComparison.Ordinal));
        _broadcaster.Verify(b => b.BroadcastFileListUpdateAsync(), Times.Never);
    }

    // Clients hear about the catch-up once, however many rows it moved.
    [Fact]
    public async Task Startup_TrackedFilesMovedWhileDown_TellsClientsOnceForAllOfThem()
    {
        TrackTwoFilesMovedWhileDown();

        await StartWatchingAsync();

        await WaitForLogAsync("Followed 2 tracked file(s) moved while their move was not seen");
        _broadcaster.Verify(b => b.BroadcastFileListUpdateAsync(), Times.Once);
        _store.Verify(
            s => s.UpdateFilePathAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), true),
            Times.Never);
    }

    // The rows that did move are news to clients, whichever move it was that failed.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Startup_OneTrackedFileCouldNotBeMoved_StillTellsClientsAboutTheOther(int failingMove)
    {
        TrackTwoFilesMovedWhileDown();
        var moves = 0;
        _store.Setup(s => s.UpdateFilePathAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Returns<string, string, CancellationToken, bool>((_, _, _, _) =>
                Interlocked.Increment(ref moves) == failingMove
                    ? Task.FromException(new InvalidOperationException("database is locked"))
                    : Task.CompletedTask);

        await StartWatchingAsync();

        await WaitForLogAsync("Followed 1 tracked file(s) moved while their move was not seen");
        Assert.Contains(_logs, message => message.Contains("Error moving tracked file", StringComparison.Ordinal));
        _broadcaster.Verify(b => b.BroadcastFileListUpdateAsync(), Times.Once);
    }

    // Stopping part-way leaves the rows moved so far at their new paths, and clients have to hear that.
    [Fact]
    public async Task Startup_StoppedAfterATrackedFileMoved_StillTellsClients()
    {
        TrackTwoFilesMovedWhileDown();
        var service = TestServices.CreateFileWatcher(
            new TestOptionsMonitor<AppSettings>(Settings()), new QueueLogger(_logs, () => null), _store.Object, _processor.Object, _broadcaster.Object);
        _services.Add(service);
        _store.Setup(s => s.UpdateFilePathAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Returns<string, string, CancellationToken, bool>(async (oldPath, newPath, _, _) =>
            {
                _calls.Enqueue($"move {oldPath} -> {newPath}");
                await service.StopAsync();
            });

        await service.StartAsync();

        await WaitForLogAsync("Incremental scan completed");
        Assert.Single(_calls, call => call.StartsWith("move", StringComparison.Ordinal));
        _broadcaster.Verify(b => b.BroadcastFileListUpdateAsync(), Times.Once);
    }

    // The rows have moved by then; a client that cannot be told must not cost the rest of the scan.
    [Fact]
    public async Task Startup_TellingClientsAboutMovedFilesFails_StillFinishesTheScan()
    {
        TrackTwoFilesMovedWhileDown();
        _broadcaster.Setup(b => b.BroadcastFileListUpdateAsync())
            .ThrowsAsync(new InvalidOperationException("client went away"));

        await StartWatchingAsync();

        Assert.Contains(_logs, message => message.Contains("Failed to broadcast file list update after following moved files", StringComparison.Ordinal));
        Assert.Contains($"scanned {_library}", _calls);
    }

    // The files that arrived still get processed when the queued files cannot be read.
    [Fact]
    public async Task Startup_ReadingTheFilesStillQueuedFails_LogsItAndStillQueuesArrivals()
    {
        var known = CreateComic("Saga", "Saga - Chapter 0001.cbz");
        var arrived = CreateComic("Saga", "Saga 002.cbz");
        _tracked = [Tracked(known, processed: true)];
        _store.Setup(s => s.GetProcessingPendingFilesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database is locked"));

        await StartWatchingAsync();

        await WaitForCallAsync($"process {arrived}");
        await WaitForLogAsync("Queued 1 file(s) that arrived and 0 file(s) that were still queued");
        Assert.Contains(_logs, message => message.Contains("Error reading the files still queued for processing", StringComparison.Ordinal));
    }

    // Reading the tracked files is the first thing the scan does; losing that scan must not lose the watcher.
    [Fact]
    public async Task Startup_ScanFails_LogsItAndKeepsWatching()
    {
        CreateComic("Saga", "Saga 001.cbz");
        _store.Setup(s => s.GetAllFilesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database is locked"));
        var service = TestServices.CreateFileWatcher(
            new TestOptionsMonitor<AppSettings>(Settings()), new QueueLogger(_logs, () => _onLog), _store.Object, _processor.Object);
        _services.Add(service);

        await service.StartAsync();

        await WaitForLogAsync("Error during incremental directory scan");
        Assert.True(service.IsRunning);
    }

    // Recording the pending flag only helps after a restart: a failure must not lose the processing.
    [Fact]
    public async Task ArrivingFile_WhoseQueuedStateCannotBeRecorded_IsStillProcessed()
    {
        _store.Setup(s => s.SetProcessingPendingAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database is locked"));
        await StartWatchingAsync();

        var arrived = MoveIn("Saga 003.cbz", _library);

        await WaitForCallAsync($"process {arrived}");
        await WaitForLogAsync("Error recording whether a file waits for processing");
    }

    private void TrackTwoFilesMovedWhileDown()
    {
        _tracked =
        [
            MovedFrom(Path.Join(_library, "One Piece", "One Piece - Chapter 0001.cbz"), CreateComic("One Piece (1997)", "One Piece - Chapter 0001.cbz")),
            MovedFrom(Path.Join(_library, "One Piece", "One Piece - Chapter 0002.cbz"), CreateComic("One Piece (1997)", "One Piece - Chapter 0002.cbz")),
        ];
    }

    private static ComicFile MovedFrom(string oldPath, string newPath)
    {
        var info = new FileInfo(newPath);
        return new ComicFile
        {
            FilePath = oldPath,
            FileName = info.Name,
            FileSize = info.Length,
            LastModified = info.LastWriteTime,
            IsRenamed = true,
            IsNormalized = true,
            IsProcessed = true,
        };
    }

    [Fact]
    public async Task Startup_FolderRenamedWhileDownWithANewFile_MovesTheKnownOneAndProcessesTheNewOne()
    {
        var oldPath = Path.Join(_library, "One Piece", "One Piece - Chapter 0001.cbz");
        var moved = CreateComic("One Piece (1997)", "One Piece - Chapter 0001.cbz");
        var arrived = CreateComic("One Piece (1997)", "One Piece - Chapter 0002.cbz");
        var info = new FileInfo(moved);
        _tracked = [Tracked(oldPath, processed: true, size: info.Length, modified: info.LastWriteTime)];

        await StartWatchingAsync();

        await WaitForCallAsync($"process {arrived}");
        Assert.Contains($"move {oldPath} -> {moved}", _calls);
        Assert.DoesNotContain($"add {moved}", _calls);
        Assert.DoesNotContain($"process {moved}", _calls);
    }

    [Fact]
    public async Task Startup_FolderCaseChangedWhileDown_RepointsTheRowInsteadOfAddingASecond()
    {
        // The old spelling only keeps naming the file on a case-insensitive file system.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var onDisk = CreateComic("Saga", "Saga 001.cbz");
        var oldSpelling = Path.Join(_library, "SAGA", "Saga 001.cbz");
        _tracked = [Tracked(oldSpelling, processed: true, size: 999, modified: new DateTime(2001, 1, 1))];

        await StartWatchingAsync();

        await WaitForLogAsync("Followed 1 tracked file(s)");
        Assert.Contains($"move {oldSpelling} -> {onDisk}", _calls);
        Assert.DoesNotContain($"add {onDisk}", _calls);
        Assert.DoesNotContain($"process {onDisk}", _calls);
    }

    [Fact]
    public async Task Startup_TrackedFileGoneAndAnotherFileOfTheSameName_IsAddedAsNew()
    {
        var oldPath = Path.Join(_library, "Saga", "Saga 001.cbz");
        var newPath = CreateComic("Saga (2012)", "Saga 001.cbz");
        var stillThere = CreateComic("Monster", "Monster 001.cbz");
        // Same name, but not the same file: the size differs.
        _tracked = [Tracked(oldPath, processed: true, size: new FileInfo(newPath).Length + 1), Tracked(stillThere, processed: true)];

        await StartWatchingAsync();

        await WaitForCallAsync($"process {newPath}");
        Assert.Contains($"add {newPath}", _calls);
        Assert.DoesNotContain(_calls, call => call.StartsWith("move", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Startup_WithAutomaticProcessingOff_DropsTheQueuedFilesWithoutProcessingThem()
    {
        var queued = CreateComic("Saga", "Saga 003.cbz");
        _tracked = [Tracked(queued, processed: false)];
        _pending = [queued];

        await StartWatchingAsync(rename: false, normalize: false);

        await WaitForCallAsync($"pending False {queued}");
        await WaitForLogAsync($"Both rename and normalize are disabled, skipping: {queued}");
        Assert.DoesNotContain($"process {queued}", _calls);
    }

    [Fact]
    public async Task QueuedProcessing_IsRecordedFromTheFilesArrivalUntilItIsServed()
    {
        _tracked = [Tracked(CreateComic("Saga", "Saga - Chapter 0001.cbz"), processed: true)];
        await StartWatchingAsync(stabilitySeconds: 1);

        var arrived = MoveIn("Saga 002.cbz", Path.Join(_library, "Saga"));

        await WaitForCallAsync($"process {arrived}");
        var forFile = _calls.Where(call => call.EndsWith(arrived, StringComparison.Ordinal)).ToList();
        Assert.Equal(
            new[] { $"add {arrived}", $"pending True {arrived}", $"pending False {arrived}", $"process {arrived}" },
            forFile);
    }

    [Fact]
    public async Task StopAsync_WhileAFileIsSettling_KeepsItsRecordAndTheNextStartProcessesIt()
    {
        _tracked = [Tracked(CreateComic("Saga", "Saga - Chapter 0001.cbz"), processed: true)];
        var monitor = new TestOptionsMonitor<AppSettings>(Settings(stabilitySeconds: 600));
        var service = await StartWatchingAsync(monitor);
        var arrived = MoveIn("Saga 002.cbz", Path.Join(_library, "Saga"));
        await WaitForCallAsync($"pending True {arrived}");

        await service.StopAsync();

        Assert.DoesNotContain($"pending False {arrived}", _calls);

        // What the store recorded is all that survives the stop.
        _tracked.Add(Tracked(arrived, processed: false));
        _pending = [arrived];
        monitor.Set(Settings(stabilitySeconds: 0));
        await service.StartAsync();

        await WaitForCallAsync($"process {arrived}");
        Assert.Equal(1, _calls.Count(call => call == $"process {arrived}"));
    }

    [Fact]
    public async Task RecoveryRescan_QueuesMissedArrivalsAndQueuedFiles_ButNoOtherTrackedFile()
    {
        var leftUnprocessed = CreateComic("Saga", "Saga 001.cbz");
        var queued = CreateComic("Saga", "Saga 002.cbz");
        _tracked = [Tracked(leftUnprocessed, processed: false), Tracked(queued, processed: false)];
        await StartWatchingAsync(stabilitySeconds: 0);
        _pending = [queued];

        // Fail the handler of the next arrival as a buffer overflow would: the watcher keeps
        // running and rescans for the events it may have lost.
        var missed = Path.Join(_library, "Saga", "Saga 003.cbz");
        var armed = 1;
        _onLog = message =>
        {
            if (message == $"[WATCHER] File created: {missed}" && Interlocked.Exchange(ref armed, 0) == 1)
            {
                throw new InternalBufferOverflowException("simulated overflow");
            }
        };

        MoveIn("Saga 003.cbz", Path.Join(_library, "Saga"));

        await WaitForLogAsync($"Recovery rescan completed: {_library}");
        await WaitForCallAsync($"process {missed}");
        await WaitForCallAsync($"process {queued}");
        Assert.DoesNotContain(_calls, call => call.EndsWith(leftUnprocessed, StringComparison.Ordinal));
    }

    [Fact]
    public async Task EndToEnd_FilesQueuedAtShutdownAndFilesThatArrivedWhileDown_AreProcessedAfterARestart()
    {
        var factory = await CreateDatabaseAsync();
        var processedBefore = CreateComic("Saga", "Saga - Chapter 0001.cbz");
        var processed = new ConcurrentQueue<string>();
        var processor = ProcessorRecording(processed);

        // First run: the existing file is tracked and marked processed; a file then arrives and
        // is still settling when the application stops.
        var firstStore = CreateRealStore(factory);
        using (var firstRun = TestServices.CreateFileWatcher(
                   new TestOptionsMonitor<AppSettings>(Settings(stabilitySeconds: 600)),
                   new QueueLogger(_logs, () => null), firstStore, processor.Object))
        {
            await firstRun.StartAsync();
            await WaitForLogAsync("Incremental scan completed");
            await firstStore.MarkFileRenamedAsync(processedBefore, true);
            await firstStore.MarkFileNormalizedAsync(processedBefore, true);

            var settling = MoveIn("Saga 002.cbz", Path.Join(_library, "Saga"));
            await WaitUntilAsync(async () => (await firstStore.GetProcessingPendingFilesAsync()).Contains(settling), $"{settling} to be recorded as queued");
            await firstRun.StopAsync();
        }

        // While the application is down another file arrives.
        var arrivedWhileDown = CreateComic("Saga", "Saga 003.cbz");
        _logs.Clear();

        // Second run, on the same database.
        var secondStore = CreateRealStore(factory);
        using var secondRun = TestServices.CreateFileWatcher(
            new TestOptionsMonitor<AppSettings>(Settings(stabilitySeconds: 0)),
            new QueueLogger(_logs, () => null), secondStore, processor.Object);
        await secondRun.StartAsync();

        var settled = Path.Join(_library, "Saga", "Saga 002.cbz");
        await WaitUntilAsync(() => Task.FromResult(processed.Contains(settled) && processed.Contains(arrivedWhileDown)), "both files to be processed");
        await WaitUntilAsync(async () => (await secondStore.GetProcessingPendingFilesAsync()).Count == 0, "the queue records to be cleared");
        Assert.DoesNotContain(processedBefore, processed);
    }

    [Fact]
    public async Task EndToEnd_FirstScanInterrupted_TheRestIsAddedUnprocessedAndOnlyLaterArrivalsAreProcessed()
    {
        var factory = await CreateDatabaseAsync();
        var addedBeforeTheInterruption = CreateComic("Saga", "Saga 001.cbz");
        var notAddedYet = CreateComic("Saga", "Saga 002.cbz");
        var processed = new ConcurrentQueue<string>();
        var processor = ProcessorRecording(processed);

        // The earlier scan added one row before the application stopped; nothing recorded it
        // as complete.
        await CreateRealStore(factory).AddFileAsync(addedBeforeTheInterruption);

        var secondStore = CreateRealStore(factory);
        using (var secondRun = TestServices.CreateFileWatcher(
                   new TestOptionsMonitor<AppSettings>(Settings()),
                   new QueueLogger(_logs, () => null), secondStore, processor.Object))
        {
            await secondRun.StartAsync();
            await WaitForLogAsync("Incremental scan completed");
            await secondRun.StopAsync();
        }

        Assert.True(await secondStore.FileExistsAsync(notAddedYet));
        Assert.True(await secondStore.IsLibraryScannedAsync(_library));
        Assert.Empty(processed);

        // That scan has now run to the end, so what appears next did arrive.
        var arrived = CreateComic("Saga", "Saga 003.cbz");
        _logs.Clear();
        using var thirdRun = TestServices.CreateFileWatcher(
            new TestOptionsMonitor<AppSettings>(Settings()),
            new QueueLogger(_logs, () => null), CreateRealStore(factory), processor.Object);
        await thirdRun.StartAsync();

        await WaitUntilAsync(() => Task.FromResult(processed.Contains(arrived)), "the file that arrived to be processed");
        Assert.DoesNotContain(notAddedYet, processed);
        Assert.DoesNotContain(addedBeforeTheInterruption, processed);
    }

    [Fact]
    public async Task Startup_CleanupRacingTheScan_CannotLoseTheRowOfAFileMovedWhileDown()
    {
        var factory = await CreateDatabaseAsync();
        var oldPath = CreateComic("One Piece", "One Piece - Chapter 0001.cbz");
        var seed = CreateRealStore(factory);
        await seed.AddFileAsync(oldPath);
        await seed.MarkFileRenamedAsync(oldPath, true);
        await seed.MarkFileNormalizedAsync(oldPath, true);
        await seed.RecordLibraryScannedAsync(_library);

        // The folder is renamed while the application is down.
        Directory.Move(Path.Join(_library, "One Piece"), Path.Join(_library, "One Piece (1997)"));
        var newPath = Path.Join(_library, "One Piece (1997)", "One Piece - Chapter 0001.cbz");

        var processed = new ConcurrentQueue<string>();
        var store = CreateRealStore(factory);
        Task<StaleCleanupResult>? cleanup = null;

        // As at startup: the cleanup starts while the scan is running, before it has read the
        // tracked files. The wait gives a cleanup that is not held off the time to prune.
        _onLog = message =>
        {
            if (message.Contains("Found 1 comic files on filesystem", StringComparison.Ordinal))
            {
                cleanup = Task.Run(() => store.CleanupStaleEntriesAsync(new StaleCleanupOptions(IsManual: true, Confirmed: true)));
                cleanup.Wait(TimeSpan.FromSeconds(1));
            }
        };

        using var service = TestServices.CreateFileWatcher(
            new TestOptionsMonitor<AppSettings>(Settings()), new QueueLogger(_logs, () => _onLog), store, ProcessorRecording(processed).Object);
        await service.StartAsync();

        await WaitForLogAsync("Incremental scan completed");
        await WaitUntilAsync(() => Task.FromResult(cleanup is { IsCompleted: true }), "the cleanup to run once the scan is done");

        Assert.Equal(0, (await cleanup!).RemovedCount);
        var rows = (await store.GetAllFilesAsync()).ToList();
        var row = Assert.Single(rows);
        Assert.Equal(newPath, row.FilePath);
        Assert.True(row.IsProcessed);
        Assert.Empty(processed);
    }

    /// <summary>
    /// A real database in a temporary file, with one connection per context as in production;
    /// the store's writers run concurrently, which a single shared connection does not allow.
    /// </summary>
    private async Task<IDbContextFactory<ComicMaintainerDbContext>> CreateDatabaseAsync()
    {
        var services = new ServiceCollection();
        var databasePath = Path.Join(_root, $"{Guid.NewGuid():N}.db");
        services.AddDbContextFactory<ComicMaintainerDbContext>(o => o.UseSqlite($"Data Source={databasePath};Pooling=False"));
        var provider = services.BuildServiceProvider();
        _providers.Add(provider);

        var factory = provider.GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
        return factory;
    }

    private static Mock<IComicProcessorService> ProcessorRecording(ConcurrentQueue<string> processed)
    {
        var processor = new Mock<IComicProcessorService>();
        processor.Setup(p => p.ProcessFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((path, _) =>
            {
                processed.Enqueue(path);
                return Task.FromResult(true);
            });
        return processor;
    }

    private void StopServiceOnFirstAdd(FileWatcherService service, bool dispose)
    {
        var armed = 1;
        _store.Setup(s => s.AddFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>(async (path, token) =>
            {
                _calls.Enqueue($"add {path}");
                if (Interlocked.Exchange(ref armed, 0) == 1)
                {
                    if (dispose)
                    {
                        service.Dispose();
                    }
                    else
                    {
                        await service.StopAsync();
                    }
                }
            });
    }

    private FileStoreService CreateRealStore(IDbContextFactory<ComicMaintainerDbContext> factory) =>
        TestServices.CreateFileStore(
            new TestOptionsMonitor<AppSettings>(Settings()),
            NullLogger<FileStoreService>.Instance,
            factory);

    private static ComicFile Tracked(string path, bool processed, long size = 5, DateTime? modified = null) => new()
    {
        FilePath = path,
        FileName = Path.GetFileName(path),
        Directory = Path.GetDirectoryName(path)!,
        FileSize = size,
        LastModified = modified ?? new DateTime(2020, 1, 1),
        IsRenamed = processed,
        IsNormalized = processed,
        IsProcessed = processed,
    };

    private string CreateComic(string folder, string fileName)
    {
        var directory = Path.Join(_library, folder);
        Directory.CreateDirectory(directory);
        var path = Path.Join(directory, fileName);
        File.WriteAllText(path, "comic");
        return path;
    }

    /// <summary>
    /// Moves a new file in from the unwatched staging directory, so its arrival raises a single
    /// "created" notification.
    /// </summary>
    private string MoveIn(string fileName, string directory)
    {
        var staged = Path.Join(_staging, fileName);
        File.WriteAllText(staged, "comic");
        var target = Path.Join(directory, fileName);
        File.Move(staged, target);
        return target;
    }

    private AppSettings Settings(bool rename = true, bool normalize = true, int stabilitySeconds = 0) => new()
    {
        WatchedDirectory = _library,
        DuplicateDirectory = string.Empty,
        WatcherEnableRename = rename,
        WatcherEnableNormalize = normalize,
        WatcherFileStabilityDelaySeconds = stabilitySeconds,
        WatcherDirectoryScanDelaySeconds = 0,
    };

    private Task<FileWatcherService> StartWatchingAsync(bool rename = true, bool normalize = true, int stabilitySeconds = 0) =>
        StartWatchingAsync(new TestOptionsMonitor<AppSettings>(Settings(rename, normalize, stabilitySeconds)));

    /// <summary>
    /// Starts a watcher and waits for its one-off initial scan so the scan cannot interleave with
    /// the events a test produces.
    /// </summary>
    private async Task<FileWatcherService> StartWatchingAsync(TestOptionsMonitor<AppSettings> monitor)
    {
        var service = TestServices.CreateFileWatcher(monitor, new QueueLogger(_logs, () => _onLog), _store.Object, _processor.Object, _broadcaster.Object);
        _services.Add(service);

        await service.StartAsync();
        Assert.True(service.IsRunning);
        await WaitForLogAsync("Incremental scan completed");
        return service;
    }

    private Task WaitForCallAsync(string call) =>
        WaitUntilAsync(
            () => Task.FromResult(_calls.Contains(call)),
            $"the call \"{call}\"",
            () => $". Calls:{Environment.NewLine}{string.Join(Environment.NewLine, _calls)}{Environment.NewLine}Logged:{Environment.NewLine}{string.Join(Environment.NewLine, _logs)}");

    private Task WaitForLogAsync(string fragment) =>
        WaitUntilAsync(
            () => Task.FromResult(_logs.Any(message => message.Contains(fragment, StringComparison.Ordinal))),
            $"a log containing \"{fragment}\"",
            () => $". Logged:{Environment.NewLine}{string.Join(Environment.NewLine, _logs)}");

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string description, Func<string>? details = null)
    {
        var deadline = DateTime.UtcNow + WaitTimeout;
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out after {WaitTimeout.TotalSeconds:0}s waiting for {description}{details?.Invoke()}");
            }

            await Task.Delay(20);
        }
    }

    /// <summary>
    /// Records every message; the hook runs first, on the logging thread, so a test can make the
    /// log call of an exact step fail.
    /// </summary>
    private sealed class QueueLogger(ConcurrentQueue<string> messages, Func<Action<string>?> hook) : ILogger<FileWatcherService>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            try
            {
                hook()?.Invoke(message);
            }
            finally
            {
                messages.Enqueue(message);
            }
        }
    }
}
