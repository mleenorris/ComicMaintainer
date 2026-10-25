using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Data;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;

namespace ComicMaintainer.Core.Services;

public partial class FileStoreService
{
    public async Task InitializeFromDatabaseAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Initializing file store from database");

            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

            // Load all files from database. We deliberately do NOT call File.Exists()
            // for each row here: with large libraries a synchronous stat() per row
            // delays startup (and thus the first file-list response) noticeably.
            // Stale rows for files no longer on disk are cleaned up asynchronously
            // by CleanupStaleEntriesAsync (and by the live FileSystemWatcher when a
            // delete is observed). Read paths now query the database directly, so
            // the worst case for a transiently missing file is one stale entry in
            // the UI until cleanup runs.
            var fileEntities = await dbContext.ComicFiles
                .AsNoTracking()
                .ToListAsync(cancellationToken);

            _logger.LogInformation("Loading {Count} files from database", fileEntities.Count);

            foreach (var entity in fileEntities)
            {
                var comicFile = ToComicFile(entity);
                _files.AddOrUpdate(entity.FilePath, comicFile, (_, _) => comicFile);

                if (entity.IsDuplicate)
                {
                    _duplicateFiles.TryAdd(entity.FilePath, true);
                }
            }

            MarkChanged();
            var loadedCount = _files.Count;
            var processedCount = _files.Values.Count(f => f.IsProcessed);
            var duplicateCount = _duplicateFiles.Count;

            _logger.LogInformation("Loaded {FileCount} files from database ({ProcessedCount} processed, {DuplicateCount} duplicates)",
                loadedCount, processedCount, duplicateCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error initializing file store from database");
        }
    }

    public async Task MarkFileBackfilledAsync(string filePath, int version, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await dbContext.ComicFiles.FirstOrDefaultAsync(e => e.FilePath == filePath, cancellationToken);
        if (entity is null) return;
        entity.WrittenMetadataVersion = version;
        entity.LastWriteAt = DateTime.UtcNow;
        if (!string.Equals(entity.MetadataSource, FileMetadataSource.UserEdit.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            entity.MetadataSource = FileMetadataSource.Scanned.ToString();
        }
        entity.UpdatedAt = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        MarkChanged();

        var updated = ToComicFile(entity);
        _files.AddOrUpdate(filePath, updated, (_, _) => updated);
        MarkChanged();
    }

    public async Task<int> MarkFilesNeedingBackfillAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken = default)
    {
        var pathList = (filePaths ?? Enumerable.Empty<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (pathList.Count == 0)
        {
            return 0;
        }

        var marked = 0;
        try
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            // Chunk to stay within SQLite's default IN-list limit.
            const int chunkSize = 500;
            for (var offset = 0; offset < pathList.Count; offset += chunkSize)
            {
                var chunk = pathList.GetRange(offset, Math.Min(chunkSize, pathList.Count - offset));
                var entities = await dbContext.ComicFiles
                    .Where(e => chunk.Contains(e.FilePath) && !e.IsDuplicate)
                    .ToListAsync(cancellationToken);

                foreach (var entity in entities)
                {
                    // Only bump when the file is actually up to date with disk;
                    // bumping makes MetadataVersion > WrittenMetadataVersion so
                    // GetFilesNeedingBackfillAsync surfaces it to the backfill job.
                    if (entity.MetadataVersion < int.MaxValue)
                    {
                        entity.MetadataVersion++;
                    }
                    entity.UpdatedAt = DateTime.UtcNow;
                    marked++;
                }

                if (entities.Count > 0)
                {
                    await dbContext.SaveChangesAsync(cancellationToken);
                    MarkChanged();

                    // Mirror the bumped versions into the in-memory snapshot so
                    // subsequent reads agree with the database.
                    foreach (var entity in entities)
                    {
                        var updated = ToComicFile(entity);
                        _files.AddOrUpdate(entity.FilePath, updated, (_, _) => updated);
                    }
                    MarkChanged();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to mark {Count} file(s) as needing metadata backfill", pathList.Count);
        }

        return marked;
    }

    public async Task<int> CleanupStaleEntriesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Starting cleanup of stale database entries");
            
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            
            // Load all file entries from database
            var fileEntities = await dbContext.ComicFiles
                .ToListAsync(cancellationToken);
            
            var removedCount = 0;
            var filesToRemove = new List<ComicFileEntity>();
            
            // Check each entry to see if the file still exists
            foreach (var entity in fileEntities)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;
                    
                if (!File.Exists(entity.FilePath))
                {
                    _logger.LogDebug("Marking stale entry for removal: {FilePath}", SanitizeForLogging(entity.FilePath));
                    filesToRemove.Add(entity);
                    
                    // Also remove from in-memory store
                    _files.TryRemove(entity.FilePath, out _);
                    _duplicateFiles.TryRemove(entity.FilePath, out _);
                    MarkChanged();
                }
            }
            
            // Remove all stale entries in a single operation
            if (filesToRemove.Any())
            {
                dbContext.ComicFiles.RemoveRange(filesToRemove);
                await dbContext.SaveChangesAsync(cancellationToken);
                MarkChanged();
                removedCount = filesToRemove.Count;
                
                _logger.LogInformation("Removed {Count} stale database entries", removedCount);
            }
            else
            {
                _logger.LogInformation("No stale entries found during cleanup");
            }
            
            return removedCount;
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning(ex, "Cleanup of stale entries was cancelled");
            return 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during cleanup of stale database entries");
            return 0;
        }
    }
}
