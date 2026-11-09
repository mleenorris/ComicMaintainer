using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Data;
using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;

namespace ComicMaintainer.Core.Services;

public partial class FileStoreService
{
    public async Task AddFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        // Validate path is within allowed directories
        if (!IsPathWithinAllowedDirectories(filePath))
        {
            _logger.LogWarning("Attempted to add file outside allowed directories: {FilePath}", SanitizeForLogging(filePath));
            return;
        }

        if (!File.Exists(filePath))
            return;

        var fileInfo = new FileInfo(filePath);

        // The database row is the record: an existing row keeps its processing state,
        // CreatedAt, metadata and version stamps (a watcher re-scan must not reset them),
        // and only its on-disk details are refreshed. The in-memory copy is updated from
        // the committed row afterwards, so a failed write cannot leave the two disagreeing.
        // (Read state is per-user and keyed by path in UserFileReadStatuses, so re-adding a
        // file cannot affect it.)
        ComicFileEntity? committed = null;
        try
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

            var entity = await dbContext.ComicFiles
                .FirstOrDefaultAsync(e => e.FilePath == filePath, cancellationToken);

            if (entity == null)
            {
                // Create new entity
                var newEntity = CreateFileEntity(filePath);
                try
                {
                    dbContext.ComicFiles.Add(newEntity);
                    await dbContext.SaveChangesAsync(cancellationToken);
                    committed = newEntity;
                    _logger.LogDebug("Added new file to database: {FilePath}", SanitizeForLogging(filePath));
                }
                catch (DbUpdateException ex) when (ex.InnerException is Microsoft.Data.Sqlite.SqliteException sqliteEx &&
                                                    sqliteEx.SqliteErrorCode == 19) // UNIQUE constraint
                {
                    // Race condition: entity was created by another thread between our check and insert
                    // Retry by fetching and updating the existing entity
                    _logger.LogDebug("File entity already exists (race condition), retrying update for {FilePath}", SanitizeForLogging(filePath));
                    // Stop tracking the failed insert, otherwise the retry's SaveChanges re-inserts it.
                    dbContext.Entry(newEntity).State = EntityState.Detached;
                    entity = await dbContext.ComicFiles.FirstOrDefaultAsync(e => e.FilePath == filePath, cancellationToken);
                    if (entity != null)
                    {
                        RefreshFromDisk(entity, fileInfo);
                        await dbContext.SaveChangesAsync(cancellationToken);
                        committed = entity;
                        _logger.LogDebug("Updated existing file in database after retry: {FilePath}", SanitizeForLogging(filePath));
                    }
                }
            }
            else
            {
                // Update existing entity (preserving renamed/normalized status)
                RefreshFromDisk(entity, fileInfo);
                await dbContext.SaveChangesAsync(cancellationToken);
                committed = entity;
                _logger.LogDebug("Updated existing file in database: {FilePath}", SanitizeForLogging(filePath));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Error persisting file to database: {FilePath}", SanitizeForLogging(filePath));
        }

        if (committed is null)
        {
            return;
        }

        await MirrorCommittedRowAsync(committed);
        MarkChanged();

        // Broadcast file list update to connected clients
        if (_eventBroadcaster != null)
        {
            try
            {
                await _eventBroadcaster.BroadcastFileListUpdateAsync();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogWarning(ex, "Failed to broadcast file list update");
            }
        }
    }

    /// <summary>
    /// Copies a file's on-disk details onto its row, leaving every stored state alone.
    /// A file that is present is no longer missing, so its <c>MissingSince</c> stamp is
    /// cleared too.
    /// </summary>
    private static void RefreshFromDisk(ComicFileEntity entity, FileInfo fileInfo)
    {
        entity.FileName = fileInfo.Name;
        entity.Directory = fileInfo.DirectoryName ?? string.Empty;
        entity.FileSize = fileInfo.Length;
        entity.LastModified = fileInfo.LastWriteTime;
        entity.IsProcessed = ComputeProcessedState(entity.IsRenamed, entity.IsNormalized);
        entity.MissingSince = null;
        entity.UpdatedAt = DateTime.UtcNow;
    }

    public async Task RemoveFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        // Remove from the database first; the in-memory copy follows the committed outcome.
        var removed = false;
        try
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

            var entity = await dbContext.ComicFiles
                .FirstOrDefaultAsync(e => e.FilePath == filePath, cancellationToken);

            if (entity != null)
            {
                dbContext.ComicFiles.Remove(entity);
                await dbContext.SaveChangesAsync(cancellationToken);
                _logger.LogDebug("Removed file from database");
            }
            else
            {
                _logger.LogDebug("File already removed from database");
            }
            removed = true;
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // File was already deleted by another process/thread - this is not an error
            _logger.LogDebug(ex, "File already removed from database (concurrency)");
            removed = true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Error removing file from database");
        }

        if (!removed)
        {
            return;
        }

        await MirrorCommittedRowsAsync(Array.Empty<ComicFileEntity>(), new[] { filePath });
        MarkChanged();

        // Broadcast file list update to connected clients
        if (_eventBroadcaster != null)
        {
            try
            {
                await _eventBroadcaster.BroadcastFileListUpdateAsync();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogWarning(ex, "Failed to broadcast file list update");
            }
        }
    }

    public async Task UpdateFilePathAsync(string oldPath, string newPath, CancellationToken cancellationToken = default, bool broadcastUpdate = true)
    {
        if (!IsPathWithinAllowedDirectories(newPath))
        {
            _logger.LogWarning("Attempted to update file path to a location outside allowed directories: {NewPath}", SanitizeForLogging(newPath));
            return;
        }

        // Moving a file onto itself changes nothing. Without this guard the new-path lookup
        // below finds the old-path row itself and the merge path deletes it.
        if (string.Equals(oldPath, newPath, StringComparison.Ordinal))
        {
            _logger.LogDebug("Old and new paths are identical, nothing to update: {FilePath}", SanitizeForLogging(newPath));
            return;
        }

        // Compute file metadata once and reuse it for every row touched below.
        var fileInfo = File.Exists(newPath) ? new FileInfo(newPath) : null;

        // Move the database row first, preserving all processing state (CreatedAt included:
        // Overview "Series Updates" buckets series by it). The in-memory copy is moved only
        // once that has committed, so a failed or cancelled update leaves both stores still
        // agreeing on the old path rather than memory showing a move the database never saw.
        ComicFileEntity? survivor = null;
        try
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

            var entity = await dbContext.ComicFiles
                .FirstOrDefaultAsync(e => e.FilePath == oldPath, cancellationToken);

            // Set once the old-path row turns out to have been deleted by a concurrent
            // writer, so the merge below must not try to delete it again.
            var oldRowDeleted = false;
            ComicFileEntity? restoredNewEntity = null;

            if (entity == null && _files.TryGetValue(oldPath, out var lastCommitted))
            {
                // The old row was deleted before this move reached the database (typically
                // the watcher seeing the move as a delete plus a create). The in-memory copy
                // is that row as it was last committed, so restore the moved file from it
                // rather than re-adding it as a new, unprocessed file whose CreatedAt is now.
                entity = ToDetachedEntity(lastCommitted, newPath, fileInfo);
                oldRowDeleted = true;
                restoredNewEntity = await FindOrRestoreMovedRowAsync(dbContext, entity, cancellationToken);
                survivor = restoredNewEntity;
                _logger.LogDebug("Old path row was already deleted, restoring the moved file from its last committed state: {OldPath} -> {NewPath}", SanitizeForLogging(oldPath), SanitizeForLogging(newPath));
            }

            if (entity != null)
            {
                // Check whether the new path already has a DB row (race with watcher / processor)
                var newEntity = restoredNewEntity ?? await dbContext.ComicFiles
                    .FirstOrDefaultAsync(e => e.FilePath == newPath, cancellationToken);

                if (newEntity == null)
                {
                    // Rename the existing row in-place to preserve all columns (including IsRenamed, IsNormalized, etc.)
                    entity.FilePath = newPath;
                    entity.FileName = fileInfo?.Name ?? Path.GetFileName(newPath);
                    entity.Directory = fileInfo?.DirectoryName ?? Path.GetDirectoryName(newPath) ?? string.Empty;
                    if (fileInfo != null)
                    {
                        entity.FileSize = fileInfo.Length;
                        entity.LastModified = fileInfo.LastWriteTime;
                    }
                    entity.MissingSince = null;
                    entity.UpdatedAt = DateTime.UtcNow;

                    try
                    {
                        await dbContext.SaveChangesAsync(cancellationToken);
                        survivor = entity;
                        _logger.LogDebug("Updated file path in database: {OldPath} -> {NewPath}", SanitizeForLogging(oldPath), SanitizeForLogging(newPath));
                    }
                    catch (DbUpdateException ex) when (IsSqliteUniqueConstraintViolation(dbContext, ex))
                    {
                        // Race condition: another writer (typically the watcher observing the
                        // move as a Created event) inserted a row for the new path between the
                        // lookup above and this save, so the rename collides with the unique
                        // index on ComicFiles.FilePath. Roll the in-flight rename back on the
                        // tracked entity and fall through to the merge path below, which folds
                        // this row's processing state onto the row that won. Without this the
                        // save failed outright, leaving both the stale old-path row and an
                        // unmerged stub behind and skipping the read-status move.
                        var entry = dbContext.Entry(entity);
                        entry.CurrentValues.SetValues(entry.OriginalValues);

                        newEntity = await dbContext.ComicFiles
                            .FirstOrDefaultAsync(e => e.FilePath == newPath, cancellationToken);

                        if (newEntity == null)
                        {
                            throw;
                        }

                        _logger.LogDebug("New path row was created concurrently, merging instead: {OldPath} -> {NewPath}", SanitizeForLogging(oldPath), SanitizeForLogging(newPath));
                    }
                    catch (DbUpdateConcurrencyException)
                    {
                        // Race condition: another writer (typically the watcher observing the
                        // move as a Deleted event for the old path) deleted the old-path row
                        // between the lookup above and this save, so the rename matched no row.
                        // The file still exists at the new path and this entity still carries
                        // its processing state, so put a row back for the new path (or pick up
                        // the one another writer has inserted there meanwhile) and fall through
                        // to the merge path below, which folds that state onto it. Without this
                        // the save failed outright, leaving the moved file without a row (losing
                        // its processing state) and skipping the read-status move.
                        dbContext.ChangeTracker.Clear();
                        oldRowDeleted = true;
                        newEntity = await FindOrRestoreMovedRowAsync(dbContext, entity, cancellationToken);
                        // That row is committed (restored here or inserted by the other writer)
                        // and the old one is gone, so memory must follow even if the merge fails.
                        survivor = newEntity;

                        _logger.LogDebug("Old path row was deleted concurrently, adopting or restoring the new-path row: {OldPath} -> {NewPath}", SanitizeForLogging(oldPath), SanitizeForLogging(newPath));
                    }
                }

                if (newEntity != null)
                {
                    // New path already has a row (found up front, by the unique-constraint
                    // recovery above, or restored after the old row was deleted concurrently).
                    // This happens when the FileSystemWatcher observes the
                    // move as a Created event and inserts a default stub row for the target
                    // *before* the processor migrates the original record.
                    // The old-path row is the authoritative carrier of processing state for
                    // this physical file (e.g. IsNormalized set by a preceding normalize),
                    // so merge that state onto the surviving row instead of discarding it.
                    // Without this merge, a normalize-then-rename sequence loses IsNormalized
                    // (the stub's default false wins), leaving the file permanently
                    // "unprocessed" until it is normalized again.
                    if (fileInfo != null)
                    {
                        newEntity.FileSize = fileInfo.Length;
                        newEntity.LastModified = fileInfo.LastWriteTime;
                    }

                    newEntity.IsRenamed = newEntity.IsRenamed || entity.IsRenamed;
                    newEntity.IsNormalized = newEntity.IsNormalized || entity.IsNormalized;
                    newEntity.IsDuplicate = newEntity.IsDuplicate || entity.IsDuplicate;
                    newEntity.IsProcessed = ComputeProcessedState(newEntity.IsRenamed, newEntity.IsNormalized);

                    // Carry over metadata/version stamps when the stub row lacks them so a
                    // later stale-retag/backfill pass does not needlessly reprocess the file.
                    if (newEntity.SeriesMetadataVersion == 0 && entity.SeriesMetadataVersion != 0)
                        newEntity.SeriesMetadataVersion = entity.SeriesMetadataVersion;
                    if (newEntity.MetadataVersion == 0 && entity.MetadataVersion != 0)
                        newEntity.MetadataVersion = entity.MetadataVersion;
                    if (newEntity.WrittenMetadataVersion == 0 && entity.WrittenMetadataVersion != 0)
                        newEntity.WrittenMetadataVersion = entity.WrittenMetadataVersion;
                    // Metadata is an owned entity keyed by its principal, so the old row's
                    // tracked instance cannot be handed to the surviving row ("part of a key
                    // and so cannot be modified" on save, which would abort the whole merge).
                    // Store a copy instead.
                    if (newEntity.Metadata == null && entity.Metadata != null)
                    {
                        newEntity.Metadata = entity.Metadata.Clone();
                        // A move keeps the file's content, so the cached archive metadata
                        // stays current as long as the size and modification time match.
                        newEntity.MetadataScannedSize = entity.MetadataScannedSize;
                        newEntity.MetadataScannedModified = entity.MetadataScannedModified;
                        newEntity.LastDbEditAt = entity.LastDbEditAt;
                        newEntity.LastWriteAt = entity.LastWriteAt;
                        newEntity.MetadataSource = entity.MetadataSource;
                    }

                    // The old row is the authoritative record for this physical file, so
                    // keep the earliest CreatedAt. The stub row inserted by the watcher
                    // stamps CreatedAt with "now"; using it would reset the series' recency
                    // window and prematurely drop it from Overview "Series Updates".
                    if (entity.CreatedAt < newEntity.CreatedAt)
                        newEntity.CreatedAt = entity.CreatedAt;

                    newEntity.MissingSince = null;
                    newEntity.UpdatedAt = DateTime.UtcNow;

                    if (!oldRowDeleted)
                        dbContext.ComicFiles.Remove(entity);

                    try
                    {
                        await dbContext.SaveChangesAsync(cancellationToken);
                    }
                    catch (DbUpdateConcurrencyException ex) when (ex.Entries.All(e => e.State == EntityState.Deleted))
                    {
                        // Race condition: the old-path row was deleted concurrently (e.g. by the
                        // watcher) after it was loaded, so its delete matched no row and rolled
                        // back the whole merge. That delete is moot now; drop it and save the
                        // merged state onto the new-path row on its own.
                        foreach (var entry in ex.Entries)
                            entry.State = EntityState.Detached;

                        await dbContext.SaveChangesAsync(cancellationToken);
                        _logger.LogDebug("Old path row was deleted concurrently during merge: {OldPath} -> {NewPath}", SanitizeForLogging(oldPath), SanitizeForLogging(newPath));
                    }
                    survivor = newEntity;
                    _logger.LogDebug("Merged processing state from stale old-path entry into existing new-path row: {OldPath} -> {NewPath}", SanitizeForLogging(oldPath), SanitizeForLogging(newPath));
                }

                // Every user's read state is keyed by path, so a rename has to repoint it or
                // the file comes back as unread for everyone. Rows already present at the new
                // path (from a watcher-inserted stub the user has since touched) win, so the
                // stale old-path rows are simply dropped after the move.
                await MoveUserReadStatusAsync(dbContext, oldPath, newPath, cancellationToken);

                // The email history is keyed by path too, or a re-send would fail or send
                // whatever now has the old name.
                await MoveEmailDeliveriesAsync(dbContext, oldPath, newPath, cancellationToken);
            }
            else
            {
                // Neither store knows the old path - ensure new path row exists. The email
                // history can still name it, when the watcher saw the move as a delete and a
                // create, or an earlier move took the row but failed before its deliveries,
                // and nothing will come back to repoint them after this.
                _logger.LogDebug("Old path not found in database during UpdateFilePathAsync, ensuring new path is persisted: {NewPath}", SanitizeForLogging(newPath));
                await MoveEmailDeliveriesAsync(dbContext, oldPath, newPath, cancellationToken);
                await AddFileAsync(newPath, cancellationToken);
                return;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (ex is OperationCanceledException)
            {
                _logger.LogDebug("Updating file path in database was cancelled: {OldPath} -> {NewPath}", SanitizeForLogging(oldPath), SanitizeForLogging(newPath));
            }
            else
            {
                _logger.LogError(ex, "Error updating file path in database: {OldPath} -> {NewPath}", SanitizeForLogging(oldPath), SanitizeForLogging(newPath));
            }

            // Nothing was committed, so both stores still agree on the old path. The caller has
            // to know: a CBR-to-CBZ conversion must not delete the original with the row left
            // behind, and folder combine must not report the file as moved.
            if (survivor is null)
            {
                throw;
            }

            // Part of the move committed before the failure (the row move itself, a restored
            // new-path row), while the tracked entity may also carry merged state that never
            // did. Mirror whatever the database now holds for the new path.
            var (loaded, row) = await TryLoadRowAsync(newPath);
            if (!loaded)
            {
                return;
            }

            survivor = row;
            if (survivor is null)
            {
                await MirrorCommittedRowsAsync(Array.Empty<ComicFileEntity>(), new[] { oldPath });
                MarkChanged();
                return;
            }
        }

        // The row move committed (even if repointing read state afterwards failed), so the
        // in-memory copy follows it.
        await MirrorCommittedRowsAsync(new[] { survivor! }, new[] { oldPath });
        MarkChanged();
        _logger.LogDebug("Updated file path in memory store: {OldPath} -> {NewPath}", SanitizeForLogging(oldPath), SanitizeForLogging(newPath));

        // Broadcast file list update
        if (broadcastUpdate && _eventBroadcaster != null)
        {
            try
            {
                await _eventBroadcaster.BroadcastFileListUpdateAsync();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogWarning(ex, "Failed to broadcast file list update after path update");
            }
        }
    }

    /// <summary>
    /// Builds an untracked row for <paramref name="newPath"/> carrying every stored state of
    /// <paramref name="file"/> (the last committed copy of the row the file was moved from).
    /// </summary>
    private static ComicFileEntity ToDetachedEntity(ComicFile file, string newPath, FileInfo? fileInfo) => new()
    {
        FilePath = newPath,
        FileName = fileInfo?.Name ?? Path.GetFileName(newPath),
        Directory = fileInfo?.DirectoryName ?? Path.GetDirectoryName(newPath) ?? string.Empty,
        FileSize = fileInfo?.Length ?? file.FileSize,
        LastModified = fileInfo?.LastWriteTime ?? file.LastModified,
        IsRenamed = file.IsRenamed,
        IsNormalized = file.IsNormalized,
        IsProcessed = ComputeProcessedState(file.IsRenamed, file.IsNormalized),
        IsDuplicate = file.IsDuplicate,
        Metadata = file.Metadata?.Clone(),
        CreatedAt = file.CreatedAt,
        UpdatedAt = DateTime.UtcNow,
        SeriesMetadataVersion = file.SeriesMetadataVersion,
        MetadataVersion = file.MetadataVersion,
        WrittenMetadataVersion = file.WrittenMetadataVersion,
        LastDbEditAt = file.LastDbEditAt,
        LastWriteAt = file.LastWriteAt,
        MetadataSource = file.MetadataSource.ToString(),
    };

    /// <summary>
    /// Reads the committed row for <paramref name="filePath"/> with a fresh context.
    /// <c>Loaded</c> is false when the database could not be read.
    /// </summary>
    private async Task<(bool Loaded, ComicFileEntity? Row)> TryLoadRowAsync(string filePath)
    {
        try
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
            var row = await dbContext.ComicFiles.AsNoTracking().FirstOrDefaultAsync(e => e.FilePath == filePath);
            return (true, row);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Could not re-read the row for {FilePath}; the in-memory copy is left as it was", SanitizeForLogging(filePath));
            return (false, null);
        }
    }

    // SQLite extended result codes (SQLITE_CONSTRAINT_UNIQUE / SQLITE_CONSTRAINT_PRIMARYKEY).
    private const int SqliteConstraintUnique = 2067;
    private const int SqliteConstraintPrimaryKey = 1555;

    private bool IsSqliteUniqueConstraintViolation(ComicMaintainerDbContext dbContext, DbUpdateException exception)
    {
        // SQLite is the application's configured provider; other providers must not use
        // SQLite result codes to classify a failed save as a recoverable path collision.
        if (dbContext.Database.ProviderName != "Microsoft.EntityFrameworkCore.Sqlite")
        {
            _logger.LogDebug("File-path collision recovery requires SQLite; database provider is {Provider}",
                dbContext.Database.ProviderName);
            return false;
        }

        return exception.GetBaseException() is Microsoft.Data.Sqlite.SqliteException sqliteEx &&
            sqliteEx.SqliteExtendedErrorCode is SqliteConstraintUnique or SqliteConstraintPrimaryKey;
    }

    /// <summary>
    /// Returns the tracked row at the path <paramref name="movedEntity"/> was being renamed to,
    /// inserting a copy of it when the old-path row was deleted concurrently and no other writer
    /// has put a row there yet. <paramref name="movedEntity"/> already holds the new path's
    /// file details; the caller merges its processing state onto the returned row.
    /// </summary>
    private async Task<ComicFileEntity> FindOrRestoreMovedRowAsync(
        ComicMaintainerDbContext dbContext,
        ComicFileEntity movedEntity,
        CancellationToken cancellationToken)
    {
        var newPath = movedEntity.FilePath;
        var existing = await dbContext.ComicFiles
            .FirstOrDefaultAsync(e => e.FilePath == newPath, cancellationToken);
        if (existing != null)
            return existing;

        // Carry the moved row's processing state, timestamps and metadata on the restored row
        // from its first save, so a concurrent reader never caches default flags for it and
        // nothing (e.g. LastWriteAt without metadata) depends on the later merge to survive.
        var restored = new ComicFileEntity();
        dbContext.Entry(restored).CurrentValues.SetValues(movedEntity);
        // Preserve every scalar except the generated key and the last-update stamp.
        restored.Id = 0;
        restored.UpdatedAt = DateTime.UtcNow;
        restored.Metadata = movedEntity.Metadata?.Clone();
        dbContext.ComicFiles.Add(restored);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return restored;
        }
        catch (DbUpdateException ex) when (IsSqliteUniqueConstraintViolation(dbContext, ex))
        {
            // Another writer (e.g. the watcher) inserted the new-path row meanwhile; merge onto it.
            if (restored.Metadata != null)
                dbContext.Entry(restored.Metadata).State = EntityState.Detached;
            dbContext.Entry(restored).State = EntityState.Detached;
            var winner = await dbContext.ComicFiles
                .FirstOrDefaultAsync(e => e.FilePath == newPath, cancellationToken);
            if (winner == null)
            {
                throw;
            }

            return winner;
        }
    }

    public async Task SetFileSeriesMetadataVersionAsync(string filePath, int version, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;
        try
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            var entity = await dbContext.ComicFiles.FirstOrDefaultAsync(e => e.FilePath == filePath, cancellationToken);
            if (entity is null) return;
            if (entity.SeriesMetadataVersion == version) return;
            entity.SeriesMetadataVersion = version;
            entity.UpdatedAt = DateTime.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);

            // Mirror the committed row so subsequent reads see the stamp.
            await MirrorCommittedRowAsync(entity);
            MarkChanged();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Failed to stamp series metadata version on {File}", SanitizeForLogging(filePath));
        }
    }

    public async Task ApplyUserMetadataEditAsync(string filePath, ComicMetadata patch, ComicMetadataFieldFlags lockFields, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(patch);
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await dbContext.ComicFiles.FirstOrDefaultAsync(e => e.FilePath == filePath, cancellationToken);
        if (entity is null)
        {
            throw new InvalidOperationException($"File is not tracked: {filePath}");
        }

        entity.Metadata ??= new ComicMetadata();
        CopyNonNullMetadataFields(entity.Metadata, patch);
        entity.Metadata.IsUserEdited = true;
        entity.Metadata.UserLockedFieldsMask |= (long)lockFields;
        if (entity.MetadataVersion < int.MaxValue) entity.MetadataVersion++;
        entity.LastDbEditAt = DateTime.UtcNow;
        entity.MetadataSource = FileMetadataSource.UserEdit.ToString();
        entity.UpdatedAt = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        await MirrorCommittedRowAsync(entity);
        await BroadcastFileListUpdateSafeAsync();
    }
}
