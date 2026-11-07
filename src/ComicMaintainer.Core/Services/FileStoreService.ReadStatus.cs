using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Data;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.EntityFrameworkCore;

namespace ComicMaintainer.Core.Services;

public partial class FileStoreService
{
    private static string ReadPathsCacheKey(string userId, long readStateVersion) =>
        $"FileStore.ReadPaths.{userId}|{readStateVersion}";

    /// <inheritdoc />
    public async Task<IReadOnlySet<string>> GetReadFilePathsAsync(string userId, CancellationToken cancellationToken = default)
        => await GetUserReadPathsAsync(userId, cancellationToken);

    /// <summary>
    /// Paths the given user has marked read. Cached because the in-memory file list
    /// (<see cref="GetFilteredFilesAsync"/>) has no way to join against the database, so
    /// without a cache every library render would re-read the whole set. The cache key is
    /// the user's own read-state version, so another user's changes never evict it.
    /// </summary>
    private async Task<HashSet<string>> GetUserReadPathsAsync(string userId, CancellationToken cancellationToken)
    {
        // Captured before reading: a change committed while we read bumps the version, so
        // whatever we cache here is never served for the newer version.
        var cacheKey = ReadPathsCacheKey(userId, GetReadStateVersion(userId));
        if (_memoryCache != null &&
            _memoryCache.TryGetValue(cacheKey, out HashSet<string>? cached) &&
            cached != null)
        {
            return cached;
        }

        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var paths = await dbContext.UserFileReadStatuses
            .AsNoTracking()
            .Where(e => e.UserId == userId && e.IsRead)
            .Select(e => e.FilePath)
            .ToListAsync(cancellationToken);

        var set = new HashSet<string>(paths, StringComparer.Ordinal);
        _memoryCache?.Set(cacheKey, set, ReadPathsCacheTtl);
        return set;
    }

    /// <summary>
    /// Records that a user's read state changed (after the change was committed). Only that
    /// user's read-state version moves: the shared <see cref="ChangeVersion"/>, and with it
    /// every other user's cached library and ETag, is left alone.
    /// </summary>
    private void InvalidateUserReadPaths(string userId)
    {
        var stale = ReadPathsCacheKey(userId, GetReadStateVersion(userId));
        MarkReadStateChanged(userId);
        _memoryCache?.Remove(stale);
    }

    /// <summary>
    /// Stamps <see cref="ComicFile.IsRead"/> on a set of files for the current user.
    /// The in-memory file store is shared across users, so read state must never be
    /// cached on it; it is applied to the returned copies instead.
    /// </summary>
    private async Task ApplyUserReadStateAsync(IReadOnlyCollection<ComicFile> files, CancellationToken cancellationToken)
    {
        var userId = CurrentUserId;
        if (userId == null || files.Count == 0)
        {
            foreach (var file in files)
            {
                file.IsRead = false;
            }
            return;
        }

        var readPaths = await GetUserReadPathsAsync(userId, cancellationToken);
        foreach (var file in files)
        {
            file.IsRead = readPaths.Contains(file.FilePath);
        }
    }

    /// <summary>
    /// Repoint every user's read status and reader progress from <paramref name="oldPath"/>
    /// to <paramref name="newPath"/> after a rename or move. Rows that already exist at the
    /// destination for a user take precedence, so stale source rows are merged or discarded.
    /// </summary>
    private async Task MoveUserReadStatusAsync(
        ComicMaintainerDbContext dbContext,
        string oldPath,
        string newPath,
        CancellationToken cancellationToken)
    {
        if (string.Equals(oldPath, newPath, StringComparison.Ordinal))
        {
            return;
        }

        var sourceRows = await dbContext.UserFileReadStatuses
            .Where(e => e.FilePath == oldPath)
            .ToListAsync(cancellationToken);

        var destinationUserIds = await dbContext.UserFileReadStatuses
            .Where(e => e.FilePath == newPath)
            .Select(e => e.UserId)
            .ToListAsync(cancellationToken);

        var occupied = new HashSet<string>(destinationUserIds, StringComparer.Ordinal);
        var now = DateTime.UtcNow;

        foreach (var row in sourceRows)
        {
            if (occupied.Contains(row.UserId))
            {
                dbContext.UserFileReadStatuses.Remove(row);
            }
            else
            {
                row.FilePath = newPath;
                row.UpdatedAt = now;
            }

        }

        var sourceProgress = await dbContext.ReadingProgresses
            .Where(e => e.ContentId == oldPath)
            .ToListAsync(cancellationToken);

        if (sourceProgress.Count > 0)
        {
            var destinationProgress = await dbContext.ReadingProgresses
                .Where(e => e.ContentId == newPath)
                .ToDictionaryAsync(e => e.UserId, e => e, StringComparer.Ordinal, cancellationToken);

            foreach (var progress in sourceProgress)
            {
                if (destinationProgress.TryGetValue(progress.UserId, out var existing))
                {
                    existing.TotalPages = Math.Max(existing.TotalPages, progress.TotalPages);
                    existing.CurrentPage = Math.Max(existing.CurrentPage, progress.CurrentPage);
                    existing.PercentComplete = Math.Max(existing.PercentComplete, progress.PercentComplete);
                    existing.CompletedAt = existing.CompletedAt.HasValue && progress.CompletedAt.HasValue
                        ? (existing.CompletedAt > progress.CompletedAt ? existing.CompletedAt : progress.CompletedAt)
                        : existing.CompletedAt ?? progress.CompletedAt;
                    existing.LastReadAt = existing.LastReadAt > progress.LastReadAt ? existing.LastReadAt : progress.LastReadAt;
                    existing.UpdatedAt = now;
                    dbContext.ReadingProgresses.Remove(progress);
                }
                else
                {
                    progress.ContentId = newPath;
                    progress.UpdatedAt = now;
                }
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        foreach (var userId in sourceRows.Select(row => row.UserId).Distinct(StringComparer.Ordinal))
        {
            InvalidateUserReadPaths(userId);
        }
        _logger.LogDebug(
            "Moved read status/progress for {Count} user(s): {OldPath} -> {NewPath}",
            sourceRows.Count, SanitizeForLogging(oldPath), SanitizeForLogging(newPath));
    }

    /// <summary>
    /// Fetch the current user's read-status row for a file, creating it if requested.
    /// Returns <c>null</c> when there is no user in scope (background work).
    /// </summary>
    private async Task<UserFileReadStatusEntity?> GetOrCreateUserReadStatusAsync(
        ComicMaintainerDbContext dbContext,
        string userId,
        string filePath,
        bool create,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.UserFileReadStatuses
            .FirstOrDefaultAsync(e => e.UserId == userId && e.FilePath == filePath, cancellationToken);

        if (entity != null || !create)
        {
            return entity;
        }

        entity = new UserFileReadStatusEntity
        {
            UserId = userId,
            FilePath = filePath,
            CurrentPage = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        dbContext.UserFileReadStatuses.Add(entity);
        return entity;
    }

    public async Task MarkFileReadAsync(string filePath, bool read, CancellationToken cancellationToken = default)
    {
        var userId = CurrentUserId;
        if (userId == null)
        {
            _logger.LogDebug(
                "Ignoring read-status update for {FilePath}: no user in scope",
                SanitizeForLogging(filePath));
            return;
        }

        try
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

            await UpsertUserReadStatusAsync(dbContext, userId, filePath, status =>
            {
                status.IsRead = read;
                status.LastReadDate = read ? DateTime.UtcNow : status.LastReadDate;
                status.UpdatedAt = DateTime.UtcNow;
            }, cancellationToken);
            InvalidateUserReadPaths(userId);
            await SyncReadingProgressAsync(dbContext, userId, new[] { filePath }, read, DateTime.UtcNow, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogDebug(
                "Updated read status for {FilePath} to {Status} for user {UserId}",
                SanitizeForLogging(filePath), read, SanitizeForLogging(userId));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller gave up (for example the request was aborted); that is not a
            // database error and must not be logged as one.
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Error updating read status in database for {FilePath}", SanitizeForLogging(filePath));
        }
    }

    public async Task MarkFilesReadAsync(IEnumerable<string> filePaths, bool read, CancellationToken cancellationToken = default)
    {
        var paths = filePaths as IList<string> ?? filePaths.ToList();
        if (paths.Count == 0)
        {
            return;
        }

        var userId = CurrentUserId;
        if (userId == null)
        {
            _logger.LogDebug("Ignoring bulk read-status update for {Count} file(s): no user in scope", paths.Count);
            return;
        }

        // One transaction, no per-file fallback: a fallback that retried every path in its own
        // transaction turned one failed (or cancelled) bulk save into thousands of writes
        // competing with the processing workers, and one logged error per file.
        try
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            var now = DateTime.UtcNow;

            // SQLite caps a statement at 999 parameters, so both the lookup and the update
            // are chunked. Chunking the lookup as well keeps a bulk "mark series read" over
            // a few thousand issues to a bounded number of round-trips.
            var existing = new Dictionary<string, UserFileReadStatusEntity>(StringComparer.Ordinal);
            foreach (var chunk in Chunk(paths, SqliteParameterChunkSize))
            {
                var rows = await dbContext.UserFileReadStatuses
                    .Where(e => e.UserId == userId && chunk.Contains(e.FilePath))
                    .ToListAsync(cancellationToken);

                foreach (var row in rows)
                {
                    existing[row.FilePath] = row;
                }
            }

            foreach (var path in paths)
            {
                if (existing.TryGetValue(path, out var row))
                {
                    row.IsRead = read;
                    if (read) row.LastReadDate = now;
                    row.UpdatedAt = now;
                }
                else
                {
                    dbContext.UserFileReadStatuses.Add(new UserFileReadStatusEntity
                    {
                        UserId = userId,
                        FilePath = path,
                        IsRead = read,
                        CurrentPage = 1,
                        LastReadDate = read ? now : null,
                        CreatedAt = now,
                        UpdatedAt = now
                    });
                }
            }

            await SyncReadingProgressAsync(dbContext, userId, paths.ToList(), read, now, cancellationToken);

            await dbContext.SaveChangesAsync(cancellationToken);
            InvalidateUserReadPaths(userId);
            _logger.LogDebug("Bulk-updated read status for {Total} files for user {UserId}", paths.Count, SanitizeForLogging(userId));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Error bulk-updating read status for {Total} files", paths.Count);
            throw;
        }
    }

    public async Task<int> MarkScopeReadAsync(FileReadScope scope, bool read, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var userId = CurrentUserId;
        if (userId == null)
        {
            _logger.LogDebug("Ignoring {Scope} read-status update: no user in scope", scope.Kind);
            return 0;
        }

        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var scopedFiles = await ScopeFilesAsync(dbContext, scope, cancellationToken);
        var scopedPaths = scopedFiles.Select(f => f.FilePath);
        var now = DateTime.UtcNow;

        // Every statement runs in one transaction: a failure or a cancellation part-way rolls
        // the whole change back instead of leaving the scope half read.
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var fileCount = await scopedFiles.CountAsync(cancellationToken);

        if (read)
        {
            await dbContext.UserFileReadStatuses
                .Where(s => s.UserId == userId && scopedPaths.Contains(s.FilePath))
                .ExecuteUpdateAsync(set => set
                    .SetProperty(s => s.IsRead, true)
                    .SetProperty(s => s.LastReadDate, now)
                    .SetProperty(s => s.UpdatedAt, now), cancellationToken);

            // Files this user has never touched have no row yet; only their paths are read.
            var missing = await scopedPaths
                .Where(path => !dbContext.UserFileReadStatuses.Any(s => s.UserId == userId && s.FilePath == path))
                .ToListAsync(cancellationToken);
            dbContext.UserFileReadStatuses.AddRange(missing.Select(path => new UserFileReadStatusEntity
            {
                UserId = userId,
                FilePath = path,
                IsRead = true,
                CurrentPage = 1,
                LastReadDate = now,
                CreatedAt = now,
                UpdatedAt = now,
            }));
            await dbContext.SaveChangesAsync(cancellationToken);

            // Keep "Continue Reading" (ReadingProgress) in step, as MarkFilesReadAsync does.
            await dbContext.ReadingProgresses
                .Where(p => p.UserId == userId && scopedPaths.Contains(p.ContentId))
                .ExecuteUpdateAsync(set => set
                    .SetProperty(p => p.CurrentPage, p => p.TotalPages > 0 ? p.TotalPages : p.CurrentPage)
                    .SetProperty(p => p.PercentComplete, 100)
                    .SetProperty(p => p.CompletedAt, p => p.CompletedAt ?? now)
                    .SetProperty(p => p.LastReadAt, now)
                    .SetProperty(p => p.UpdatedAt, now), cancellationToken);
        }
        else
        {
            // A missing row already means unread, so only existing rows change.
            await dbContext.UserFileReadStatuses
                .Where(s => s.UserId == userId && scopedPaths.Contains(s.FilePath))
                .ExecuteUpdateAsync(set => set
                    .SetProperty(s => s.IsRead, false)
                    .SetProperty(s => s.UpdatedAt, now), cancellationToken);

            await dbContext.ReadingProgresses
                .Where(p => p.UserId == userId && scopedPaths.Contains(p.ContentId))
                .ExecuteUpdateAsync(set => set
                    .SetProperty(p => p.CurrentPage, 1)
                    .SetProperty(p => p.PercentComplete, 0)
                    .SetProperty(p => p.CompletedAt, (DateTime?)null)
                    .SetProperty(p => p.LastReadAt, now)
                    .SetProperty(p => p.UpdatedAt, now), cancellationToken);
        }

        // The resume page of a file with reading progress follows that progress.
        await dbContext.UserFileReadStatuses
            .Where(s => s.UserId == userId
                && scopedPaths.Contains(s.FilePath)
                && dbContext.ReadingProgresses.Any(p => p.UserId == userId && p.ContentId == s.FilePath))
            .ExecuteUpdateAsync(set => set
                .SetProperty(s => s.CurrentPage, s => dbContext.ReadingProgresses
                    .Where(p => p.UserId == userId && p.ContentId == s.FilePath)
                    .Select(p => p.CurrentPage)
                    .First())
                .SetProperty(s => s.UpdatedAt, now), cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        InvalidateUserReadPaths(userId);

        _logger.LogInformation(
            "Marked {Count} file(s) in scope {Scope} as {Status} for user {UserId}",
            fileCount, scope.Kind, read ? "read" : "unread", SanitizeForLogging(userId));
        return fileCount;
    }

    /// <summary>The tracked rows a <see cref="FileReadScope"/> selects, as a query.</summary>
    private async Task<IQueryable<ComicFileEntity>> ScopeFilesAsync(
        ComicMaintainerDbContext dbContext,
        FileReadScope scope,
        CancellationToken cancellationToken)
    {
        var files = dbContext.ComicFiles.AsNoTracking();
        switch (scope.Kind)
        {
            case FileReadScopeKind.Library:
                return files;
            case FileReadScopeKind.Folder:
                // The same folder-key mapping the folder's file list uses.
                var watchedDir = _settings.CurrentValue?.WatchedDirectory;
                var directories = (await GetDistinctDirectoriesAsync(dbContext, cancellationToken))
                    .Where(d => string.Equals(ComputeFolderKeyFromDirectory(d, watchedDir), scope.FolderKey, StringComparison.Ordinal))
                    .ToList();
                return files.Where(f => directories.Contains(f.Directory));
            default:
                var paths = scope.FilePaths ?? Array.Empty<string>();
                return files.Where(f => paths.Contains(f.FilePath));
        }
    }

    public async Task SaveReadingProgressAsync(string filePath, int currentPage, CancellationToken cancellationToken = default)
    {
        if (currentPage < 1)
        {
            _logger.LogWarning("Invalid page number {Page} for {FilePath}, must be >= 1", currentPage, SanitizeForLogging(filePath));
            return;
        }

        var userId = CurrentUserId;
        if (userId == null)
        {
            _logger.LogDebug(
                "Ignoring reading-progress update for {FilePath}: no user in scope",
                SanitizeForLogging(filePath));
            return;
        }

        try
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

            await UpsertUserReadStatusAsync(dbContext, userId, filePath, status =>
            {
                status.CurrentPage = currentPage;
                status.LastReadDate = DateTime.UtcNow;
                status.UpdatedAt = DateTime.UtcNow;
            }, cancellationToken);
            _logger.LogDebug("Updated reading progress for {FilePath} to page {Page}", SanitizeForLogging(filePath), currentPage);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Error saving reading progress for {FilePath}", SanitizeForLogging(filePath));
        }
    }

    public async Task<int> GetReadingProgressAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var userId = CurrentUserId;
        if (userId == null)
        {
            return 1;
        }

        try
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

            var currentPage = await dbContext.UserFileReadStatuses
                .AsNoTracking()
                .Where(e => e.UserId == userId && e.FilePath == filePath)
                .Select(e => (int?)e.CurrentPage)
                .FirstOrDefaultAsync(cancellationToken);

            return currentPage is > 0 ? currentPage.Value : 1;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Error getting reading progress for {FilePath}", SanitizeForLogging(filePath));
            return 1; // Default to page 1 on error
        }
    }

    /// <summary>
    /// Apply <paramref name="update"/> to the user's read-status row for a file, creating the
    /// row if needed, and persist it, tolerating the race where a concurrent request inserted
    /// the same (user, path) pair between our lookup and this save.
    /// </summary>
    /// <remarks>
    /// On that race only <paramref name="update"/> is replayed on the row that won. Copying
    /// the row we failed to insert would also copy the defaults this request never meant to
    /// set (unread, page 1) over the winner's change: the reader posts progress and mark-read
    /// together for a comic whose first page shown is its last, so a finished comic stayed
    /// unread or its saved page went back to 1.
    /// </remarks>
    private async Task UpsertUserReadStatusAsync(
        ComicMaintainerDbContext dbContext,
        string userId,
        string filePath,
        Action<UserFileReadStatusEntity> update,
        CancellationToken cancellationToken)
    {
        var status = (await GetOrCreateUserReadStatusAsync(dbContext, userId, filePath, create: true, cancellationToken))!;
        update(status);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Microsoft.Data.Sqlite.SqliteException sqliteEx &&
                                           sqliteEx.SqliteErrorCode == 19) // UNIQUE constraint
        {
            dbContext.Entry(status).State = EntityState.Detached;

            var winner = await dbContext.UserFileReadStatuses
                .FirstOrDefaultAsync(e => e.UserId == userId && e.FilePath == filePath, cancellationToken);

            if (winner == null)
            {
                throw;
            }

            update(winner);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Keep the reader's per-user <see cref="ReadingProgressEntity"/> in step with a
    /// read/unread change made from the library.
    /// </summary>
    /// <remarks>
    /// "Continue Reading" is derived from ReadingProgress alone, so without this a user who
    /// marked a series read from the library would still see it offered as in-progress.
    /// Only existing rows are updated: a file the user has never opened has no progress
    /// record, is therefore not in Continue Reading, and creating one would require reading
    /// the archive for a page count — prohibitive for a bulk mark over a whole series.
    /// </remarks>
    private static async Task SyncReadingProgressAsync(
        ComicMaintainerDbContext dbContext,
        string userId,
        IReadOnlyCollection<string> filePaths,
        bool read,
        DateTime now,
        CancellationToken cancellationToken)
    {
        foreach (var chunk in Chunk(filePaths, SqliteParameterChunkSize))
        {
            var rows = await dbContext.ReadingProgresses
                .Where(p => p.UserId == userId && chunk.Contains(p.ContentId))
                .ToListAsync(cancellationToken);
            var statuses = await dbContext.UserFileReadStatuses
                .Where(p => p.UserId == userId && chunk.Contains(p.FilePath))
                .ToDictionaryAsync(p => p.FilePath, p => p, StringComparer.Ordinal, cancellationToken);

            foreach (var row in rows)
            {
                if (read)
                {
                    if (row.TotalPages > 0)
                    {
                        row.CurrentPage = row.TotalPages;
                    }
                    row.PercentComplete = 100;
                    row.CompletedAt ??= now;
                }
                else
                {
                    // Reopening a finished issue starts it over; leaving CurrentPage at the
                    // last page would put the reader straight back at the end.
                    row.CurrentPage = 1;
                    row.PercentComplete = 0;
                    row.CompletedAt = null;
                }

                row.LastReadAt = now;
                row.UpdatedAt = now;

                if (statuses.TryGetValue(row.ContentId, out var status))
                {
                    status.CurrentPage = row.CurrentPage;
                    status.UpdatedAt = now;
                }
            }
        }
    }
}
