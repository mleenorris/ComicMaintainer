using ComicMaintainer.Core.Models;

namespace ComicMaintainer.Core.Interfaces;

/// <summary>
/// Service for processing comic files
/// </summary>
public interface IComicProcessorService
{
    /// <summary>
    /// Process a single comic file
    /// </summary>
    Task<bool> ProcessFileAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Process multiple comic files as a batch job
    /// </summary>
    Task<Guid> ProcessFilesAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get job status
    /// </summary>
    ProcessingJob? GetJob(Guid jobId);

    /// <summary>
    /// Get currently active job, if any
    /// </summary>
    ProcessingJob? GetActiveJob();

    /// <summary>
    /// Get metadata from a comic file
    /// </summary>
    Task<ComicMetadata?> GetMetadataAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Update metadata for a comic file
    /// </summary>
    Task<bool> UpdateMetadataAsync(string filePath, ComicMetadata metadata, CancellationToken cancellationToken = default);

    /// <summary>
    /// Rename multiple comic files based on metadata as a batch job
    /// </summary>
    Task<Guid> RenameFilesAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken = default);

    /// <summary>
    /// Normalize metadata for multiple comic files as a batch job
    /// </summary>
    Task<Guid> NormalizeFilesAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken = default);
}
