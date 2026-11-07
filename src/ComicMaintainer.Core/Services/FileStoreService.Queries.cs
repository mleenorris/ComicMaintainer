using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Data;
using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.EntityFrameworkCore;

namespace ComicMaintainer.Core.Services;

public partial class FileStoreService
{
    public async Task<IEnumerable<ComicFile>> GetAllFilesAsync(CancellationToken cancellationToken = default)
    {
        var files = _files.Values.Select(f => f.ShallowCopy()).ToList();
        await ApplyUserReadStateAsync(files, cancellationToken);
        return files;
    }

    public async Task<IEnumerable<ComicFile>> GetFilteredFilesAsync(string? filter = null, CancellationToken cancellationToken = default)
    {
        var totalFileCount = _files.Count;
        _logger.LogDebug("GetFilteredFilesAsync: Starting with {TotalFiles} files in store, filter: '{Filter}'", totalFileCount, filter ?? "none");

        // Copies, not the stored instances: read state is per-user and must never be written
        // onto the entries shared by every request.
        var allFiles = _files.Values.Select(f => f.ShallowCopy()).ToList();
        // Stamp per-user read state before filtering so the "read"/"unread" filters and the
        // IsRead flag the caller ultimately projects agree with each other.
        await ApplyUserReadStateAsync(allFiles, cancellationToken);

        var files = allFiles.AsEnumerable();

        if (!string.IsNullOrEmpty(filter))
        {
            var filterLower = filter.ToLower();
            _logger.LogDebug("GetFilteredFilesAsync: Applying filter: '{Filter}'", filterLower);
            
            files = filterLower switch
            {
                "processed" => files.Where(f => f.IsProcessed),
                "unprocessed" => files.Where(f => !f.IsProcessed && !f.IsDuplicate),
                "duplicates" => files.Where(f => f.IsDuplicate),
                "renamed" => files.Where(f => f.IsRenamed),
                "normalized" => files.Where(f => f.IsNormalized),
                "read" => files.Where(f => f.IsRead),
                "unread" => files.Where(f => !f.IsRead),
                _ => files
            };
            
            var filteredList = files.ToList();
            _logger.LogDebug("GetFilteredFilesAsync: Filter '{Filter}' resulted in {FilteredCount} files (reduced from {TotalFiles})", 
                filterLower, filteredList.Count, totalFileCount);
            
            // Log breakdown of file states for unprocessed filter
            if (filterLower == "unprocessed")
            {
                var processedCount = allFiles.Count(f => f.IsProcessed);
                var duplicateCount = allFiles.Count(f => f.IsDuplicate);
                var unprocessedNonDuplicateCount = allFiles.Count(f => !f.IsProcessed && !f.IsDuplicate);
                
                _logger.LogDebug("GetFilteredFilesAsync: File state breakdown - Total: {Total}, Processed: {Processed}, Duplicates: {Duplicates}, Unprocessed (non-duplicate): {Unprocessed}",
                    totalFileCount, processedCount, duplicateCount, unprocessedNonDuplicateCount);
            }
            
            return filteredList;
        }

        _logger.LogDebug("GetFilteredFilesAsync: No filter applied, returning all {TotalFiles} files", totalFileCount);
        return allFiles;
    }

    /// <remarks>
    /// Counted in SQL with the same predicates as <see cref="GetUnmarkedCountAsync"/> and the
    /// file-list filters, so <c>/api/files/counts</c>, the list's <c>unmarked_count</c> and the
    /// filtered pages always agree. Cached against <see cref="ChangeVersion"/> like the
    /// unmarked count, so polling the counts does not re-scan the table.
    /// </remarks>
    public async Task<(int total, int processed, int unprocessed, int duplicates)> GetFileCountsAsync(CancellationToken cancellationToken = default)
    {
        var cacheKey = $"FileStore.FileCounts.{ChangeVersion}";
        if (_memoryCache != null && _memoryCache.TryGetValue(cacheKey, out (int, int, int, int) cached))
        {
            return cached;
        }

        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var counts = await dbContext.ComicFiles
            .AsNoTracking()
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Total = g.Count(),
                Processed = g.Count(f => f.IsRenamed && f.IsNormalized),
                Unprocessed = g.Count(f => !(f.IsRenamed && f.IsNormalized) && !f.IsDuplicate),
                Duplicates = g.Count(f => f.IsDuplicate),
            })
            .FirstOrDefaultAsync(cancellationToken);

        var result = counts is null
            ? (0, 0, 0, 0)
            : (counts.Total, counts.Processed, counts.Unprocessed, counts.Duplicates);
        _memoryCache?.Set(cacheKey, result, UnmarkedCountCacheTtl);
        return result;
    }

    public Task<bool> IsFileProcessedAsync(string filePath, CancellationToken cancellationToken = default)
    {
        // Processed status is now computed from renamed AND normalized states
        if (_files.TryGetValue(filePath, out var file))
        {
            return Task.FromResult(file.IsRenamed && file.IsNormalized);
        }
        return Task.FromResult(false);
    }

    public Task<bool> IsFileRenamedAsync(string filePath, CancellationToken cancellationToken = default)
    {
        // Check if file is already renamed
        if (_files.TryGetValue(filePath, out var file))
        {
            return Task.FromResult(file.IsRenamed);
        }
        return Task.FromResult(false);
    }

    public Task<bool> IsFileNormalizedAsync(string filePath, CancellationToken cancellationToken = default)
    {
        // Check if file is already normalized
        if (_files.TryGetValue(filePath, out var file))
        {
            return Task.FromResult(file.IsNormalized);
        }
        return Task.FromResult(false);
    }

    public Task<bool> FileExistsAsync(string filePath, CancellationToken cancellationToken = default)
    {
        // Check in-memory store first for performance
        var exists = _files.ContainsKey(filePath);
        return Task.FromResult(exists);
    }

    public async Task<IReadOnlyList<string>> GetFilesWithStaleSeriesMetadataAsync(
        IEnumerable<string> filePaths,
        int currentVersion,
        CancellationToken cancellationToken = default)
    {
        var pathList = (filePaths ?? Enumerable.Empty<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (pathList.Count == 0)
        {
            return Array.Empty<string>();
        }

        var stale = new List<string>();
        try
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            // Chunk to stay within SQLite's default IN-list limit.
            const int chunkSize = 500;
            for (var offset = 0; offset < pathList.Count; offset += chunkSize)
            {
                var chunk = pathList.GetRange(offset, Math.Min(chunkSize, pathList.Count - offset));
                var rows = await dbContext.ComicFiles
                    .AsNoTracking()
                    .Where(e => chunk.Contains(e.FilePath) && e.SeriesMetadataVersion < currentVersion)
                    .Select(e => e.FilePath)
                    .ToListAsync(cancellationToken);
                stale.AddRange(rows);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Failed to query stale series-metadata-version files (current={Version})", currentVersion);
        }
        return stale;
    }


    public async Task<ComicFile?> GetFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return null;

        try
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            var entity = await dbContext.ComicFiles
                .AsNoTracking()
                .FirstOrDefaultAsync(e => e.FilePath == filePath, cancellationToken);
            return entity is null ? null : ToComicFile(entity);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Failed to load file row for {FilePath}", SanitizeForLogging(filePath));
            return _files.TryGetValue(filePath, out var cached) ? cached : null;
        }
    }

    public async Task<IReadOnlyList<ComicFile>> GetFilesNeedingBackfillAsync(int max, string? afterFilePath = null, CancellationToken cancellationToken = default)
    {
        if (max <= 0) return Array.Empty<ComicFile>();
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var query = dbContext.ComicFiles
            .AsNoTracking()
            .Where(e => !e.IsDuplicate && e.MetadataVersion > e.WrittenMetadataVersion);
        if (afterFilePath is not null)
        {
            query = query.Where(e => string.Compare(e.FilePath, afterFilePath) > 0);
        }

        var rows = await query
            .OrderBy(e => e.FilePath)
            .Take(max)
            .ToListAsync(cancellationToken);
        return rows.Select(ToComicFile).ToList();
    }
}
