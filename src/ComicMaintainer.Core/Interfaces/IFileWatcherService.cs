using ComicMaintainer.Core.Models;

namespace ComicMaintainer.Core.Interfaces;

/// <summary>
/// Service for watching file system changes
/// </summary>
public interface IFileWatcherService
{
    /// <summary>
    /// Starts watching the configured directory
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops watching the directory
    /// </summary>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current watcher status
    /// </summary>
    bool IsRunning { get; }

    /// <summary>
    /// DEPRECATED: Enable or disable the watcher
    /// Direct watcher control is deprecated. Use WatcherEnableRename and WatcherEnableNormalize settings instead.
    /// </summary>
    [Obsolete("Direct watcher enable/disable is deprecated. Use WatcherEnableRename and WatcherEnableNormalize settings instead.")]
    void SetEnabled(bool enabled);

    /// <summary>
    /// Marks a file path as having just been modified by the application itself
    /// (for example when embedding a series cover into an archive) so the
    /// resulting file-system events are treated as self-induced and do not
    /// trigger reprocessing. The suppression is time-bounded by the watcher's
    /// stability-delay window.
    /// </summary>
    void SuppressProcessing(string filePath);
}
