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
    /// Enable or disable the watcher
    /// </summary>
    void SetEnabled(bool enabled);
}
