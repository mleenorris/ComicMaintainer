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
    private FileSystemWatcher? _watcher;
    private bool _enabled;
    private readonly object _lock = new();
    private bool _initialized = false;

    public bool IsRunning => _watcher?.EnableRaisingEvents ?? false;

    public FileWatcherService(
        IOptions<AppSettings> settings,
        ILogger<FileWatcherService> logger,
        IFileStoreService fileStore,
        IComicProcessorService processor)
    {
        _settings = settings.Value;
        _logger = logger;
        _fileStore = fileStore;
        _processor = processor;
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
                _logger.LogInformation("Watcher is disabled (both rename and normalize are disabled), not starting");
                return;
            }

            if (_watcher != null && _watcher.EnableRaisingEvents)
            {
                _logger.LogInformation("Watcher is already running");
                return;
            }

            if (!Directory.Exists(_settings.WatchedDirectory))
            {
                _logger.LogError("Watched directory does not exist: {Directory}", _settings.WatchedDirectory);
                return;
            }

            _watcher = new FileSystemWatcher(_settings.WatchedDirectory)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                Filter = "*.*",
                IncludeSubdirectories = true
            };

            _watcher.Created += OnFileCreated;
            _watcher.Changed += OnFileChanged;
            _watcher.Renamed += OnFileRenamed;
            _watcher.Deleted += OnFileDeleted;

            _watcher.EnableRaisingEvents = true;
            _logger.LogInformation("File watcher started for directory: {Directory}", _settings.WatchedDirectory);
            
            // Set flag to initialize outside the lock
            if (!_initialized)
            {
                shouldInitialize = true;
                _initialized = true;
            }
        }
        
        // Initialize file store from database before scanning files (only once)
        if (shouldInitialize)
        {
            await _fileStore.InitializeFromDatabaseAsync(cancellationToken);
        }
        
        // Perform initial scan of existing files
        _ = Task.Run(async () => await ScanExistingFilesAsync(cancellationToken));
    }
    
    /// <summary>
    /// Scans the watched directory for existing comic files and adds them to the file store
    /// Only scans for new files not already in the database
    /// </summary>
    private async Task ScanExistingFilesAsync(CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Starting incremental scan of directory: {Directory}", _settings.WatchedDirectory);
            
            var comicFiles = Directory.EnumerateFiles(_settings.WatchedDirectory, "*.*", SearchOption.AllDirectories)
                .Where(IsComicFile)
                .ToList();
            
            _logger.LogInformation("Found {Count} comic files on filesystem", comicFiles.Count);
            
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
                    _logger.LogError(ex, "Error adding file during incremental scan: {File}", file);
                }
            }
            
            _logger.LogInformation("Incremental scan completed. Added {Count} new files", newFileCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during incremental directory scan");
        }
    }

    /// <summary>
    /// Scans a specific directory for comic files and processes them
    /// </summary>
    private async Task ScanDirectoryAsync(string directoryPath, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Scanning directory for comic files: {Directory}", directoryPath);
            
            if (!Directory.Exists(directoryPath))
            {
                _logger.LogWarning("Directory no longer exists: {Directory}", directoryPath);
                return;
            }
            
            var comicFiles = Directory.EnumerateFiles(directoryPath, "*.*", SearchOption.AllDirectories)
                .Where(IsComicFile)
                .ToList();
            
            _logger.LogInformation("Found {Count} comic files in new directory: {Directory}", comicFiles.Count, directoryPath);
            
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
                    _logger.LogError(ex, "Error processing file from new directory: {File}", file);
                }
            }
            
            _logger.LogInformation("Directory scan completed: {Directory}", directoryPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error scanning directory: {Directory}", directoryPath);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
                _watcher = null;
                _logger.LogInformation("File watcher stopped");
            }
        }

        return Task.CompletedTask;
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
            _logger.LogInformation("Directory created: {Path}, scanning for comic files", e.FullPath);
            _ = Task.Run(async () =>
            {
                // Give the system time to finish copying files into the directory
                await Task.Delay(TimeSpan.FromSeconds(_settings.WatcherDirectoryScanDelaySeconds));
                await ScanDirectoryAsync(e.FullPath);
            });
        }
        else if (IsComicFile(e.FullPath))
        {
            _logger.LogInformation("File created: {Path}", e.FullPath);
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
            _logger.LogInformation("File changed: {Path}", e.FullPath);
            _ = Task.Run(async () =>
            {
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
            _logger.LogInformation("File renamed: {OldPath} -> {NewPath}", e.OldFullPath, e.FullPath);
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
                    _logger.LogError(ex, "Error processing renamed file: {Path}", e.FullPath);
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
        
        _logger.LogInformation("File deleted: {Path}", e.FullPath);
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
                    _logger.LogInformation("File already fully processed (renamed and normalized), skipping: {File}", filePath);
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
                    _logger.LogInformation("File already processed or normalized, skipping: {File}", filePath);
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
                    _logger.LogInformation("File already processed or renamed, skipping: {File}", filePath);
                    return false;
                }
                return true;
            }

            // If both are disabled, no processing needed
            _logger.LogInformation("Both rename and normalize are disabled, skipping: {File}", filePath);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking if file should be processed: {File}", filePath);
            return false;
        }
    }
}
