using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Data;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.EntityFrameworkCore;

namespace ComicMaintainer.Core.Services;

public partial class FileStoreService
{
    /// <summary>
    /// Identifier used to scope read-status subqueries. Returns an empty string when there
    /// is no user in scope, which matches no row (UserId is always non-empty) and so makes
    /// every file read as unread — the correct answer for background work.
    /// </summary>
    private string ReadScopeUserId => CurrentUserId ?? string.Empty;

    /// <summary>
    /// Apply the named status filter to a <see cref="ComicFileEntity"/> query.
    /// Matches the semantics of <see cref="GetFilteredFilesAsync"/>.
    /// </summary>
    /// <remarks>
    /// The read/unread filters are evaluated as a correlated EXISTS against
    /// <c>UserFileReadStatuses</c> so they stay scoped to the calling user and are applied
    /// in SQL, before paging.
    /// </remarks>
    private IQueryable<ComicFileEntity> ApplyEntityFilter(
        ComicMaintainerDbContext dbContext,
        IQueryable<ComicFileEntity> q,
        string? filter)
    {
        if (string.IsNullOrEmpty(filter)) return q;
        var userId = ReadScopeUserId;
        return filter.ToLowerInvariant() switch
        {
            "processed" => q.Where(f => f.IsRenamed && f.IsNormalized),
            "unprocessed" => q.Where(f => !(f.IsRenamed && f.IsNormalized) && !f.IsDuplicate),
            "duplicates" => q.Where(f => f.IsDuplicate),
            "renamed" => q.Where(f => f.IsRenamed),
            "normalized" => q.Where(f => f.IsNormalized),
            "read" => q.Where(f => dbContext.UserFileReadStatuses
                .Any(r => r.UserId == userId && r.FilePath == f.FilePath && r.IsRead)),
            "unread" => q.Where(f => !dbContext.UserFileReadStatuses
                .Any(r => r.UserId == userId && r.FilePath == f.FilePath && r.IsRead)),
            _ => q
        };
    }

    private static IQueryable<ComicFileEntity> ApplyEntitySearch(IQueryable<ComicFileEntity> q, string? search)
    {
        if (string.IsNullOrEmpty(search)) return q;
        var pattern = $"%{search}%";
        // The path ends with the file name, so matching the path alone finds every file a match on the
        // name would, with one LIKE per row instead of two.
        return q.Where(f => EF.Functions.Like(f.FilePath, pattern));
    }

    private static IOrderedQueryable<ComicFileEntity> ApplyEntityOrder(IQueryable<ComicFileEntity> q, string? sort, string? direction)
    {
        var dir = (direction ?? "asc").ToLowerInvariant();
        return (sort?.ToLowerInvariant(), dir) switch
        {
            ("name", "desc") => q.OrderByDescending(f => f.FileName),
            ("date", "asc") => q.OrderBy(f => f.LastModified),
            ("date", "desc") => q.OrderByDescending(f => f.LastModified),
            ("size", "asc") => q.OrderBy(f => f.FileSize),
            ("size", "desc") => q.OrderByDescending(f => f.FileSize),
            _ => q.OrderBy(f => f.FileName)
        };
    }

    /// <summary>
    /// Projection shape used by paged file queries. Kept private so we can
    /// translate the EF Select cleanly without pulling DateTime conversion
    /// helpers into the SQL expression tree.
    /// </summary>
    private sealed class FileProjection
    {
        public string FilePath { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public long FileSize { get; set; }
        public DateTime LastModified { get; set; }
        public bool IsRenamed { get; set; }
        public bool IsNormalized { get; set; }
        public bool IsDuplicate { get; set; }
        public bool IsRead { get; set; }
    }

    private static FileDto ToFileDto(FileProjection p) => new()
    {
        // "relative_path" carries the full path; see FileDto.RelativePath.
        RelativePath = p.FilePath,
        Name = p.FileName,
        Size = p.FileSize,
        Modified = ToUnixSeconds(p.LastModified),
        Processed = p.IsRenamed && p.IsNormalized,
        Renamed = p.IsRenamed,
        Normalized = p.IsNormalized,
        Duplicate = p.IsDuplicate,
        Read = p.IsRead,
    };

    public async Task<PagedFilesResult> GetFilesPageAsync(
        string? filter = null,
        string? search = null,
        string? sort = "name",
        string? direction = "asc",
        int page = 1,
        int perPage = 100,
        CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var readUserId = ReadScopeUserId;

        var query = dbContext.ComicFiles.AsNoTracking().AsQueryable();
        query = ApplyEntityFilter(dbContext, query, filter);
        query = ApplyEntitySearch(query, search);

        // Page-through requests for the same view re-ask for the same total, so memoize it
        // against the store's change version and, because the read/unread filters depend on
        // it, the caller's own read-state version (which ChangeVersion does not track).
        var countCacheKey = $"FileStore.PageCount|{ChangeVersion}|{readUserId}|{GetReadStateVersion(readUserId)}|{filter}|{search}";
        if (!TryCountFromStatusCounts(filter, search, out var totalFiles)
            && !_memoryCache.TryGetValue(countCacheKey, out totalFiles))
        {
            totalFiles = await query.CountAsync(cancellationToken);
            _memoryCache.Set(countCacheKey, totalFiles, DerivedCacheTtl);
        }

        var ordered = ApplyEntityOrder(query, sort, direction);

        List<FileProjection> rows;
        int currentPage;
        int totalPages;

        if (perPage == -1)
        {
            rows = await ordered.Select(f => new FileProjection
            {
                FilePath = f.FilePath,
                FileName = f.FileName,
                FileSize = f.FileSize,
                LastModified = f.LastModified,
                IsRenamed = f.IsRenamed,
                IsNormalized = f.IsNormalized,
                IsDuplicate = f.IsDuplicate,
                IsRead = dbContext.UserFileReadStatuses
                    .Any(r => r.UserId == readUserId && r.FilePath == f.FilePath && r.IsRead),
            }).ToListAsync(cancellationToken);
            currentPage = 1;
            totalPages = 1;
        }
        else
        {
            var safePerPage = Math.Max(1, perPage);
            totalPages = totalFiles == 0 ? 1 : (int)Math.Ceiling((double)totalFiles / safePerPage);
            currentPage = Math.Max(1, Math.Min(page, totalPages));

            rows = await ordered
                .Skip((currentPage - 1) * safePerPage)
                .Take(safePerPage)
                .Select(f => new FileProjection
                {
                    FilePath = f.FilePath,
                    FileName = f.FileName,
                    FileSize = f.FileSize,
                    LastModified = f.LastModified,
                    IsRenamed = f.IsRenamed,
                    IsNormalized = f.IsNormalized,
                    IsDuplicate = f.IsDuplicate,
                    IsRead = dbContext.UserFileReadStatuses
                        .Any(r => r.UserId == readUserId && r.FilePath == f.FilePath && r.IsRead),
                })
                .ToListAsync(cancellationToken);
        }

        return new PagedFilesResult
        {
            Files = rows.Select(ToFileDto).ToList(),
            Page = currentPage,
            TotalPages = totalPages,
            TotalFiles = totalFiles,
        };
    }

    /// <summary>
    /// The number of files a page query selects, from the counts kept with the in-memory copy, when
    /// they can answer it: no search, and a status filter (or none) as <see cref="ApplyEntityFilter"/>
    /// reads it. The read/unread filters and searches are counted in SQL.
    /// </summary>
    private bool TryCountFromStatusCounts(string? filter, string? search, out int count)
    {
        count = 0;
        if (!string.IsNullOrEmpty(search) || !TryGetStatusCounts(out var kept))
        {
            return false;
        }

        int? selected = string.IsNullOrEmpty(filter) ? kept.Total : filter.ToLowerInvariant() switch
        {
            "processed" => kept.Processed,
            "unprocessed" => kept.Unprocessed,
            "duplicates" => kept.Duplicates,
            "renamed" => kept.Renamed,
            "normalized" => kept.Normalized,
            "read" or "unread" => null,
            _ => kept.Total,
        };
        count = selected ?? 0;
        return selected.HasValue;
    }

    public async Task<IReadOnlyList<FileDto>> GetFolderFilesAsync(
        string folderKey,
        string? filter = null,
        string? search = null,
        string? sort = "name",
        string? direction = "asc",
        CancellationToken cancellationToken = default)
    {
        var key = folderKey ?? string.Empty;

        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        // The raw Directory values that map to the requested folder key, so the file-level
        // filter is a `WHERE Directory IN (...)` that uses the Directory index.
        var matchedDirs = await GetDirectoriesOfFolderAsync(dbContext, key, cancellationToken);

        if (matchedDirs.Count == 0)
        {
            return Array.Empty<FileDto>();
        }

        var readUserId = ReadScopeUserId;
        var query = dbContext.ComicFiles
            .AsNoTracking()
            .Where(f => matchedDirs.Contains(f.Directory));

        query = ApplyEntityFilter(dbContext, query, filter);
        query = ApplyEntitySearch(query, search);
        var ordered = ApplyEntityOrder(query, sort, direction);

        var rows = await ordered.Select(f => new FileProjection
        {
            FilePath = f.FilePath,
            FileName = f.FileName,
            FileSize = f.FileSize,
            LastModified = f.LastModified,
            IsRenamed = f.IsRenamed,
            IsNormalized = f.IsNormalized,
            IsDuplicate = f.IsDuplicate,
            IsRead = dbContext.UserFileReadStatuses
                .Any(r => r.UserId == readUserId && r.FilePath == f.FilePath && r.IsRead),
        }).ToListAsync(cancellationToken);

        return rows.Select(ToFileDto).ToList();
    }

    /// <summary>
    /// The raw <c>Directory</c> values whose folder key (<see cref="ComputeFolderKeyFromDirectory"/>) is
    /// <paramref name="folderKey"/>.
    /// </summary>
    private async Task<IReadOnlyList<string>> GetDirectoriesOfFolderAsync(
        ComicMaintainerDbContext dbContext,
        string folderKey,
        CancellationToken cancellationToken)
    {
        var folders = await GetDirectoriesByFolderAsync(dbContext, cancellationToken);
        return folders.TryGetValue(folderKey, out var directories) ? directories : Array.Empty<string>();
    }

    /// <summary>
    /// Every distinct raw <c>Directory</c> value, grouped by its folder key, cached until the file
    /// store changes (or the watched directory does), so opening a folder in a large library
    /// neither re-runs a DISTINCT over every row nor resolves the full path of every directory.
    /// </summary>
    private async Task<Dictionary<string, List<string>>> GetDirectoriesByFolderAsync(
        ComicMaintainerDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var watchedDir = _settings.CurrentValue?.WatchedDirectory;
        var cacheKey = $"FileStore.DirectoriesByFolder|{ChangeVersion}|{watchedDir}";
        if (_memoryCache.TryGetValue(cacheKey, out Dictionary<string, List<string>>? cached) && cached != null)
        {
            return cached;
        }

        var directories = await dbContext.ComicFiles
            .AsNoTracking()
            .Select(f => f.Directory)
            .Distinct()
            .ToListAsync(cancellationToken);

        var byFolder = directories
            .GroupBy(d => ComputeFolderKeyFromDirectory(d, watchedDir), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        _memoryCache.Set(cacheKey, byFolder, DerivedCacheTtl);
        return byFolder;
    }

    public async Task<int> GetUnmarkedCountAsync(CancellationToken cancellationToken = default)
    {
        // Every status write moves ChangeVersion on, so during processing a cached count rarely
        // survives; the counts kept with the in-memory copy need no query at all.
        if (TryGetStatusCounts(out var kept))
        {
            return kept.Unprocessed;
        }

        var changeVersion = ChangeVersion;
        var cacheKey = $"{UnmarkedCountCacheKeyPrefix}.{changeVersion}";
        if (_memoryCache.TryGetValue(cacheKey, out int cached))
        {
            return cached;
        }

        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var count = await dbContext.ComicFiles
            .AsNoTracking()
            .CountAsync(f => !(f.IsRenamed && f.IsNormalized) && !f.IsDuplicate, cancellationToken);

        _memoryCache.Set(cacheKey, count, UnmarkedCountCacheTtl);
        return count;
    }

    public async Task<FolderSummariesResult> GetFolderSummariesAsync(
        string? filter = null,
        string? search = null,
        string? sort = "name",
        string? direction = "asc",
        int offset = 0,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        var sorted = await GetSortedFolderSummariesAsync(filter, search, sort, direction, cancellationToken);

        var total = sorted.Count;
        var safeOffset = Math.Max(0, offset);
        var safeLimit = Math.Max(0, limit);

        var page = safeLimit <= 0
            ? new List<FolderSummaryDto>()
            : sorted.Skip(safeOffset).Take(safeLimit).ToList();

        return new FolderSummariesResult
        {
            Folders = page,
            Offset = safeOffset,
            Limit = safeLimit,
            TotalFolders = total
        };
    }

    /// <summary>
    /// Every folder the query selects, summarized and sorted. Paging through the folder list asks for
    /// the same list once per page, and grouping it means reading every matching row, so it is cached
    /// against the store's change version and, for the read/unread filters, the caller's read-state
    /// version (which <see cref="ChangeVersion"/> does not track).
    /// </summary>
    private async Task<IReadOnlyList<FolderSummaryDto>> GetSortedFolderSummariesAsync(
        string? filter,
        string? search,
        string? sort,
        string? direction,
        CancellationToken cancellationToken)
    {
        var watchedDir = _settings.CurrentValue?.WatchedDirectory;
        var filterKey = filter?.ToLowerInvariant();
        var dir = (direction ?? "asc").ToLowerInvariant();
        var sortKey = (sort ?? "name").ToLowerInvariant();
        var readUserId = ReadScopeUserId;
        var readState = filterKey is "read" or "unread"
            ? $"{readUserId}|{GetReadStateVersion(readUserId)}"
            : string.Empty;
        var cacheKey = $"FileStore.FolderSummaries|{ChangeVersion}|{watchedDir}|{readState}|{filterKey}|{sortKey}|{dir}|{search}";
        if (_memoryCache.TryGetValue(cacheKey, out IReadOnlyList<FolderSummaryDto>? cached) && cached != null)
        {
            return cached;
        }

        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var query = dbContext.ComicFiles.AsNoTracking().AsQueryable();
        query = ApplyEntityFilter(dbContext, query, filter);
        query = ApplyEntitySearch(query, search);

        // Aggregate per raw Directory in the database. The number of distinct
        // directories is typically small relative to the file count, so the
        // post-processing (map Directory -> folder key, then sort) runs
        // on a tiny in-memory set.
        var rawGroups = await query
            .GroupBy(f => f.Directory)
            .Select(g => new
            {
                Directory = g.Key,
                FileCount = g.Count(),
                TotalSize = g.Sum(f => f.FileSize),
                LastModified = g.Max(f => f.LastModified),
                UnmarkedCount = g.Count(f => !(f.IsRenamed && f.IsNormalized) && !f.IsDuplicate),
                DuplicateCount = g.Count(f => f.IsDuplicate),
            })
            .ToListAsync(cancellationToken);

        // Multiple raw directories can collapse to the same folder key (e.g.
        // when watched-dir normalization differs), so re-aggregate by key.
        var grouped = rawGroups
            .GroupBy(g => ComputeFolderKeyFromDirectory(g.Directory, watchedDir))
            .Select(grp => new FolderSummaryDto
            {
                Path = grp.Key,
                FileCount = grp.Sum(x => x.FileCount),
                TotalSize = grp.Sum(x => x.TotalSize),
                LastModified = ToUnixSeconds(grp.Max(x => x.LastModified)),
                UnmarkedCount = grp.Sum(x => x.UnmarkedCount),
                DuplicateCount = grp.Sum(x => x.DuplicateCount),
            })
            .ToList();

        IReadOnlyList<FolderSummaryDto> sorted = sortKey switch
        {
            "date" => (dir == "desc"
                ? grouped.OrderByDescending(g => g.LastModified)
                : grouped.OrderBy(g => g.LastModified)).ToList(),
            "size" => (dir == "desc"
                ? grouped.OrderByDescending(g => g.TotalSize)
                : grouped.OrderBy(g => g.TotalSize)).ToList(),
            _ => (dir == "desc"
                ? grouped.OrderByDescending(g => g.Path, StringComparer.OrdinalIgnoreCase)
                : grouped.OrderBy(g => g.Path, StringComparer.OrdinalIgnoreCase)).ToList(),
        };

        _memoryCache.Set(cacheKey, sorted, DerivedCacheTtl);
        return sorted;
    }
}
