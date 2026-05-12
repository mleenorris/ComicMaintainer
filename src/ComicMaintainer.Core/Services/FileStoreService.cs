using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// In-memory file store service (can be replaced with database implementation)
/// </summary>
public class FileStoreService : IFileStoreService
{
    private readonly ConcurrentDictionary<string, ComicFile> _files = new();
    private readonly ConcurrentDictionary<string, bool> _duplicateFiles = new();
    private readonly AppSettings _settings;
    private readonly ILogger<FileStoreService> _logger;
    private readonly IDbContextFactory<ComicMaintainerDbContext> _dbContextFactory;
    private readonly IEventBroadcaster? _eventBroadcaster;

    public FileStoreService(
        IOptions<AppSettings> settings,
        ILogger<FileStoreService> logger,
        IDbContextFactory<ComicMaintainerDbContext> dbContextFactory,
        IEventBroadcaster? eventBroadcaster = null)
    {
        _settings = settings.Value;
        _logger = logger;
        _dbContextFactory = dbContextFactory;
        _eventBroadcaster = eventBroadcaster;
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
            var watchedDir = Path.GetFullPath(_settings.WatchedDirectory);
            var duplicateDir = !string.IsNullOrEmpty(_settings.DuplicateDirectory) 
                ? Path.GetFullPath(_settings.DuplicateDirectory) 
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
            UpdatedAt = DateTime.UtcNow
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
            
            // Load all files from database
            var fileEntities = await dbContext.ComicFiles
                .ToListAsync(cancellationToken);
            
            _logger.LogInformation("Loading {Count} files from database", fileEntities.Count);
            
            // Populate in-memory collections
            foreach (var entity in fileEntities)
            {
                // Only add file if it still exists on filesystem
                if (File.Exists(entity.FilePath))
                {
                    var comicFile = new ComicFile
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
                        Metadata = entity.Metadata
                    };
                    
                    _files.AddOrUpdate(entity.FilePath, comicFile, (_, _) => comicFile);
                    
                    if (entity.IsDuplicate)
                    {
                        _duplicateFiles.TryAdd(entity.FilePath, true);
                    }
                }
                else
                {
                    // File no longer exists, remove from database
                    _logger.LogDebug("File no longer exists, will be removed from database: {FilePath}", SanitizeForLogging(entity.FilePath));
                    dbContext.ComicFiles.Remove(entity);
                }
            }
            
            await dbContext.SaveChangesAsync(cancellationToken);
            
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
                Metadata = existingFile.Metadata
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
        foreach (var filePath in filePaths)
        {
            await MarkFileReadAsync(filePath, read, cancellationToken);
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
}
