using ComicMaintainer.Core.Data;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;

namespace ComicMaintainer.Core.Services;

public partial class FileStoreService
{
    /// <summary>
    /// DEPRECATED: Processed status is now computed from IsRenamed && IsNormalized.
    /// This method is kept for backward compatibility but does nothing.
    /// Use MarkFileRenamedAsync and MarkFileNormalizedAsync instead.
    /// </summary>
    [Obsolete("Processed status is now computed from renamed and normalized states. Use MarkFileRenamedAsync and MarkFileNormalizedAsync instead.")]
    public Task MarkFileProcessedAsync(string filePath, bool processed, CancellationToken cancellationToken = default)
    {
        _logger.LogWarning("MarkFileProcessedAsync is deprecated. Processed status is computed from renamed and normalized states.");
        // No-op: processed state is now computed, not stored
        return Task.CompletedTask;
    }

    public Task MarkFileDuplicateAsync(string filePath, bool duplicate, CancellationToken cancellationToken = default)
        => UpdateFileStatusAsync(filePath, "duplicate", duplicate, entity => entity.IsDuplicate = duplicate, cancellationToken);

    public Task MarkFileRenamedAsync(string filePath, bool renamed, CancellationToken cancellationToken = default)
        => UpdateFileStatusAsync(filePath, "renamed", renamed, entity => entity.IsRenamed = renamed, cancellationToken);

    public Task MarkFileNormalizedAsync(string filePath, bool normalized, CancellationToken cancellationToken = default)
        => UpdateFileStatusAsync(filePath, "normalized", normalized, entity => entity.IsNormalized = normalized, cancellationToken);

    /// <summary>
    /// Applies one status flag to a file's row and commits it, creating the row first for a
    /// file that is on disk inside the library but not tracked yet. The in-memory copy is
    /// updated from the committed row afterwards, so a failed or cancelled write leaves the
    /// database and memory exactly as they were (and agreeing) instead of memory showing a
    /// change the database never saw, which a restart would then silently undo.
    /// </summary>
    private async Task UpdateFileStatusAsync(
        string filePath,
        string statusName,
        bool status,
        Action<ComicFileEntity> apply,
        CancellationToken cancellationToken)
    {
        void ApplyStatus(ComicFileEntity entity)
        {
            apply(entity);
            entity.IsProcessed = ComputeProcessedState(entity.IsRenamed, entity.IsNormalized);
            entity.UpdatedAt = DateTime.UtcNow;
        }

        ComicFileEntity? committed = null;
        try
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

            var entity = await dbContext.ComicFiles
                .FirstOrDefaultAsync(e => e.FilePath == filePath, cancellationToken);

            if (entity != null)
            {
                ApplyStatus(entity);
                await dbContext.SaveChangesAsync(cancellationToken);
                committed = entity;
                _logger.LogDebug("Updated {StatusName} status for {FilePath} to {Status}", statusName, SanitizeForLogging(filePath), status);
            }
            else if (IsPathWithinAllowedDirectories(filePath) && File.Exists(filePath))
            {
                var newEntity = CreateFileEntity(filePath);
                ApplyStatus(newEntity);
                try
                {
                    dbContext.ComicFiles.Add(newEntity);
                    await dbContext.SaveChangesAsync(cancellationToken);
                    committed = newEntity;
                    _logger.LogDebug("Created file entity and set {StatusName} status for {FilePath} to {Status}", statusName, SanitizeForLogging(filePath), status);
                }
                catch (DbUpdateException ex) when (ex.InnerException is Microsoft.Data.Sqlite.SqliteException sqliteEx &&
                                                    sqliteEx.SqliteErrorCode == 19) // UNIQUE constraint
                {
                    // Race condition: entity was created by another thread between our check and insert
                    _logger.LogDebug("File entity already exists (race condition), retrying update for {FilePath}", SanitizeForLogging(filePath));
                    // Stop tracking the failed insert, otherwise the retry's SaveChanges re-inserts it.
                    dbContext.Entry(newEntity).State = EntityState.Detached;
                    entity = await dbContext.ComicFiles.FirstOrDefaultAsync(e => e.FilePath == filePath, cancellationToken);
                    if (entity != null)
                    {
                        ApplyStatus(entity);
                        await dbContext.SaveChangesAsync(cancellationToken);
                        committed = entity;
                        _logger.LogDebug("Updated {StatusName} status for {FilePath} to {Status} after retry", statusName, SanitizeForLogging(filePath), status);
                    }
                }
            }
            else
            {
                _logger.LogDebug("File {FilePath} not found on filesystem or outside allowed directories, skipping database creation", SanitizeForLogging(filePath));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Error updating {StatusName} status in database for {FilePath}", statusName, SanitizeForLogging(filePath));
        }

        if (committed is null)
        {
            return;
        }

        await MirrorCommittedRowAsync(committed);
        MarkChanged();
    }

    public async Task<int> ClearProcessedStatusAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken = default)
    {
        if (filePaths == null)
        {
            return 0;
        }

        var pathList = filePaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (pathList.Count == 0)
        {
            return 0;
        }

        var updated = 0;
        try
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

            // Chunk to keep the IN clause within SQLite's default limit (999).
            const int chunkSize = 500;
            var now = DateTime.UtcNow;
            for (var offset = 0; offset < pathList.Count; offset += chunkSize)
            {
                var chunk = pathList.GetRange(offset, Math.Min(chunkSize, pathList.Count - offset));
                var flagged = await dbContext.ComicFiles
                    .Where(e => chunk.Contains(e.FilePath)
                        && (e.IsRenamed || e.IsNormalized || e.IsProcessed || e.IsDuplicate))
                    .ToListAsync(cancellationToken);
                if (flagged.Count == 0)
                {
                    continue;
                }

                foreach (var entity in flagged)
                {
                    entity.IsRenamed = false;
                    entity.IsNormalized = false;
                    entity.IsProcessed = false;
                    entity.IsDuplicate = false;
                    entity.UpdatedAt = now;
                }

                await dbContext.SaveChangesAsync(cancellationToken);
                updated += flagged.Count;

                // Mirror each committed chunk, so GetAllFilesAsync()/IsFileRenamedAsync() agree
                // with the database even when a later chunk fails.
                await MirrorCommittedRowsAsync(flagged);
                MarkChanged();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Error clearing processed status for {Count} file(s)", pathList.Count);
        }

        if (updated > 0)
        {
            _logger.LogInformation(
                "ClearProcessedStatusAsync: Cleared renamed/normalized/processed/duplicate flags on {Count} file(s)",
                updated);

            if (_eventBroadcaster != null)
            {
                try
                {
                    await _eventBroadcaster.BroadcastFileListUpdateAsync();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _logger.LogDebug(ex, "ClearProcessedStatusAsync: file list broadcast failed");
                }
            }
        }

        return updated;
    }
}
