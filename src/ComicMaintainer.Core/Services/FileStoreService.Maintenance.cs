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

            // Held while reading and replacing, so a write committing during the load is
            // refreshed after the load has been applied rather than overwritten by it.
            await _mirrorLock.WaitAsync(cancellationToken);
            try
            {
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

                // The database is the record, so the in-memory copy is replaced rather than
                // merged: a row deleted since the last load (for example by a database reset)
                // must disappear from memory too, not linger as a file every endpoint still lists.
                var loadedPaths = new HashSet<string>(StringComparer.Ordinal);
                foreach (var entity in fileEntities)
                {
                    ApplyCommittedRow(entity);
                    loadedPaths.Add(entity.FilePath);
                }

                foreach (var stalePath in _files.Keys.Where(path => !loadedPaths.Contains(path)).ToList())
                {
                    ForgetCommittedRow(stalePath);
                }

                // From here on every commit is mirrored, so the counts kept with the copy describe the table.
                lock (_statusCountsLock)
                {
                    _mirrorsDatabase = true;
                }
            }
            finally
            {
                _mirrorLock.Release();
            }

            MarkChanged();
            TryGetStatusCounts(out var counts);
            var loadedCount = counts.Total;
            var processedCount = counts.Processed;
            var duplicateCount = counts.Duplicates;

            _logger.LogInformation("Loaded {FileCount} files from database ({ProcessedCount} processed, {DuplicateCount} duplicates)",
                loadedCount, processedCount, duplicateCount);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
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

        await MirrorCommittedRowAsync(entity);
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

                    // Mirror the committed versions into the in-memory copy so
                    // subsequent reads agree with the database.
                    await MirrorCommittedRowsAsync(entities);
                    MarkChanged();
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Failed to mark {Count} file(s) as needing metadata backfill", pathList.Count);
        }

        return marked;
    }
}
