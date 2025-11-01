using ComicMaintainer.Core.Models;

namespace ComicMaintainer.Core.Interfaces;

/// <summary>
/// Service for managing processing history
/// </summary>
public interface IProcessingHistoryService
{
    /// <summary>
    /// Get processing history with pagination
    /// </summary>
    Task<(IEnumerable<ProcessingHistoryEntry> history, int total)> GetHistoryAsync(
        int limit = 50, 
        int offset = 0, 
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Add a processing history entry
    /// </summary>
    Task AddHistoryEntryAsync(
        ProcessingHistoryEntry entry, 
        CancellationToken cancellationToken = default);
}
