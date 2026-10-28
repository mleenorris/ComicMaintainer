using System.Collections.Concurrent;
using System.ComponentModel;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Service for watching file system changes in the comic directory
/// </summary>
public class FileWatcherService : IFileWatcherService, IDisposable
{
    /// <summary>Wait after the first failed attempt to recreate a failed watcher.</summary>
    private static readonly TimeSpan RestartInitialDelay = TimeSpan.FromSeconds(1);

    /// <summary>Upper bound on the wait between attempts to recreate a failed watcher.</summary>
    private static readonly TimeSpan RestartMaxDelay = TimeSpan.FromMinutes(1);

    private readonly IOptionsMonitor<AppSettings> _settingsMonitor;
    private readonly ILogger<FileWatcherService> _logger;
    private readonly IFileStoreService _fileStore;
    private readonly IComicProcessorService _processor;
    private readonly IEventBroadcaster? _eventBroadcaster;
    private readonly TimeProvider _timeProvider;
    private FileSystemWatcher? _watcher;
    private bool _enabled;
    private string? _activeWatchedDirectory;
    private readonly object _lock = new();
    private bool _initialized = false;
    private readonly IDisposable? _settingsChangeSubscription;
    private bool _disposed;

    /// <summary>
    /// Set while a failed watcher is being recreated (guarded by <see cref="_lock"/>). Stopping
    /// or disposing the service takes and cancels it, which ends the retries.
    /// </summary>
    private CancellationTokenSource? _restartCts;

    /// <summary>
    /// The watcher whose start (enabling its events) is in progress, written under
    /// <see cref="_lock"/>. <see cref="OnWatcherError"/> reads it without the lock to tell errors
    /// raised while a watcher starts apart from errors of a running watcher.
    /// </summary>
    private volatile FileSystemWatcher? _startingWatcher;

    /// <summary>
    /// Pending per-file processing requests, keyed by full path. A burst of file-system events for
    /// the same path coalesces into a single delayed run: each new event cancels the previously
    /// scheduled debounce and re-arms it, so we only act once the file has stopped changing and we
    /// never queue duplicate work while a change is still pending for that path.
    /// </summary>
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pendingProcessing =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Paths currently being processed, used to prevent concurrent processing of the same file.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.Ordinal);

    /// <summary>
    /// Paths the watcher recently modified itself (via the processor renaming/rewriting archives).
    /// File-system events that arrive for these paths within a short window are the watcher's own
    /// changes echoing back and are ignored so we don't re-process work we just performed.
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTime> _recentlyProcessed = new(StringComparer.Ordinal);

    /// <summary>
    /// Returns the current AppSettings snapshot. Reading via the monitor on every access ensures
    /// that watcher decisions (rename/normalize toggles, debounce delays, watched directory)
    /// pick up the latest configuration without restarting the process.
    /// </summary>
    private AppSettings _settings => _settingsMonitor.CurrentValue;

    public bool IsRunning => _watcher?.EnableRaisingEvents ?? false;

    public FileWatcherService(
        IOptionsMonitor<AppSettings> settings,
        ILogger<FileWatcherService> logger,
        IFileStoreService fileStore,
        IComicProcessorService processor,
        IEventBroadcaster? eventBroadcaster = null,
        TimeProvider? timeProvider = null)
    {
        _settingsMonitor = settings;
        _logger = logger;
        _fileStore = fileStore;
        _processor = processor;
        _eventBroadcaster = eventBroadcaster;
        _timeProvider = timeProvider ?? TimeProvider.System;
        // Watcher is enabled if either rename or normalize is enabled
        _enabled = _settings.WatcherEnableRename || _settings.WatcherEnableNormalize;

        // Subscribe to settings changes so that toggling WatcherEnableRename / WatcherEnableNormalize
        // or changing WatchedDirectory at runtime starts/stops/rebinds the FileSystemWatcher.
        _settingsChangeSubscription = _settingsMonitor.OnChange(HandleSettingsChanged);
    }

    private void HandleSettingsChanged(AppSettings newSettings)
    {
        if (_disposed)
        {
            return;
        }

        var newEnabled = newSettings.WatcherEnableRename || newSettings.WatcherEnableNormalize;
        var newWatchedDirectory = newSettings.WatchedDirectory;

        bool needsStop;
        bool needsStart;
        lock (_lock)
        {
            var directoryChanged = !string.Equals(_activeWatchedDirectory, newWatchedDirectory, StringComparison.Ordinal)
                && _watcher != null;
            var enabledFlipped = _enabled != newEnabled;

            // The live watcher always monitors for new files regardless of the rename/normalize
            // toggles, so toggling "enabled" no longer starts or stops it. We only stop/rebind the
            // watcher when the watched directory itself changes.
            needsStop = _watcher != null && directoryChanged;
            // Start (or rebind) whenever we are not currently watching or the directory changed.
            needsStart = _watcher == null || directoryChanged;

            if (enabledFlipped || directoryChanged)
            {
                _logger.LogInformation(LoggingHelper.WithWatcherPrefix(
                    "Detected settings change affecting watcher (processing enabled: {OldEnabled} -> {NewEnabled}, directoryChanged: {DirectoryChanged}). Reconfiguring..."),
                    _enabled, newEnabled, directoryChanged);
            }

            _enabled = newEnabled;
        }

        if (!needsStop && !needsStart)
        {
            return;
        }

        // Run reconfiguration asynchronously on the thread pool. We intentionally do NOT block
        // the configuration-change notification thread on async I/O (file enumeration, event
        // broadcasting), which could deadlock under some synchronization contexts and would
        // delay subsequent configuration callbacks.
        _ = Task.Run(async () =>
        {
            try
            {
                if (needsStop)
                {
                    await StopAsync();
                }
                if (needsStart)
                {
                    await StartAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, LoggingHelper.WithWatcherPrefix("Error reconfiguring watcher after settings change"));
            }
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _settingsChangeSubscription?.Dispose();
        CancelAllPending();

        CancellationTokenSource? restart;
        lock (_lock)
        {
            restart = _restartCts;
            _restartCts = null;

            if (_watcher != null)
            {
                try
                {
                    _watcher.EnableRaisingEvents = false;
                    _watcher.Dispose();
                }
                catch
                {
                    // Best-effort disposal
                }
                _watcher = null;
            }
        }

        CancelRestart(restart);
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        bool shouldInitialize = false;
        bool watcherStarted = false;
        bool watchedDirectoryExists = Directory.Exists(_settings.WatchedDirectory);

        lock (_lock)
        {
            // "Enabled" tracks whether automatic processing (rename/normalize) is on. It no longer
            // gates the live file-system watcher: the watcher always monitors for new files so that
            // any file added to the watched directory is at least recorded in the file list, even
            // when both rename and normalize are turned off.
            _enabled = _settings.WatcherEnableRename || _settings.WatcherEnableNormalize;

            if (!watchedDirectoryExists)
            {
                _logger.LogError(LoggingHelper.WithWatcherPrefix("Watched directory does not exist: {Directory}"), _settings.WatchedDirectory);
            }
            else if (_watcher != null && _watcher.EnableRaisingEvents)
            {
                _logger.LogInformation(LoggingHelper.WithWatcherPrefix("Watcher is already running"));
            }
            else
            {
                if (!_enabled)
                {
                    _logger.LogInformation(LoggingHelper.WithWatcherPrefix(
                        "Automatic processing is disabled (both rename and normalize are off); the watcher will still monitor for new files and keep the file list up to date without renaming or normalizing them."));
                }

                StartWatcherLocked(_settings.WatchedDirectory);
                watcherStarted = true;
            }

            // The file store inventory is used by features that don't depend on live watcher
            // events (e.g. the "Scan Unmarked" endpoint, file listings, counts). Initialize it once
            // per process whenever the watched directory exists.
            if (!_initialized && watchedDirectoryExists)
            {
                shouldInitialize = true;
                _initialized = true;
            }
        }

        // Broadcast watcher status change (only when the watcher actually started this call to
        // avoid spurious status events on repeated StartAsync calls).
        if (watcherStarted)
        {
            await BroadcastStatusAsync(IsRunning);
        }

        // Initialize file store from database before scanning files (only once).
        // Runs regardless of whether the live watcher is enabled.
        if (shouldInitialize)
        {
            await _fileStore.InitializeFromDatabaseAsync(cancellationToken);

            // Perform initial scan of existing files so the in-memory store reflects what's on
            // disk. Only runs once per process (gated by _initialized).
            _ = Task.Run(async () => await ScanExistingFilesAsync(cancellationToken));
        }
    }

    /// <summary>
    /// Creates and starts the watcher for <paramref name="directory"/>, replacing any previous
    /// one. Callers hold <see cref="_lock"/>.
    /// </summary>
    private void StartWatcherLocked(string directory)
    {
        // A watcher that stopped itself (Windows does so after an error) is replaced, not leaked.
        // It is retired before the new one is enabled so the two never raise events together.
        if (_watcher != null)
        {
            DisposeWatcher(_watcher);
            _watcher = null;
            _activeWatchedDirectory = null;
        }

        var watcher = new FileSystemWatcher(directory)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            Filter = "*.*",
            IncludeSubdirectories = true
        };
        // Enlarge the internal buffer so bursts of file-system events (e.g. a large batch of
        // new files landing at once) are less likely to overflow and be dropped by the OS.
        watcher.InternalBufferSize = ClampInternalBufferSize(_settings.WatcherInternalBufferSizeKB);

        watcher.Created += OnFileCreated;
        watcher.Changed += OnFileChanged;
        watcher.Renamed += OnFileRenamed;
        watcher.Deleted += OnFileDeleted;
        watcher.Error += OnWatcherError;

        _startingWatcher = watcher;
        try
        {
            watcher.EnableRaisingEvents = true;
        }
        catch
        {
            watcher.Dispose();
            throw;
        }
        finally
        {
            _startingWatcher = null;
        }

        _watcher = watcher;
        _activeWatchedDirectory = directory;
        _logger.LogInformation(LoggingHelper.WithWatcherPrefix("File watcher started for directory: {Directory}"), directory);
    }
    
    /// <summary>
    /// Scans the watched directory for existing comic files and adds them to the file store
    /// Only scans for new files not already in the database
    /// </summary>
    private async Task ScanExistingFilesAsync(CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation(LoggingHelper.WithWatcherPrefix("Starting incremental scan of directory: {Directory}"), _settings.WatchedDirectory);
            
            var comicFiles = Directory.EnumerateFiles(_settings.WatchedDirectory, "*.*", SearchOption.AllDirectories)
                .Where(IsComicFile)
                .ToList();
            
            _logger.LogInformation(LoggingHelper.WithWatcherPrefix("Found {Count} comic files on filesystem"), comicFiles.Count);
            
            // Snapshot the tracked paths once instead of one FileExistsAsync
            // call per file. Ordinal matches the store's own key comparison.
            var trackedPaths = new HashSet<string>(
                (await _fileStore.GetAllFilesAsync(cancellationToken) ?? Enumerable.Empty<ComicFile>())
                    .Select(f => f.FilePath)
                    .Where(p => !string.IsNullOrWhiteSpace(p)),
                StringComparer.Ordinal);

            var newFileCount = 0;
            foreach (var file in comicFiles)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;
                    
                try
                {
                    // Only add if not already in the store
                    if (!trackedPaths.Contains(file))
                    {
                        await _fileStore.AddFileAsync(file, cancellationToken);
                        newFileCount++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, LoggingHelper.WithWatcherPrefix("Error adding file during incremental scan: {File}"), file);
                }
            }
            
            _logger.LogInformation(LoggingHelper.WithWatcherPrefix("Incremental scan completed. Added {Count} new files"), newFileCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, LoggingHelper.WithWatcherPrefix("Error during incremental directory scan"));
        }
    }

    /// <summary>
    /// Scans a specific directory for comic files and processes them
    /// </summary>
    private async Task ScanDirectoryAsync(string directoryPath, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation(LoggingHelper.WithWatcherPrefix("Scanning directory for comic files: {Directory}"), directoryPath);
            
            if (!Directory.Exists(directoryPath))
            {
                _logger.LogWarning(LoggingHelper.WithWatcherPrefix("Directory no longer exists: {Directory}"), directoryPath);
                return;
            }
            
            var comicFiles = Directory.EnumerateFiles(directoryPath, "*.*", SearchOption.AllDirectories)
                .Where(IsComicFile)
                .ToList();
            
            _logger.LogInformation(LoggingHelper.WithWatcherPrefix("Found {Count} comic files in new directory: {Directory}"), comicFiles.Count, directoryPath);
            
            foreach (var file in comicFiles)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;
                    
                try
                {
                    await _fileStore.AddFileAsync(file, cancellationToken);

                    // Route through the coalescing scheduler so multiple files (and any follow-up
                    // change events they trigger) are debounced and de-duplicated instead of each
                    // blocking on its own stability delay.
                    ScheduleProcessing(file);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, LoggingHelper.WithWatcherPrefix("Error processing file from new directory: {File}"), file);
                }
            }
            
            _logger.LogInformation(LoggingHelper.WithWatcherPrefix("Directory scan completed: {Directory}"), directoryPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, LoggingHelper.WithWatcherPrefix("Error scanning directory: {Directory}"), directoryPath);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        CancelAllPending();

        CancellationTokenSource? restart;
        lock (_lock)
        {
            restart = _restartCts;
            _restartCts = null;

            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
                _watcher = null;
                _activeWatchedDirectory = null;
                _logger.LogInformation(LoggingHelper.WithWatcherPrefix("File watcher stopped"));
            }
        }

        CancelRestart(restart);

        // Broadcast watcher status change
        await BroadcastStatusAsync(false);
    }

    private async Task BroadcastStatusAsync(bool running)
    {
        if (_eventBroadcaster == null)
        {
            return;
        }

        try
        {
            await _eventBroadcaster.BroadcastWatcherStatusAsync(running, _enabled);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to broadcast watcher status change");
        }
    }

    /// <summary>
    /// DEPRECATED: Direct watcher enable/disable is no longer supported.
    /// Watcher is now controlled by WatcherEnableRename and WatcherEnableNormalize settings.
    /// This method is kept for backward compatibility but does nothing.
    /// </summary>
    [Obsolete("Direct watcher enable/disable is deprecated. Use WatcherEnableRename and WatcherEnableNormalize settings instead.")]
    public void SetEnabled(bool enabled)
    {
        _logger.LogWarning("SetEnabled is deprecated. Watcher is now controlled by WatcherEnableRename and WatcherEnableNormalize settings.");
        // No-op: watcher is now controlled by rename/normalize settings
    }

    /// <summary>
    /// Public entry point for application code that modifies a watched file on
    /// purpose (e.g. embedding a series cover into the first archive) and wants
    /// the resulting file-system events suppressed instead of reprocessed.
    /// </summary>
    public void SuppressProcessing(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        MarkSelfInducedChange(filePath);
    }

    private void OnFileCreated(object sender, FileSystemEventArgs e)
    {
        // Ignore temporary files immediately to avoid unnecessary processing
        if (IsTemporaryFile(e.FullPath))
        {
            return;
        }
        
        // Check if it's a directory
        if (Directory.Exists(e.FullPath))
        {
            _logger.LogInformation(LoggingHelper.WithWatcherPrefix("Directory created: {Path}, scanning for comic files"), e.FullPath);
            _ = Task.Run(async () =>
            {
                // Give the system time to finish copying files into the directory
                await Task.Delay(TimeSpan.FromSeconds(_settings.WatcherDirectoryScanDelaySeconds));
                await ScanDirectoryAsync(e.FullPath);
            });
        }
        else if (IsComicFile(e.FullPath))
        {
            _logger.LogInformation(LoggingHelper.WithWatcherPrefix("File created: {Path}"), e.FullPath);
            _ = Task.Run(async () =>
            {
                try
                {
                    await _fileStore.AddFileAsync(e.FullPath);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, LoggingHelper.WithWatcherPrefix("Error adding created file to store: {Path}"), e.FullPath);
                }
            });

            // Debounce and process (coalesces with any other pending events for this path).
            ScheduleProcessing(e.FullPath);
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        // Ignore temporary files immediately to avoid unnecessary processing
        if (IsTemporaryFile(e.FullPath))
        {
            return;
        }
        
        if (IsComicFile(e.FullPath))
        {
            _logger.LogInformation(LoggingHelper.WithWatcherPrefix("File changed: {Path}"), e.FullPath);

            // Debounce and process. Scheduling here coalesces the storm of Changed events that a
            // single copy/write produces (and any change the processor itself makes) into one run,
            // and the stability check inside the scheduled work happens only after the file settles.
            ScheduleProcessing(e.FullPath);
        }
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        // Ignore if the target (new) file is a temporary file
        // Note: We only check the target path to allow renames FROM temporary files TO comic files,
        // which is a common pattern when files are moved into the watched directory
        if (IsTemporaryFile(e.FullPath))
        {
            return;
        }
        
        if (IsComicFile(e.FullPath))
        {
            _logger.LogInformation(LoggingHelper.WithWatcherPrefix("File renamed: {OldPath} -> {NewPath}"), e.OldFullPath, e.FullPath);

            // Any work still pending for the old path is now stale.
            CancelPending(e.OldFullPath);

            _ = Task.Run(async () =>
            {
                try
                {
                    // Preserve processing state from the old path - avoids re-processing files
                    // that were renamed by the processor itself.
                    await _fileStore.UpdateFilePathAsync(e.OldFullPath, e.FullPath);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, LoggingHelper.WithWatcherPrefix("Error updating renamed file path: {Path}"), e.FullPath);
                }
            });

            // Debounce and process the new path. Scheduling consults the (now updated) state only
            // after the stability delay, so a rename performed by the processor itself is skipped.
            ScheduleProcessing(e.FullPath);
        }
    }

    private void OnFileDeleted(object sender, FileSystemEventArgs e)
    {
        // Ignore temporary files immediately to avoid unnecessary processing
        if (IsTemporaryFile(e.FullPath))
        {
            return;
        }
        
        _logger.LogInformation(LoggingHelper.WithWatcherPrefix("File deleted: {Path}"), e.FullPath);

        // Drop any pending processing for a file that no longer exists.
        CancelPending(e.FullPath);
        _recentlyProcessed.TryRemove(e.FullPath, out _);

        _ = Task.Run(async () =>
        {
            await _fileStore.RemoveFileAsync(e.FullPath);
        });
    }

    /// <summary>
    /// Handles FileSystemWatcher errors, after which file-system events may have been missed.
    /// <para>
    /// Some errors leave the watcher running. The most important is an
    /// <see cref="InternalBufferOverflowException"/>: when file-system events arrive faster than
    /// they can be drained, the OS drops events that we never see. An error raised while a watcher
    /// is being started concerns a subdirectory that could not be watched (for example for lack of
    /// permission, or because the inotify watch limit was reached), which a new watcher would hit
    /// again. Missed files would otherwise stay unprocessed until the service is restarted, so we
    /// rescan the watched directory and re-queue its comic files.
    /// </para>
    /// <para>
    /// Any other error can leave the watcher dead: on Windows it stops itself when its directory
    /// becomes unavailable (a network share disconnecting, the folder being removed), and on
    /// Linux an exception ends its event loop while it still reports itself as running. Such a
    /// watcher is replaced, see <see cref="RestartFaultedWatcherAsync"/>.
    /// </para>
    /// </summary>
    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        var ex = e.GetException();

        var starting = sender != null && ReferenceEquals(sender, _startingWatcher);
        if (starting || ex is InternalBufferOverflowException)
        {
            _logger.LogError(ex, LoggingHelper.WithWatcherPrefix(
                "File watcher reported an error; some file-system events may have been lost. Triggering a recovery rescan."));

            // A watcher started by a restart is rescanned once it runs, so subdirectories it
            // cannot watch don't each queue another full rescan.
            if (!starting || Volatile.Read(ref _restartCts) == null)
            {
                // Recover off the event thread so we don't block the watcher's notification pipeline.
                _ = Task.Run(async () => await RecoverFromMissedEventsAsync());
            }

            return;
        }

        _logger.LogError(ex, LoggingHelper.WithWatcherPrefix(
            "File watcher reported an error and may have stopped; recreating it and rescanning for missed files."));

        // Never wait for _lock on this thread: the watcher can raise errors while holding an
        // internal lock that disposing it under _lock also takes.
        if (sender is FileSystemWatcher faulted)
        {
            _ = Task.Run(() => RestartFaultedWatcherAsync(faulted));
        }
    }

    /// <summary>
    /// Disposes a watcher that reported an error and starts a replacement, retrying with
    /// exponential backoff while the watched directory is unavailable. Once a watcher runs again
    /// the watched directory is rescanned for files that arrived in the meantime.
    /// </summary>
    private async Task RestartFaultedWatcherAsync(FileSystemWatcher faulted)
    {
        // Only this method disposes the restart: StopAsync and Dispose just cancel it.
        using var restart = new CancellationTokenSource();
        var retrying = false;
        lock (_lock)
        {
            if (!ReferenceEquals(_watcher, faulted))
            {
                _logger.LogDebug(LoggingHelper.WithWatcherPrefix(
                    "Ignoring the error of a file watcher that has already been stopped or replaced"));
                return;
            }

            DisposeWatcher(faulted);
            _watcher = null;
            _activeWatchedDirectory = null;

            // A restart that is already retrying replaces this watcher too.
            if (_restartCts == null)
            {
                _restartCts = restart;
                retrying = true;
            }
        }

        await BroadcastStatusAsync(false);

        if (retrying)
        {
            await RestartWatcherAsync(restart);
        }
    }

    /// <summary>
    /// Starts a watcher, waiting <see cref="RestartInitialDelay"/> after the first failed attempt
    /// and twice as long after each further one, up to <see cref="RestartMaxDelay"/>, until a
    /// watcher runs or StopAsync or Dispose takes <paramref name="restart"/>.
    /// </summary>
    private async Task RestartWatcherAsync(CancellationTokenSource restart)
    {
        var token = restart.Token;
        var startedHere = false;
        try
        {
            var delay = RestartInitialDelay;
            for (var attempt = 1; ; attempt++)
            {
                var directory = _settings.WatchedDirectory;
                var available = Directory.Exists(directory);
                Exception? failure = null;

                lock (_lock)
                {
                    if (!ReferenceEquals(_restartCts, restart))
                    {
                        _logger.LogDebug(LoggingHelper.WithWatcherPrefix("Stopped trying to recreate the file watcher"));
                        return;
                    }

                    if (_watcher == null && available)
                    {
                        try
                        {
                            StartWatcherLocked(directory);
                            startedHere = true;
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or Win32Exception)
                        {
                            failure = ex;
                        }
                    }

                    // Running again, started here or by a settings change in the meantime.
                    if (_watcher != null)
                    {
                        _restartCts = null;
                        break;
                    }
                }

                var level = attempt == 1 ? LogLevel.Warning : LogLevel.Debug;
                if (failure == null)
                {
                    _logger.Log(level, LoggingHelper.WithWatcherPrefix(
                        "Cannot recreate the file watcher while the watched directory is unavailable: {Directory}. Retrying in {Delay}."),
                        directory, delay);
                }
                else
                {
                    _logger.Log(level, failure, LoggingHelper.WithWatcherPrefix(
                        "Failed to recreate the file watcher for directory: {Directory}. Retrying in {Delay}."),
                        directory, delay);
                }

                await Task.Delay(delay, _timeProvider, token);
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, RestartMaxDelay.Ticks));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _logger.LogDebug(LoggingHelper.WithWatcherPrefix("Stopped trying to recreate the file watcher"));
            return;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, LoggingHelper.WithWatcherPrefix("Error recreating the file watcher"));
            return;
        }
        finally
        {
            // Release the restart after an unexpected failure so a later error can retry again.
            lock (_lock)
            {
                if (ReferenceEquals(_restartCts, restart))
                {
                    _restartCts = null;
                }
            }
        }

        if (startedHere)
        {
            await BroadcastStatusAsync(IsRunning);
        }

        await RecoverFromMissedEventsAsync();
    }

    /// <summary>
    /// Ends a restart taken out of <see cref="_restartCts"/>. Call it after releasing
    /// <see cref="_lock"/>: cancelling can resume the restart loop on this thread.
    /// </summary>
    private static void CancelRestart(CancellationTokenSource? restart)
    {
        try
        {
            restart?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The restart loop already ended and disposed it.
        }
    }

    private void DisposeWatcher(FileSystemWatcher watcher)
    {
        try
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or Win32Exception)
        {
            // Best-effort disposal of a watcher that may already have failed
            _logger.LogDebug(ex, LoggingHelper.WithWatcherPrefix("Error disposing a file watcher"));
        }
    }

    /// <summary>
    /// Re-enumerates the watched directory and re-queues every comic file for processing. Used to
    /// recover after a watcher buffer overflow (or other watcher error) where individual file
    /// events were dropped. Files already in their final state are cheaply skipped by the
    /// stability/should-process checks inside <see cref="ScheduleProcessing"/>.
    /// </summary>
    private async Task RecoverFromMissedEventsAsync()
    {
        if (_disposed)
        {
            return;
        }

        string? directory;
        lock (_lock)
        {
            directory = _activeWatchedDirectory;
        }

        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            _logger.LogWarning(LoggingHelper.WithWatcherPrefix(
                "Recovery rescan skipped: watched directory is unavailable: {Directory}"), directory);
            return;
        }

        try
        {
            _logger.LogInformation(LoggingHelper.WithWatcherPrefix(
                "Starting recovery rescan of directory: {Directory}"), directory);

            var comicFiles = Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories)
                .Where(IsComicFile)
                .ToList();

            _logger.LogInformation(LoggingHelper.WithWatcherPrefix(
                "Recovery rescan found {Count} comic files; re-queuing for processing"), comicFiles.Count);

            foreach (var file in comicFiles)
            {
                if (_disposed)
                {
                    break;
                }

                try
                {
                    await _fileStore.AddFileAsync(file);
                    ScheduleProcessing(file);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, LoggingHelper.WithWatcherPrefix(
                        "Error re-queuing file during recovery rescan: {File}"), file);
                }
            }

            _logger.LogInformation(LoggingHelper.WithWatcherPrefix(
                "Recovery rescan completed: {Directory}"), directory);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, LoggingHelper.WithWatcherPrefix(
                "Error during recovery rescan of directory: {Directory}"), directory);
        }
    }

    /// <summary>
    /// Clamps the configured internal buffer size (in KB) to the range the OS accepts. The minimum
    /// useful size is 4 KB and Windows caps the buffer at 64 KB; values outside that range are
    /// coerced so a misconfiguration can't disable the watcher.
    /// </summary>
    private static int ClampInternalBufferSize(int configuredKB)
    {
        const int minBytes = 4 * 1024;
        const int maxBytes = 64 * 1024;
        var bytes = configuredKB * 1024;
        return Math.Clamp(bytes, minBytes, maxBytes);
    }

    /// <summary>
    /// Schedules a file for processing after the configured stability delay, coalescing repeated
    /// events for the same path. If a request for this path is already pending, its debounce timer
    /// is reset so the bursty stream of file-system events a single operation produces collapses
    /// into one processing run. Self-induced changes (files the watcher just renamed/normalized) and
    /// files already being processed are skipped to eliminate redundant work.
    /// </summary>
    private void ScheduleProcessing(string filePath)
    {
        if (_disposed)
        {
            return;
        }

        // Ignore events that are just the watcher's own recent change echoing back from the OS.
        if (IsSelfInducedChange(filePath))
        {
            _logger.LogDebug(LoggingHelper.WithWatcherPrefix("Ignoring self-induced change for recently processed file: {File}"), filePath);
            return;
        }

        var cts = new CancellationTokenSource();

        // Coalesce: install this request as the pending one for the path, cancelling any earlier
        // debounce so only the most recent event in a burst survives.
        _pendingProcessing.AddOrUpdate(filePath, cts, (_, existing) =>
        {
            try
            {
                existing.Cancel();
                existing.Dispose();
            }
            catch
            {
                // Best-effort cancellation of the superseded debounce.
            }
            return cts;
        });

        var token = cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                // Debounce window. A newer event for the same path cancels this token and re-arms
                // a fresh delay, so we only proceed once the file has been quiet for the full delay.
                await Task.Delay(TimeSpan.FromSeconds(_settings.WatcherFileStabilityDelaySeconds), token);
            }
            catch (OperationCanceledException)
            {
                return; // Superseded by a newer event (or shutdown); that request will run instead.
            }

            // Claim the pending slot. If we are no longer the registered request, a newer event has
            // taken over and will perform the work, so defer to it.
            if (!_pendingProcessing.TryRemove(new KeyValuePair<string, CancellationTokenSource>(filePath, cts)))
            {
                return;
            }
            cts.Dispose();

            // Prevent processing the same file concurrently. If a run is already in flight, re-arm
            // the debounce so the file is re-evaluated once that run finishes rather than now.
            if (!_inFlight.TryAdd(filePath, 0))
            {
                ScheduleProcessing(filePath);
                return;
            }

            try
            {
                var shouldProcess = await ShouldProcessFileAsync(filePath);
                if (!shouldProcess)
                {
                    return;
                }

                // Mark before and after processing: the processor rewrites/renames the archive,
                // which fires fresh file-system events that we must recognise as our own.
                MarkSelfInducedChange(filePath);
                await _processor.ProcessFileAsync(filePath);
                MarkSelfInducedChange(filePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, LoggingHelper.WithWatcherPrefix("Error processing file: {File}"), filePath);
            }
            finally
            {
                _inFlight.TryRemove(filePath, out _);
            }
        });
    }

    /// <summary>
    /// Cancels and discards any pending (debounced) processing request for the given path.
    /// </summary>
    private void CancelPending(string filePath)
    {
        if (_pendingProcessing.TryRemove(filePath, out var cts))
        {
            try
            {
                cts.Cancel();
                cts.Dispose();
            }
            catch
            {
                // Best-effort cancellation.
            }
        }
    }

    /// <summary>
    /// Cancels every pending processing request (used on stop/dispose).
    /// </summary>
    private void CancelAllPending()
    {
        foreach (var key in _pendingProcessing.Keys.ToList())
        {
            CancelPending(key);
        }
    }

    /// <summary>
    /// Records that the watcher itself just modified a path so that the resulting file-system
    /// events can be recognised as self-induced and ignored.
    /// </summary>
    private void MarkSelfInducedChange(string filePath)
    {
        _recentlyProcessed[filePath] = DateTime.UtcNow;
    }

    /// <summary>
    /// Returns true if the given path was modified by the watcher within the suppression window,
    /// meaning the event is the watcher's own change echoing back and should be ignored.
    /// </summary>
    private bool IsSelfInducedChange(string filePath)
    {
        PruneRecentlyProcessed(out var window);
        if (_recentlyProcessed.TryGetValue(filePath, out var when) && DateTime.UtcNow - when < window)
        {
            return true;
        }
        return false;
    }

    /// <summary>
    /// Removes expired entries from the self-induced-change tracker to keep it bounded.
    /// </summary>
    private void PruneRecentlyProcessed(out TimeSpan window)
    {
        var windowSeconds = Math.Max(_settings.WatcherFileStabilityDelaySeconds, 5);
        window = TimeSpan.FromSeconds(windowSeconds);
        var cutoff = DateTime.UtcNow - window;
        foreach (var kvp in _recentlyProcessed)
        {
            if (kvp.Value < cutoff)
            {
                _recentlyProcessed.TryRemove(kvp.Key, out _);
            }
        }
    }

    private static bool IsComicFile(string path)
    {
        return ComicFileExtensions.IsComicArchive(path) && !IsTemporaryFile(path);
    }

    /// <summary>
    /// Check if a file is a temporary file that should be ignored
    /// </summary>
    private static bool IsTemporaryFile(string path)
    {
        var fileName = Path.GetFileName(path);
        
        // Ignore files starting with .tmp_ (common temporary file pattern)
        if (fileName.StartsWith(".tmp_", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        
        // Ignore files with .tmp extension
        if (path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        
        return false;
    }

    /// <summary>
    /// Determine if a file should be processed based on watcher settings and file state
    /// </summary>
    private async Task<bool> ShouldProcessFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            var renameEnabled = _settings.WatcherEnableRename;
            var normalizeEnabled = _settings.WatcherEnableNormalize;

            // If both are enabled, only process if not already fully processed
            if (renameEnabled && normalizeEnabled)
            {
                var isProcessed = await _fileStore.IsFileProcessedAsync(filePath, cancellationToken);
                if (isProcessed)
                {
                    _logger.LogInformation(LoggingHelper.WithWatcherPrefix("File already fully processed (renamed and normalized), skipping: {File}"), filePath);
                    return false;
                }
                return true;
            }

            // If only normalize is enabled, skip if already normalized
            if (!renameEnabled && normalizeEnabled)
            {
                var isProcessed = await _fileStore.IsFileProcessedAsync(filePath, cancellationToken);
                var isNormalized = await _fileStore.IsFileNormalizedAsync(filePath, cancellationToken);
                if (isProcessed || isNormalized)
                {
                    _logger.LogInformation(LoggingHelper.WithWatcherPrefix("File already processed or normalized, skipping: {File}"), filePath);
                    return false;
                }
                return true;
            }

            // If only rename is enabled, skip if already renamed
            if (renameEnabled && !normalizeEnabled)
            {
                var isProcessed = await _fileStore.IsFileProcessedAsync(filePath, cancellationToken);
                var isRenamed = await _fileStore.IsFileRenamedAsync(filePath, cancellationToken);
                if (isProcessed || isRenamed)
                {
                    _logger.LogInformation(LoggingHelper.WithWatcherPrefix("File already processed or renamed, skipping: {File}"), filePath);
                    return false;
                }
                return true;
            }

            // If both are disabled, no processing needed
            _logger.LogInformation(LoggingHelper.WithWatcherPrefix("Both rename and normalize are disabled, skipping: {File}"), filePath);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, LoggingHelper.WithWatcherPrefix("Error checking if file should be processed: {File}"), filePath);
            return false;
        }
    }
}
