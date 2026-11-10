using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Bulk path changes for whole directories, used when a folder is renamed, moved or deleted
/// outside the application (a file manager, a downloader, a shell).
/// </summary>
public partial class FileStoreService
{
    private static readonly char[] DirectorySeparators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    /// <inheritdoc />
    public async Task<int> MoveDirectoryAsync(string oldDirectory, string newDirectory, CancellationToken cancellationToken = default)
    {
        if (!TryGetDirectoryPrefix(oldDirectory, out var oldPrefix) ||
            !TryGetDirectoryPrefix(newDirectory, out var newPrefix) ||
            string.Equals(oldPrefix, newPrefix, StringComparison.Ordinal))
        {
            return 0;
        }

        if (!IsPathWithinAllowedDirectories(newDirectory))
        {
            _logger.LogWarning("Attempted to move tracked files to a directory outside allowed directories: {NewDirectory}", SanitizeForLogging(newDirectory));
            return 0;
        }

        string Rebase(string path) => newPrefix + path[oldPrefix.Length..];

        var cachedPaths = TrackedPathsUnder(oldPrefix);
        var moved = 0;
        IReadOnlyCollection<ComicFileEntity> movedRows = Array.Empty<ComicFileEntity>();
        var affectedUsers = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

            // The watcher calls this for every rename it cannot tell is a file's, most of them of
            // files that are not comics; don't take SQLite's write lock for those.
            if (cachedPaths.Count == 0 && !await AnyRowUnderAsync(dbContext.ComicFiles, e => e.FilePath, oldPrefix, cancellationToken))
            {
                return 0;
            }

            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

            var rows = await RowsUnderAsync(dbContext.ComicFiles, e => e.FilePath, oldPrefix, cachedPaths, cancellationToken);
            if (rows.Count > 0)
            {
                // A row may already exist at a destination path, e.g. one a rescan added for the
                // renamed folder before this ran. The moved row is the file's record (processing
                // state, user metadata edits and locks), so it wins and absorbs the other's state.
                var destinationPaths = rows.Select(r => Rebase(r.FilePath)).ToList();
                var occupied = new Dictionary<string, ComicFileEntity>(StringComparer.Ordinal);
                foreach (var chunk in Chunk(destinationPaths, SqliteParameterChunkSize))
                {
                    foreach (var entity in await dbContext.ComicFiles.Where(e => chunk.Contains(e.FilePath)).ToListAsync(cancellationToken))
                    {
                        occupied[entity.FilePath] = entity;
                    }
                }

                foreach (var row in rows)
                {
                    if (!occupied.TryGetValue(Rebase(row.FilePath), out var existing))
                    {
                        continue;
                    }

                    MergeIntoMovedRow(row, existing);
                    dbContext.ComicFiles.Remove(existing);
                }

                // Clear the destinations before renaming onto them: the path index is unique.
                await dbContext.SaveChangesAsync(cancellationToken);

                var now = DateTime.UtcNow;
                foreach (var row in rows)
                {
                    row.FilePath = Rebase(row.FilePath);
                    row.Directory = Path.GetDirectoryName(row.FilePath) ?? string.Empty;
                    row.UpdatedAt = now;
                }

                await dbContext.SaveChangesAsync(cancellationToken);
                moved = rows.Count;
                movedRows = rows;
            }

            await MoveUserStateUnderAsync(dbContext, oldPrefix, Rebase, cachedPaths, affectedUsers, cancellationToken);

            await MoveEmailDeliveriesUnderAsync(dbContext, oldPrefix, Rebase, cachedPaths, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error moving tracked files from {OldDirectory} to {NewDirectory}",
                SanitizeForLogging(oldDirectory), SanitizeForLogging(newDirectory));
            return 0;
        }

        // The database now has every row under the new directory; make the in-memory copy agree.
        await MirrorCommittedRowsAsync(movedRows, cachedPaths);

        foreach (var userId in affectedUsers)
        {
            InvalidateUserReadPaths(userId);
        }

        MarkChanged();
        if (moved > 0 || cachedPaths.Count > 0)
        {
            _logger.LogInformation("Moved {Count} tracked file(s) from {OldDirectory} to {NewDirectory}",
                Math.Max(moved, cachedPaths.Count), SanitizeForLogging(oldDirectory), SanitizeForLogging(newDirectory));
            await BroadcastFileListUpdateSafeAsync();
        }

        return Math.Max(moved, cachedPaths.Count);
    }

    /// <inheritdoc />
    public async Task<int> RemoveDirectoryAsync(string directory, CancellationToken cancellationToken = default)
    {
        if (!TryGetDirectoryPrefix(directory, out var prefix))
        {
            return 0;
        }

        var cachedPaths = TrackedPathsUnder(prefix);
        var removed = 0;
        var removedPaths = new List<string>(cachedPaths);

        try
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            var rows = await RowsUnderAsync(dbContext.ComicFiles, e => e.FilePath, prefix, cachedPaths, cancellationToken);
            if (rows.Count > 0)
            {
                dbContext.ComicFiles.RemoveRange(rows);
                await dbContext.SaveChangesAsync(cancellationToken);
                removed = rows.Count;
                removedPaths.AddRange(rows.Select(r => r.FilePath));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error removing tracked files under {Directory}", SanitizeForLogging(directory));
            return 0;
        }

        await MirrorCommittedRowsAsync(Array.Empty<ComicFileEntity>(), removedPaths);

        if (removed > 0 || cachedPaths.Count > 0)
        {
            // Derived views (the series library snapshot, the library ETag) key their caches on
            // the change version; they must not keep serving the removed files.
            MarkChanged();
            _logger.LogInformation("Removed {Count} tracked file(s) under deleted directory {Directory}",
                Math.Max(removed, cachedPaths.Count), SanitizeForLogging(directory));
            await BroadcastFileListUpdateSafeAsync();
        }

        return Math.Max(removed, cachedPaths.Count);
    }

    /// <summary>
    /// Repoints every user's read status and reader progress under <paramref name="oldPrefix"/>.
    /// As for a single rename (<see cref="MoveUserReadStatusAsync"/>), a user's row that already
    /// exists at the destination wins for read status, and progress is merged.
    /// </summary>
    private async Task MoveUserStateUnderAsync(
        ComicMaintainerDbContext dbContext,
        string oldPrefix,
        Func<string, string> rebase,
        IReadOnlyCollection<string> cachedPaths,
        HashSet<string> affectedUsers,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;

        var statuses = await RowsUnderAsync(dbContext.UserFileReadStatuses, e => e.FilePath, oldPrefix, cachedPaths, cancellationToken);
        if (statuses.Count > 0)
        {
            var destinations = statuses.Select(s => rebase(s.FilePath)).Distinct(StringComparer.Ordinal).ToList();
            var occupied = new HashSet<(string UserId, string FilePath)>();
            foreach (var chunk in Chunk(destinations, SqliteParameterChunkSize))
            {
                foreach (var row in await dbContext.UserFileReadStatuses
                             .Where(e => chunk.Contains(e.FilePath))
                             .Select(e => new { e.UserId, e.FilePath })
                             .ToListAsync(cancellationToken))
                {
                    occupied.Add((row.UserId, row.FilePath));
                }
            }

            foreach (var status in statuses)
            {
                affectedUsers.Add(status.UserId);
                var newPath = rebase(status.FilePath);
                if (occupied.Contains((status.UserId, newPath)))
                {
                    dbContext.UserFileReadStatuses.Remove(status);
                }
                else
                {
                    status.FilePath = newPath;
                    status.UpdatedAt = now;
                }
            }
        }

        var progresses = await RowsUnderAsync(dbContext.ReadingProgresses, e => e.ContentId, oldPrefix, cachedPaths, cancellationToken);
        if (progresses.Count > 0)
        {
            var destinations = progresses.Select(p => rebase(p.ContentId)).Distinct(StringComparer.Ordinal).ToList();
            var existing = new Dictionary<(string UserId, string ContentId), ReadingProgressEntity>();
            foreach (var chunk in Chunk(destinations, SqliteParameterChunkSize))
            {
                foreach (var row in await dbContext.ReadingProgresses.Where(e => chunk.Contains(e.ContentId)).ToListAsync(cancellationToken))
                {
                    existing[(row.UserId, row.ContentId)] = row;
                }
            }

            foreach (var progress in progresses)
            {
                affectedUsers.Add(progress.UserId);
                var newPath = rebase(progress.ContentId);
                if (existing.TryGetValue((progress.UserId, newPath), out var destination))
                {
                    destination.TotalPages = Math.Max(destination.TotalPages, progress.TotalPages);
                    destination.CurrentPage = Math.Max(destination.CurrentPage, progress.CurrentPage);
                    destination.PercentComplete = Math.Max(destination.PercentComplete, progress.PercentComplete);
                    destination.CompletedAt = destination.CompletedAt.HasValue && progress.CompletedAt.HasValue
                        ? (destination.CompletedAt > progress.CompletedAt ? destination.CompletedAt : progress.CompletedAt)
                        : destination.CompletedAt ?? progress.CompletedAt;
                    destination.LastReadAt = destination.LastReadAt > progress.LastReadAt ? destination.LastReadAt : progress.LastReadAt;
                    destination.UpdatedAt = now;
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
    }

    /// <summary>
    /// Folds the state of a row found at a moved file's destination into the moved row, which
    /// is about to replace it, so nothing either of them recorded is lost.
    /// </summary>
    private static void MergeIntoMovedRow(ComicFileEntity moved, ComicFileEntity existing)
    {
        moved.IsRenamed |= existing.IsRenamed;
        moved.IsNormalized |= existing.IsNormalized;
        moved.IsDuplicate |= existing.IsDuplicate;
        moved.IsProcessingPending |= existing.IsProcessingPending;
        moved.IsProcessed = ComputeProcessedState(moved.IsRenamed, moved.IsNormalized);
        if (existing.CreatedAt < moved.CreatedAt)
        {
            moved.CreatedAt = existing.CreatedAt;
        }

        if (moved.Metadata is null && existing.Metadata is not null)
        {
            // Owned entities are keyed by their owner, so the other row's instance cannot move.
            moved.Metadata = existing.Metadata.Clone();
            moved.LastDbEditAt = existing.LastDbEditAt;
            moved.LastWriteAt = existing.LastWriteAt;
            moved.MetadataSource = existing.MetadataSource;
        }

        moved.SeriesMetadataVersion = Math.Max(moved.SeriesMetadataVersion, existing.SeriesMetadataVersion);
    }

    /// <summary>
    /// Rows whose path (<paramref name="pathOf"/>) is under <paramref name="prefix"/>. Found with
    /// a range scan of the path index, which matches the prefix exactly, plus the in-memory
    /// matches (<paramref name="cachedPaths"/>), which on Windows include paths spelled in
    /// another case; every candidate is then checked with the platform's path comparison.
    /// </summary>
    private static async Task<List<T>> RowsUnderAsync<T>(
        IQueryable<T> rows,
        System.Linq.Expressions.Expression<Func<T, string>> pathOf,
        string prefix,
        IReadOnlyCollection<string> cachedPaths,
        CancellationToken cancellationToken)
        where T : class
    {
        var path = pathOf.Compile();

        var found = await rows
            .Where(BuildRange(pathOf, prefix))
            .ToListAsync(cancellationToken);

        foreach (var chunk in Chunk(cachedPaths, SqliteParameterChunkSize))
        {
            found.AddRange(await rows.Where(BuildIn(pathOf, chunk)).ToListAsync(cancellationToken));
        }

        return found
            .Distinct()
            .Where(row => IsUnderPrefix(path(row), prefix))
            .ToList();
    }

    /// <summary>
    /// Whether any row's path (<paramref name="pathOf"/>) starts with <paramref name="prefix"/>
    /// exactly, found with a range scan of the path index. Paths spelled in another case are
    /// not found; callers look for those in the in-memory store.
    /// </summary>
    private static Task<bool> AnyRowUnderAsync<T>(
        IQueryable<T> rows,
        System.Linq.Expressions.Expression<Func<T, string>> pathOf,
        string prefix,
        CancellationToken cancellationToken)
        where T : class =>
        rows.AnyAsync(BuildRange(pathOf, prefix), cancellationToken);

    /// <summary>
    /// The paths that start with <paramref name="prefix"/>, which ends with a separator, as a
    /// range the path index serves: every such path sorts at or after "dir/" and before "dir0"
    /// (the separator's successor), in SQLite's binary order as in ordinal order.
    /// </summary>
    private static System.Linq.Expressions.Expression<Func<T, bool>> BuildRange<T>(
        System.Linq.Expressions.Expression<Func<T, string>> pathOf, string prefix)
    {
        var lower = prefix;
        var upper = prefix[..^1] + (char)(prefix[^1] + 1);

        // string.Compare(path, bound) translates to a plain comparison in SQL.
        var compare = typeof(string).GetMethod(nameof(string.Compare), [typeof(string), typeof(string)])!;
        var body = System.Linq.Expressions.Expression.AndAlso(
            System.Linq.Expressions.Expression.GreaterThanOrEqual(
                System.Linq.Expressions.Expression.Call(compare, pathOf.Body, System.Linq.Expressions.Expression.Constant(lower)),
                System.Linq.Expressions.Expression.Constant(0)),
            System.Linq.Expressions.Expression.LessThan(
                System.Linq.Expressions.Expression.Call(compare, pathOf.Body, System.Linq.Expressions.Expression.Constant(upper)),
                System.Linq.Expressions.Expression.Constant(0)));
        return System.Linq.Expressions.Expression.Lambda<Func<T, bool>>(body, pathOf.Parameters);
    }

    private static System.Linq.Expressions.Expression<Func<T, bool>> BuildIn<T>(
        System.Linq.Expressions.Expression<Func<T, string>> pathOf, List<string> paths)
    {
        var contains = typeof(List<string>).GetMethod(nameof(List<string>.Contains), [typeof(string)])!;
        var body = System.Linq.Expressions.Expression.Call(System.Linq.Expressions.Expression.Constant(paths), contains, pathOf.Body);
        return System.Linq.Expressions.Expression.Lambda<Func<T, bool>>(body, pathOf.Parameters);
    }

    /// <summary>Tracked in-memory paths under <paramref name="prefix"/>.</summary>
    private List<string> TrackedPathsUnder(string prefix) =>
        _files.Keys.Where(path => IsUnderPrefix(path, prefix)).ToList();

    private static bool IsUnderPrefix(string path, string prefix) =>
        path.Length > prefix.Length && path.StartsWith(prefix, LibraryPathValidator.PathComparison);

    /// <summary>
    /// The full path of <paramref name="directory"/> followed by one separator: the prefix the
    /// paths of everything inside it start with. False for a blank or unusable path, or a root.
    /// </summary>
    private static bool TryGetDirectoryPrefix(string? directory, out string prefix)
    {
        prefix = string.Empty;
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        try
        {
            var trimmed = Path.GetFullPath(directory).TrimEnd(DirectorySeparators);
            if (trimmed.Length == 0 || string.IsNullOrEmpty(Path.GetFileName(trimmed)))
            {
                return false;
            }

            prefix = trimmed + Path.DirectorySeparatorChar;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
