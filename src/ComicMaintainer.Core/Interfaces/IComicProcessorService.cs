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
    /// Returns null without opening the file when its path resolves outside the
    /// watched and duplicate directories.
    /// </summary>
    Task<SeriesMetadata?> GetSeriesMetadataAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies the fields of <paramref name="metadata"/> that carry a value as a metadata edit
    /// (see <see cref="ApplyMetadataEditAsync"/>); fields left empty are kept as they are.
    /// </summary>
    Task<bool> UpdateMetadataAsync(string filePath, ComicMetadata metadata, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies a metadata edit to one file: the <paramref name="fields"/> of
    /// <paramref name="metadata"/> are merged into the archive's ComicInfo.xml, an empty value
    /// removes its element, and every other element is kept. When the archive is not rewritten
    /// (a CBR/RAR/ZIP while converting them to CBZ is turned off) the edit is recorded in the
    /// database instead.
    /// </summary>
    Task<MetadataWriteResult> ApplyMetadataEditAsync(
        string filePath,
        ComicMetadata metadata,
        ComicMetadataFieldFlags fields,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Like <see cref="ApplyMetadataEditAsync"/>, but only ever writes the archive: an archive
    /// that is not rewritten is reported as <see cref="MetadataWriteOutcome.NotWritable"/> and
    /// nothing is recorded. Used to copy edits already stored in the database into archives.
    /// </summary>
    Task<MetadataWriteResult> WriteMetadataAsync(
        string filePath,
        ComicMetadata metadata,
        ComicMetadataFieldFlags fields,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Remove the ComicInfo.xml entry from a comic archive, leaving the file
    /// without any embedded metadata. Succeeds, leaving the archive untouched
    /// (<see cref="MetadataWriteOutcome.Unchanged"/>), when it had no
    /// ComicInfo.xml to begin with. A CBR, RAR or ZIP becomes a new CBZ
    /// (<see cref="MetadataWriteOutcome.ConvertedToCbz"/>, at the returned path);
    /// while converting them is turned off the removal fails and the archive is
    /// not touched. Fails without touching the file when its path resolves
    /// outside the watched and duplicate directories.
    /// </summary>
    Task<MetadataWriteResult> RemoveMetadataAsync(string filePath, CancellationToken cancellationToken = default);

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
    /// Applies the fields of <paramref name="metadata"/> that carry a value to multiple comic
    /// files as a batch job; fields left empty are kept as they are.
    /// </summary>
    Task<Guid> UpdateMetadataAsync(IEnumerable<string> filePaths, ComicMetadata metadata, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies the <paramref name="fields"/> of <paramref name="metadata"/> as a metadata edit
    /// (see <see cref="ApplyMetadataEditAsync"/>) to multiple comic files as a batch job. Every
    /// other field of each file is kept as it is.
    /// </summary>
    Task<Guid> UpdateMetadataAsync(
        IEnumerable<string> filePaths,
        ComicMetadata metadata,
        ComicMetadataFieldFlags fields,
        CancellationToken cancellationToken = default);

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
    ///   It runs without the per-file <see cref="IFileOperationLock"/>: an item
    ///   that moves, rewrites or deletes an archive itself must hold that file's
    ///   lock while it does, and must not call a public file operation of this
    ///   service on the same file meanwhile (the lock is not reentrant).
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
    Task<bool> DeleteJobAsync(Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancel a running job
    /// </summary>
    bool CancelJob(Guid jobId);

    /// <summary>
    /// Add jobs recovered from durable storage to the in-memory job list, so the API can still
    /// answer for jobs that were created before the process restarted. Jobs already present in
    /// memory are left untouched.
    /// </summary>
    void RestoreJobs(IEnumerable<ProcessingJob> jobs);
}
