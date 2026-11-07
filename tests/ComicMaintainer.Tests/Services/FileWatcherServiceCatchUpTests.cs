using System.Collections.Concurrent;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using Microsoft.Data.Sqlite;
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
/// were deliberately left unprocessed, and without processing a library it has never tracked.
/// </summary>
/// <remarks>
/// Driven by real file-system events in a unique temp directory, mostly against a mocked store
/// and processor; the last test runs the real store on SQLite across a restart. Every wait polls
/// for a recorded call or log entry, bounded by <see cref="WaitTimeout"/>; there are no fixed
/// sleeps, so the assertions that something did not happen are made once a later step that
/// follows it has been observed.
/// </remarks>
public sealed class FileWatcherServiceCatchUpTests : IDisposable
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(15);

    private readonly string _root = Path.Join(Path.GetTempPath(), $"fws_catchup_{Guid.NewGuid():N}");
    private readonly string _library;
    private readonly string _staging;
    private readonly Mock<IFileStoreService> _store = new();
    private readonly Mock<IComicProcessorService> _processor = new();
    private readonly ConcurrentQueue<string> _logs = new();

    /// <summary>Store and processor calls in the order they were made.</summary>
    private readonly ConcurrentQueue<string> _calls = new();

    private readonly List<FileWatcherService> _services = new();
    private List<ComicFile> _tracked = new();
    private List<string> _pending = new();
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
    public async Task Startup_LibraryNotTrackedYet_AddsItsFilesWithoutProcessingThem()
    {
        var first = CreateComic("Saga", "Saga 001.cbz");
        var second = CreateComic("Monster", "Monster 001.cbz");
        // Rows of another library (an earlier watched directory) do not make this one known.
        _tracked = [Tracked(Path.Join(_root, "old-library", "Saga", "Saga 001.cbz"), processed: true)];

        await StartWatchingAsync();

        await WaitForLogAsync($"No file of {_library} was tracked before, so its 2 existing files were added without processing them");
        Assert.Contains($"add {first}", _calls);
        Assert.Contains($"add {second}", _calls);
        Assert.DoesNotContain(_calls, call => call.StartsWith("pending", StringComparison.Ordinal) || call.StartsWith("process", StringComparison.Ordinal));
        _store.Verify(s => s.IsFileProcessedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
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

    [Fact]
    public async Task Startup_TrackedFileGoneAndAnotherFileOfTheSameName_IsAddedAsNew()
    {
        var oldPath = Path.Join(_library, "Saga", "Saga 001.cbz");
        var newPath = CreateComic("Saga (2012)", "Saga 001.cbz");
        // Same name, but not the same file: the size differs.
        _tracked = [Tracked(oldPath, processed: true, size: new FileInfo(newPath).Length + 1)];

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
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddDbContextFactory<ComicMaintainerDbContext>(o => o.UseSqlite(connection));
        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
        }

        var processedBefore = CreateComic("Saga", "Saga - Chapter 0001.cbz");
        var processed = new ConcurrentQueue<string>();
        var processor = new Mock<IComicProcessorService>();
        processor.Setup(p => p.ProcessFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((path, _) =>
            {
                processed.Enqueue(path);
                return Task.FromResult(true);
            });

        // First run: the existing file is tracked and marked processed; a file then arrives and
        // is still settling when the application stops.
        var firstStore = CreateRealStore(factory);
        using (var firstRun = new FileWatcherService(
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
        using var secondRun = new FileWatcherService(
            new TestOptionsMonitor<AppSettings>(Settings(stabilitySeconds: 0)),
            new QueueLogger(_logs, () => null), secondStore, processor.Object);
        await secondRun.StartAsync();

        var settled = Path.Join(_library, "Saga", "Saga 002.cbz");
        await WaitUntilAsync(() => Task.FromResult(processed.Contains(settled) && processed.Contains(arrivedWhileDown)), "both files to be processed");
        await WaitUntilAsync(async () => (await secondStore.GetProcessingPendingFilesAsync()).Count == 0, "the queue records to be cleared");
        Assert.DoesNotContain(processedBefore, processed);
    }

    private FileStoreService CreateRealStore(IDbContextFactory<ComicMaintainerDbContext> factory) =>
        new(
            new TestOptionsMonitor<AppSettings>(Settings()),
            NullLogger<FileStoreService>.Instance,
            factory);

    private static ComicFile Tracked(string path, bool processed, long size = 5) => new()
    {
        FilePath = path,
        FileName = Path.GetFileName(path),
        Directory = Path.GetDirectoryName(path)!,
        FileSize = size,
        LastModified = new DateTime(2020, 1, 1),
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
        var service = new FileWatcherService(monitor, new QueueLogger(_logs, () => _onLog), _store.Object, _processor.Object);
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
