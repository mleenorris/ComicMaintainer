namespace ComicMaintainer.Core.Interfaces;

/// <summary>
/// Interface for broadcasting events to connected clients
/// </summary>
public interface IEventBroadcaster
{
    /// <summary>
    /// Broadcast job progress update
    /// </summary>
    /// <param name="jobId">The unique identifier for the job</param>
    /// <param name="status">The current status of the job (queued, running, completed, etc.)</param>
    /// <param name="processed">Total number of files attempted so far (successes + failures)</param>
    /// <param name="total">Total number of files in the job</param>
    /// <param name="success">Number of files successfully processed</param>
    /// <param name="errors">Number of files that failed processing</param>
    Task BroadcastJobUpdateAsync(Guid jobId, string status, int processed, int total, int success, int errors);
    
    /// <summary>
    /// Broadcast file processed event
    /// </summary>
    Task BroadcastFileProcessedAsync(string filename, bool success, string? error = null);
    
    /// <summary>
    /// Broadcast watcher status change
    /// </summary>
    Task BroadcastWatcherStatusAsync(bool running, bool enabled);
    
    /// <summary>
    /// Broadcast file list update (when files are added/removed from the file store)
    /// </summary>
    Task BroadcastFileListUpdateAsync();
}
