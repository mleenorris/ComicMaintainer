using System.Collections.Concurrent;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Covers the <see cref="FileWatcherService"/> paths that <see cref="FileWatcherServiceTests"/>
/// leaves out: rebinding on settings changes, status-broadcast failures, new-directory scans,
/// deletions, the single-mode "should process" checks and recovery after a watcher error,
/// including recreating a watcher that may have stopped.
/// </summary>
/// <remarks>
/// The event handlers are private, so they are driven by real file-system events in a unique
/// temp directory. Files are moved in from an unwatched sibling staging directory so that each
/// arrival raises a single "created" notification. Every wait completes from a recorded log
/// entry or mock call (TaskCompletionSource, bounded by <see cref="WaitTimeout"/>); there are no
/// fixed sleeps. A watcher error is simulated by making the service's own "File created" handler
/// throw: FileSystemWatcher reports handler exceptions through its Error event. Waits between
/// attempts to recreate a watcher run on a <see cref="RecordingTimeProvider"/>.
/// </remarks>
public sealed class FileWatcherServiceCoverageTests : IDisposable
{
    public enum ProcessingMode
    {
        RenameOnly,
        NormalizeOnly,
    }

    public enum Unavailability
    {
        DirectoryDeleted,
        WatcherStopped,
    }

    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"fws_cov_{Guid.NewGuid():N}");
    private readonly string _library;
    private readonly string _staging;
    private readonly RecordingLogger _logger = new();
    private readonly Mock<IFileStoreService> _store = new();
    private readonly Mock<IComicProcessorService> _processor = new();
    private readonly Recorder<string> _added = new();
    private readonly Recorder<string> _removed = new();
    private readonly Recorder<string> _processed = new();
    private readonly RecordingTimeProvider _time = new();
    private readonly List<FileWatcherService> _services = new();
    private volatile Func<string, Task>? _onAdd;

    public FileWatcherServiceCoverageTests()
    {
        _library = Path.Combine(_root, "library");
        _staging = Path.Combine(_root, "staging");
        Directory.CreateDirectory(_library);
        Directory.CreateDirectory(_staging);

        _store.Setup(s => s.AddFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((path, _) =>
            {
                _added.Add(path);
                return _onAdd?.Invoke(path) ?? Task.CompletedTask;
            });
        _store.Setup(s => s.RemoveFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((path, _) =>
            {
                _removed.Add(path);
                return Task.CompletedTask;
            });
        _processor.Setup(p => p.ProcessFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((path, _) =>
            {
                _processed.Add(path);
                return Task.FromResult(true);
            });
    }

    public void Dispose()
    {
        _logger.OnLog = null;
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
    public async Task SettingsChange_ToAnotherDirectory_RebindsWatcherAndBroadcastsEachTransition()
    {
        var otherLibrary = Path.Combine(_root, "other-library");
        Directory.CreateDirectory(otherLibrary);
        var broadcasts = new Recorder<(bool Running, bool Enabled)>();
        var broadcaster = new Mock<IEventBroadcaster>();
        broadcaster.Setup(b => b.BroadcastWatcherStatusAsync(It.IsAny<bool>(), It.IsAny<bool>()))
            .Returns<bool, bool>((running, enabled) =>
            {
                broadcasts.Add((running, enabled));
                return Task.CompletedTask;
            });
        var monitor = new TestOptionsMonitor<AppSettings>(Settings());
        var service = CreateService(monitor, broadcaster.Object);
        await service.StartAsync();
        await _logger.WaitForAsync(LogLevel.Information, "Incremental scan completed");

        monitor.Set(Settings(directory: otherLibrary));

        await broadcasts.WaitUntilAsync(b => b.Count >= 3, "the start, stop and restart status broadcasts");
        Assert.Equal(new[] { (true, true), (false, true), (true, true) }, broadcasts.Snapshot());
        Assert.True(service.IsRunning);
        AssertLogged(LogLevel.Information, "directoryChanged: True");
        AssertLogged(LogLevel.Information, "File watcher stopped");
        AssertLogged(LogLevel.Information, $"File watcher started for directory: {otherLibrary}");
        _store.Verify(s => s.InitializeFromDatabaseAsync(It.IsAny<CancellationToken>()), Times.Once);

        var leftBehind = MoveIn("left-behind.cbz");
        var arrival = MoveIn("arrival.cbz", otherLibrary);
        await _added.WaitForAsync(p => p == arrival, "the comic that arrived in the new directory");
        Assert.DoesNotContain(leftBehind, _added.Snapshot());
    }

    [Fact]
    public async Task SettingsChange_WhenRestartFails_LogsReconfigurationError()
    {
        var failure = new InvalidOperationException("database unavailable");
        _store.Setup(s => s.InitializeFromDatabaseAsync(It.IsAny<CancellationToken>())).ThrowsAsync(failure);
        var monitor = new TestOptionsMonitor<AppSettings>(Settings());
        CreateService(monitor);

        // Never started, so any settings notification starts the watcher in the background.
        monitor.Set(Settings());

        var error = await _logger.WaitForAsync(LogLevel.Error, "Error reconfiguring watcher after settings change");
        Assert.Same(failure, error.Exception);
        AssertLogged(LogLevel.Information, $"File watcher started for directory: {_library}");
    }

    [Fact]
    public void Dispose_IsIdempotentAndIgnoresLaterSettingsNotifications()
    {
        Action<AppSettings, string?>? listener = null;
        var subscription = new Mock<IDisposable>();
        var monitor = new Mock<IOptionsMonitor<AppSettings>>();
        monitor.Setup(m => m.CurrentValue).Returns(Settings());
        monitor.Setup(m => m.OnChange(It.IsAny<Action<AppSettings, string?>>()))
            .Callback<Action<AppSettings, string?>>(l => listener = l)
            .Returns(subscription.Object);
        var service = CreateService(monitor.Object);

        service.Dispose();
        service.Dispose();

        subscription.Verify(s => s.Dispose(), Times.Once);
        Assert.NotNull(listener);

        // A notification racing with disposal. Turning processing off would otherwise be logged
        // synchronously and start the watcher in the background.
        listener!(Settings(rename: false, normalize: false), null);

        Assert.Empty(_logger.Entries.Snapshot());
        Assert.False(service.IsRunning);
    }

    [Fact]
    public async Task StartAndStop_BroadcastFailures_AreLoggedAsWarnings()
    {
        var failure = new InvalidOperationException("hub offline");
        var broadcaster = new Mock<IEventBroadcaster>();
        broadcaster.Setup(b => b.BroadcastWatcherStatusAsync(It.IsAny<bool>(), It.IsAny<bool>())).ThrowsAsync(failure);
        var service = await StartWatchingAsync(broadcaster: broadcaster.Object);

        await service.StopAsync();

        Assert.False(service.IsRunning);
        var warnings = _logger.Entries.Snapshot().Where(e => e.Level == LogLevel.Warning).ToList();
        Assert.Equal(2, warnings.Count);
        Assert.All(warnings, w =>
        {
            Assert.Equal("Failed to broadcast watcher status change", w.Message);
            Assert.Same(failure, w.Exception);
        });
        broadcaster.Verify(b => b.BroadcastWatcherStatusAsync(true, true), Times.Once);
        broadcaster.Verify(b => b.BroadcastWatcherStatusAsync(false, true), Times.Once);
    }

    [Fact]
    public async Task InitialScan_WithoutTrackedSnapshot_AddsEveryComicAndContinuesPastFailures()
    {
        var ok = Path.Combine(_library, "ok.cbz");
        var bad = Path.Combine(_library, "bad.cbz");
        File.WriteAllText(ok, "comic");
        File.WriteAllText(bad, "comic");
        File.WriteAllText(Path.Combine(_library, "notes.txt"), "not a comic");
        _store.Setup(s => s.GetAllFilesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult<IEnumerable<ComicFile>>(null!));
        var failure = new IOException("unreadable archive");
        _onAdd = path => path == bad ? Task.FromException(failure) : Task.CompletedTask;

        await StartWatchingAsync();

        var error = Assert.Single(_logger.Entries.Snapshot(), e => e.Level == LogLevel.Error);
        Assert.Equal($"[WATCHER] Error adding file during incremental scan: {bad}", error.Message);
        Assert.Same(failure, error.Exception);
        AssertLogged(LogLevel.Information, "Found 2 comic files on filesystem");
        AssertLogged(LogLevel.Information, "Incremental scan completed. Added 1 new files");
        Assert.Equal(new[] { bad, ok }, _added.Snapshot().Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task DirectoryArrival_ScansItsComicsAndQueuesThemForProcessing()
    {
        await StartWatchingAsync();
        var failure = new IOException("unreadable archive");
        _onAdd = path => Path.GetFileName(path) == "broken.cbz" ? Task.FromException(failure) : Task.CompletedTask;

        var series = MoveInDirectory("Series", "001.cbz", "002.cbr", "broken.cbz", "cover.jpg");

        await _logger.WaitForAsync(LogLevel.Information, $"Directory scan completed: {series}");
        AssertLogged(LogLevel.Information, $"Directory created: {series}, scanning for comic files");
        AssertLogged(LogLevel.Information, $"Found 3 comic files in new directory: {series}");
        var broken = Path.Combine(series, "broken.cbz");
        var error = Assert.Single(_logger.Entries.Snapshot(), e => e.Level == LogLevel.Error);
        Assert.Equal($"[WATCHER] Error processing file from new directory: {broken}", error.Message);
        Assert.Same(failure, error.Exception);
        Assert.DoesNotContain(_added.Snapshot(), p => p.EndsWith("cover.jpg", StringComparison.Ordinal));

        var first = Path.Combine(series, "001.cbz");
        var second = Path.Combine(series, "002.cbr");
        await _processed.WaitUntilAsync(p => p.Contains(first) && p.Contains(second), "both readable comics to be processed");
        Assert.DoesNotContain(broken, _processed.Snapshot());
    }

    [Fact]
    public async Task DirectoryArrival_RemovedBeforeItIsScanned_IsSkippedWithWarning()
    {
        await StartWatchingAsync();
        var gone = Path.Combine(_library, "Gone");
        var hookFailures = new ConcurrentQueue<Exception>();
        _logger.OnLog = entry =>
        {
            if (entry.Message == $"[WATCHER] Scanning directory for comic files: {gone}")
            {
                try
                {
                    Directory.Delete(gone, recursive: true);
                }
                catch (Exception ex)
                {
                    hookFailures.Enqueue(ex);
                }
            }
        };

        MoveInDirectory("Gone", "001.cbz");

        await _logger.WaitForAsync(LogLevel.Warning, $"Directory no longer exists: {gone}");
        Assert.Empty(hookFailures);
        AssertNotLogged("comic files in new directory");
        Assert.DoesNotContain(_added.Snapshot(), p => p.StartsWith(gone, StringComparison.Ordinal));
    }

    [Fact]
    public async Task DirectoryArrival_UnexpectedScanFailure_IsLogged()
    {
        await StartWatchingAsync();
        var batch = Path.Combine(_library, "Batch");
        var failure = new InvalidOperationException("log sink failure");
        _logger.OnLog = entry =>
        {
            if (entry.Message.Contains($"comic files in new directory: {batch}", StringComparison.Ordinal))
            {
                throw failure;
            }
        };

        MoveInDirectory("Batch", "001.cbz");

        var error = await _logger.WaitForAsync(LogLevel.Error, $"Error scanning directory: {batch}");
        Assert.Same(failure, error.Exception);
        AssertNotLogged("Directory scan completed");
        Assert.Empty(_added.Snapshot());
    }

    [Fact]
    public async Task DirectoryArrival_WhenServiceIsDisposedMidScan_DoesNotQueueProcessing()
    {
        var service = await StartWatchingAsync();
        _onAdd = _ =>
        {
            service.Dispose();
            return Task.CompletedTask;
        };

        var batch = MoveInDirectory("Batch", "001.cbz");

        await _logger.WaitForAsync(LogLevel.Information, $"Directory scan completed: {batch}");
        Assert.Equal(new[] { Path.Combine(batch, "001.cbz") }, _added.Snapshot());
        Assert.False(service.IsRunning);
        Assert.Empty(_processed.Snapshot());
        _store.Verify(s => s.IsFileProcessedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Deletion_DropsPendingProcessingAndRemovesFileFromStore()
    {
        await StartWatchingAsync(Settings(stabilitySeconds: 600));
        var comic = MoveIn("short-lived.cbz");
        await _logger.WaitForAsync(LogLevel.Information, $"File created: {comic}");

        File.Delete(comic);

        await _removed.WaitForAsync(p => p == comic, "the deleted comic to be removed from the store");
        AssertLogged(LogLevel.Information, $"File deleted: {comic}");
        Assert.Empty(_processed.Snapshot());
    }

    [Fact]
    public async Task StopAsync_WithDebouncesPending_StopsWithoutProcessing()
    {
        var service = await StartWatchingAsync(Settings(stabilitySeconds: 600));
        MoveIn("first.cbz");
        MoveIn("second.cbz");
        var last = MoveIn("third.cbz");
        // Events are raised one at a time on the watcher's thread, so once the third arrival is
        // logged the first two have been fully handled and are waiting out their debounce.
        await _logger.WaitForAsync(LogLevel.Information, $"File created: {last}");

        await service.StopAsync();

        Assert.False(service.IsRunning);
        AssertLogged(LogLevel.Information, "File watcher stopped");
        Assert.Empty(_processed.Snapshot());
        _store.Verify(s => s.IsFileProcessedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SuppressProcessing_BlankPath_IsIgnored(string? path)
    {
        var service = CreateService(new TestOptionsMonitor<AppSettings>(Settings()));

        var exception = Record.Exception(() => service.SuppressProcessing(path!));

        Assert.Null(exception);
        Assert.Empty(_logger.Entries.Snapshot());
    }

    [Fact]
    public async Task NonComicFiles_AreIgnoredWhenCreatedChangedOrRenamed()
    {
        await StartWatchingAsync();
        var notes = Path.Combine(_library, "notes.txt");
        File.WriteAllText(notes, "first draft");
        File.AppendAllText(notes, ", second draft");
        File.Move(notes, Path.Combine(_library, "notes-final.txt"));
        var control = MoveIn("control.cbz");

        // Events are delivered in order, so once the comic shows up every notification for the
        // text file has been handled.
        await _logger.WaitForAsync(LogLevel.Information, $"File created: {control}");

        Assert.DoesNotContain(_logger.Entries.Snapshot(), e =>
            e.Message.Contains("notes", StringComparison.Ordinal)
            && !e.Message.Contains("File deleted", StringComparison.Ordinal));
        Assert.DoesNotContain(_added.Snapshot(), p => p.Contains("notes", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(ProcessingMode.RenameOnly)]
    [InlineData(ProcessingMode.NormalizeOnly)]
    public async Task SingleModeProcessing_SkipsComicsAlreadyHandledForThatMode(ProcessingMode mode)
    {
        var renameOnly = mode == ProcessingMode.RenameOnly;
        var fresh = Path.Combine(_library, "fresh.cbz");
        var flagged = Path.Combine(_library, "flagged.cbz");
        var processed = Path.Combine(_library, "processed.cbz");
        _store.Setup(s => s.IsFileProcessedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((p, _) => Task.FromResult(p == processed));
        if (renameOnly)
        {
            _store.Setup(s => s.IsFileRenamedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns<string, CancellationToken>((p, _) => Task.FromResult(p == flagged));
        }
        else
        {
            _store.Setup(s => s.IsFileNormalizedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns<string, CancellationToken>((p, _) => Task.FromResult(p == flagged));
        }
        await StartWatchingAsync(Settings(rename: renameOnly, normalize: !renameOnly));

        MoveIn("fresh.cbz");
        MoveIn("flagged.cbz");
        MoveIn("processed.cbz");

        var skipped = renameOnly
            ? "File already processed or renamed, skipping: "
            : "File already processed or normalized, skipping: ";
        await _logger.WaitForAsync(LogLevel.Information, skipped + flagged);
        await _logger.WaitForAsync(LogLevel.Information, skipped + processed);
        await _processed.WaitForAsync(p => p == fresh, "the unhandled comic to be processed");
        Assert.DoesNotContain(flagged, _processed.Snapshot());
        Assert.DoesNotContain(processed, _processed.Snapshot());
        AssertNotLogged(skipped + fresh);
        if (renameOnly)
        {
            _store.Verify(s => s.IsFileNormalizedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        else
        {
            _store.Verify(s => s.IsFileRenamedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }
    }

    [Fact]
    public async Task ProcessingCheckFailure_IsLoggedAndTheComicIsNotProcessed()
    {
        var failure = new InvalidOperationException("store offline");
        _store.Setup(s => s.IsFileProcessedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(failure);
        await StartWatchingAsync();

        var comic = MoveIn("unknown.cbz");

        var error = await _logger.WaitForAsync(LogLevel.Error, $"Error checking if file should be processed: {comic}");
        Assert.Same(failure, error.Exception);
        Assert.Empty(_processed.Snapshot());
    }

    [Fact]
    public async Task WatcherError_TriggersRecoveryRescanThatRequeuesComics()
    {
        var existing = Path.Combine(_library, "existing.cbz");
        var bad = Path.Combine(_library, "bad.cbz");
        File.WriteAllText(existing, "comic");
        File.WriteAllText(bad, "comic");
        await StartWatchingAsync();
        var addFailure = new IOException("unreadable archive");
        _onAdd = path => path == bad ? Task.FromException(addFailure) : Task.CompletedTask;
        var (trigger, watcherFailure) = ArmWatcherFailure("trigger.cbz");

        MoveIn("trigger.cbz");

        var reported = await _logger.WaitForAsync(LogLevel.Error, "File watcher reported an error");
        Assert.Same(watcherFailure, reported.Exception);
        await _logger.WaitForAsync(LogLevel.Information, $"Recovery rescan completed: {_library}");
        AssertLogged(LogLevel.Information, $"Starting recovery rescan of directory: {_library}");
        AssertLogged(LogLevel.Information, "Recovery rescan found 3 comic files");
        var requeueError = Assert.Single(_logger.Entries.Snapshot(), e =>
            e.Level == LogLevel.Error && e.Message.Contains("Error re-queuing", StringComparison.Ordinal));
        Assert.Equal($"[WATCHER] Error re-queuing file during recovery rescan: {bad}", requeueError.Message);
        Assert.Same(addFailure, requeueError.Exception);

        // The arrival whose notification was lost is picked up by the rescan.
        Assert.Contains(trigger, _added.Snapshot());
        await _processed.WaitUntilAsync(p => p.Contains(trigger) && p.Contains(existing), "the rescanned comics to be processed");
        Assert.DoesNotContain(bad, _processed.Snapshot());
    }

    [Fact]
    public async Task WatcherError_RecoveryStopsRequeuingOnceTheServiceIsDisposed()
    {
        var service = await StartWatchingAsync();
        var hookFailures = new ConcurrentQueue<Exception>();
        var (trigger, _) = ArmWatcherFailure("trigger.cbz", entry =>
        {
            if (entry.Message.Contains("Recovery rescan found", StringComparison.Ordinal))
            {
                try
                {
                    service.Dispose();
                }
                catch (Exception ex)
                {
                    hookFailures.Enqueue(ex);
                }
            }
        });

        MoveIn("trigger.cbz");

        await _logger.WaitForAsync(LogLevel.Information, $"Recovery rescan completed: {_library}");
        Assert.Empty(hookFailures);
        AssertLogged(LogLevel.Information, "Recovery rescan found 1 comic files");
        Assert.DoesNotContain(trigger, _added.Snapshot());
        Assert.Empty(_processed.Snapshot());
    }

    [Fact]
    public async Task BufferOverflow_RescansWithoutRecreatingTheWatcher()
    {
        await StartWatchingAsync();
        var (trigger, overflow) = ArmWatcherFailure("trigger.cbz", failure: new InternalBufferOverflowException("simulated overflow"));

        MoveIn("trigger.cbz");

        var reported = await _logger.WaitForAsync(LogLevel.Error, "File watcher reported an error; some file-system events may have been lost");
        Assert.Same(overflow, reported.Exception);
        await _logger.WaitForAsync(LogLevel.Information, $"Recovery rescan completed: {_library}");
        Assert.Contains(trigger, _added.Snapshot());
        AssertNotLogged("may have stopped");
        Assert.Equal(1, CountLogged("File watcher started for directory"));
        Assert.Empty(_time.Delays.Snapshot());
    }

    [Theory]
    [InlineData(Unavailability.DirectoryDeleted)]
    [InlineData(Unavailability.WatcherStopped)]
    public async Task BufferOverflow_WhenWatchedDirectoryIsUnavailable_SkipsRecoveryRescan(Unavailability cause)
    {
        var service = await StartWatchingAsync();
        var hookFailures = new ConcurrentQueue<Exception>();
        var (trigger, _) = ArmWatcherFailure("trigger.cbz", failure: new InternalBufferOverflowException("simulated overflow"), alsoOnLog: entry =>
        {
            // Runs on the FileSystemWatcher's thread, before the recovery is queued; an exception
            // escaping here would take down the test host, so failures are recorded instead.
            if (!entry.Message.Contains("File watcher reported an error", StringComparison.Ordinal))
            {
                return;
            }

            try
            {
                if (cause == Unavailability.DirectoryDeleted)
                {
                    Directory.Delete(_library, recursive: true);
                }
                else
                {
                    service.StopAsync().GetAwaiter().GetResult();
                }
            }
            catch (Exception ex)
            {
                hookFailures.Enqueue(ex);
            }
        });

        MoveIn("trigger.cbz");

        await _logger.WaitForAsync(LogLevel.Warning, "Recovery rescan skipped: watched directory is unavailable");
        Assert.Empty(hookFailures);
        AssertNotLogged("Starting recovery rescan");
        AssertNotLogged("may have stopped");
        Assert.DoesNotContain(trigger, _added.Snapshot());
    }

    [Fact]
    public async Task WatcherError_RecreatesTheWatcherAndRescans()
    {
        var broadcasts = new Recorder<(bool Running, bool Enabled)>();
        var broadcaster = new Mock<IEventBroadcaster>();
        broadcaster.Setup(b => b.BroadcastWatcherStatusAsync(It.IsAny<bool>(), It.IsAny<bool>()))
            .Returns<bool, bool>((running, enabled) =>
            {
                broadcasts.Add((running, enabled));
                return Task.CompletedTask;
            });
        var service = await StartWatchingAsync(broadcaster: broadcaster.Object);
        var (trigger, failure) = ArmWatcherFailure("trigger.cbz");

        MoveIn("trigger.cbz");

        var reported = await _logger.WaitForAsync(LogLevel.Error, "File watcher reported an error and may have stopped");
        Assert.Same(failure, reported.Exception);
        await _logger.WaitForAsync(LogLevel.Information, $"Recovery rescan completed: {_library}");
        Assert.True(service.IsRunning);
        Assert.Equal(2, CountLogged($"File watcher started for directory: {_library}"));
        await broadcasts.WaitUntilAsync(b => b.Count >= 3, "the start, stop and restart status broadcasts");
        Assert.Equal(new[] { (true, true), (false, true), (true, true) }, broadcasts.Snapshot());
        Assert.Contains(trigger, _added.Snapshot());
        Assert.Empty(_time.Delays.Snapshot());

        // The recreated watcher picks up new arrivals.
        var arrival = MoveIn("arrival.cbz");
        await _added.WaitForAsync(p => p == arrival, "the comic that arrived after the watcher was recreated");
    }

    [Fact]
    public async Task WatcherError_WhileDirectoryIsUnavailable_RetriesWithExponentialBackoff()
    {
        var service = await StartWatchingAsync();
        var hookFailures = new ConcurrentQueue<Exception>();
        ArmWatcherFailure("trigger.cbz", alsoOnLog: entry => DeleteLibraryOnRestart(entry, hookFailures));

        MoveIn("trigger.cbz");

        var expected = new[] { 1, 2, 4, 8, 16, 32, 60, 60 }.Select(s => TimeSpan.FromSeconds(s)).ToArray();
        for (var i = 0; i < expected.Length; i++)
        {
            await _time.Delays.WaitUntilAsync(d => d.Count > i, $"backoff wait #{i + 1}");
            Assert.Equal(expected[i], _time.Delays.Snapshot()[i]);
            Assert.False(service.IsRunning);
            if (i < expected.Length - 1)
            {
                _time.Advance(expected[i]);
            }
        }

        Assert.Empty(hookFailures);
        var unavailable = _logger.Entries.Snapshot()
            .Where(e => e.Message.Contains("Cannot recreate the file watcher while the watched directory is unavailable", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(expected.Length, unavailable.Count);
        Assert.Equal(LogLevel.Warning, unavailable[0].Level);
        Assert.All(unavailable.Skip(1), e => Assert.Equal(LogLevel.Debug, e.Level));
        Assert.Equal(1, CountLogged("File watcher started for directory"));

        // Once the directory is back the next attempt recreates the watcher and rescans it.
        Directory.CreateDirectory(_library);
        _time.Advance(expected[^1]);

        await _logger.WaitForAsync(LogLevel.Information, $"Recovery rescan completed: {_library}");
        Assert.True(service.IsRunning);
        Assert.Equal(2, CountLogged($"File watcher started for directory: {_library}"));
        Assert.Equal(expected.Length, _time.Delays.Snapshot().Count);
    }

    [Fact]
    public async Task WatcherError_StalledStatusDelivery_DoesNotBlockRecreationOrRescan()
    {
        var broadcasts = new Recorder<(bool Running, bool Enabled)>();
        var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var broadcaster = new Mock<IEventBroadcaster>();
        broadcaster.Setup(b => b.BroadcastWatcherStatusAsync(It.IsAny<bool>(), It.IsAny<bool>()))
            .Returns<bool, bool>((running, enabled) =>
            {
                broadcasts.Add((running, enabled));
                return running ? Task.CompletedTask : stalled.Task;
            });
        var service = await StartWatchingAsync(broadcaster: broadcaster.Object);
        ArmWatcherFailure("trigger.cbz");

        MoveIn("trigger.cbz");

        await _logger.WaitForAsync(LogLevel.Information, $"Recovery rescan completed: {_library}");
        Assert.True(service.IsRunning);
        Assert.Equal(2, CountLogged($"File watcher started for directory: {_library}"));

        // The "running" status waits behind the stalled "stopped" one so their order is kept.
        await broadcasts.WaitUntilAsync(b => b.Count >= 2, "the stalled stop status broadcast");
        Assert.Equal(new[] { (true, true), (false, true) }, broadcasts.Snapshot());

        stalled.SetResult();

        await broadcasts.WaitUntilAsync(b => b.Count >= 3, "the restart status broadcast");
        Assert.Equal(new[] { (true, true), (false, true), (true, true) }, broadcasts.Snapshot());
    }

    [Fact]
    public async Task WatcherError_DirectoryChangedBeforeRestartAttempt_StartsTheNewDirectory()
    {
        var otherLibrary = Path.Combine(_root, "other-library");
        Directory.CreateDirectory(otherLibrary);
        var current = Settings();
        AppSettings? afterNextRead = null;
        var monitor = new Mock<IOptionsMonitor<AppSettings>>();
        monitor.Setup(m => m.CurrentValue).Returns(() =>
        {
            // Changes the settings right after the restart attempt reads the watched directory,
            // before it takes the service's lock.
            var value = Volatile.Read(ref current);
            var next = Interlocked.Exchange(ref afterNextRead, null);
            if (next != null)
            {
                Volatile.Write(ref current, next);
            }

            return value;
        });
        var service = CreateService(monitor.Object);
        await service.StartAsync();
        await _logger.WaitForAsync(LogLevel.Information, "Incremental scan completed");
        var hookFailures = new ConcurrentQueue<Exception>();
        ArmWatcherFailure("trigger.cbz", alsoOnLog: entry => DeleteLibraryOnRestart(entry, hookFailures));

        MoveIn("trigger.cbz");
        await _time.Delays.WaitUntilAsync(d => d.Count == 1, "the first backoff wait");
        Assert.Empty(hookFailures);

        Directory.CreateDirectory(_library);
        Volatile.Write(ref afterNextRead, Settings(directory: otherLibrary));
        _time.Advance(_time.Delays.Snapshot()[0]);

        await _logger.WaitForAsync(LogLevel.Information, $"Recovery rescan completed: {otherLibrary}");
        Assert.True(service.IsRunning);
        Assert.Equal(1, CountLogged($"File watcher started for directory: {otherLibrary}"));
        Assert.Equal(1, CountLogged($"File watcher started for directory: {_library}"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WatcherError_StopOrDisposeDuringBackoff_EndsTheRetries(bool dispose)
    {
        var service = await StartWatchingAsync();
        var hookFailures = new ConcurrentQueue<Exception>();
        ArmWatcherFailure("trigger.cbz", alsoOnLog: entry => DeleteLibraryOnRestart(entry, hookFailures));

        MoveIn("trigger.cbz");
        await _time.Delays.WaitUntilAsync(d => d.Count == 1, "the first backoff wait");

        if (dispose)
        {
            service.Dispose();
        }
        else
        {
            await service.StopAsync();
        }

        await _logger.WaitForAsync(LogLevel.Debug, "Stopped trying to recreate the file watcher");
        Assert.Empty(hookFailures);

        Directory.CreateDirectory(_library);
        _time.Advance(TimeSpan.FromMinutes(5));

        Assert.False(service.IsRunning);
        Assert.Single(_time.Delays.Snapshot());
        Assert.Equal(1, CountLogged("File watcher started for directory"));
        AssertNotLogged("Starting recovery rescan");
        AssertNotLogged("Recovery rescan");
    }

    [Fact]
    public async Task WatcherError_FromAStoppedWatcher_IsIgnored()
    {
        var service = await StartWatchingAsync();
        var hookFailures = new ConcurrentQueue<Exception>();
        ArmWatcherFailure("trigger.cbz", alsoOnLog: entry =>
        {
            // Runs on the FileSystemWatcher's thread, before the restart is queued.
            if (!entry.Message.Contains("may have stopped", StringComparison.Ordinal))
            {
                return;
            }

            try
            {
                service.StopAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                hookFailures.Enqueue(ex);
            }
        });

        MoveIn("trigger.cbz");

        await _logger.WaitForAsync(LogLevel.Debug, "Ignoring the error of a file watcher that has already been stopped or replaced");
        Assert.Empty(hookFailures);
        Assert.False(service.IsRunning);
        Assert.Equal(1, CountLogged("File watcher started for directory"));
        AssertNotLogged("Starting recovery rescan");
        AssertNotLogged("Recovery rescan");
        Assert.Empty(_time.Delays.Snapshot());
    }

    [Fact]
    public async Task WatcherError_DirectoryVanishingMidRescan_IsLogged()
    {
        await StartWatchingAsync();
        var hookFailures = new ConcurrentQueue<Exception>();
        ArmWatcherFailure("trigger.cbz", entry =>
        {
            // The library disappears between the rescan's existence check and its enumeration.
            if (entry.Message.Contains("Starting recovery rescan of directory", StringComparison.Ordinal))
            {
                try
                {
                    Directory.Delete(_library, recursive: true);
                }
                catch (Exception ex)
                {
                    hookFailures.Enqueue(ex);
                }
            }
        });

        MoveIn("trigger.cbz");

        var error = await _logger.WaitForAsync(LogLevel.Error, $"Error during recovery rescan of directory: {_library}");
        Assert.Empty(hookFailures);
        Assert.IsType<DirectoryNotFoundException>(error.Exception);
        AssertNotLogged("Recovery rescan completed");
    }

    private AppSettings Settings(string? directory = null, bool rename = true, bool normalize = true, int stabilitySeconds = 0) => new()
    {
        WatchedDirectory = directory ?? _library,
        WatcherEnableRename = rename,
        WatcherEnableNormalize = normalize,
        WatcherFileStabilityDelaySeconds = stabilitySeconds,
        WatcherDirectoryScanDelaySeconds = 0,
    };

    private FileWatcherService CreateService(IOptionsMonitor<AppSettings> monitor, IEventBroadcaster? broadcaster = null)
    {
        var service = new FileWatcherService(monitor, _logger, _store.Object, _processor.Object, broadcaster, _time);
        _services.Add(service);
        return service;
    }

    /// <summary>
    /// Starts a watcher and waits for its one-off initial scan so the scan cannot interleave with
    /// the events a test produces.
    /// </summary>
    private async Task<FileWatcherService> StartWatchingAsync(AppSettings? settings = null, IEventBroadcaster? broadcaster = null)
    {
        var service = CreateService(new TestOptionsMonitor<AppSettings>(settings ?? Settings()), broadcaster);
        await service.StartAsync();
        Assert.True(service.IsRunning);
        await _logger.WaitForAsync(LogLevel.Information, "Incremental scan completed");
        return service;
    }

    private string MoveIn(string fileName, string? directory = null)
    {
        var staged = Path.Combine(_staging, fileName);
        File.WriteAllText(staged, "comic");
        var target = Path.Combine(directory ?? _library, fileName);
        File.Move(staged, target);
        return target;
    }

    private string MoveInDirectory(string name, params string[] fileNames)
    {
        var staged = Path.Combine(_staging, name);
        Directory.CreateDirectory(staged);
        foreach (var fileName in fileNames)
        {
            File.WriteAllText(Path.Combine(staged, fileName), "comic");
        }

        var target = Path.Combine(_library, name);
        Directory.Move(staged, target);
        return target;
    }

    /// <summary>
    /// Makes the service's "File created" handler throw <paramref name="failure"/> (by default an
    /// <see cref="IOException"/>) once for <paramref name="fileName"/>, by failing the log call it
    /// starts with. FileSystemWatcher reports the exception through its Error event, which is the
    /// path a buffer overflow or a failing watcher takes too.
    /// </summary>
    private (string Trigger, Exception Failure) ArmWatcherFailure(string fileName, Action<LogEntry>? alsoOnLog = null, Exception? failure = null)
    {
        var trigger = Path.Combine(_library, fileName);
        failure ??= new IOException("simulated watcher failure");
        var armed = 1;
        _logger.OnLog = entry =>
        {
            if (entry.Message == $"[WATCHER] File created: {trigger}" && Interlocked.Exchange(ref armed, 0) == 1)
            {
                throw failure;
            }

            alsoOnLog?.Invoke(entry);
        };
        return (trigger, failure);
    }

    /// <summary>
    /// Deletes the library when the service reports that its watcher may have stopped, before the
    /// restart is queued, so the restart finds the watched directory unavailable.
    /// </summary>
    private void DeleteLibraryOnRestart(LogEntry entry, ConcurrentQueue<Exception> hookFailures)
    {
        if (!entry.Message.Contains("may have stopped", StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            Directory.Delete(_library, recursive: true);
        }
        catch (Exception ex)
        {
            hookFailures.Enqueue(ex);
        }
    }

    private int CountLogged(string fragment) =>
        _logger.Entries.Snapshot().Count(e => e.Message.Contains(fragment, StringComparison.Ordinal));

    private void AssertLogged(LogLevel level, string fragment) =>
        Assert.True(_logger.Has(level, fragment), $"Expected a {level} log containing \"{fragment}\". Logged:{Environment.NewLine}{_logger.Dump()}");

    private void AssertNotLogged(string fragment) =>
        Assert.False(
            _logger.Entries.Snapshot().Any(e => e.Message.Contains(fragment, StringComparison.Ordinal)),
            $"Expected no log containing \"{fragment}\". Logged:{Environment.NewLine}{_logger.Dump()}");

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception)
    {
        public override string ToString() => $"{Level}: {Message}";
    }

    /// <summary>
    /// Thread-safe list that lets a test await a condition over the items recorded so far.
    /// </summary>
    private sealed class Recorder<T>
    {
        private readonly object _gate = new();
        private readonly List<T> _items = new();
        private readonly List<Waiter> _waiters = new();

        public void Add(T item)
        {
            var satisfied = new List<Waiter>();
            lock (_gate)
            {
                _items.Add(item);
                foreach (var waiter in _waiters)
                {
                    if (waiter.Condition(_items))
                    {
                        satisfied.Add(waiter);
                    }
                }

                _waiters.RemoveAll(satisfied.Contains);
            }

            foreach (var waiter in satisfied)
            {
                waiter.Signal.TrySetResult();
            }
        }

        public IReadOnlyList<T> Snapshot()
        {
            lock (_gate)
            {
                return _items.ToArray();
            }
        }

        public async Task WaitUntilAsync(Func<IReadOnlyList<T>, bool> condition, string description)
        {
            var waiter = new Waiter(condition);
            lock (_gate)
            {
                if (condition(_items))
                {
                    return;
                }

                _waiters.Add(waiter);
            }

            try
            {
                await waiter.Signal.Task.WaitAsync(WaitTimeout);
            }
            catch (TimeoutException)
            {
                lock (_gate)
                {
                    _waiters.Remove(waiter);
                }

                throw new TimeoutException(
                    $"Timed out after {WaitTimeout.TotalSeconds:0}s waiting for {description}. Recorded:{Environment.NewLine}{string.Join(Environment.NewLine, Snapshot())}");
            }
        }

        public async Task<T> WaitForAsync(Func<T, bool> match, string description)
        {
            await WaitUntilAsync(items => items.Any(match), description);
            return Snapshot().First(match);
        }

        private sealed class Waiter(Func<IReadOnlyList<T>, bool> condition)
        {
            public Func<IReadOnlyList<T>, bool> Condition { get; } = condition;

            public TaskCompletionSource Signal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary>
    /// <see cref="FakeTimeProvider"/> wrapper that records the due time of every timer created, so
    /// a test can await the service's next wait before advancing past it.
    /// </summary>
    private sealed class RecordingTimeProvider : TimeProvider
    {
        private readonly FakeTimeProvider _time = new();

        public Recorder<TimeSpan> Delays { get; } = new();

        public override long TimestampFrequency => _time.TimestampFrequency;

        public override TimeZoneInfo LocalTimeZone => _time.LocalTimeZone;

        public override DateTimeOffset GetUtcNow() => _time.GetUtcNow();

        public override long GetTimestamp() => _time.GetTimestamp();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = _time.CreateTimer(callback, state, dueTime, period);
            Delays.Add(dueTime);
            return timer;
        }

        public void Advance(TimeSpan delta) => _time.Advance(delta);
    }

    private sealed class RecordingLogger : ILogger<FileWatcherService>
    {
        private volatile Action<LogEntry>? _onLog;

        public Recorder<LogEntry> Entries { get; } = new();

        /// <summary>
        /// Runs synchronously on the logging thread before the entry is recorded, so a test can
        /// act at an exact point of the service's work or make that log call fail.
        /// </summary>
        public Action<LogEntry>? OnLog
        {
            get => _onLog;
            set => _onLog = value;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var entry = new LogEntry(logLevel, formatter(state, exception), exception);
            try
            {
                _onLog?.Invoke(entry);
            }
            finally
            {
                Entries.Add(entry);
            }
        }

        public bool Has(LogLevel level, string fragment) =>
            Entries.Snapshot().Any(e => e.Level == level && e.Message.Contains(fragment, StringComparison.Ordinal));

        public Task<LogEntry> WaitForAsync(LogLevel level, string fragment) =>
            Entries.WaitForAsync(
                e => e.Level == level && e.Message.Contains(fragment, StringComparison.Ordinal),
                $"a {level} log containing \"{fragment}\"");

        public string Dump() => string.Join(Environment.NewLine, Entries.Snapshot());
    }
}
