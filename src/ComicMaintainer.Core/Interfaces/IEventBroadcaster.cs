namespace ComicMaintainer.Core.Interfaces;

/// <summary>
/// Interface for broadcasting events to connected clients
/// </summary>
public interface IEventBroadcaster
{
    /// <summary>
    /// Broadcast job progress update
    /// </summary>
    Task BroadcastJobUpdateAsync(Guid jobId, string status, int processed, int total, int success, int errors);
    
    /// <summary>
    /// Broadcast file processed event
    /// </summary>
    Task BroadcastFileProcessedAsync(string filename, bool success, string? error = null);
    
    /// <summary>
    /// Broadcast watcher status change
    /// </summary>
    Task BroadcastWatcherStatusAsync(bool running, bool enabled);
}
