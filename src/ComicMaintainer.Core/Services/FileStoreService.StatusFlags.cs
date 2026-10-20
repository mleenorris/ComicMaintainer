using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
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

    public async Task MarkFileDuplicateAsync(string filePath, bool duplicate, CancellationToken cancellationToken = default)
    {
        if (duplicate)
        {
            _duplicateFiles.TryAdd(filePath, true);
        }
        else
        {
            _duplicateFiles.TryRemove(filePath, out _);
        }

        if (_files.TryGetValue(filePath, out var file))
        {
            file.IsDuplicate = duplicate;
        }

        // Persist to database
        try
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

            var entity = await dbContext.ComicFiles
                .FirstOrDefaultAsync(e => e.FilePath == filePath, cancellationToken);

            if (entity != null)
            {
                entity.IsDuplicate = duplicate;
                entity.UpdatedAt = DateTime.UtcNow;
                await dbContext.SaveChangesAsync(cancellationToken);
                _logger.LogDebug("Updated duplicate status for {FilePath} to {Status}", SanitizeForLogging(filePath), duplicate);
            }
            else
            {
                // Create entity if it doesn't exist
                if (IsPathWithinAllowedDirectories(filePath) && File.Exists(filePath))
                {
                    try
                    {
                        entity = CreateFileEntity(filePath, isDuplicate: duplicate);
                        dbContext.ComicFiles.Add(entity);
                        await dbContext.SaveChangesAsync(cancellationToken);
                        _logger.LogDebug("Created file entity and set duplicate status for {FilePath} to {Status}", SanitizeForLogging(filePath), duplicate);
                    }
                    catch (DbUpdateException ex) when (ex.InnerException is Microsoft.Data.Sqlite.SqliteException sqliteEx &&
                                                        sqliteEx.SqliteErrorCode == 19) // UNIQUE constraint
                    {
                        // Race condition: entity was created by another thread between our check and insert
                        // Retry by fetching and updating the existing entity
                        _logger.LogDebug("File entity already exists (race condition), retrying update for {FilePath}", SanitizeForLogging(filePath));
                        entity = await dbContext.ComicFiles.FirstOrDefaultAsync(e => e.FilePath == filePath, cancellationToken);
                        if (entity != null)
                        {
                            entity.IsDuplicate = duplicate;
                            entity.UpdatedAt = DateTime.UtcNow;
                            await dbContext.SaveChangesAsync(cancellationToken);
                            _logger.LogDebug("Updated duplicate status for {FilePath} to {Status} after retry", SanitizeForLogging(filePath), duplicate);
                        }
                    }
                }
                else
                {
                    _logger.LogDebug("File {FilePath} not found on filesystem or outside allowed directories, skipping database creation", SanitizeForLogging(filePath));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating duplicate status in database for {FilePath}", SanitizeForLogging(filePath));
        }
    }

    public async Task MarkFileRenamedAsync(string filePath, bool renamed, CancellationToken cancellationToken = default)
    {
        if (_files.TryGetValue(filePath, out var file))
        {
            file.IsRenamed = renamed;
            // Update IsProcessed based on both renamed and normalized states
            file.IsProcessed = file.IsRenamed && file.IsNormalized;
        }

        // Persist to database
        try
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

            var entity = await dbContext.ComicFiles
                .FirstOrDefaultAsync(e => e.FilePath == filePath, cancellationToken);

            if (entity != null)
            {
                entity.IsRenamed = renamed;
                // Update IsProcessed based on both states
                entity.IsProcessed = ComputeProcessedState(entity.IsRenamed, entity.IsNormalized);
                entity.UpdatedAt = DateTime.UtcNow;
                await dbContext.SaveChangesAsync(cancellationToken);
                _logger.LogDebug("Updated renamed status for {FilePath} to {Status}", SanitizeForLogging(filePath), renamed);
            }
            else
            {
                // Create entity if it doesn't exist
                if (IsPathWithinAllowedDirectories(filePath) && File.Exists(filePath))
                {
                    try
                    {
                        entity = CreateFileEntity(filePath);
                        entity.IsRenamed = renamed;
                        entity.IsProcessed = ComputeProcessedState(renamed, entity.IsNormalized);
                        dbContext.ComicFiles.Add(entity);
                        await dbContext.SaveChangesAsync(cancellationToken);
                        _logger.LogDebug("Created file entity and set renamed status for {FilePath} to {Status}", SanitizeForLogging(filePath), renamed);
                    }
                    catch (DbUpdateException ex) when (ex.InnerException is Microsoft.Data.Sqlite.SqliteException sqliteEx &&
                                                        sqliteEx.SqliteErrorCode == 19) // UNIQUE constraint
                    {
                        // Race condition: entity was created by another thread between our check and insert
                        _logger.LogDebug("File entity already exists (race condition), retrying update for {FilePath}", SanitizeForLogging(filePath));
                        entity = await dbContext.ComicFiles.FirstOrDefaultAsync(e => e.FilePath == filePath, cancellationToken);
                        if (entity != null)
                        {
                            entity.IsRenamed = renamed;
                            entity.IsProcessed = ComputeProcessedState(entity.IsRenamed, entity.IsNormalized);
                            entity.UpdatedAt = DateTime.UtcNow;
                            await dbContext.SaveChangesAsync(cancellationToken);
                            _logger.LogDebug("Updated renamed status for {FilePath} to {Status} after retry", SanitizeForLogging(filePath), renamed);
                        }
                    }
                }
                else
                {
                    _logger.LogDebug("File {FilePath} not found on filesystem or outside allowed directories, skipping database creation", SanitizeForLogging(filePath));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating renamed status in database for {FilePath}", SanitizeForLogging(filePath));
        }
    }

    public async Task MarkFileNormalizedAsync(string filePath, bool normalized, CancellationToken cancellationToken = default)
    {
        if (_files.TryGetValue(filePath, out var file))
        {
            file.IsNormalized = normalized;
            // Update IsProcessed based on both renamed and normalized states
            file.IsProcessed = file.IsRenamed && file.IsNormalized;
        }

        // Persist to database
        try
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

            var entity = await dbContext.ComicFiles
                .FirstOrDefaultAsync(e => e.FilePath == filePath, cancellationToken);

            if (entity != null)
            {
                entity.IsNormalized = normalized;
                // Update IsProcessed based on both states
                entity.IsProcessed = ComputeProcessedState(entity.IsRenamed, entity.IsNormalized);
                entity.UpdatedAt = DateTime.UtcNow;
                await dbContext.SaveChangesAsync(cancellationToken);
                _logger.LogDebug("Updated normalized status for {FilePath} to {Status}", SanitizeForLogging(filePath), normalized);
            }
            else
            {
                // Create entity if it doesn't exist
                if (IsPathWithinAllowedDirectories(filePath) && File.Exists(filePath))
                {
                    try
                    {
                        entity = CreateFileEntity(filePath);
                        entity.IsNormalized = normalized;
                        entity.IsProcessed = ComputeProcessedState(entity.IsRenamed, normalized);
                        dbContext.ComicFiles.Add(entity);
                        await dbContext.SaveChangesAsync(cancellationToken);
                        _logger.LogDebug("Created file entity and set normalized status for {FilePath} to {Status}", SanitizeForLogging(filePath), normalized);
                    }
                    catch (DbUpdateException ex) when (ex.InnerException is Microsoft.Data.Sqlite.SqliteException sqliteEx &&
                                                        sqliteEx.SqliteErrorCode == 19) // UNIQUE constraint
                    {
                        // Race condition: entity was created by another thread between our check and insert
                        _logger.LogDebug("File entity already exists (race condition), retrying update for {FilePath}", SanitizeForLogging(filePath));
                        entity = await dbContext.ComicFiles.FirstOrDefaultAsync(e => e.FilePath == filePath, cancellationToken);
                        if (entity != null)
                        {
                            entity.IsNormalized = normalized;
                            entity.IsProcessed = ComputeProcessedState(entity.IsRenamed, entity.IsNormalized);
                            entity.UpdatedAt = DateTime.UtcNow;
                            await dbContext.SaveChangesAsync(cancellationToken);
                            _logger.LogDebug("Updated normalized status for {FilePath} to {Status} after retry", SanitizeForLogging(filePath), normalized);
                        }
                    }
                }
                else
                {
                    _logger.LogDebug("File {FilePath} not found on filesystem or outside allowed directories, skipping database creation", SanitizeForLogging(filePath));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating normalized status in database for {FilePath}", SanitizeForLogging(filePath));
        }
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
                var entities = await dbContext.ComicFiles
                    .Where(e => chunk.Contains(e.FilePath))
                    .ToListAsync(cancellationToken);

                foreach (var entity in entities)
                {
                    if (entity.IsRenamed || entity.IsNormalized || entity.IsProcessed || entity.IsDuplicate)
                    {
                        entity.IsRenamed = false;
                        entity.IsNormalized = false;
                        entity.IsProcessed = false;
                        entity.IsDuplicate = false;
                        entity.UpdatedAt = now;
                        updated++;
                    }
                }

                if (entities.Count > 0)
                {
                    await dbContext.SaveChangesAsync(cancellationToken);
                }
            }

            // Mirror the change into the in-memory snapshot so that subsequent
            // GetAllFilesAsync()/IsFileRenamedAsync() calls reflect reality
            // immediately (FileStoreService keeps a process-local cache that
            // would otherwise return stale "renamed/normalized" flags).
            foreach (var path in pathList)
            {
                if (_files.TryGetValue(path, out var file))
                {
                    file.IsRenamed = false;
                    file.IsNormalized = false;
                    file.IsProcessed = false;
                    file.IsDuplicate = false;
                }
                _duplicateFiles.TryRemove(path, out _);
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
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "ClearProcessedStatusAsync: file list broadcast failed");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error clearing processed status for {Count} file(s)", pathList.Count);
        }

        return updated;
    }
}
