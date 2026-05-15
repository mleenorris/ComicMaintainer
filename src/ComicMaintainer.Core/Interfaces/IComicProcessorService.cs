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
    /// Process a single comic file, optionally forcing reprocessing even when already marked complete.
    /// </summary>
    Task<bool> ProcessFileAsync(string filePath, bool forceReprocess, CancellationToken cancellationToken = default);

    /// <summary>
    /// Process multiple comic files as a batch job
    /// </summary>
    Task<Guid> ProcessFilesAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken = default);

    /// <summary>
    /// Process multiple comic files as a batch job, optionally forcing reprocessing.
    /// </summary>
    Task<Guid> ProcessFilesAsync(IEnumerable<string> filePaths, bool forceReprocess, CancellationToken cancellationToken = default);

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
    /// Get series-focused metadata, including alternate/grouping fields when available.
    /// </summary>
    Task<SeriesMetadata?> GetSeriesMetadataAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Update metadata for a comic file
    /// </summary>
    Task<bool> UpdateMetadataAsync(string filePath, ComicMetadata metadata, CancellationToken cancellationToken = default);

    /// <summary>
    /// Rename multiple comic files based on metadata as a batch job
    /// </summary>
    Task<Guid> RenameFilesAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken = default);

    /// <summary>
    /// Rename multiple comic files as a batch job, optionally forcing already renamed files to be evaluated again.
    /// </summary>
    Task<Guid> RenameFilesAsync(IEnumerable<string> filePaths, bool forceReprocess, CancellationToken cancellationToken = default);

    /// <summary>
    /// Normalize metadata for multiple comic files as a batch job
    /// </summary>
    Task<Guid> NormalizeFilesAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken = default);

    /// <summary>
    /// Normalize multiple comic files as a batch job, optionally forcing already normalized files to be updated again.
    /// </summary>
    Task<Guid> NormalizeFilesAsync(IEnumerable<string> filePaths, bool forceReprocess, CancellationToken cancellationToken = default);

    /// <summary>
    /// Update metadata for multiple comic files as a batch job
    /// </summary>
    Task<Guid> UpdateMetadataAsync(IEnumerable<string> filePaths, ComicMetadata metadata, CancellationToken cancellationToken = default);

    /// <summary>
    /// Normalize metadata and then rename multiple comic files as a single batch job.
    /// For each file, normalization is performed first (so the series name reflects the
    /// containing folder) and the rename step uses the freshly normalized metadata.
    /// Both phases force reprocessing so files whose state is already marked complete
    /// in the database are still updated to match the new folder.
    /// </summary>
    Task<Guid> NormalizeAndRenameFilesAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get all jobs
    /// </summary>
    IEnumerable<ProcessingJob> GetAllJobs();

    /// <summary>
    /// Delete a job from the job history
    /// </summary>
    bool DeleteJob(Guid jobId);

    /// <summary>
    /// Cancel a running job
    /// </summary>
    bool CancelJob(Guid jobId);
}
