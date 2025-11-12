using ComicMaintainer.Core.Models;

namespace ComicMaintainer.Core.Interfaces;

/// <summary>
/// Service for managing file storage and tracking
/// </summary>
public interface IFileStoreService
{
    /// <summary>
    /// Get all comic files
    /// </summary>
    Task<IEnumerable<ComicFile>> GetAllFilesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get filtered files based on status
    /// </summary>
    Task<IEnumerable<ComicFile>> GetFilteredFilesAsync(string? filter = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Add a file to the store
    /// </summary>
    Task AddFileAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Remove a file from the store
    /// </summary>
    Task RemoveFileAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// DEPRECATED: Mark a file as processed (only when both renamed and normalized)
    /// Processed status is now computed automatically from renamed and normalized states.
    /// This method is kept for backward compatibility but does nothing.
    /// </summary>
    [Obsolete("Processed status is now computed from renamed and normalized states. Use MarkFileRenamedAsync and MarkFileNormalizedAsync instead.")]
    Task MarkFileProcessedAsync(string filePath, bool processed, CancellationToken cancellationToken = default);

    /// <summary>
    /// Mark a file as renamed
    /// </summary>
    Task MarkFileRenamedAsync(string filePath, bool renamed, CancellationToken cancellationToken = default);

    /// <summary>
    /// Mark a file as normalized
    /// </summary>
    Task MarkFileNormalizedAsync(string filePath, bool normalized, CancellationToken cancellationToken = default);

    /// <summary>
    /// Mark a file as duplicate
    /// </summary>
    Task MarkFileDuplicateAsync(string filePath, bool duplicate, CancellationToken cancellationToken = default);

    /// <summary>
    /// Mark a file as read
    /// </summary>
    Task MarkFileReadAsync(string filePath, bool read, CancellationToken cancellationToken = default);

    /// <summary>
    /// Mark multiple files as read or unread
    /// </summary>
    Task MarkFilesReadAsync(IEnumerable<string> filePaths, bool read, CancellationToken cancellationToken = default);

    /// <summary>
    /// Save reading progress (current page) for a comic file
    /// </summary>
    Task SaveReadingProgressAsync(string filePath, int currentPage, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get reading progress (current page) for a comic file
    /// </summary>
    Task<int> GetReadingProgressAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get file count statistics
    /// </summary>
    Task<(int total, int processed, int unprocessed, int duplicates)> GetFileCountsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Initialize file store by loading existing processed/duplicate status from database
    /// </summary>
    Task InitializeFromDatabaseAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if a file is already marked as processed
    /// </summary>
    Task<bool> IsFileProcessedAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if a file is already marked as renamed
    /// </summary>
    Task<bool> IsFileRenamedAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if a file is already marked as normalized
    /// </summary>
    Task<bool> IsFileNormalizedAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if a file exists in the store
    /// </summary>
    Task<bool> FileExistsAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Remove stale entries from database where files no longer exist on disk
    /// </summary>
    Task<int> CleanupStaleEntriesAsync(CancellationToken cancellationToken = default);
}
