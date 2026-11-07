using ComicMaintainer.Core.Models;

namespace ComicMaintainer.Core.Interfaces;

/// <summary>
/// Service for managing processing history
/// </summary>
public interface IProcessingHistoryService
{
    /// <summary>
    /// Get processing history with pagination, newest first. The total is counted once and
    /// then kept up to date as entries are added or pruned, instead of counting the whole
    /// table for every page.
    /// </summary>
    Task<(IEnumerable<ProcessingHistoryEntry> history, int total)> GetHistoryAsync(
        int limit = 50, 
        int offset = 0, 
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Add a processing history entry. A successful entry that records a no-op (it carries a
    /// note such as "File already normalized" and its before and after values are identical)
    /// is not stored: the history records changes and failures, not every visit to a file.
    /// </summary>
    Task AddHistoryEntryAsync(
        ProcessingHistoryEntry entry, 
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete entries older than <paramref name="retentionDays"/> days (0 keeps entries
    /// regardless of age) and all but the newest <paramref name="maxEntries"/> entries
    /// (0 disables the cap). Deletes in small batches so a large backlog never holds the
    /// database write lock for long. Returns the number of entries deleted.
    /// </summary>
    Task<int> PruneAsync(int retentionDays, int maxEntries, CancellationToken cancellationToken = default);
}
