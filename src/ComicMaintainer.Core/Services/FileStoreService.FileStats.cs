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
        foreach (var path in filePaths ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(path) || stats.ContainsKey(path)) continue;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) continue;
                stats[path] = (info.Length, info.LastWriteTime);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "RefreshFileStats: stat failed for {FilePath}", SanitizeForLogging(path));
            }
        }

        if (stats.Count == 0) return 0;

        var changed = 0;
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
                    changed++;
                }
            }
            if (changed > 0)
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                MarkChanged();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RefreshFileStats: failed to persist file stats for {Count} file(s)", stats.Count);
        }

        // Mirror into the in-memory snapshot regardless of DB outcome so the
        // next scan compares against what is actually on disk.
        foreach (var (path, (size, lastWrite)) in stats)
        {
            if (_files.TryGetValue(path, out var file))
            {
                file.FileSize = size;
                file.LastModified = lastWrite;
            }
        }
        MarkChanged();

        return changed;
    }
}
