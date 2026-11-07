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

    /// <summary>
    /// Get up to <paramref name="max"/> files whose DB metadata has not been written back, ordered by
    /// file path. Pass the last path of the previous page as <paramref name="afterFilePath"/> to read
    /// the next page.
    /// </summary>
    Task<IReadOnlyList<ComicFile>> GetFilesNeedingBackfillAsync(int max, string? afterFilePath = null, CancellationToken cancellationToken = default);

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
    /// Mark multiple files as read or unread for the current user, in one transaction.
    /// Throws when the change could not be saved (nothing is saved then), including
    /// <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> fires.
    /// </summary>
    Task MarkFilesReadAsync(IEnumerable<string> filePaths, bool read, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks every tracked file in <paramref name="scope"/> read or unread for the current
    /// user with set-based statements in a single transaction (reading progress included),
    /// so a whole-library change needs neither a client-side path list nor a write per file.
    /// Nothing is saved when it fails or <paramref name="cancellationToken"/> fires; the
    /// exception propagates. Returns the number of tracked files in the scope, or 0 when no
    /// user is in scope.
    /// </summary>
    Task<int> MarkScopeReadAsync(FileReadScope scope, bool read, CancellationToken cancellationToken = default);

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
    /// so the database record follows the file without resetting its status. Identical paths
    /// are a no-op.
    /// </summary>
    /// <param name="broadcastUpdate">
    /// When true (default) a file list update event is broadcast after a successful update.
    /// Callers performing bulk updates (e.g. combining folders containing many files) should
    /// pass false to avoid flooding clients with one event per file and emit a single
    /// broadcast at the end of the bulk operation instead.
    /// </param>
    Task UpdateFilePathAsync(string oldPath, string newPath, CancellationToken cancellationToken = default, bool broadcastUpdate = true);

    /// <summary>
    /// Re-reads the on-disk size and last-write time of each tracked file in
    /// <paramref name="filePaths"/> and stores them as <c>FileSize</c> /
    /// <c>LastModified</c> (local time, matching <see cref="AddFileAsync"/>).
    /// Unlike <see cref="AddFileAsync"/> this touches nothing else and does
    /// not broadcast. Untracked or missing files are skipped. Returns the
    /// number of rows whose stats changed.
    /// </summary>
    Task<int> RefreshFileStatsAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cache the metadata just written into the file's archive, stamped with the file's
    /// stored size and modification time (call it after <see cref="RefreshFileStatsAsync"/>),
    /// so the library shows it without reading the archive again. A user edit not yet
    /// written to the archive is kept. Never throws: if the metadata cannot be cached, the
    /// background archive-metadata scan reads the archive instead.
    /// </summary>
    Task RecordArchiveMetadataAsync(string filePath, ComicMetadata metadata, CancellationToken cancellationToken = default);

    /// <summary>
    /// Up to <paramref name="max"/> tracked files, in id order after <paramref name="afterId"/>,
    /// whose cached archive metadata is missing or was read from a different version of the
    /// file (its stored size or modification time has changed since). Files with a user edit
    /// waiting to be written to the archive are left out.
    /// </summary>
    Task<IReadOnlyList<ArchiveMetadataScanCandidate>> GetFilesNeedingMetadataScanAsync(int afterId, int max, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cache the metadata the background scan read from <paramref name="candidate"/>'s
    /// archive. Returns false, recording nothing, when the row changed since it was selected
    /// (the file was modified, replaced or edited meanwhile); a later scan reads it again.
    /// </summary>
    Task<bool> RecordScannedArchiveMetadataAsync(ArchiveMetadataScanCandidate candidate, ComicMetadata metadata, CancellationToken cancellationToken = default);

    /// <summary>
    /// Remove stale entries from the database for files that no longer exist on disk,
    /// guarded so a missing or unmounted library can never wipe it: see
    /// <see cref="StaleCleanupResult"/> for the outcomes. Never throws; failures and
    /// cancellation are reported through <see cref="StaleCleanupResult.Status"/>.
    /// </summary>
    Task<StaleCleanupResult> CleanupStaleEntriesAsync(StaleCleanupOptions options, CancellationToken cancellationToken = default);

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
