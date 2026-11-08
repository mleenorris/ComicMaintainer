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
    /// <see cref="_lock"/>. <see cref="OnWatcherError"/> reads it without waiting for the lock to
    /// tell errors raised synchronously while a watcher starts apart from errors of a running
    /// watcher.
    /// </summary>
    private volatile FileSystemWatcher? _startingWatcher;

    /// <summary>
    /// The latest queued watcher status broadcast (guarded by <see cref="_statusLock"/>). Each
    /// broadcast runs after the previous one so clients see statuses in order. Statuses are queued
    /// under <see cref="_lock"/> together with the change they report, and nothing waits for their
    /// delivery, so slow clients cannot hold up starting, stopping or recreating the watcher.
    /// </summary>
    private Task _statusDelivery = Task.CompletedTask;
    private readonly object _statusLock = new();

    /// <summary>
    /// The latest queued store update for a directory change (guarded by
    /// <see cref="_directoryWorkLock"/>); each runs after the previous one, see
    /// <see cref="QueueDirectoryWork"/>.
    /// </summary>
    private Task _directoryWork = Task.CompletedTask;
    private readonly object _directoryWorkLock = new();

    /// <summary>
    /// The scan that catches up with the changes made while the application was not running,
    /// started once per process by <see cref="StartAsync"/>. Later scans wait for it, so a
    /// recovery rescan cannot take the files it has yet to add for new arrivals.
    /// </summary>
    private volatile Task _initialScan = Task.CompletedTask;

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
            catch (Exception ex) when (ex is not OutOfMemoryException)
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
                catch (Exception ex) when (ex is not OutOfMemoryException)
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
        bool started = false;
        var watchedDirectory = _settings.WatchedDirectory;
        bool watchedDirectoryExists = Directory.Exists(watchedDirectory);

        lock (_lock)
        {
            // "Enabled" tracks whether automatic processing (rename/normalize) is on. It no longer
            // gates the live file-system watcher: the watcher always monitors for new files so that
            // any file added to the watched directory is at least recorded in the file list, even
            // when both rename and normalize are turned off.
            _enabled = _settings.WatcherEnableRename || _settings.WatcherEnableNormalize;

            if (!watchedDirectoryExists)
            {
                _logger.LogError(LoggingHelper.WithWatcherPrefix("Watched directory does not exist: {Directory}"), watchedDirectory);
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

                StartWatcherLocked(watchedDirectory);
                started = true;

                // Broadcast watcher status change (only when the watcher actually started this
                // call to avoid spurious status events on repeated StartAsync calls).
                QueueStatusBroadcast(IsRunning);
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

        // Initialize file store from database before scanning files (only once).
        // Runs regardless of whether the live watcher is enabled.
        if (shouldInitialize)
        {
            await _fileStore.InitializeFromDatabaseAsync(cancellationToken);

            // Catch up with what changed while the application was not running: the in-memory
            // store is brought in line with the disk, and the files that arrived meanwhile or
            // were still queued when it stopped are queued for processing.
            _initialScan = Task.Run(async () => await ScanExistingFilesAsync(watchedDirectory, cancellationToken));
        }
        else if (started)
        {
            // The watcher was stopped, or rebound to another directory, after the store was
            // initialized: catch up with what it did not see while it was not watching.
            var previousScan = _initialScan;
            _ = Task.Run(async () =>
            {
                await previousScan;
                await ScanExistingFilesAsync(watchedDirectory, CancellationToken.None);
            });
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

        watcher.Created += (sender, e) => HandleWatcherEvent(sender, () => OnFileCreated(sender, e));
        watcher.Changed += (sender, e) => HandleWatcherEvent(sender, () => OnFileChanged(sender, e));
        watcher.Renamed += (sender, e) => HandleWatcherEvent(sender, () => OnFileRenamed(sender, e));
        watcher.Deleted += (sender, e) => HandleWatcherEvent(sender, () => OnFileDeleted(sender, e));
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
    /// Catches up with the changes made to <paramref name="directory"/> while the watcher was not
    /// watching it (the application was down, or the watcher stopped or watched another
    /// directory), which no file-system event will report.
    /// <para>
    /// Comic files the store does not track are added, unless they are tracked files moved
    /// meanwhile (see <see cref="MovedFileMatcher"/>), whose rows follow them. Files that arrived
    /// are queued for processing like files the live watcher sees arrive, as are the files that
    /// were still queued when the watcher stopped (<see cref="IFileStoreService.GetProcessingPendingFilesAsync"/>).
    /// Tracked files are otherwise left alone: one that is processed stays so, and one left
    /// unprocessed (it failed, or processing was off when it arrived) is not retried.
    /// </para>
    /// <para>
    /// A library the store tracks no file of yet (a first start, or a newly chosen watched
    /// directory) is an existing collection rather than new arrivals: its files are added but
    /// not processed, since processing rewrites and renames every archive.
    /// </para>
    /// </summary>
    private async Task ScanExistingFilesAsync(string directory, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation(LoggingHelper.WithWatcherPrefix("Starting incremental scan of directory: {Directory}"), directory);

            var comicFiles = Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories)
                .Where(IsComicFile)
                .ToList();

            _logger.LogInformation(LoggingHelper.WithWatcherPrefix("Found {Count} comic files on filesystem"), comicFiles.Count);

            // Snapshot the tracked files once instead of one FileExistsAsync call per file.
            var trackedFiles = await SnapshotTrackedFilesAsync(cancellationToken);
            var trackedPaths = new HashSet<string>(trackedFiles.Select(f => f.FilePath), StringComparer.Ordinal);
            var libraryIsNew = !trackedFiles.Any(f => LibraryPathValidator.IsSameOrUnder(f.FilePath, directory));
            var moves = new MovedFileMatcher(trackedFiles, comicFiles, directory);

            var newFileCount = 0;
            var movedFiles = new List<(string OldPath, string NewPath)>();
            var arrived = new List<string>();
            foreach (var file in comicFiles)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                try
                {
                    // Only add if not already in the store
                    if (trackedPaths.Contains(file))
                    {
                        continue;
                    }

                    if (moves.TryTake(file, out var oldPath))
                    {
                        movedFiles.Add((oldPath, file));
                        continue;
                    }

                    await _fileStore.AddFileAsync(file, cancellationToken);
                    newFileCount++;
                    if (!libraryIsNew)
                    {
                        arrived.Add(file);
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _logger.LogError(ex, LoggingHelper.WithWatcherPrefix("Error adding file during incremental scan: {File}"), file);
                }
            }

            await FollowMovedFilesAsync(movedFiles, cancellationToken);

            _logger.LogInformation(LoggingHelper.WithWatcherPrefix("Incremental scan completed. Added {Count} new files"), newFileCount);
            if (libraryIsNew && newFileCount > 0)
            {
                _logger.LogInformation(LoggingHelper.WithWatcherPrefix(
                    "No file of {Directory} was tracked before, so its {Count} existing files were added without processing them; process them from the file list when you want them renamed and normalized"),
                    directory, newFileCount);
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                await QueueMissedProcessingAsync(directory, arrived, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, LoggingHelper.WithWatcherPrefix("Error during incremental directory scan"));
        }
    }

    /// <summary>
    /// The files the store tracks, read once for a whole scan.
    /// </summary>
    private async Task<IReadOnlyList<ComicFile>> SnapshotTrackedFilesAsync(CancellationToken cancellationToken) =>
        (await _fileStore.GetAllFilesAsync(cancellationToken) ?? Enumerable.Empty<ComicFile>())
            .Where(f => !string.IsNullOrWhiteSpace(f.FilePath))
            .ToList();

    /// <summary>
    /// Moves the rows of tracked files that were moved while no event reported it to their new
    /// paths, with their processing state, user metadata edits and every user's read state.
    /// </summary>
    private async Task FollowMovedFilesAsync(IReadOnlyList<(string OldPath, string NewPath)> movedFiles, CancellationToken cancellationToken)
    {
        for (var i = 0; i < movedFiles.Count; i++)
        {
            var (oldPath, newPath) = movedFiles[i];
            try
            {
                // One file list update for the lot, not one per file.
                await _fileStore.UpdateFilePathAsync(oldPath, newPath, cancellationToken, broadcastUpdate: i == movedFiles.Count - 1);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogError(ex, LoggingHelper.WithWatcherPrefix("Error moving tracked file: {OldPath} -> {NewPath}"), oldPath, newPath);
            }
        }

        if (movedFiles.Count > 0)
        {
            _logger.LogInformation(LoggingHelper.WithWatcherPrefix("Followed {Count} tracked file(s) moved while their move was not seen"), movedFiles.Count);
        }
    }

    /// <summary>
    /// Queues the files that <paramref name="arrived"/> under <paramref name="directory"/>, and
    /// the files there that were still queued for processing when the watcher last stopped. The
    /// usual checks then skip a file that turns out to be processed or that the current
    /// settings do not process.
    /// </summary>
    private async Task QueueMissedProcessingAsync(string directory, IReadOnlyCollection<string> arrived, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> pending;
        try
        {
            pending = await _fileStore.GetProcessingPendingFilesAsync(cancellationToken) ?? [];
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && ex is not OperationCanceledException)
        {
            _logger.LogError(ex, LoggingHelper.WithWatcherPrefix("Error reading the files still queued for processing"));
            pending = [];
        }

        foreach (var file in arrived)
        {
            ScheduleProcessing(file);
        }

        var queuedPending = 0;
        var arrivedPaths = new HashSet<string>(arrived, StringComparer.Ordinal);
        foreach (var file in pending)
        {
            if (arrivedPaths.Contains(file) || !LibraryPathValidator.IsSameOrUnder(file, directory))
            {
                continue;
            }

            if (File.Exists(file))
            {
                // Already recorded as pending.
                ScheduleProcessing(file, recordPending: false);
                queuedPending++;
            }
            else
            {
                await RecordPendingAsync(file, false);
            }
        }

        if (arrived.Count > 0 || queuedPending > 0)
        {
            _logger.LogInformation(LoggingHelper.WithWatcherPrefix(
                "Queued {Arrived} file(s) that arrived and {Pending} file(s) that were still queued while the watcher was not running: {Directory}"),
                arrived.Count, queuedPending, directory);
        }
    }

    /// <summary>
    /// Pairs comic files found under paths the store does not track with tracked files missing
    /// from their paths, so that a file moved (typically with its series folder) while no event
    /// reported it keeps its row, and with it its processing state, user metadata edits and
    /// read state, instead of coming back as a new, unprocessed file. A pair must have the same
    /// file name, size and last-write time, and be the only candidate; anything less certain
    /// is left as a new file.
    /// </summary>
    private sealed class MovedFileMatcher
    {
        private readonly Dictionary<(string Name, long Size, long ModifiedSeconds), List<string>> _missing = new();

        public MovedFileMatcher(IEnumerable<ComicFile> trackedFiles, IEnumerable<string> filesOnDisk, string directory)
        {
            var onDisk = new HashSet<string>(filesOnDisk, StringComparer.FromComparison(LibraryPathValidator.PathComparison));
            foreach (var file in trackedFiles)
            {
                if (onDisk.Contains(file.FilePath) || !LibraryPathValidator.IsSameOrUnder(file.FilePath, directory))
                {
                    continue;
                }

                var key = (Path.GetFileName(file.FilePath), file.FileSize, ToSeconds(file.LastModified));
                if (!_missing.TryGetValue(key, out var paths))
                {
                    _missing[key] = paths = new List<string>(1);
                }

                paths.Add(file.FilePath);
            }
        }

        /// <summary>
        /// The tracked file that <paramref name="path"/> is the moved copy of, if there is exactly
        /// one; it is then no longer offered for another path.
        /// </summary>
        public bool TryTake(string path, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? oldPath)
        {
            oldPath = null;
            if (_missing.Count == 0)
            {
                return false;
            }

            FileInfo info;
            try
            {
                info = new FileInfo(path);
                if (!info.Exists)
                {
                    return false;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }

            var key = (info.Name, info.Length, ToSeconds(info.LastWriteTime));
            if (!_missing.TryGetValue(key, out var candidates) || candidates.Count != 1)
            {
                return false;
            }

            oldPath = candidates[0];
            _missing.Remove(key);
            return true;
        }

        // Some file systems keep write times to the second (or two), so compare seconds only.
        private static long ToSeconds(DateTime time) => time.Ticks / TimeSpan.TicksPerSecond;
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
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _logger.LogError(ex, LoggingHelper.WithWatcherPrefix("Error processing file from new directory: {File}"), file);
                }
            }
            
            _logger.LogInformation(LoggingHelper.WithWatcherPrefix("Directory scan completed: {Directory}"), directoryPath);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, LoggingHelper.WithWatcherPrefix("Error scanning directory: {Directory}"), directoryPath);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
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

            // Broadcast watcher status change
            QueueStatusBroadcast(false);
        }

        CancelRestart(restart);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Queues a watcher status broadcast behind the earlier ones, preserving their order, without
    /// waiting for its delivery. Callers hold <see cref="_lock"/> so that the order of the queued
    /// statuses matches the order of the changes they report.
    /// </summary>
    private void QueueStatusBroadcast(bool running)
    {
        if (_eventBroadcaster == null)
        {
            return;
        }

        lock (_statusLock)
        {
            _statusDelivery = _statusDelivery
                .ContinueWith(_ => BroadcastStatusAsync(running), CancellationToken.None,
                    TaskContinuationOptions.DenyChildAttach, TaskScheduler.Default)
                .Unwrap();
        }
    }

    private async Task BroadcastStatusAsync(bool running)
    {
        if (_eventBroadcaster == null)
        {
            return;
        }

        // The next status is only sent once this one has finished, so a client can never receive
        // them out of order; the broadcaster bounds how long a stalled client can hold it up.
        try
        {
            await _eventBroadcaster.BroadcastWatcherStatusAsync(running, _enabled);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
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

    /// <summary>
    /// Runs a file-system event handler, and treats an exception it throws as a watcher error.
    /// <para>
    /// FileSystemWatcher does not handle exceptions from its handlers the same way everywhere. On
    /// Linux it reports them through its Error event and stops; on Windows the exception is
    /// unhandled on the I/O thread and ends the whole process. Routing it to
    /// <see cref="OnWatcherError"/> here gives both platforms the Linux behavior: the error is
    /// logged and the watcher is recovered.
    /// </para>
    /// </summary>
    private void HandleWatcherEvent(object? sender, Action handle)
    {
        try
        {
            handle();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            OnWatcherError(sender!, new ErrorEventArgs(ex));
        }
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

            // Debounce and process (coalesces with any other pending events for this path). The
            // request is recorded below, once the file has a row to record it on; it is
            // registered first so that the recording finds it.
            ScheduleProcessing(e.FullPath, recordPending: false);

            _ = Task.Run(async () =>
            {
                try
                {
                    await _fileStore.AddFileAsync(e.FullPath);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _logger.LogError(ex, LoggingHelper.WithWatcherPrefix("Error adding created file to store: {Path}"), e.FullPath);
                    return;
                }

                await RecordPendingOnceTrackedAsync(e.FullPath);
            });
        }
    }

    /// <summary>
    /// Records that a file the watcher has just started tracking waits for processing, unless
    /// its processing request has already been served (see <see cref="ScheduleProcessing"/>).
    /// </summary>
    private Task RecordPendingOnceTrackedAsync(string filePath) =>
        _pendingProcessing.ContainsKey(filePath) ? RecordPendingAsync(filePath, true) : Task.CompletedTask;

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

            // Debounce and process the new path. Scheduling consults the (now updated) state only
            // after the stability delay, so a rename performed by the processor itself is skipped.
            // The request is recorded below, once the row is at the new path.
            ScheduleProcessing(e.FullPath, recordPending: false);

            _ = Task.Run(async () =>
            {
                try
                {
                    // Preserve processing state from the old path - avoids re-processing files
                    // that were renamed by the processor itself.
                    await _fileStore.UpdateFilePathAsync(e.OldFullPath, e.FullPath);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _logger.LogError(ex, LoggingHelper.WithWatcherPrefix("Error updating renamed file path: {Path}"), e.FullPath);
                    return;
                }

                await RecordPendingOnceTrackedAsync(e.FullPath);
            });
            return;
        }

        if (IsComicFile(e.OldFullPath) && !Directory.Exists(e.FullPath))
        {
            // A comic renamed to a name that is not a comic ("x.cbz" -> "x.cbz.bak") has left
            // the library just as if it had been deleted, unless the rename was the first step
            // of replacing it in place, see ForgetRenamedAwayFile.
            _logger.LogInformation(LoggingHelper.WithWatcherPrefix("File renamed to a non-comic name: {OldPath} -> {NewPath}"), e.OldFullPath, e.FullPath);
            ForgetRenamedAwayFile(e.OldFullPath);
            return;
        }

        // Anything else may be a directory (typically a series folder) renamed or moved within
        // the library. The OS reports only the directory itself, not the files under it, so
        // every tracked file under the old path is moved to the new one, keeping its processing
        // state, user metadata edits and every user's read state. For a plain non-comic file
        // nothing is tracked under its path and this changes nothing.
        var pendingUnderOld = CancelPendingUnder(e.OldFullPath);
        QueueDirectoryWork(async () =>
        {
            var moved = await _fileStore.MoveDirectoryAsync(e.OldFullPath, e.FullPath);
            if (!Directory.Exists(e.FullPath))
            {
                return;
            }

            _logger.LogInformation(LoggingHelper.WithWatcherPrefix("Directory renamed: {OldPath} -> {NewPath}; moved {Count} tracked file(s)"),
                e.OldFullPath, e.FullPath, moved);

            // Files that were waiting to settle under the old path are now under the new one.
            foreach (var path in pendingUnderOld)
            {
                ScheduleProcessing(Path.Join(e.FullPath, Path.GetRelativePath(e.OldFullPath, path)));
            }

            // Comics under the directory that were never tracked are new to the library.
            await TrackUntrackedFilesAsync(e.FullPath);
        });
    }

    private void OnFileDeleted(object sender, FileSystemEventArgs e)
    {
        // Ignore temporary files immediately to avoid unnecessary processing
        if (IsTemporaryFile(e.FullPath))
        {
            return;
        }

        _logger.LogInformation(LoggingHelper.WithWatcherPrefix("File deleted: {Path}"), e.FullPath);

        if (IsComicFile(e.FullPath))
        {
            ForgetFile(e.FullPath);
            return;
        }

        // The path may have been a directory, deleted or moved out of the library. The OS
        // reports only the directory itself when it is moved out, so every tracked file under it
        // is removed here; otherwise those rows would stay behind pointing at missing files.
        CancelPending(e.FullPath);
        CancelPendingUnder(e.FullPath);
        QueueDirectoryWork(() => _fileStore.RemoveDirectoryAsync(e.FullPath));
    }

    /// <summary>
    /// Stops tracking a comic renamed to a non-comic name once the file has settled, unless its
    /// path holds a file again by then.
    /// <para>
    /// Renaming a file aside is how many tools replace it safely: on Windows, File.Replace
    /// (which the processor and other taggers use to rewrite an archive) renames the original to
    /// its backup name and then moves the new archive into place, and the second step may be
    /// reported as a creation rather than a rename. The file never left the library, so its
    /// row (processing state, user metadata edits and field locks) must stay. The processor's
    /// own backup name is ignored outright (see <see cref="IsTemporaryFile"/>); this check
    /// covers every other tool.
    /// </para>
    /// </summary>
    private void ForgetRenamedAwayFile(string filePath)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, _settings.WatcherFileStabilityDelaySeconds)));

            if (_disposed)
            {
                return;
            }

            if (File.Exists(filePath))
            {
                _logger.LogInformation(LoggingHelper.WithWatcherPrefix(
                    "File was replaced in place rather than renamed away, keeping it: {Path}"), filePath);
                return;
            }

            ForgetFile(filePath);
        });
    }

    /// <summary>
    /// Stops tracking a comic file that no longer exists under its path.
    /// </summary>
    private void ForgetFile(string filePath)
    {
        // Drop any pending processing for a file that no longer exists.
        CancelPending(filePath);
        _recentlyProcessed.TryRemove(filePath, out _);

        _ = Task.Run(async () =>
        {
            try
            {
                await _fileStore.RemoveFileAsync(filePath);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogError(ex, LoggingHelper.WithWatcherPrefix("Error removing deleted file from store: {Path}"), filePath);
            }
        });
    }

    /// <summary>
    /// Runs a store update for a whole directory after the ones queued before it. Directory
    /// events must be applied in the order they happened (a folder renamed twice moves its
    /// files twice), and the OS raises them faster than the store applies them.
    /// </summary>
    private void QueueDirectoryWork(Func<Task> work)
    {
        lock (_directoryWorkLock)
        {
            _directoryWork = _directoryWork
                .ContinueWith(async _ =>
                {
                    try
                    {
                        await work();
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        _logger.LogError(ex, LoggingHelper.WithWatcherPrefix("Error updating tracked files for a directory change"));
                    }
                }, CancellationToken.None, TaskContinuationOptions.DenyChildAttach, TaskScheduler.Default)
                .Unwrap();
        }
    }

    /// <summary>
    /// Adds the comic files under <paramref name="directory"/> that the store does not track yet
    /// and queues them for processing; tracked files are left as they are.
    /// </summary>
    private async Task TrackUntrackedFilesAsync(string directory)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories).Where(IsComicFile))
            {
                if (_disposed)
                {
                    return;
                }

                if (await _fileStore.FileExistsAsync(file))
                {
                    continue;
                }

                await _fileStore.AddFileAsync(file);
                ScheduleProcessing(file);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, LoggingHelper.WithWatcherPrefix("Error tracking new files in directory: {Directory}"), directory);
        }
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

        // Only an error raised synchronously by enabling the watcher (on the thread that holds
        // _lock) is a start error; one from the notification thread is a running watcher's.
        var starting = sender != null && ReferenceEquals(sender, _startingWatcher) && Monitor.IsEntered(_lock);
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

            // Queued under the lock so a replacement's "running" status cannot overtake it.
            QueueStatusBroadcast(false);
        }

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
        try
        {
            var delay = RestartInitialDelay;
            var attempt = 0;
            while (true)
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

                    // A settings change since the directory was read may have found no watcher
                    // and queued a start for the new directory only; read it again so the old
                    // one is not started and left running.
                    if (!string.Equals(directory, _settings.WatchedDirectory, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (_watcher == null && available)
                    {
                        try
                        {
                            StartWatcherLocked(directory);
                            QueueStatusBroadcast(IsRunning);
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

                attempt++;
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
    /// Re-enumerates the watched directory to recover after a watcher buffer overflow (or other
    /// watcher error) where individual file events were dropped, or while a failed watcher was
    /// being recreated. Comic files the store does not track are files whose arrival was missed:
    /// they are added and queued for processing, except tracked files moved meanwhile, whose rows
    /// follow them (see <see cref="MovedFileMatcher"/>). Files whose processing was queued
    /// already are queued again. Tracked files are otherwise left alone, as at startup (see
    /// <see cref="ScanExistingFilesAsync"/>), so that a watcher error cannot set off processing
    /// of the files that were deliberately left unprocessed.
    /// </summary>
    private async Task RecoverFromMissedEventsAsync()
    {
        if (_disposed)
        {
            return;
        }

        // Until the startup scan has added what it found, files it has yet to add would look
        // like missed arrivals here.
        await _initialScan;

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

            var trackedFiles = await SnapshotTrackedFilesAsync(CancellationToken.None);
            var trackedPaths = new HashSet<string>(trackedFiles.Select(f => f.FilePath), StringComparer.Ordinal);
            var moves = new MovedFileMatcher(trackedFiles, comicFiles, directory);
            var movedFiles = new List<(string OldPath, string NewPath)>();

            foreach (var file in comicFiles)
            {
                if (_disposed)
                {
                    break;
                }

                if (trackedPaths.Contains(file))
                {
                    continue;
                }

                try
                {
                    if (moves.TryTake(file, out var oldPath))
                    {
                        movedFiles.Add((oldPath, file));
                        continue;
                    }

                    await _fileStore.AddFileAsync(file);
                    ScheduleProcessing(file);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _logger.LogError(ex, LoggingHelper.WithWatcherPrefix(
                        "Error re-queuing file during recovery rescan: {File}"), file);
                }
            }

            if (!_disposed)
            {
                await FollowMovedFilesAsync(movedFiles, CancellationToken.None);
                await QueueMissedProcessingAsync(directory, [], CancellationToken.None);
            }

            _logger.LogInformation(LoggingHelper.WithWatcherPrefix(
                "Recovery rescan completed: {Directory}"), directory);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
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
    /// <para>
    /// The request is also recorded in the store (<see cref="IFileStoreService.SetProcessingPendingAsync"/>)
    /// until it runs, so that one still waiting when the watcher stops is queued again at its
    /// next start (<see cref="ScanExistingFilesAsync"/>). Pass <paramref name="recordPending"/>
    /// false when the caller records it itself, after the file's row exists, or when it is
    /// recorded already.
    /// </para>
    /// </summary>
    private void ScheduleProcessing(string filePath, bool recordPending = true)
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
        var superseded = false;

        // Coalesce: install this request as the pending one for the path, cancelling any earlier
        // debounce so only the most recent event in a burst survives. (A factory can run more
        // than once under contention; the last one to run is the one that took effect.)
        _pendingProcessing.AddOrUpdate(filePath, _ =>
        {
            superseded = false;
            return cts;
        }, (_, existing) =>
        {
            superseded = true;
            try
            {
                existing.Cancel();
                existing.Dispose();
            }
            catch (Exception ex) when (ex is ObjectDisposedException or AggregateException)
            {
                // Best-effort cancellation of the superseded debounce.
            }
            return cts;
        });

        // A request that replaces another is recorded already.
        recordPending &= !superseded;

        var token = cts.Token;
        _ = Task.Run(async () =>
        {
            if (recordPending)
            {
                await RecordPendingAsync(filePath, true);
            }

            try
            {
                // Debounce window. A newer event for the same path cancels this token and re-arms
                // a fresh delay, so we only proceed once the file has been quiet for the full delay.
                await Task.Delay(TimeSpan.FromSeconds(_settings.WatcherFileStabilityDelaySeconds), token);
            }
            catch (OperationCanceledException)
            {
                // Superseded by a newer event, which will run instead (and is recorded as
                // pending), or the watcher stopped, in which case the record stays for its next
                // start, or the file was deleted or moved away.
                return;
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
                ScheduleProcessing(filePath, recordPending: false);
                return;
            }

            try
            {
                // The request is being served now. Clearing the record before processing rather
                // than after means a file the processor renames does not carry it to its new
                // path, and a file whose processing brings the application down is not retried
                // at every start.
                await RecordPendingAsync(filePath, false);

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
            catch (Exception ex) when (ex is not OutOfMemoryException)
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
    /// Records in the store whether <paramref name="filePath"/> waits for automatic processing,
    /// see <see cref="ScheduleProcessing"/>. Best effort: a failure costs at most a missed
    /// catch-up after a restart, so it is logged rather than allowed to stop the processing.
    /// </summary>
    private async Task RecordPendingAsync(string filePath, bool pending)
    {
        try
        {
            await _fileStore.SetProcessingPendingAsync(filePath, pending);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, LoggingHelper.WithWatcherPrefix("Error recording whether a file waits for processing: {File}"), filePath);
        }
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
            catch (Exception ex) when (ex is ObjectDisposedException or AggregateException)
            {
                // Best-effort cancellation.
            }
        }
    }

    /// <summary>
    /// Cancels the pending processing requests for every path under
    /// <paramref name="directory"/> and returns those paths.
    /// </summary>
    private List<string> CancelPendingUnder(string directory)
    {
        var prefix = Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar;
        var cancelled = _pendingProcessing.Keys
            .Where(path => path.StartsWith(prefix, LibraryPathValidator.PathComparison))
            .ToList();
        foreach (var path in cancelled)
        {
            CancelPending(path);
        }

        return cancelled;
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
        foreach (var kvp in _recentlyProcessed.Where(entry => entry.Value < cutoff))
        {
            _recentlyProcessed.TryRemove(kvp.Key, out _);
        }
    }

    private static bool IsComicFile(string path)
    {
        return ComicFileExtensions.IsComicArchive(path) && !IsTemporaryFile(path);
    }

    /// <summary>
    /// The suffix the processor appends to an archive's name for the backup it keeps while it
    /// replaces the archive (see ComicProcessorService.ReplaceFileWithRetryAsync).
    /// </summary>
    private const string ArchiveBackupSuffix = ".backup";

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

        // Ignore the backup the processor keeps while it rewrites an archive ("x.cbz.backup").
        // On Windows, File.Replace reports moving the archive to it as a rename, which is not
        // the archive leaving the library.
        if (fileName.EndsWith(ArchiveBackupSuffix, StringComparison.OrdinalIgnoreCase)
            && ComicFileExtensions.IsComicArchive(fileName[..^ArchiveBackupSuffix.Length]))
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
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, LoggingHelper.WithWatcherPrefix("Error checking if file should be processed: {File}"), filePath);
            return false;
        }
    }
}
