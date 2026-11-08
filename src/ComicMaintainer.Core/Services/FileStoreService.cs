using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Utilities;
using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.EntityFrameworkCore;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Tracks the library's comic files in the <c>ComicFiles</c> table, with an in-memory
/// copy of every row for the hot per-file checks and the series library build.
/// </summary>
/// <remarks>
/// The database is the single source of truth. Every write commits to the database
/// first and only then refreshes the in-memory copy from the committed row
/// (<see cref="MirrorCommittedRowsAsync"/>), so a failed or cancelled write leaves both
/// exactly as they were and every endpoint, whichever of the two it reads, reports the
/// same state.
/// </remarks>
public partial class FileStoreService : IFileStoreService, IChangeVersionSource, IUserReadStateSource
{
    private const string UnmarkedCountCacheKeyPrefix = "FileStore.UnmarkedCount";
    private static readonly TimeSpan UnmarkedCountCacheTtl = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReadPathsCacheTtl = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Lifetime of values derived from the file table (page counts, distinct
    /// directories). Their cache keys embed <see cref="ChangeVersion"/>, so a
    /// mutation makes them unreachable immediately; the TTL just evicts the
    /// orphaned entries and bounds staleness from out-of-band DB edits.
    /// </summary>
    private static readonly TimeSpan DerivedCacheTtl = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Batch size for IN-list queries. SQLite defaults to a 999-parameter limit per
    /// statement; 500 leaves headroom for the other predicates in the same query.
    /// </summary>
    private const int SqliteParameterChunkSize = 500;

    /// <summary>
    /// In-memory copy of every committed <c>ComicFiles</c> row, keyed by path. Entries are
    /// replaced wholesale (never mutated in place) and only after the database commit, so a
    /// reader always sees a row exactly as it was saved.
    /// </summary>
    private readonly ConcurrentDictionary<string, ComicFile> _files = new();

    /// <summary>Serializes refreshing <see cref="_files"/> from the database; see <see cref="MirrorCommittedRowsAsync"/>.</summary>
    private readonly SemaphoreSlim _mirrorLock = new(1, 1);
    private readonly IOptionsMonitor<AppSettings> _settings;
    private readonly ILogger<FileStoreService> _logger;
    private readonly IDbContextFactory<ComicMaintainerDbContext> _dbContextFactory;
    private readonly IEventBroadcaster? _eventBroadcaster;
    private readonly IMemoryCache? _memoryCache;
    private readonly IUserContextAccessor? _userContext;
    private long _changeVersion;

    /// <summary>Per-user read-state counters; see <see cref="GetReadStateVersion"/>.</summary>
    private readonly ConcurrentDictionary<string, long> _readStateVersions = new(StringComparer.Ordinal);

    /// <inheritdoc />
    /// <remarks>
    /// Bumped whenever the tracked file list or a file's stored state changes. Per-user
    /// read state does not bump it (see <see cref="GetReadStateVersion"/>), so one user
    /// marking an issue read does not invalidate every user's cached library. Derived
    /// views (series library snapshot, combinable folders, folder-key maps) key their
    /// caches on this value.
    /// </remarks>
    public long ChangeVersion => Interlocked.Read(ref _changeVersion);

    /// <summary>Records that the file list or a file's stored state changed.</summary>
    private void MarkChanged() => Interlocked.Increment(ref _changeVersion);

    /// <inheritdoc />
    public long GetReadStateVersion(string userId)
        => _readStateVersions.TryGetValue(userId, out var version) ? version : 0;

    /// <summary>Records that <paramref name="userId"/>'s read state changed.</summary>
    private void MarkReadStateChanged(string userId)
        => _readStateVersions.AddOrUpdate(userId, 1, (_, version) => version + 1);

    /// <summary>
    /// Replaces the in-memory copy of a row with what was just committed to the database.
    /// Only ever called after a successful <c>SaveChanges</c>, which keeps the copy in step
    /// with the database instead of ahead of it.
    /// </summary>
    private void ApplyCommittedRow(ComicFileEntity entity)
        => _files[entity.FilePath] = ToInMemoryCopy(entity);

    /// <summary>Drops the in-memory copy of a row whose deletion was just committed.</summary>
    private void ForgetCommittedRow(string filePath)
        => _files.TryRemove(filePath, out _);

    /// <summary>Refreshes the in-memory copy of one row after its write committed.</summary>
    private Task MirrorCommittedRowAsync(ComicFileEntity saved)
        => MirrorCommittedRowsAsync(new[] { saved });

    /// <summary>
    /// Refreshes the in-memory copy of the given rows after their writes committed, from
    /// what the database holds now rather than from the tracked entities. Those entities
    /// were loaded before the save and EF writes only the columns that changed, so copying
    /// one could put back a stale value of a column another writer committed in between.
    /// </summary>
    /// <remarks>
    /// Reading and applying happen under one lock, so the refreshes of a row cannot be
    /// applied out of order: whichever runs last read the row after the last commit. SQLite
    /// has a single writer anyway, and this read is far cheaper than the write it follows,
    /// so the lock costs no meaningful throughput. Should the read fail, the entities are
    /// the best knowledge available. Not cancellable: the write has already committed.
    /// </remarks>
    /// <param name="saved">Rows that were inserted or updated.</param>
    /// <param name="removed">Paths whose rows were deleted (or moved away).</param>
    private async Task MirrorCommittedRowsAsync(
        IReadOnlyCollection<ComicFileEntity> saved,
        IReadOnlyCollection<string>? removed = null)
    {
        var paths = saved.Select(e => e.FilePath)
            .Concat(removed ?? Array.Empty<string>())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        await _mirrorLock.WaitAsync(CancellationToken.None);
        try
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(CancellationToken.None);
            foreach (var chunk in paths.Chunk(SqliteParameterChunkSize))
            {
                var rows = await dbContext.ComicFiles
                    .AsNoTracking()
                    .Where(e => chunk.Contains(e.FilePath))
                    .ToDictionaryAsync(e => e.FilePath, StringComparer.Ordinal, CancellationToken.None);
                foreach (var path in chunk)
                {
                    if (rows.TryGetValue(path, out var row))
                    {
                        ApplyCommittedRow(row);
                    }
                    else
                    {
                        ForgetCommittedRow(path);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Could not re-read {Count} committed row(s); updating the in-memory copy from the saved values", paths.Count);
            foreach (var entity in saved)
            {
                ApplyCommittedRow(entity);
            }
            foreach (var path in removed ?? Array.Empty<string>())
            {
                ForgetCommittedRow(path);
            }
        }
        finally
        {
            _mirrorLock.Release();
        }
    }

    public FileStoreService(
        IOptionsMonitor<AppSettings> settings,
        ILogger<FileStoreService> logger,
        IDbContextFactory<ComicMaintainerDbContext> dbContextFactory,
        IEventBroadcaster? eventBroadcaster = null,
        IMemoryCache? memoryCache = null,
        IUserContextAccessor? userContext = null)
    {
        _settings = settings;
        _logger = logger;
        _dbContextFactory = dbContextFactory;
        _eventBroadcaster = eventBroadcaster;
        _memoryCache = memoryCache;
        _userContext = userContext;
    }

    /// <summary>
    /// Identifier of the user whose read state should be reflected, or <c>null</c> when
    /// running outside a request. Read state is per-user, so background work (scans,
    /// audits, the watcher) has no meaningful answer and reports everything as unread.
    /// </summary>
    private string? CurrentUserId
    {
        get
        {
            var userId = _userContext?.UserId;
            return string.IsNullOrEmpty(userId) ? null : userId;
        }
    }

    private static string SanitizeForLogging(string? input)
    {
        if (string.IsNullOrEmpty(input))
            return string.Empty;
        
        // Remove newlines and carriage returns to prevent log forging
        return input.Replace("\r", "").Replace("\n", "");
    }

    /// <summary>
    /// Computes the processed state from renamed and normalized states
    /// </summary>
    private static bool ComputeProcessedState(bool isRenamed, bool isNormalized)
    {
        return isRenamed && isNormalized;
    }

    private bool IsPathWithinAllowedDirectories(string filePath)
    {
        try
        {
            // GetFullPath collapses every "." and ".." segment, so traversal out of the library
            // ends up outside both roots and is rejected. Names that merely contain consecutive
            // dots ("What If...?") are legitimate comics. The store only tracks paths, so the
            // lexical check is enough; code that opens a file resolves links itself.
            var settings = _settings.CurrentValue;
            return LibraryPathValidator.IsLexicallyWithin(filePath, settings.WatchedDirectory, settings.DuplicateDirectory);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Error validating path: {FilePath}", SanitizeForLogging(filePath));
            return false;
        }
    }


    private static ComicFile ToComicFile(ComicFileEntity entity)
    {
        Enum.TryParse<FileMetadataSource>(entity.MetadataSource, ignoreCase: true, out var source);
        return new ComicFile
        {
            FilePath = entity.FilePath,
            FileName = entity.FileName,
            Directory = entity.Directory,
            FileSize = entity.FileSize,
            LastModified = entity.LastModified,
            CreatedAt = entity.CreatedAt,
            IsProcessed = ComputeProcessedState(entity.IsRenamed, entity.IsNormalized),
            IsRenamed = entity.IsRenamed,
            IsNormalized = entity.IsNormalized,
            IsDuplicate = entity.IsDuplicate,
            // Read state is per-user and lives in UserFileReadStatuses; it is stamped onto
            // the returned ComicFile by ApplyUserReadStateAsync. The legacy global
            // ComicFiles.IsRead column is deliberately not read here.
            IsRead = false,
            Metadata = entity.Metadata?.Clone(),
            SeriesMetadataVersion = entity.SeriesMetadataVersion,
            MetadataVersion = entity.MetadataVersion,
            WrittenMetadataVersion = entity.WrittenMetadataVersion,
            LastDbEditAt = entity.LastDbEditAt,
            LastWriteAt = entity.LastWriteAt,
            MetadataSource = source
        };
    }

    /// <summary>
    /// The row as kept in <see cref="_files"/>: without the free-text archive fields
    /// (summary, authors, tags) of a row whose metadata the scan cached, which nothing reads
    /// from memory and which would otherwise sit on the heap for every issue in the library.
    /// <see cref="GetFileAsync"/> reads them from the database. A user's edits are kept whole,
    /// because a row can be restored from this copy after a concurrent delete (a restored
    /// scanned row loses its scan stamp, so the background scan fills those fields back in).
    /// </summary>
    private static ComicFile ToInMemoryCopy(ComicFileEntity entity)
    {
        var file = ToComicFile(entity);
        if (file.Metadata is { IsUserEdited: false } metadata)
        {
            metadata.Summary = null;
            metadata.Authors = new List<string>();
            metadata.Tags = new List<string>();
        }
        return file;
    }

    private static void CopyNonNullMetadataFields(ComicMetadata target, ComicMetadata patch)
    {
        if (patch.Series is not null) target.Series = patch.Series;
        if (patch.Title is not null) target.Title = patch.Title;
        if (patch.Issue is not null) target.Issue = patch.Issue;
        if (patch.Volume is not null) target.Volume = patch.Volume;
        if (patch.Publisher is not null) target.Publisher = patch.Publisher;
        if (patch.Year.HasValue) target.Year = patch.Year;
        if (patch.Summary is not null) target.Summary = patch.Summary;
        if (patch.Authors.Count > 0) target.Authors = new List<string>(patch.Authors);
        if (patch.Tags.Count > 0) target.Tags = new List<string>(patch.Tags);
    }

    private async Task BroadcastFileListUpdateSafeAsync()
    {
        // Invalidate cached aggregates whose values depend on the file list,
        // so the next request recomputes them from the database.
        MarkChanged();

        if (_eventBroadcaster is null) return;
        try
        {
            await _eventBroadcaster.BroadcastFileListUpdateAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Failed to broadcast file list update");
        }
    }

    private static ComicFileEntity CreateFileEntity(string filePath, bool isDuplicate = false)
    {
        var fileInfo = new FileInfo(filePath);
        return new ComicFileEntity
        {
            FilePath = filePath,
            FileName = fileInfo.Name,
            Directory = fileInfo.DirectoryName ?? string.Empty,
            FileSize = fileInfo.Length,
            LastModified = fileInfo.LastWriteTime,
            IsProcessed = false, // Will be computed from IsRenamed && IsNormalized
            IsRenamed = false, // Initialize to false by default
            IsNormalized = false, // Initialize to false by default
            IsDuplicate = isDuplicate,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            MetadataSource = FileMetadataSource.Scanned.ToString()
        };
    }

    /// <summary>
    /// Split a sequence into fixed-size batches. Used to keep generated SQL inside
    /// SQLite's 999-parameter limit.
    /// </summary>
    private static IEnumerable<List<T>> Chunk<T>(IEnumerable<T> source, int size)
    {
        var batch = new List<T>(size);
        foreach (var item in source)
        {
            batch.Add(item);
            if (batch.Count == size)
            {
                yield return batch;
                batch = new List<T>(size);
            }
        }

        if (batch.Count > 0)
        {
            yield return batch;
        }
    }


    /// <summary>
    /// Compute the directory key for a file path, relative to the watched
    /// directory, normalized to forward slashes. Returns empty string when the
    /// file lives directly inside the watched directory.
    /// </summary>
    public static string ComputeFolderKey(string filePath, string? watchedDirectory)
    {
        if (string.IsNullOrEmpty(filePath))
            return string.Empty;

        try
        {
            var fileDir = Path.GetDirectoryName(filePath) ?? string.Empty;

            if (!string.IsNullOrEmpty(watchedDirectory))
            {
                // Normalize and strip trailing directory separators so paths
                // like "/tmp/" and "/tmp" compare equal.
                var watchedFull = Path.GetFullPath(watchedDirectory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var fileDirFull = string.IsNullOrEmpty(fileDir)
                    ? string.Empty
                    : Path.GetFullPath(fileDir)
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                if (!string.IsNullOrEmpty(fileDirFull) && LibraryPathValidator.IsSameOrUnder(fileDirFull, watchedFull))
                {
                    var rel = Path.GetRelativePath(watchedFull, fileDirFull);
                    if (rel == "." || string.IsNullOrEmpty(rel))
                    {
                        return string.Empty;
                    }
                    return rel.Replace('\\', '/');
                }
            }

            // Fallback: file lives outside watched dir, use directory portion.
            return fileDir.Replace('\\', '/');
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Compute the directory key for a raw directory path, relative to the
    /// watched directory, normalized to forward slashes. Returns empty string
    /// when the directory IS the watched directory itself. Used by the
    /// DB-backed folder summary aggregation, which groups by raw
    /// <c>Directory</c> column values before mapping them to folder keys.
    /// </summary>
    public static string ComputeFolderKeyFromDirectory(string? directory, string? watchedDirectory)
    {
        if (string.IsNullOrEmpty(directory))
            return string.Empty;

        try
        {
            if (!string.IsNullOrEmpty(watchedDirectory))
            {
                var watchedFull = Path.GetFullPath(watchedDirectory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var dirFull = Path.GetFullPath(directory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                if (LibraryPathValidator.IsSameOrUnder(dirFull, watchedFull))
                {
                    var rel = Path.GetRelativePath(watchedFull, dirFull);
                    if (rel == "." || string.IsNullOrEmpty(rel))
                    {
                        return string.Empty;
                    }
                    return rel.Replace('\\', '/');
                }
            }

            return directory.Replace('\\', '/');
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return string.Empty;
        }
    }

    private static long ToUnixSeconds(DateTime value)
    {
        return value.Kind == DateTimeKind.Utc
            ? new DateTimeOffset(value, TimeSpan.Zero).ToUnixTimeSeconds()
            : new DateTimeOffset(value).ToUnixTimeSeconds();
    }
}
