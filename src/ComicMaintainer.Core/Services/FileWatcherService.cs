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
        _enabled = _settings.WatcherEnabled;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (!_enabled)
            {
                _logger.LogInformation("Watcher is disabled, not starting");
                return Task.CompletedTask;
            }

            if (_watcher != null && _watcher.EnableRaisingEvents)
            {
                _logger.LogInformation("Watcher is already running");
                return Task.CompletedTask;
            }

            if (!Directory.Exists(_settings.WatchedDirectory))
            {
                _logger.LogError("Watched directory does not exist: {Directory}", _settings.WatchedDirectory);
                return Task.CompletedTask;
            }

            _watcher = new FileSystemWatcher(_settings.WatchedDirectory)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                Filter = "*.*",
                IncludeSubdirectories = true
            };

            _watcher.Created += OnFileCreated;
            _watcher.Changed += OnFileChanged;
            _watcher.Renamed += OnFileRenamed;
            _watcher.Deleted += OnFileDeleted;

            _watcher.EnableRaisingEvents = true;
            _logger.LogInformation("File watcher started for directory: {Directory}", _settings.WatchedDirectory);
        }

        return Task.CompletedTask;
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

    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        if (!enabled)
        {
            StopAsync().Wait();
        }
        else if (enabled && !IsRunning)
        {
            StartAsync().Wait();
        }
    }

    private void OnFileCreated(object sender, FileSystemEventArgs e)
    {
        if (IsComicFile(e.FullPath))
        {
            _logger.LogInformation("File created: {Path}", e.FullPath);
            _ = Task.Run(async () =>
            {
                await _fileStore.AddFileAsync(e.FullPath);
                // Debounce and process
                await Task.Delay(TimeSpan.FromSeconds(30));
                await _processor.ProcessFileAsync(e.FullPath);
            });
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        if (IsComicFile(e.FullPath))
        {
            _logger.LogInformation("File changed: {Path}", e.FullPath);
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30));
                await _processor.ProcessFileAsync(e.FullPath);
            });
        }
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        if (IsComicFile(e.FullPath))
        {
            _logger.LogInformation("File renamed: {OldPath} -> {NewPath}", e.OldFullPath, e.FullPath);
            _ = Task.Run(async () =>
            {
                await _fileStore.RemoveFileAsync(e.OldFullPath);
                await _fileStore.AddFileAsync(e.FullPath);
            });
        }
    }

    private void OnFileDeleted(object sender, FileSystemEventArgs e)
    {
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
}
