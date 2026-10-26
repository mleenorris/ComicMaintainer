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
        
        // Check if file exists in database to preserve status
        bool isRenamed = false;
        bool isNormalized = false;
        bool isDuplicate = _duplicateFiles.ContainsKey(filePath);
        // Default the creation stamp to now for a brand-new file; if the file is already
        // tracked in the database, preserve its original CreatedAt so re-adds (e.g. watcher
        // re-scan) don't reset the series recency window used by Overview "Series Updates".
        DateTime createdAt = DateTime.UtcNow;
        // Durable per-file metadata and version stamps that must survive a re-add, so a
        // watcher re-scan doesn't discard authored metadata and force a needless reprocess.
        // (Read state is no longer carried here: it is per-user, keyed by path in
        // UserFileReadStatuses, and therefore unaffected by re-adding the file.)
        ComicFile? tracked = null;

        try
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            
            var entity = await dbContext.ComicFiles
                .FirstOrDefaultAsync(e => e.FilePath == filePath, cancellationToken);
                
            if (entity != null)
            {
                // Preserve existing status from database
                isRenamed = entity.IsRenamed;
                isNormalized = entity.IsNormalized;
                isDuplicate = entity.IsDuplicate;
                createdAt = entity.CreatedAt;
                tracked = ToComicFile(entity);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading file status from database: {FilePath}", SanitizeForLogging(filePath));
        }

        // Fall back to the in-memory snapshot when the database row could not be read
        // (or does not exist yet) so a transient database error can't silently drop
        // read/metadata state either.
        if (tracked is null && _files.TryGetValue(filePath, out var cachedFile))
        {
            tracked = cachedFile;
        }

        var comicFile = new ComicFile
        {
            FilePath = filePath,
            FileName = fileInfo.Name,
            Directory = fileInfo.DirectoryName ?? string.Empty,
            FileSize = fileInfo.Length,
            LastModified = fileInfo.LastWriteTime,
            CreatedAt = createdAt,
            IsRenamed = isRenamed,
            IsNormalized = isNormalized,
            IsProcessed = ComputeProcessedState(isRenamed, isNormalized),
            IsDuplicate = isDuplicate,
            // Per-user read state lives in UserFileReadStatuses and is keyed by path, so a
            // re-add cannot lose it and there is nothing to carry over onto the shared
            // in-memory entry (which is visible to every user).
            IsRead = false,
            Metadata = tracked?.Metadata?.Clone(),
            SeriesMetadataVersion = tracked?.SeriesMetadataVersion ?? 0,
            MetadataVersion = tracked?.MetadataVersion ?? 0,
            WrittenMetadataVersion = tracked?.WrittenMetadataVersion ?? 0,
            LastDbEditAt = tracked?.LastDbEditAt,
            LastWriteAt = tracked?.LastWriteAt,
            MetadataSource = tracked?.MetadataSource ?? FileMetadataSource.Scanned
        };

        _files.AddOrUpdate(filePath, comicFile, (_, _) => comicFile);
        MarkChanged();

        // Persist to database
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
                    MarkChanged();
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
                        entity.FileName = fileInfo.Name;
                        entity.Directory = fileInfo.DirectoryName ?? string.Empty;
                        entity.FileSize = fileInfo.Length;
                        entity.LastModified = fileInfo.LastWriteTime;
                        entity.IsProcessed = ComputeProcessedState(entity.IsRenamed, entity.IsNormalized);
                        entity.IsDuplicate = comicFile.IsDuplicate;
                        entity.UpdatedAt = DateTime.UtcNow;
                        await dbContext.SaveChangesAsync(cancellationToken);
                        MarkChanged();
                        _logger.LogDebug("Updated existing file in database after retry: {FilePath}", SanitizeForLogging(filePath));
                    }
                }
            }
            else
            {
                // Update existing entity (preserving renamed/normalized status)
                entity.FileName = fileInfo.Name;
                entity.Directory = fileInfo.DirectoryName ?? string.Empty;
                entity.FileSize = fileInfo.Length;
                entity.LastModified = fileInfo.LastWriteTime;
                entity.IsProcessed = ComputeProcessedState(entity.IsRenamed, entity.IsNormalized);
                entity.IsDuplicate = comicFile.IsDuplicate;
                entity.UpdatedAt = DateTime.UtcNow;
                await dbContext.SaveChangesAsync(cancellationToken);
                MarkChanged();
                _logger.LogDebug("Updated existing file in database: {FilePath}", SanitizeForLogging(filePath));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error persisting file to database: {FilePath}", SanitizeForLogging(filePath));
        }
        
        // Broadcast file list update to connected clients
        if (_eventBroadcaster != null)
        {
            try
            {
                await _eventBroadcaster.BroadcastFileListUpdateAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to broadcast file list update");
            }
        }
    }

    public async Task RemoveFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        _files.TryRemove(filePath, out _);
        _duplicateFiles.TryRemove(filePath, out _);
        MarkChanged();

        // Remove from database
        try
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            
            var entity = await dbContext.ComicFiles
                .FirstOrDefaultAsync(e => e.FilePath == filePath, cancellationToken);

            if (entity != null)
            {
                dbContext.ComicFiles.Remove(entity);
                await dbContext.SaveChangesAsync(cancellationToken);
                MarkChanged();
                _logger.LogDebug("Removed file from database");
            }
            else
            {
                _logger.LogDebug("File already removed from database");
            }
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // File was already deleted by another process/thread - this is not an error
            _logger.LogDebug(ex, "File already removed from database (concurrency)");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing file from database");
        }
        
        // Broadcast file list update to connected clients
        if (_eventBroadcaster != null)
        {
            try
            {
                await _eventBroadcaster.BroadcastFileListUpdateAsync();
            }
            catch (Exception ex)
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

        // Compute file metadata once and reuse in both the in-memory and database updates.
        var fileInfo = File.Exists(newPath) ? new FileInfo(newPath) : null;

        // Move the in-memory entry, preserving all processing state
        if (_files.TryRemove(oldPath, out var existingFile))
        {
            _duplicateFiles.TryRemove(oldPath, out _);

            var newFile = new ComicFile
            {
                FilePath = newPath,
                FileName = fileInfo?.Name ?? Path.GetFileName(newPath),
                Directory = fileInfo?.DirectoryName ?? Path.GetDirectoryName(newPath) ?? string.Empty,
                FileSize = fileInfo?.Length ?? existingFile.FileSize,
                LastModified = fileInfo?.LastWriteTime ?? existingFile.LastModified,
                // Preserve the original CreatedAt so a rename/normalize does not reset
                // it to the default (DateTime.MinValue). Overview "Series Updates" buckets
                // series by the newest file's CreatedAt within a 30-day window, so losing
                // this timestamp would drop a series out of the list far earlier than 30 days.
                CreatedAt = existingFile.CreatedAt,
                IsRenamed = existingFile.IsRenamed,
                IsNormalized = existingFile.IsNormalized,
                IsProcessed = existingFile.IsProcessed,
                IsDuplicate = existingFile.IsDuplicate,
                // Read state is per-user; the UserFileReadStatuses rows are repointed at
                // the new path below rather than carried on the shared in-memory entry.
                IsRead = false,
                Metadata = existingFile.Metadata,
                SeriesMetadataVersion = existingFile.SeriesMetadataVersion,
                MetadataVersion = existingFile.MetadataVersion,
                WrittenMetadataVersion = existingFile.WrittenMetadataVersion,
                LastDbEditAt = existingFile.LastDbEditAt,
                LastWriteAt = existingFile.LastWriteAt,
                MetadataSource = existingFile.MetadataSource
            };
            _files[newPath] = newFile;
            if (newFile.IsDuplicate)
                _duplicateFiles[newPath] = true;
            MarkChanged();

            _logger.LogDebug("Updated file path in memory store: {OldPath} -> {NewPath}", SanitizeForLogging(oldPath), SanitizeForLogging(newPath));
        }
        else
        {
            // Old path not in memory - ensure the new path is tracked
            await AddFileAsync(newPath, cancellationToken);
            return;
        }

        // Update the database entry, preserving processing state
        try
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

            var entity = await dbContext.ComicFiles
                .FirstOrDefaultAsync(e => e.FilePath == oldPath, cancellationToken);

            if (entity != null)
            {
                // Check whether the new path already has a DB row (race with watcher / processor)
                var newEntity = await dbContext.ComicFiles
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
                    entity.UpdatedAt = DateTime.UtcNow;

                    try
                    {
                        await dbContext.SaveChangesAsync(cancellationToken);
                        MarkChanged();
                        _logger.LogDebug("Updated file path in database: {OldPath} -> {NewPath}", SanitizeForLogging(oldPath), SanitizeForLogging(newPath));
                    }
                    catch (DbUpdateException ex) when (ex.InnerException is Microsoft.Data.Sqlite.SqliteException sqliteEx &&
                                                        sqliteEx.SqliteErrorCode == 19) // UNIQUE constraint
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
                }

                if (newEntity != null)
                {
                    // New path already has a row (found up front, or by the unique-constraint
                    // recovery above). This happens when the FileSystemWatcher observes the
                    // move as a Created event and inserts a default stub row for the target
                    // *before* the processor migrates the original record.
                    // The old-path row is the authoritative carrier of processing state for
                    // this physical file (e.g. IsNormalized set by a preceding normalize),
                    // so merge that state onto the surviving row instead of discarding it.
                    // Without this merge, a normalize-then-rename sequence loses IsNormalized
                    // (the stub's default false wins), leaving the file permanently
                    // "unprocessed" until it is normalized again.
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

                    newEntity.UpdatedAt = DateTime.UtcNow;

                    dbContext.ComicFiles.Remove(entity);
                    await dbContext.SaveChangesAsync(cancellationToken);
                    MarkChanged();
                    _logger.LogDebug("Merged processing state from stale old-path entry into existing new-path row: {OldPath} -> {NewPath}", SanitizeForLogging(oldPath), SanitizeForLogging(newPath));
                }

                // Every user's read state is keyed by path, so a rename has to repoint it or
                // the file comes back as unread for everyone. Rows already present at the new
                // path (from a watcher-inserted stub the user has since touched) win, so the
                // stale old-path rows are simply dropped after the move.
                await MoveUserReadStatusAsync(dbContext, oldPath, newPath, cancellationToken);
            }
            else
            {
                // Old path not in DB - ensure new path row exists
                _logger.LogDebug("Old path not found in database during UpdateFilePathAsync, ensuring new path is persisted: {NewPath}", SanitizeForLogging(newPath));
                await AddFileAsync(newPath, cancellationToken);
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating file path in database: {OldPath} -> {NewPath}", SanitizeForLogging(oldPath), SanitizeForLogging(newPath));
        }

        // Broadcast file list update
        if (broadcastUpdate && _eventBroadcaster != null)
        {
            try
            {
                await _eventBroadcaster.BroadcastFileListUpdateAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to broadcast file list update after path update");
            }
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
            MarkChanged();

            // Mirror into the in-memory snapshot so subsequent reads see the stamp.
            if (_files.TryGetValue(filePath, out var file))
            {
                file.SeriesMetadataVersion = version;
                MarkChanged();
            }
        }
        catch (Exception ex)
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
        MarkChanged();

        var updated = ToComicFile(entity);
        _files.AddOrUpdate(filePath, updated, (_, _) => updated);
        MarkChanged();
        await BroadcastFileListUpdateSafeAsync();
    }
}
