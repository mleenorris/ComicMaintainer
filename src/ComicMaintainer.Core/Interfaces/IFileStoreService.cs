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
    /// Mark a file as processed
    /// </summary>
    Task MarkFileProcessedAsync(string filePath, bool processed, CancellationToken cancellationToken = default);

    /// <summary>
    /// Mark a file as duplicate
    /// </summary>
    Task MarkFileDuplicateAsync(string filePath, bool duplicate, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get file count statistics
    /// </summary>
    Task<(int total, int processed, int unprocessed, int duplicates)> GetFileCountsAsync(CancellationToken cancellationToken = default);
}
