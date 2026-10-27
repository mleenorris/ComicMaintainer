using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.EntityFrameworkCore;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// In-memory file store service (can be replaced with database implementation)
/// </summary>
public partial class FileStoreService : IFileStoreService, IChangeVersionSource
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
    /// Path comparison for library-root containment: case-insensitive on Windows and
    /// case-sensitive elsewhere, the same rule as <c>LibraryPathValidator.IsWithin</c>.
    /// </summary>
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private readonly ConcurrentDictionary<string, ComicFile> _files = new();
    private readonly ConcurrentDictionary<string, bool> _duplicateFiles = new();
    private readonly IOptionsMonitor<AppSettings> _settings;
    private readonly ILogger<FileStoreService> _logger;
    private readonly IDbContextFactory<ComicMaintainerDbContext> _dbContextFactory;
    private readonly IEventBroadcaster? _eventBroadcaster;
    private readonly IMemoryCache? _memoryCache;
    private readonly IUserContextAccessor? _userContext;
    private long _changeVersion;

    /// <inheritdoc />
    /// <remarks>
    /// Bumped whenever the in-memory file snapshot or any user's read state
    /// changes. Derived views (series library snapshot, combinable folders,
    /// folder-key maps) key their caches on this value.
    /// </remarks>
    public long ChangeVersion => Interlocked.Read(ref _changeVersion);

    /// <summary>Records that the file snapshot or read state changed.</summary>
    private void MarkChanged() => Interlocked.Increment(ref _changeVersion);

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
            // ends up outside both roots and is rejected by the containment checks below. Names
            // that merely contain consecutive dots ("What If...?") are legitimate comics.
            var fullPath = Path.GetFullPath(filePath);

            // Ensure path is within allowed directories
            var watchedDir = Path.GetFullPath(_settings.CurrentValue.WatchedDirectory);
            var duplicateDir = !string.IsNullOrEmpty(_settings.CurrentValue.DuplicateDirectory) 
                ? Path.GetFullPath(_settings.CurrentValue.DuplicateDirectory) 
                : string.Empty;

            var isInWatchedDir = IsSameOrUnder(fullPath, watchedDir);
            var isInDuplicateDir = !string.IsNullOrEmpty(duplicateDir) && 
                                  IsSameOrUnder(fullPath, duplicateDir);

            return isInWatchedDir || isInDuplicateDir;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error validating path: {FilePath}", SanitizeForLogging(filePath));
            return false;
        }
    }

    /// <summary>
    /// Returns true when the already normalized <paramref name="fullPath"/> is
    /// <paramref name="directory"/> itself or lives beneath it. Trailing separators on either
    /// side are ignored, and a sibling that merely shares the directory's name as a prefix
    /// (<c>/comics2</c> next to <c>/comics</c>) is outside.
    /// </summary>
    private static bool IsSameOrUnder(string fullPath, string directory)
    {
        var root = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(
                   fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), root, PathComparison)
               || fullPath.StartsWith(root + Path.DirectorySeparatorChar, PathComparison);
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to broadcast file list update");
        }
    }

    private ComicFileEntity CreateFileEntity(string filePath, bool? isDuplicate = null)
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
            IsDuplicate = isDuplicate ?? _duplicateFiles.ContainsKey(filePath),
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

                if (!string.IsNullOrEmpty(fileDirFull) && IsSameOrUnder(fileDirFull, watchedFull))
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
        catch
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

                if (IsSameOrUnder(dirFull, watchedFull))
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
        catch
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
