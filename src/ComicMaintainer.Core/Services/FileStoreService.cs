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
                _ => files
            };
        }

        return Task.FromResult(files.ToList().AsEnumerable());
    }

    public async Task AddFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
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
                // Create new entity
                entity = CreateFileEntity(filePath);
                dbContext.ComicFiles.Add(entity);
                await dbContext.SaveChangesAsync(cancellationToken);
                _logger.LogDebug("Added new file to database: {FilePath}", SanitizeForLogging(filePath));
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
                if (File.Exists(filePath))
                {
                    entity = CreateFileEntity(filePath, isProcessed: processed);
                    dbContext.ComicFiles.Add(entity);
                    await dbContext.SaveChangesAsync(cancellationToken);
                    _logger.LogDebug("Created file entity and set processing status for {FilePath} to {Status}", SanitizeForLogging(filePath), processed);
                }
                else
                {
                    _logger.LogDebug("File {FilePath} not found on filesystem, skipping database creation", SanitizeForLogging(filePath));
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
                if (File.Exists(filePath))
                {
                    entity = CreateFileEntity(filePath, isDuplicate: duplicate);
                    dbContext.ComicFiles.Add(entity);
                    await dbContext.SaveChangesAsync(cancellationToken);
                    _logger.LogDebug("Created file entity and set duplicate status for {FilePath} to {Status}", SanitizeForLogging(filePath), duplicate);
                }
                else
                {
                    _logger.LogDebug("File {FilePath} not found on filesystem, skipping database creation", SanitizeForLogging(filePath));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating duplicate status in database for {FilePath}", SanitizeForLogging(filePath));
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
            
            // Load all processed and duplicate files from database
            var processedFiles = await dbContext.ComicFiles
                .Where(e => e.IsProcessed)
                .Select(e => e.FilePath)
                .ToListAsync(cancellationToken);
            
            var duplicateFiles = await dbContext.ComicFiles
                .Where(e => e.IsDuplicate)
                .Select(e => e.FilePath)
                .ToListAsync(cancellationToken);
            
            // Populate in-memory dictionaries
            foreach (var filePath in processedFiles)
            {
                _processedFiles.TryAdd(filePath, true);
            }
            
            foreach (var filePath in duplicateFiles)
            {
                _duplicateFiles.TryAdd(filePath, true);
            }
            
            _logger.LogInformation("Loaded {ProcessedCount} processed files and {DuplicateCount} duplicate files from database", 
                processedFiles.Count, duplicateFiles.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error initializing file store from database");
        }
    }
}
