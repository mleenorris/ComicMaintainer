using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// In-memory file store service (can be replaced with database implementation)
/// </summary>
public class FileStoreService : IFileStoreService
{
    private readonly ConcurrentDictionary<string, ComicFile> _files = new();
    private readonly ConcurrentDictionary<string, bool> _processedFiles = new();
    private readonly ConcurrentDictionary<string, bool> _duplicateFiles = new();
    private readonly AppSettings _settings;
    private readonly ILogger<FileStoreService> _logger;
    private readonly IServiceProvider _serviceProvider;

    public FileStoreService(
        IOptions<AppSettings> settings,
        ILogger<FileStoreService> logger,
        IServiceProvider serviceProvider)
    {
        _settings = settings.Value;
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    private static string SanitizeForLogging(string? input)
    {
        if (string.IsNullOrEmpty(input))
            return string.Empty;
        
        // Remove newlines and carriage returns to prevent log forging
        return input.Replace("\r", "").Replace("\n", "");
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

    private ComicFileEntity CreateFileEntity(string filePath, bool? isProcessed = null, bool? isDuplicate = null)
    {
        var fileInfo = new FileInfo(filePath);
        return new ComicFileEntity
        {
            FilePath = filePath,
            FileName = fileInfo.Name,
            Directory = fileInfo.DirectoryName ?? string.Empty,
            FileSize = fileInfo.Length,
            LastModified = fileInfo.LastWriteTime,
            IsProcessed = isProcessed ?? _processedFiles.ContainsKey(filePath),
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
        var files = _files.Values.AsEnumerable();

        if (!string.IsNullOrEmpty(filter))
        {
            files = filter.ToLower() switch
            {
                "processed" => files.Where(f => f.IsProcessed),
                "unprocessed" => files.Where(f => !f.IsProcessed && !f.IsDuplicate),
                "duplicates" => files.Where(f => f.IsDuplicate),
                "renamed" => files.Where(f => f.IsRenamed),
                "normalized" => files.Where(f => f.IsNormalized),
                _ => files
            };
        }

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
        var comicFile = new ComicFile
        {
            FilePath = filePath,
            FileName = fileInfo.Name,
            Directory = fileInfo.DirectoryName ?? string.Empty,
            FileSize = fileInfo.Length,
            LastModified = fileInfo.LastWriteTime,
            IsProcessed = _processedFiles.ContainsKey(filePath),
            IsDuplicate = _duplicateFiles.ContainsKey(filePath)
        };

        _files.AddOrUpdate(filePath, comicFile, (_, _) => comicFile);

        // Persist to database
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ComicMaintainerDbContext>();
            
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
                        entity.IsProcessed = comicFile.IsProcessed;
                        entity.IsDuplicate = comicFile.IsDuplicate;
                        entity.UpdatedAt = DateTime.UtcNow;
                        await dbContext.SaveChangesAsync(cancellationToken);
                        _logger.LogDebug("Updated existing file in database after retry: {FilePath}", SanitizeForLogging(filePath));
                    }
                }
            }
            else
            {
                // Update existing entity
                entity.FileName = fileInfo.Name;
                entity.Directory = fileInfo.DirectoryName ?? string.Empty;
                entity.FileSize = fileInfo.Length;
                entity.LastModified = fileInfo.LastWriteTime;
                entity.IsProcessed = comicFile.IsProcessed;
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
    }

    public async Task RemoveFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        _files.TryRemove(filePath, out _);
        _processedFiles.TryRemove(filePath, out _);
        _duplicateFiles.TryRemove(filePath, out _);

        // Remove from database
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ComicMaintainerDbContext>();
            
            var entity = await dbContext.ComicFiles
                .FirstOrDefaultAsync(e => e.FilePath == filePath, cancellationToken);

            if (entity != null)
            {
                dbContext.ComicFiles.Remove(entity);
                await dbContext.SaveChangesAsync(cancellationToken);
                _logger.LogDebug("Removed file from database: {FilePath}", SanitizeForLogging(filePath));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing file from database: {FilePath}", SanitizeForLogging(filePath));
        }
    }

    public async Task MarkFileProcessedAsync(string filePath, bool processed, CancellationToken cancellationToken = default)
    {
        if (processed)
        {
            _processedFiles.TryAdd(filePath, true);
        }
        else
        {
            _processedFiles.TryRemove(filePath, out _);
        }

        if (_files.TryGetValue(filePath, out var file))
        {
            file.IsProcessed = processed;
        }

        // Persist to database
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ComicMaintainerDbContext>();
            
            var entity = await dbContext.ComicFiles
                .FirstOrDefaultAsync(e => e.FilePath == filePath, cancellationToken);

            if (entity != null)
            {
                entity.IsProcessed = processed;
                entity.UpdatedAt = DateTime.UtcNow;
                await dbContext.SaveChangesAsync(cancellationToken);
                _logger.LogDebug("Updated processing status for {FilePath} to {Status}", SanitizeForLogging(filePath), processed);
            }
            else
            {
                // Create entity if it doesn't exist
                if (IsPathWithinAllowedDirectories(filePath) && File.Exists(filePath))
                {
                    try
                    {
                        entity = CreateFileEntity(filePath, isProcessed: processed);
                        dbContext.ComicFiles.Add(entity);
                        await dbContext.SaveChangesAsync(cancellationToken);
                        _logger.LogDebug("Created file entity and set processing status for {FilePath} to {Status}", SanitizeForLogging(filePath), processed);
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
                            entity.IsProcessed = processed;
                            entity.UpdatedAt = DateTime.UtcNow;
                            await dbContext.SaveChangesAsync(cancellationToken);
                            _logger.LogDebug("Updated processing status for {FilePath} to {Status} after retry", SanitizeForLogging(filePath), processed);
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
            _logger.LogError(ex, "Error updating processing status in database for {FilePath}", SanitizeForLogging(filePath));
        }
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
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ComicMaintainerDbContext>();
            
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
        bool isProcessed = false;
        if (_files.TryGetValue(filePath, out var file))
        {
            file.IsRenamed = renamed;
            // Update IsProcessed - file is processed if either renamed OR normalized
            file.IsProcessed = file.IsRenamed || file.IsNormalized;
            isProcessed = file.IsProcessed;
        }

        // Update the processed files dictionary
        if (isProcessed)
        {
            _processedFiles.TryAdd(filePath, true);
        }
        else
        {
            _processedFiles.TryRemove(filePath, out _);
        }

        // Persist to database
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ComicMaintainerDbContext>();
            
            var entity = await dbContext.ComicFiles
                .FirstOrDefaultAsync(e => e.FilePath == filePath, cancellationToken);

            if (entity != null)
            {
                entity.IsRenamed = renamed;
                // Update IsProcessed - file is processed if either renamed OR normalized
                entity.IsProcessed = entity.IsRenamed || entity.IsNormalized;
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
                        entity.IsProcessed = renamed || entity.IsNormalized; // Processed if either renamed OR normalized
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
                            entity.IsProcessed = entity.IsRenamed || entity.IsNormalized;
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
        bool isProcessed = false;
        if (_files.TryGetValue(filePath, out var file))
        {
            file.IsNormalized = normalized;
            // Update IsProcessed - file is processed if either renamed OR normalized
            file.IsProcessed = file.IsRenamed || file.IsNormalized;
            isProcessed = file.IsProcessed;
        }

        // Update the processed files dictionary
        if (isProcessed)
        {
            _processedFiles.TryAdd(filePath, true);
        }
        else
        {
            _processedFiles.TryRemove(filePath, out _);
        }

        // Persist to database
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ComicMaintainerDbContext>();
            
            var entity = await dbContext.ComicFiles
                .FirstOrDefaultAsync(e => e.FilePath == filePath, cancellationToken);

            if (entity != null)
            {
                entity.IsNormalized = normalized;
                // Update IsProcessed - file is processed if either renamed OR normalized
                entity.IsProcessed = entity.IsRenamed || entity.IsNormalized;
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
                        entity.IsProcessed = entity.IsRenamed || normalized; // Processed if either renamed OR normalized
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
                            entity.IsProcessed = entity.IsRenamed || entity.IsNormalized;
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
            
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ComicMaintainerDbContext>();
            
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
                        IsProcessed = entity.IsProcessed,
                        IsRenamed = entity.IsRenamed,
                        IsNormalized = entity.IsNormalized,
                        IsDuplicate = entity.IsDuplicate,
                        Metadata = entity.Metadata
                    };
                    
                    _files.AddOrUpdate(entity.FilePath, comicFile, (_, _) => comicFile);
                    
                    if (entity.IsProcessed)
                    {
                        _processedFiles.TryAdd(entity.FilePath, true);
                    }
                    
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
            var processedCount = _processedFiles.Count;
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
        // Check the authoritative source - the _processedFiles dictionary
        // This dictionary is maintained by MarkFileProcessedAsync and InitializeFromDatabaseAsync
        var isProcessed = _processedFiles.ContainsKey(filePath);
        return Task.FromResult(isProcessed);
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
}
