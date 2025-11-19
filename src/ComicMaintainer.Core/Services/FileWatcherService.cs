using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Service for watching file system changes in the comic directory
/// </summary>
public class FileWatcherService : IFileWatcherService
{
    private readonly AppSettings _settings;
    private readonly ILogger<FileWatcherService> _logger;
    private readonly IFileStoreService _fileStore;
    private readonly IComicProcessorService _processor;
    private readonly IEventBroadcaster? _eventBroadcaster;
    private List<FileSystemWatcher> _watchers = new();
    private bool _enabled;
    private readonly object _lock = new();
    private bool _initialized = false;

    public bool IsRunning => _watchers.Any(w => w.EnableRaisingEvents);

    public FileWatcherService(
        IOptions<AppSettings> settings,
        ILogger<FileWatcherService> logger,
        IFileStoreService fileStore,
        IComicProcessorService processor,
        IEventBroadcaster? eventBroadcaster = null)
    {
        _settings = settings.Value;
        _logger = logger;
        _fileStore = fileStore;
        _processor = processor;
        _eventBroadcaster = eventBroadcaster;
        // Watcher is enabled if either rename or normalize is enabled
        _enabled = _settings.WatcherEnableRename || _settings.WatcherEnableNormalize;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        bool shouldInitialize = false;
        
        lock (_lock)
        {
            // Watcher is enabled if either rename or normalize is enabled
            _enabled = _settings.WatcherEnableRename || _settings.WatcherEnableNormalize;
            
            if (!_enabled)
            {
                _logger.LogInformation(LoggingHelper.WithWatcherPrefix("Watcher is disabled (both rename and normalize are disabled), not starting"));
                return;
            }

            if (_watchers.Any(w => w.EnableRaisingEvents))
            {
                _logger.LogInformation(LoggingHelper.WithWatcherPrefix("Watcher is already running"));
                return;
            }

            // Get all directories to watch
            var directoriesToWatch = _settings.GetAllWatchedDirectories().ToList();
            
            if (!directoriesToWatch.Any())
            {
                _logger.LogError(LoggingHelper.WithWatcherPrefix("No directories configured to watch"));
                return;
            }

            // Create a watcher for each directory
            foreach (var directory in directoriesToWatch)
            {
                if (!Directory.Exists(directory))
                {
                    _logger.LogWarning(LoggingHelper.WithWatcherPrefix("Watched directory does not exist, skipping: {Directory}"), directory);
                    continue;
                }

                var watcher = new FileSystemWatcher(directory)
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    Filter = "*.*",
                    IncludeSubdirectories = true
                };

                watcher.Created += OnFileCreated;
                watcher.Changed += OnFileChanged;
                watcher.Renamed += OnFileRenamed;
                watcher.Deleted += OnFileDeleted;

                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
                
                _logger.LogInformation(LoggingHelper.WithWatcherPrefix("File watcher started for directory: {Directory}"), directory);
            }
            
            if (!_watchers.Any())
            {
                _logger.LogError(LoggingHelper.WithWatcherPrefix("Failed to start any watchers - no valid directories found"));
                return;
            }
            
            // Set flag to initialize outside the lock
            if (!_initialized)
            {
                shouldInitialize = true;
                _initialized = true;
            }
        }
        
        // Broadcast watcher status change
        if (_eventBroadcaster != null)
        {
            try
            {
                await _eventBroadcaster.BroadcastWatcherStatusAsync(IsRunning, _enabled);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to broadcast watcher status change");
            }
        }
        
        // Initialize file store from database before scanning files (only once)
        if (shouldInitialize)
        {
            await _fileStore.InitializeFromDatabaseAsync(cancellationToken);
        }
        
        // Perform initial scan of existing files in all watched directories
        _ = Task.Run(async () => await ScanExistingFilesAsync(cancellationToken));
    }
    
    /// <summary>
    /// Scans the watched directories for existing comic files and adds them to the file store
    /// Only scans for new files not already in the database
    /// </summary>
    private async Task ScanExistingFilesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var directoriesToWatch = _settings.GetAllWatchedDirectories().ToList();
            _logger.LogInformation(LoggingHelper.WithWatcherPrefix("Starting incremental scan of {Count} directories"), directoriesToWatch.Count);
            
            var totalNewFileCount = 0;
            foreach (var directory in directoriesToWatch)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;
                    
                if (!Directory.Exists(directory))
                {
                    _logger.LogWarning(LoggingHelper.WithWatcherPrefix("Directory does not exist, skipping scan: {Directory}"), directory);
                    continue;
                }
                
                var comicFiles = Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories)
                    .Where(IsComicFile)
                    .ToList();
                
                _logger.LogInformation(LoggingHelper.WithWatcherPrefix("Found {Count} comic files in {Directory}"), comicFiles.Count, directory);
                
                // Check each file individually to avoid loading all files into memory
                var newFileCount = 0;
                foreach (var file in comicFiles)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;
                        
                    try
                    {
                        // Only add if not already in the store
                        if (!await _fileStore.FileExistsAsync(file, cancellationToken))
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
                
                totalNewFileCount += newFileCount;
                _logger.LogInformation(LoggingHelper.WithWatcherPrefix("Incremental scan of {Directory} completed. Added {Count} new files"), directory, newFileCount);
            }
            
            _logger.LogInformation(LoggingHelper.WithWatcherPrefix("All directory scans completed. Total new files added: {Count}"), totalNewFileCount);
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
                    
                    // Check if file should be processed based on settings and current state
                    var shouldProcess = await ShouldProcessFileAsync(file, cancellationToken);
                    if (!shouldProcess)
                    {
                        continue;
                    }
                    
                    // Process each file after a delay to avoid overwhelming the system
                    await Task.Delay(TimeSpan.FromSeconds(_settings.WatcherFileStabilityDelaySeconds), cancellationToken);
                    await _processor.ProcessFileAsync(file, cancellationToken);
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
        lock (_lock)
        {
            foreach (var watcher in _watchers)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            _watchers.Clear();
            _logger.LogInformation(LoggingHelper.WithWatcherPrefix("All file watchers stopped"));
        }

        // Broadcast watcher status change
        if (_eventBroadcaster != null)
        {
            try
            {
                await _eventBroadcaster.BroadcastWatcherStatusAsync(false, _enabled);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to broadcast watcher status change");
            }
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
                await _fileStore.AddFileAsync(e.FullPath);
                
                // Check if file should be processed based on settings and current state
                var shouldProcess = await ShouldProcessFileAsync(e.FullPath);
                if (!shouldProcess)
                {
                    return;
                }
                
                // Debounce and process
                await Task.Delay(TimeSpan.FromSeconds(_settings.WatcherFileStabilityDelaySeconds));
                await _processor.ProcessFileAsync(e.FullPath);
            });
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
            _ = Task.Run(async () =>
            {
                // Check if file should be processed based on settings and current state
                var shouldProcess = await ShouldProcessFileAsync(e.FullPath);
                if (!shouldProcess)
                {
                    return;
                }
                
                await Task.Delay(TimeSpan.FromSeconds(_settings.WatcherFileStabilityDelaySeconds));
                await _processor.ProcessFileAsync(e.FullPath);
            });
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
            _ = Task.Run(async () =>
            {
                try
                {
                    await _fileStore.RemoveFileAsync(e.OldFullPath);
                    await _fileStore.AddFileAsync(e.FullPath);
                    
                    // Check if file should be processed based on settings and current state
                    var shouldProcess = await ShouldProcessFileAsync(e.FullPath);
                    if (!shouldProcess)
                    {
                        return;
                    }
                    
                    // Process the renamed file after a delay
                    await Task.Delay(TimeSpan.FromSeconds(_settings.WatcherFileStabilityDelaySeconds));
                    await _processor.ProcessFileAsync(e.FullPath);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, LoggingHelper.WithWatcherPrefix("Error processing renamed file: {Path}"), e.FullPath);
                }
            });
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
        _ = Task.Run(async () =>
        {
            await _fileStore.RemoveFileAsync(e.FullPath);
        });
    }

    private static bool IsComicFile(string path)
    {
        return ComicFileExtensions.IsComicArchive(path);
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
