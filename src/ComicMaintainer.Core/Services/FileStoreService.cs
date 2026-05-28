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
public class FileStoreService : IFileStoreService
{
    private const string UnmarkedCountCacheKey = "FileStore.UnmarkedCount";
    private static readonly TimeSpan UnmarkedCountCacheTtl = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<string, ComicFile> _files = new();
    private readonly ConcurrentDictionary<string, bool> _duplicateFiles = new();
    private readonly IOptionsMonitor<AppSettings> _settings;
    private readonly ILogger<FileStoreService> _logger;
    private readonly IDbContextFactory<ComicMaintainerDbContext> _dbContextFactory;
    private readonly IEventBroadcaster? _eventBroadcaster;
    private readonly IMemoryCache? _memoryCache;

    public FileStoreService(
        IOptionsMonitor<AppSettings> settings,
        ILogger<FileStoreService> logger,
        IDbContextFactory<ComicMaintainerDbContext> dbContextFactory,
        IEventBroadcaster? eventBroadcaster = null,
        IMemoryCache? memoryCache = null)
    {
        _settings = settings;
        _logger = logger;
        _dbContextFactory = dbContextFactory;
        _eventBroadcaster = eventBroadcaster;
        _memoryCache = memoryCache;
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
            // Get the full path to resolve any relative paths
            var fullPath = Path.GetFullPath(filePath);
            
            // Check for path traversal attempts
            if (fullPath.Contains(".."))
            {
                _logger.LogWarning("Path traversal attempt detected: {FilePath}", SanitizeForLogging(filePath));
                return false;
            }

            // Ensure path is within allowed directories
            var watchedDir = Path.GetFullPath(_settings.CurrentValue.WatchedDirectory);
            var duplicateDir = !string.IsNullOrEmpty(_settings.CurrentValue.DuplicateDirectory) 
                ? Path.GetFullPath(_settings.CurrentValue.DuplicateDirectory) 
                : string.Empty;

            var isInWatchedDir = fullPath.StartsWith(watchedDir, StringComparison.OrdinalIgnoreCase);
            var isInDuplicateDir = !string.IsNullOrEmpty(duplicateDir) && 
                                  fullPath.StartsWith(duplicateDir, StringComparison.OrdinalIgnoreCase);

            return isInWatchedDir || isInDuplicateDir;
        }
        catch (Exception ex)
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
            IsProcessed = ComputeProcessedState(entity.IsRenamed, entity.IsNormalized),
            IsRenamed = entity.IsRenamed,
            IsNormalized = entity.IsNormalized,
            IsDuplicate = entity.IsDuplicate,
            IsRead = entity.IsRead,
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
        _memoryCache?.Remove(UnmarkedCountCacheKey);

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

    public Task<IEnumerable<ComicFile>> GetAllFilesAsync(CancellationToken cancellationToken = default)
    {
        var files = _files.Values.ToList();
        return Task.FromResult<IEnumerable<ComicFile>>(files);
    }

    public Task<IEnumerable<ComicFile>> GetFilteredFilesAsync(string? filter = null, CancellationToken cancellationToken = default)
    {
        var totalFileCount = _files.Count;
        _logger.LogDebug("GetFilteredFilesAsync: Starting with {TotalFiles} files in store, filter: '{Filter}'", totalFileCount, filter ?? "none");
        
        var files = _files.Values.AsEnumerable();

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
                var allFilesList = _files.Values.ToList();
                var processedCount = allFilesList.Count(f => f.IsProcessed);
                var duplicateCount = allFilesList.Count(f => f.IsDuplicate);
                var unprocessedNonDuplicateCount = allFilesList.Count(f => !f.IsProcessed && !f.IsDuplicate);
                
                _logger.LogDebug("GetFilteredFilesAsync: File state breakdown - Total: {Total}, Processed: {Processed}, Duplicates: {Duplicates}, Unprocessed (non-duplicate): {Unprocessed}",
                    totalFileCount, processedCount, duplicateCount, unprocessedNonDuplicateCount);
            }
            
            return Task.FromResult(filteredList.AsEnumerable());
        }

        _logger.LogDebug("GetFilteredFilesAsync: No filter applied, returning all {TotalFiles} files", totalFileCount);
        return Task.FromResult(files.ToList().AsEnumerable());
    }

    public async Task AddFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        // Validate path is within allowed directories
        if (!IsPathWithinAllowedDirectories(filePath))
        {
            _logger.LogWarning("Attempted to add file outside allowed directories: {FilePath}", SanitizeForLogging(filePath));
            return;
        }

        if (!File.Exists(filePath))
            return;

        var fileInfo = new FileInfo(filePath);
        
        // Check if file exists in database to preserve status
        bool isRenamed = false;
        bool isNormalized = false;
        bool isDuplicate = _duplicateFiles.ContainsKey(filePath);
        
        try
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            
            var entity = await dbContext.ComicFiles
                .FirstOrDefaultAsync(e => e.FilePath == filePath, cancellationToken);
                
            if (entity != null)
            {
                // Preserve existing status from database
                isRenamed = entity.IsRenamed;
                isNormalized = entity.IsNormalized;
                isDuplicate = entity.IsDuplicate;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading file status from database: {FilePath}", SanitizeForLogging(filePath));
        }
        
        var comicFile = new ComicFile
        {
            FilePath = filePath,
            FileName = fileInfo.Name,
            Directory = fileInfo.DirectoryName ?? string.Empty,
            FileSize = fileInfo.Length,
            LastModified = fileInfo.LastWriteTime,
            IsRenamed = isRenamed,
            IsNormalized = isNormalized,
            IsProcessed = ComputeProcessedState(isRenamed, isNormalized),
            IsDuplicate = isDuplicate
        };

        _files.AddOrUpdate(filePath, comicFile, (_, _) => comicFile);

        // Persist to database
        try
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            
            var entity = await dbContext.ComicFiles
                .FirstOrDefaultAsync(e => e.FilePath == filePath, cancellationToken);

            if (entity == null)
            {
                try
                {
                    // Create new entity
                    entity = CreateFileEntity(filePath);
                    dbContext.ComicFiles.Add(entity);
                    await dbContext.SaveChangesAsync(cancellationToken);
                    _logger.LogDebug("Added new file to database: {FilePath}", SanitizeForLogging(filePath));
                }
                catch (DbUpdateException ex) when (ex.InnerException is Microsoft.Data.Sqlite.SqliteException sqliteEx && 
                                                    sqliteEx.SqliteErrorCode == 19) // UNIQUE constraint
                {
                    // Race condition: entity was created by another thread between our check and insert
                    // Retry by fetching and updating the existing entity
                    _logger.LogDebug("File entity already exists (race condition), retrying update for {FilePath}", SanitizeForLogging(filePath));
                    entity = await dbContext.ComicFiles.FirstOrDefaultAsync(e => e.FilePath == filePath, cancellationToken);
                    if (entity != null)
                    {
                        entity.FileName = fileInfo.Name;
                        entity.Directory = fileInfo.DirectoryName ?? string.Empty;
                        entity.FileSize = fileInfo.Length;
                        entity.LastModified = fileInfo.LastWriteTime;
                        entity.IsProcessed = ComputeProcessedState(entity.IsRenamed, entity.IsNormalized);
                        entity.IsDuplicate = comicFile.IsDuplicate;
                        entity.UpdatedAt = DateTime.UtcNow;
                        await dbContext.SaveChangesAsync(cancellationToken);
                        _logger.LogDebug("Updated existing file in database after retry: {FilePath}", SanitizeForLogging(filePath));
                    }
                }
            }
            else
            {
                // Update existing entity (preserving renamed/normalized status)
                entity.FileName = fileInfo.Name;
                entity.Directory = fileInfo.DirectoryName ?? string.Empty;
                entity.FileSize = fileInfo.Length;
                entity.LastModified = fileInfo.LastWriteTime;
                entity.IsProcessed = ComputeProcessedState(entity.IsRenamed, entity.IsNormalized);
                entity.IsDuplicate = comicFile.IsDuplicate;
                entity.UpdatedAt = DateTime.UtcNow;
                await dbContext.SaveChangesAsync(cancellationToken);
                _logger.LogDebug("Updated existing file in database: {FilePath}", SanitizeForLogging(filePath));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error persisting file to database: {FilePath}", SanitizeForLogging(filePath));
        }
        
        // Broadcast file list update to connected clients
        if (_eventBroadcaster != null)
        {
            try
            {
                await _eventBroadcaster.BroadcastFileListUpdateAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to broadcast file list update");
            }
        }
    }

    public async Task RemoveFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        _files.TryRemove(filePath, out _);
        _duplicateFiles.TryRemove(filePath, out _);

        // Remove from database
        try
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            
            var entity = await dbContext.ComicFiles
                .FirstOrDefaultAsync(e => e.FilePath == filePath, cancellationToken);

            if (entity != null)
            {
                dbContext.ComicFiles.Remove(entity);
                await dbContext.SaveChangesAsync(cancellationToken);
                _logger.LogDebug("Removed file from database: {FilePath}", SanitizeForLogging(filePath));
            }
            else
            {
                _logger.LogDebug("File already removed from database: {FilePath}", SanitizeForLogging(filePath));
            }
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // File was already deleted by another process/thread - this is not an error
            _logger.LogDebug(ex, "File already removed from database (concurrency): {FilePath}", SanitizeForLogging(filePath));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing file from database: {FilePath}", SanitizeForLogging(filePath));
        }
        
        // Broadcast file list update to connected clients
        if (_eventBroadcaster != null)
        {
            try
            {
                await _eventBroadcaster.BroadcastFileListUpdateAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to broadcast file list update");
            }
        }
    }

    /// <summary>
    /// DEPRECATED: Processed status is now computed from IsRenamed && IsNormalized.
    /// This method is kept for backward compatibility but does nothing.
    /// Use MarkFileRenamedAsync and MarkFileNormalizedAsync instead.
    /// </summary>
    [Obsolete("Processed status is now computed from renamed and normalized states. Use MarkFileRenamedAsync and MarkFileNormalizedAsync instead.")]
    public Task MarkFileProcessedAsync(string filePath, bool processed, CancellationToken cancellationToken = default)
    {
        _logger.LogWarning("MarkFileProcessedAsync is deprecated. Processed status is computed from renamed and normalized states.");
        // No-op: processed state is now computed, not stored
        return Task.CompletedTask;
    }

    public async Task MarkFileDuplicateAsync(string filePath, bool duplicate, CancellationToken cancellationToken = default)
    {
        if (duplicate)
        {
            _duplicateFiles.TryAdd(filePath, true);
        }
        else
        {
            _duplicateFiles.TryRemove(filePath, out _);
        }

        if (_files.TryGetValue(filePath, out var file))
        {
            file.IsDuplicate = duplicate;
        }

        // Persist to database
        try
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

            var entity = await dbContext.ComicFiles
                .FirstOrDefaultAsync(e => e.FilePath == filePath, cancellationToken);

            if (entity != null)
            {
                entity.IsDuplicate = duplicate;
                entity.UpdatedAt = DateTime.UtcNow;
                await dbContext.SaveChangesAsync(cancellationToken);
                _logger.LogDebug("Updated duplicate status for {FilePath} to {Status}", SanitizeForLogging(filePath), duplicate);
            }
            else
            {
                // Create entity if it doesn't exist
                if (IsPathWithinAllowedDirectories(filePath) && File.Exists(filePath))
                {
                    try
                    {
                        entity = CreateFileEntity(filePath, isDuplicate: duplicate);
                        dbContext.ComicFiles.Add(entity);
                        await dbContext.SaveChangesAsync(cancellationToken);
                        _logger.LogDebug("Created file entity and set duplicate status for {FilePath} to {Status}", SanitizeForLogging(filePath), duplicate);
                    }
                    catch (DbUpdateException ex) when (ex.InnerException is Microsoft.Data.Sqlite.SqliteException sqliteEx &&
                                                        sqliteEx.SqliteErrorCode == 19) // UNIQUE constraint
                    {
                        // Race condition: entity was created by another thread between our check and insert
                        // Retry by fetching and updating the existing entity
                        _logger.LogDebug("File entity already exists (race condition), retrying update for {FilePath}", SanitizeForLogging(filePath));
                        entity = await dbContext.ComicFiles.FirstOrDefaultAsync(e => e.FilePath == filePath, cancellationToken);
                        if (entity != null)
                        {
                            entity.IsDuplicate = duplicate;
                            entity.UpdatedAt = DateTime.UtcNow;
                            await dbContext.SaveChangesAsync(cancellationToken);
                            _logger.LogDebug("Updated duplicate status for {FilePath} to {Status} after retry", SanitizeForLogging(filePath), duplicate);
                        }
                    }
                }
                else
                {
                    _logger.LogDebug("File {FilePath} not found on filesystem or outside allowed directories, skipping database creation", SanitizeForLogging(filePath));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating duplicate status in database for {FilePath}", SanitizeForLogging(filePath));
        }
    }

    public async Task MarkFileRenamedAsync(string filePath, bool renamed, CancellationToken cancellationToken = default)
    {
        if (_files.TryGetValue(filePath, out var file))
        {
            file.IsRenamed = renamed;
            // Update IsProcessed based on both renamed and normalized states
            file.IsProcessed = file.IsRenamed && file.IsNormalized;
        }

        // Persist to database
        try
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

            var entity = await dbContext.ComicFiles
                .FirstOrDefaultAsync(e => e.FilePath == filePath, cancellationToken);

            if (entity != null)
            {
                entity.IsRenamed = renamed;
                // Update IsProcessed based on both states
                entity.IsProcessed = ComputeProcessedState(entity.IsRenamed, entity.IsNormalized);
                entity.UpdatedAt = DateTime.UtcNow;
                await dbContext.SaveChangesAsync(cancellationToken);
                _logger.LogDebug("Updated renamed status for {FilePath} to {Status}", SanitizeForLogging(filePath), renamed);
            }
            else
            {
                // Create entity if it doesn't exist
                if (IsPathWithinAllowedDirectories(filePath) && File.Exists(filePath))
                {
                    try
                    {
                        entity = CreateFileEntity(filePath);
                        entity.IsRenamed = renamed;
                        entity.IsProcessed = ComputeProcessedState(renamed, entity.IsNormalized);
                        dbContext.ComicFiles.Add(entity);
                        await dbContext.SaveChangesAsync(cancellationToken);
                        _logger.LogDebug("Created file entity and set renamed status for {FilePath} to {Status}", SanitizeForLogging(filePath), renamed);
                    }
                    catch (DbUpdateException ex) when (ex.InnerException is Microsoft.Data.Sqlite.SqliteException sqliteEx &&
                                                        sqliteEx.SqliteErrorCode == 19) // UNIQUE constraint
                    {
                        // Race condition: entity was created by another thread between our check and insert
                        _logger.LogDebug("File entity already exists (race condition), retrying update for {FilePath}", SanitizeForLogging(filePath));
                        entity = await dbContext.ComicFiles.FirstOrDefaultAsync(e => e.FilePath == filePath, cancellationToken);
                        if (entity != null)
                        {
                            entity.IsRenamed = renamed;
                            entity.IsProcessed = ComputeProcessedState(entity.IsRenamed, entity.IsNormalized);
                            entity.UpdatedAt = DateTime.UtcNow;
                            await dbContext.SaveChangesAsync(cancellationToken);
                            _logger.LogDebug("Updated renamed status for {FilePath} to {Status} after retry", SanitizeForLogging(filePath), renamed);
                        }
                    }
                }
                else
                {
                    _logger.LogDebug("File {FilePath} not found on filesystem or outside allowed directories, skipping database creation", SanitizeForLogging(filePath));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating renamed status in database for {FilePath}", SanitizeForLogging(filePath));
        }
    }

    public async Task MarkFileNormalizedAsync(string filePath, bool normalized, CancellationToken cancellationToken = default)
    {
        if (_files.TryGetValue(filePath, out var file))
        {
            file.IsNormalized = normalized;
            // Update IsProcessed based on both renamed and normalized states
            file.IsProcessed = file.IsRenamed && file.IsNormalized;
        }

        // Persist to database
        try
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

            var entity = await dbContext.ComicFiles
                .FirstOrDefaultAsync(e => e.FilePath == filePath, cancellationToken);

            if (entity != null)
            {
                entity.IsNormalized = normalized;
                // Update IsProcessed based on both states
                entity.IsProcessed = ComputeProcessedState(entity.IsRenamed, entity.IsNormalized);
                entity.UpdatedAt = DateTime.UtcNow;
                await dbContext.SaveChangesAsync(cancellationToken);
                _logger.LogDebug("Updated normalized status for {FilePath} to {Status}", SanitizeForLogging(filePath), normalized);
            }
            else
            {
                // Create entity if it doesn't exist
                if (IsPathWithinAllowedDirectories(filePath) && File.Exists(filePath))
                {
                    try
                    {
                        entity = CreateFileEntity(filePath);
                        entity.IsNormalized = normalized;
                        entity.IsProcessed = ComputeProcessedState(entity.IsRenamed, normalized);
                        dbContext.ComicFiles.Add(entity);
                        await dbContext.SaveChangesAsync(cancellationToken);
                        _logger.LogDebug("Created file entity and set normalized status for {FilePath} to {Status}", SanitizeForLogging(filePath), normalized);
                    }
                    catch (DbUpdateException ex) when (ex.InnerException is Microsoft.Data.Sqlite.SqliteException sqliteEx &&
                                                        sqliteEx.SqliteErrorCode == 19) // UNIQUE constraint
                    {
                        // Race condition: entity was created by another thread between our check and insert
                        _logger.LogDebug("File entity already exists (race condition), retrying update for {FilePath}", SanitizeForLogging(filePath));
                        entity = await dbContext.ComicFiles.FirstOrDefaultAsync(e => e.FilePath == filePath, cancellationToken);
                        if (entity != null)
                        {
                            entity.IsNormalized = normalized;
                            entity.IsProcessed = ComputeProcessedState(entity.IsRenamed, entity.IsNormalized);
                            entity.UpdatedAt = DateTime.UtcNow;
                            await dbContext.SaveChangesAsync(cancellationToken);
                            _logger.LogDebug("Updated normalized status for {FilePath} to {Status} after retry", SanitizeForLogging(filePath), normalized);
                        }
                    }
                }
                else
                {
                    _logger.LogDebug("File {FilePath} not found on filesystem or outside allowed directories, skipping database creation", SanitizeForLogging(filePath));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating normalized status in database for {FilePath}", SanitizeForLogging(filePath));
        }
    }

    public async Task<int> ClearProcessedStatusAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken = default)
    {
        if (filePaths == null)
        {
            return 0;
        }

        var pathList = filePaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (pathList.Count == 0)
        {
            return 0;
        }

        var updated = 0;
        try
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

            // Chunk to keep the IN clause within SQLite's default limit (999).
            const int chunkSize = 500;
            var now = DateTime.UtcNow;
            for (var offset = 0; offset < pathList.Count; offset += chunkSize)
            {
                var chunk = pathList.GetRange(offset, Math.Min(chunkSize, pathList.Count - offset));
                var entities = await dbContext.ComicFiles
                    .Where(e => chunk.Contains(e.FilePath))
                    .ToListAsync(cancellationToken);

                foreach (var entity in entities)
                {
                    if (entity.IsRenamed || entity.IsNormalized || entity.IsProcessed || entity.IsDuplicate)
                    {
                        entity.IsRenamed = false;
                        entity.IsNormalized = false;
                        entity.IsProcessed = false;
                        entity.IsDuplicate = false;
                        entity.UpdatedAt = now;
                        updated++;
                    }
                }

                if (entities.Count > 0)
                {
                    await dbContext.SaveChangesAsync(cancellationToken);
                }
            }

            // Mirror the change into the in-memory snapshot so that subsequent
            // GetAllFilesAsync()/IsFileRenamedAsync() calls reflect reality
            // immediately (FileStoreService keeps a process-local cache that
            // would otherwise return stale "renamed/normalized" flags).
            foreach (var path in pathList)
            {
                if (_files.TryGetValue(path, out var file))
                {
                    file.IsRenamed = false;
                    file.IsNormalized = false;
                    file.IsProcessed = false;
                    file.IsDuplicate = false;
                }
                _duplicateFiles.TryRemove(path, out _);
            }

            if (updated > 0)
            {
                _logger.LogInformation(
                    "ClearProcessedStatusAsync: Cleared renamed/normalized/processed/duplicate flags on {Count} file(s)",
                    updated);

                if (_eventBroadcaster != null)
                {
                    try
                    {
                        await _eventBroadcaster.BroadcastFileListUpdateAsync();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "ClearProcessedStatusAsync: file list broadcast failed");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error clearing processed status for {Count} file(s)", pathList.Count);
        }

        return updated;
    }

    public Task<(int total, int processed, int unprocessed, int duplicates)> GetFileCountsAsync(CancellationToken cancellationToken = default)
    {
        var files = _files.Values.ToList();
        var total = files.Count;
        var processed = files.Count(f => f.IsProcessed);
        var duplicates = files.Count(f => f.IsDuplicate);
        var unprocessed = files.Count(f => !f.IsProcessed && !f.IsDuplicate);

        return Task.FromResult((total, processed, unprocessed, duplicates));
    }

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

    public async Task UpdateFilePathAsync(string oldPath, string newPath, CancellationToken cancellationToken = default, bool broadcastUpdate = true)
    {
        if (!IsPathWithinAllowedDirectories(newPath))
        {
            _logger.LogWarning("Attempted to update file path to a location outside allowed directories: {NewPath}", SanitizeForLogging(newPath));
            return;
        }

        // Compute file metadata once and reuse in both the in-memory and database updates.
        var fileInfo = File.Exists(newPath) ? new FileInfo(newPath) : null;

        // Move the in-memory entry, preserving all processing state
        if (_files.TryRemove(oldPath, out var existingFile))
        {
            _duplicateFiles.TryRemove(oldPath, out _);

            var newFile = new ComicFile
            {
                FilePath = newPath,
                FileName = fileInfo?.Name ?? Path.GetFileName(newPath),
                Directory = fileInfo?.DirectoryName ?? Path.GetDirectoryName(newPath) ?? string.Empty,
                FileSize = fileInfo?.Length ?? existingFile.FileSize,
                LastModified = fileInfo?.LastWriteTime ?? existingFile.LastModified,
                IsRenamed = existingFile.IsRenamed,
                IsNormalized = existingFile.IsNormalized,
                IsProcessed = existingFile.IsProcessed,
                IsDuplicate = existingFile.IsDuplicate,
                IsRead = existingFile.IsRead,
                Metadata = existingFile.Metadata,
                SeriesMetadataVersion = existingFile.SeriesMetadataVersion,
                MetadataVersion = existingFile.MetadataVersion,
                WrittenMetadataVersion = existingFile.WrittenMetadataVersion,
                LastDbEditAt = existingFile.LastDbEditAt,
                LastWriteAt = existingFile.LastWriteAt,
                MetadataSource = existingFile.MetadataSource
            };
            _files[newPath] = newFile;
            if (newFile.IsDuplicate)
                _duplicateFiles[newPath] = true;

            _logger.LogDebug("Updated file path in memory store: {OldPath} -> {NewPath}", SanitizeForLogging(oldPath), SanitizeForLogging(newPath));
        }
        else
        {
            // Old path not in memory - ensure the new path is tracked
            await AddFileAsync(newPath, cancellationToken);
            return;
        }

        // Update the database entry, preserving processing state
        try
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

            var entity = await dbContext.ComicFiles
                .FirstOrDefaultAsync(e => e.FilePath == oldPath, cancellationToken);

            if (entity != null)
            {
                // Check whether the new path already has a DB row (race with watcher / processor)
                var newEntity = await dbContext.ComicFiles
                    .FirstOrDefaultAsync(e => e.FilePath == newPath, cancellationToken);

                if (newEntity == null)
                {
                    // Rename the existing row in-place to preserve all columns (including IsRenamed, IsNormalized, etc.)
                    entity.FilePath = newPath;
                    entity.FileName = fileInfo?.Name ?? Path.GetFileName(newPath);
                    entity.Directory = fileInfo?.DirectoryName ?? Path.GetDirectoryName(newPath) ?? string.Empty;
                    if (fileInfo != null)
                    {
                        entity.FileSize = fileInfo.Length;
                        entity.LastModified = fileInfo.LastWriteTime;
                    }
                    entity.UpdatedAt = DateTime.UtcNow;
                    await dbContext.SaveChangesAsync(cancellationToken);
                    _logger.LogDebug("Updated file path in database: {OldPath} -> {NewPath}", SanitizeForLogging(oldPath), SanitizeForLogging(newPath));
                }
                else
                {
                    // New path already exists - remove the stale old row and keep the newer one
                    dbContext.ComicFiles.Remove(entity);
                    await dbContext.SaveChangesAsync(cancellationToken);
                    _logger.LogDebug("Removed stale old-path entry after duplicate new-path detected: {OldPath}", SanitizeForLogging(oldPath));
                }
            }
            else
            {
                // Old path not in DB - ensure new path row exists
                _logger.LogDebug("Old path not found in database during UpdateFilePathAsync, ensuring new path is persisted: {NewPath}", SanitizeForLogging(newPath));
                await AddFileAsync(newPath, cancellationToken);
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating file path in database: {OldPath} -> {NewPath}", SanitizeForLogging(oldPath), SanitizeForLogging(newPath));
        }

        // Broadcast file list update
        if (broadcastUpdate && _eventBroadcaster != null)
        {
            try
            {
                await _eventBroadcaster.BroadcastFileListUpdateAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to broadcast file list update after path update");
            }
        }
    }

    public async Task SetFileSeriesMetadataVersionAsync(string filePath, int version, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;
        try
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            var entity = await dbContext.ComicFiles.FirstOrDefaultAsync(e => e.FilePath == filePath, cancellationToken);
            if (entity is null) return;
            if (entity.SeriesMetadataVersion == version) return;
            entity.SeriesMetadataVersion = version;
            entity.UpdatedAt = DateTime.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);

            // Mirror into the in-memory snapshot so subsequent reads see the stamp.
            if (_files.TryGetValue(filePath, out var file))
            {
                file.SeriesMetadataVersion = version;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to stamp series metadata version on {File}", SanitizeForLogging(filePath));
        }
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
        catch (Exception ex)
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load file row for {FilePath}", SanitizeForLogging(filePath));
            return _files.TryGetValue(filePath, out var cached) ? cached : null;
        }
    }

    public async Task ApplyUserMetadataEditAsync(string filePath, ComicMetadata patch, ComicMetadataFieldFlags lockFields, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(patch);
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await dbContext.ComicFiles.FirstOrDefaultAsync(e => e.FilePath == filePath, cancellationToken);
        if (entity is null)
        {
            throw new InvalidOperationException($"File is not tracked: {filePath}");
        }

        entity.Metadata ??= new ComicMetadata();
        CopyNonNullMetadataFields(entity.Metadata, patch);
        entity.Metadata.IsUserEdited = true;
        entity.Metadata.UserLockedFieldsMask |= (long)lockFields;
        if (entity.MetadataVersion < int.MaxValue) entity.MetadataVersion++;
        entity.LastDbEditAt = DateTime.UtcNow;
        entity.MetadataSource = FileMetadataSource.UserEdit.ToString();
        entity.UpdatedAt = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        var updated = ToComicFile(entity);
        _files.AddOrUpdate(filePath, updated, (_, _) => updated);
        await BroadcastFileListUpdateSafeAsync();
    }

    public async Task<IReadOnlyList<ComicFile>> GetFilesNeedingBackfillAsync(int max, CancellationToken cancellationToken = default)
    {
        if (max <= 0) return Array.Empty<ComicFile>();
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await dbContext.ComicFiles
            .AsNoTracking()
            .Where(e => !e.IsDuplicate && e.MetadataVersion > e.WrittenMetadataVersion)
            .OrderByDescending(e => e.UpdatedAt)
            .Take(max)
            .ToListAsync(cancellationToken);
        return rows.Select(ToComicFile).ToList();
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

        var updated = ToComicFile(entity);
        _files.AddOrUpdate(filePath, updated, (_, _) => updated);
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
                }
            }
            
            // Remove all stale entries in a single operation
            if (filesToRemove.Any())
            {
                dbContext.ComicFiles.RemoveRange(filesToRemove);
                await dbContext.SaveChangesAsync(cancellationToken);
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

    public async Task MarkFileReadAsync(string filePath, bool read, CancellationToken cancellationToken = default)
    {
        if (_files.TryGetValue(filePath, out var file))
        {
            file.IsRead = read;
        }

        // Persist to database
        try
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

            var entity = await dbContext.ComicFiles
                .FirstOrDefaultAsync(e => e.FilePath == filePath, cancellationToken);

            if (entity != null)
            {
                entity.IsRead = read;
                entity.UpdatedAt = DateTime.UtcNow;
                await dbContext.SaveChangesAsync(cancellationToken);
                _logger.LogDebug("Updated read status for {FilePath} to {Status}", SanitizeForLogging(filePath), read);
            }
            else
            {
                // Create entity if it doesn't exist
                if (IsPathWithinAllowedDirectories(filePath) && File.Exists(filePath))
                {
                    try
                    {
                        entity = CreateFileEntity(filePath);
                        entity.IsRead = read;
                        dbContext.ComicFiles.Add(entity);
                        await dbContext.SaveChangesAsync(cancellationToken);
                        _logger.LogDebug("Created file entity and set read status for {FilePath} to {Status}", SanitizeForLogging(filePath), read);
                    }
                    catch (DbUpdateException ex) when (ex.InnerException is Microsoft.Data.Sqlite.SqliteException sqliteEx &&
                                                        sqliteEx.SqliteErrorCode == 19) // UNIQUE constraint
                    {
                        // Race condition: entity was created by another thread between our check and insert
                        _logger.LogDebug("File entity already exists (race condition), retrying update for {FilePath}", SanitizeForLogging(filePath));
                        entity = await dbContext.ComicFiles.FirstOrDefaultAsync(e => e.FilePath == filePath, cancellationToken);
                        if (entity != null)
                        {
                            entity.IsRead = read;
                            entity.UpdatedAt = DateTime.UtcNow;
                            await dbContext.SaveChangesAsync(cancellationToken);
                            _logger.LogDebug("Updated read status for {FilePath} to {Status} after retry", SanitizeForLogging(filePath), read);
                        }
                    }
                }
                else
                {
                    _logger.LogDebug("File {FilePath} not found on filesystem or outside allowed directories, skipping database creation", SanitizeForLogging(filePath));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating read status in database for {FilePath}", SanitizeForLogging(filePath));
        }
    }

    public async Task MarkFilesReadAsync(IEnumerable<string> filePaths, bool read, CancellationToken cancellationToken = default)
    {
        // Materialize once so we can iterate twice (in-memory update + bulk DB update).
        var paths = filePaths as IList<string> ?? filePaths.ToList();
        if (paths.Count == 0)
        {
            return;
        }

        // Update in-memory state up-front
        foreach (var filePath in paths)
        {
            if (_files.TryGetValue(filePath, out var file))
            {
                file.IsRead = read;
            }
        }

        // Single connection + single bulk UPDATE for all existing rows.
        try
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

            var now = DateTime.UtcNow;
            var updated = await dbContext.ComicFiles
                .Where(e => paths.Contains(e.FilePath))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(e => e.IsRead, read)
                    .SetProperty(e => e.UpdatedAt, now),
                    cancellationToken);

            _logger.LogDebug("Bulk-updated read status for {Updated}/{Total} files", updated, paths.Count);

            // If some files didn't exist in DB yet, fall back to per-file insert path
            // only for the missing ones to avoid re-updating rows we just touched.
            if (updated < paths.Count)
            {
                var existingPaths = await dbContext.ComicFiles
                    .AsNoTracking()
                    .Where(e => paths.Contains(e.FilePath))
                    .Select(e => e.FilePath)
                    .ToListAsync(cancellationToken);

                var missing = new HashSet<string>(paths, StringComparer.Ordinal);
                missing.ExceptWith(existingPaths);

                foreach (var filePath in missing)
                {
                    await MarkFileReadAsync(filePath, read, cancellationToken);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error bulk-updating read status; falling back to per-file updates");
            foreach (var filePath in paths)
            {
                await MarkFileReadAsync(filePath, read, cancellationToken);
            }
        }
    }

    public async Task SaveReadingProgressAsync(string filePath, int currentPage, CancellationToken cancellationToken = default)
    {
        if (currentPage < 1)
        {
            _logger.LogWarning("Invalid page number {Page} for {FilePath}, must be >= 1", currentPage, SanitizeForLogging(filePath));
            return;
        }

        try
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            
            var readStatus = await dbContext.FileReadStatuses
                .FirstOrDefaultAsync(e => e.FilePath == filePath, cancellationToken);

            if (readStatus != null)
            {
                readStatus.CurrentPage = currentPage;
                readStatus.LastReadDate = DateTime.UtcNow;
                readStatus.UpdatedAt = DateTime.UtcNow;
                await dbContext.SaveChangesAsync(cancellationToken);
                _logger.LogDebug("Updated reading progress for {FilePath} to page {Page}", SanitizeForLogging(filePath), currentPage);
            }
            else
            {
                // Create new read status entry
                readStatus = new FileReadStatusEntity
                {
                    FilePath = filePath,
                    CurrentPage = currentPage,
                    IsRead = false,
                    LastReadDate = DateTime.UtcNow,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                dbContext.FileReadStatuses.Add(readStatus);
                await dbContext.SaveChangesAsync(cancellationToken);
                _logger.LogDebug("Created read status and set page {Page} for {FilePath}", currentPage, SanitizeForLogging(filePath));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving reading progress for {FilePath}", SanitizeForLogging(filePath));
        }
    }

    public async Task<int> GetReadingProgressAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            
            var readStatus = await dbContext.FileReadStatuses
                .FirstOrDefaultAsync(e => e.FilePath == filePath, cancellationToken);

            if (readStatus != null)
            {
                _logger.LogDebug("Retrieved reading progress for {FilePath}: page {Page}", SanitizeForLogging(filePath), readStatus.CurrentPage);
                return readStatus.CurrentPage;
            }
            else
            {
                _logger.LogDebug("No reading progress found for {FilePath}, returning page 1", SanitizeForLogging(filePath));
                return 1; // Default to page 1 if no progress saved
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting reading progress for {FilePath}", SanitizeForLogging(filePath));
            return 1; // Default to page 1 on error
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

                if (!string.IsNullOrEmpty(fileDirFull) &&
                    (string.Equals(fileDirFull, watchedFull, StringComparison.OrdinalIgnoreCase) ||
                     fileDirFull.StartsWith(watchedFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
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

                if (string.Equals(dirFull, watchedFull, StringComparison.OrdinalIgnoreCase) ||
                    dirFull.StartsWith(watchedFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
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

    /// <summary>
    /// Apply the named status filter to a <see cref="ComicFileEntity"/> query.
    /// Matches the semantics of <see cref="GetFilteredFilesAsync"/>.
    /// </summary>
    private static IQueryable<ComicFileEntity> ApplyEntityFilter(IQueryable<ComicFileEntity> q, string? filter)
    {
        if (string.IsNullOrEmpty(filter)) return q;
        return filter.ToLowerInvariant() switch
        {
            "processed" => q.Where(f => f.IsRenamed && f.IsNormalized),
            "unprocessed" => q.Where(f => !(f.IsRenamed && f.IsNormalized) && !f.IsDuplicate),
            "duplicates" => q.Where(f => f.IsDuplicate),
            "renamed" => q.Where(f => f.IsRenamed),
            "normalized" => q.Where(f => f.IsNormalized),
            "read" => q.Where(f => f.IsRead),
            "unread" => q.Where(f => !f.IsRead),
            _ => q
        };
    }

    private static IQueryable<ComicFileEntity> ApplyEntitySearch(IQueryable<ComicFileEntity> q, string? search)
    {
        if (string.IsNullOrEmpty(search)) return q;
        var pattern = $"%{search}%";
        return q.Where(f => EF.Functions.Like(f.FileName, pattern) || EF.Functions.Like(f.FilePath, pattern));
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

    private static long ToUnixSeconds(DateTime value)
    {
        return value.Kind == DateTimeKind.Utc
            ? new DateTimeOffset(value, TimeSpan.Zero).ToUnixTimeSeconds()
            : new DateTimeOffset(value).ToUnixTimeSeconds();
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

        var query = dbContext.ComicFiles.AsNoTracking().AsQueryable();
        query = ApplyEntityFilter(query, filter);
        query = ApplyEntitySearch(query, search);

        var totalFiles = await query.CountAsync(cancellationToken);

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
                IsRead = f.IsRead,
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
                    IsRead = f.IsRead,
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

    public async Task<IReadOnlyList<FileDto>> GetFolderFilesAsync(
        string folderKey,
        string? filter = null,
        string? search = null,
        string? sort = "name",
        string? direction = "asc",
        CancellationToken cancellationToken = default)
    {
        var key = folderKey ?? string.Empty;
        var watchedDir = _settings.CurrentValue?.WatchedDirectory;

        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        // Resolve raw Directory values that map to the requested folder key.
        // The set of distinct directories is typically small (one per folder)
        // so loading and mapping in memory is cheap, and lets us do the
        // file-level filter as `WHERE Directory IN (...)` which uses the
        // Directory index.
        var distinctDirs = await dbContext.ComicFiles
            .AsNoTracking()
            .Select(f => f.Directory)
            .Distinct()
            .ToListAsync(cancellationToken);

        var matchedDirs = distinctDirs
            .Where(d => string.Equals(ComputeFolderKeyFromDirectory(d, watchedDir), key, StringComparison.Ordinal))
            .ToList();

        if (matchedDirs.Count == 0)
        {
            return Array.Empty<FileDto>();
        }

        var query = dbContext.ComicFiles
            .AsNoTracking()
            .Where(f => matchedDirs.Contains(f.Directory));

        query = ApplyEntityFilter(query, filter);
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
            IsRead = f.IsRead,
        }).ToListAsync(cancellationToken);

        return rows.Select(ToFileDto).ToList();
    }

    public async Task<int> GetUnmarkedCountAsync(CancellationToken cancellationToken = default)
    {
        if (_memoryCache != null && _memoryCache.TryGetValue(UnmarkedCountCacheKey, out int cached))
        {
            return cached;
        }

        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var count = await dbContext.ComicFiles
            .AsNoTracking()
            .CountAsync(f => !(f.IsRenamed && f.IsNormalized) && !f.IsDuplicate, cancellationToken);

        _memoryCache?.Set(UnmarkedCountCacheKey, count, UnmarkedCountCacheTtl);
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
        var watchedDir = _settings.CurrentValue?.WatchedDirectory;

        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var query = dbContext.ComicFiles.AsNoTracking().AsQueryable();
        query = ApplyEntityFilter(query, filter);
        query = ApplyEntitySearch(query, search);

        // Aggregate per raw Directory in the database. The number of distinct
        // directories is typically small relative to the file count, so the
        // post-processing (map Directory -> folder key, then sort/page) runs
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

        var dir = (direction ?? "asc").ToLowerInvariant();
        var sortKey = (sort ?? "name").ToLowerInvariant();
        grouped = sortKey switch
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

        var total = grouped.Count;
        var safeOffset = Math.Max(0, offset);
        var safeLimit = Math.Max(0, limit);

        var page = safeLimit <= 0
            ? new List<FolderSummaryDto>()
            : grouped.Skip(safeOffset).Take(safeLimit).ToList();

        return new FolderSummariesResult
        {
            Folders = page,
            Offset = safeOffset,
            Limit = safeLimit,
            TotalFolders = total
        };
    }
}
