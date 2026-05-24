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
    /// Remove the ComicInfo.xml entry from a comic archive, leaving the file
    /// without any embedded metadata. Returns true on success (including when
    /// the archive had no ComicInfo.xml to begin with — the file is rewritten
    /// either way so callers can treat it as a no-op for the metadata layer).
    /// </summary>
    Task<bool> RemoveMetadataAsync(string filePath, CancellationToken cancellationToken = default);

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
    /// Remove the embedded ComicInfo.xml from multiple comic files as a batch
    /// job. Each successfully rewritten file is also marked as unprocessed so
    /// it will be re-evaluated on the next processing run.
    /// </summary>
    Task<Guid> RemoveMetadataFromFilesAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete multiple comic files (from disk and the file store) as a batch
    /// job. Emits the standard <c>job_updated</c> + <c>file_processed</c> SSE
    /// events so the UI's existing progress modal works unchanged.
    /// </summary>
    Task<Guid> DeleteFilesAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken = default);

    /// <summary>
    /// Generic background job entry point used by callers that need to run a
    /// custom per-item operation through the standard job pipeline (i.e.
    /// surface progress + completion via <c>job_updated</c> / <c>file_processed</c>
    /// SSE events and the existing UI progress modal).
    /// </summary>
    /// <param name="operationName">Stable identifier used in log messages.</param>
    /// <param name="trackedItems">
    ///   The list of items the job will iterate over. Typically file paths,
    ///   but can be any string identifier — the job's <see cref="ProcessingJob.Files"/>
    ///   list mirrors this collection so the UI can render per-item progress.
    /// </param>
    /// <param name="itemOperation">
    ///   Per-item operation invoked once per <paramref name="trackedItems"/>
    ///   entry. Returning <c>false</c> increments the job's failure count and
    ///   records <paramref name="failureMessage"/> as the error for that item.
    /// </param>
    /// <param name="failureMessage">Error string recorded when <paramref name="itemOperation"/> returns false.</param>
    /// <param name="postLoopAsync">
    ///   Optional cleanup / follow-up work that runs after the per-item loop
    ///   completes but before the job is marked <c>Completed</c>. Exceptions
    ///   from this hook flip the job to <c>Failed</c>.
    /// </param>
    Task<Guid> RunCustomBatchJobAsync(
        string operationName,
        IEnumerable<string> trackedItems,
        Func<string, CancellationToken, Task<bool>> itemOperation,
        string failureMessage,
        Func<CancellationToken, Task>? postLoopAsync = null,
        CancellationToken cancellationToken = default);

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
