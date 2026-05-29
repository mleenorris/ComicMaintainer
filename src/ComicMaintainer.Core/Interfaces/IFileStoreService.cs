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

    Task<ComicFile?> GetFileAsync(string filePath, CancellationToken cancellationToken = default);

    Task ApplyUserMetadataEditAsync(string filePath, ComicMetadata patch, ComicMetadataFieldFlags lockFields, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ComicFile>> GetFilesNeedingBackfillAsync(int max, CancellationToken cancellationToken = default);

    Task MarkFileBackfilledAsync(string filePath, int version, CancellationToken cancellationToken = default);

    /// <summary>
    /// Flag the given tracked files as needing a metadata backfill by bumping
    /// each file's <c>MetadataVersion</c> so it becomes greater than its
    /// <c>WrittenMetadataVersion</c>. The scheduled metadata-backfill job then
    /// picks them up and rewrites each archive's ComicInfo.xml from the
    /// DB-authoritative metadata (including the resolved <c>&lt;Series&gt;</c>).
    /// Returns the number of files that were flagged. Duplicates are skipped.
    /// </summary>
    Task<int> MarkFilesNeedingBackfillAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken = default);

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
    /// Clear the renamed/normalized/processed flags for the given files in a single
    /// batched database update. Used to "un-mark" files so that subsequent rename
    /// and normalize passes will reprocess them, without requiring a full database
    /// reset. Files not present in the store are skipped silently. Returns the
    /// number of database rows that were actually updated.
    /// </summary>
    Task<int> ClearProcessedStatusAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken = default);

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
    /// Update the tracked file path from oldPath to newPath, preserving all processing state
    /// (IsRenamed, IsNormalized, IsDuplicate, IsRead, Metadata).  Used when a file is renamed
    /// so the database record follows the file without resetting its status.
    /// </summary>
    /// <param name="broadcastUpdate">
    /// When true (default) a file list update event is broadcast after a successful update.
    /// Callers performing bulk updates (e.g. combining folders containing many files) should
    /// pass false to avoid flooding clients with one event per file and emit a single
    /// broadcast at the end of the bulk operation instead.
    /// </param>
    Task UpdateFilePathAsync(string oldPath, string newPath, CancellationToken cancellationToken = default, bool broadcastUpdate = true);

    /// <summary>
    /// Remove stale entries from database where files no longer exist on disk
    /// </summary>
    Task<int> CleanupStaleEntriesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stamp the given file with the series-metadata-cache version it was
    /// most recently normalized against. The library-scan job compares this
    /// against the current cache record's <c>MetadataVersion</c> to detect
    /// stale files without re-reading every archive.
    /// </summary>
    Task SetFileSeriesMetadataVersionAsync(string filePath, int version, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the subset of <paramref name="filePaths"/> whose tracked
    /// <c>SeriesMetadataVersion</c> stamp is strictly less than the supplied
    /// <paramref name="currentVersion"/>. Used by the library-scan job to
    /// identify files needing re-normalization without re-reading every
    /// archive.
    /// </summary>
    Task<IReadOnlyList<string>> GetFilesWithStaleSeriesMetadataAsync(
        IEnumerable<string> filePaths,
        int currentVersion,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns aggregate folder summaries (one entry per directory) used by the
    /// incremental library view. Folders are sorted and paged using
    /// offset/limit semantics.
    /// </summary>
    Task<FolderSummariesResult> GetFolderSummariesAsync(
        string? filter = null,
        string? search = null,
        string? sort = "name",
        string? direction = "asc",
        int offset = 0,
        int limit = 100,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a paged slice of files projected to <see cref="FileDto"/>, with
    /// filtering, search, sort and paging applied directly in the database.
    /// Used by file-list endpoints to avoid materializing every <see cref="ComicFile"/>
    /// per request. Pass <paramref name="perPage"/> = -1 to return all matching rows.
    /// </summary>
    Task<PagedFilesResult> GetFilesPageAsync(
        string? filter = null,
        string? search = null,
        string? sort = "name",
        string? direction = "asc",
        int page = 1,
        int perPage = 100,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the files in the supplied folder (folder key relative to the
    /// watched directory) projected to <see cref="FileDto"/>, with filtering,
    /// search and sort applied in the database. The returned list is fully
    /// materialized (no paging) to match the existing folder files endpoint.
    /// </summary>
    Task<IReadOnlyList<FileDto>> GetFolderFilesAsync(
        string folderKey,
        string? filter = null,
        string? search = null,
        string? sort = "name",
        string? direction = "asc",
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the count of unmarked files (not processed and not duplicate).
    /// The result is cached for a short window to avoid recomputing it on every
    /// list endpoint request; the cache is invalidated whenever the file list
    /// changes (add/remove/mark/etc.).
    /// </summary>
    Task<int> GetUnmarkedCountAsync(CancellationToken cancellationToken = default);
}
