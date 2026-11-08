using ComicMaintainer.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ComicMaintainer.Core.Services;

public partial class FileStoreService
{
    // Bound the IN (...) list so huge batches don't exceed SQLite's variable limit.
    private const int RefreshStatsBatchSize = 500;

    public async Task<int> RefreshFileStatsAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken = default)
    {
        var stats = new Dictionary<string, (long Size, DateTime LastWrite)>(StringComparer.Ordinal);
        var pending = (filePaths ?? Enumerable.Empty<string>())
            .Where(path => !string.IsNullOrWhiteSpace(path) && !stats.ContainsKey(path));
        foreach (var path in pending)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) continue;
                stats[path] = (info.Length, info.LastWriteTime);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
            {
                _logger.LogDebug(ex, "RefreshFileStats: stat failed for {FilePath}", SanitizeForLogging(path));
            }
        }

        if (stats.Count == 0) return 0;

        var changedEntities = new List<ComicFileEntity>();
        try
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            foreach (var batch in stats.Keys.Chunk(RefreshStatsBatchSize))
            {
                var entities = await dbContext.ComicFiles
                    .Where(e => batch.Contains(e.FilePath))
                    .ToListAsync(cancellationToken);
                foreach (var entity in entities)
                {
                    var (size, lastWrite) = stats[entity.FilePath];
                    if (entity.FileSize == size && entity.LastModified == lastWrite) continue;
                    entity.FileSize = size;
                    entity.LastModified = lastWrite;
                    entity.UpdatedAt = DateTime.UtcNow;
                    changedEntities.Add(entity);
                }
            }
            if (changedEntities.Count > 0)
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Nothing was committed, so memory is left as it was: it still agrees with the
            // database, and the next scan sees the same difference and tries again.
            _logger.LogWarning(ex, "RefreshFileStats: failed to persist file stats for {Count} file(s)", stats.Count);
            return 0;
        }

        if (changedEntities.Count == 0) return 0;

        await MirrorCommittedRowsAsync(changedEntities);
        MarkChanged();

        return changedEntities.Count;
    }
}
