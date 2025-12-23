using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Services;

public class FileStoreServiceTests
{
    private readonly FileStoreService _service;
    private readonly string _testDirectory;
    private readonly IServiceProvider _serviceProvider;
    private readonly string _dbName;

    public FileStoreServiceTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"filestore_tests_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDirectory);
        _dbName = $"TestDb_{Guid.NewGuid()}";

        var settings = new AppSettings
        {
            WatchedDirectory = _testDirectory
        };
        var options = Options.Create(settings);
        
        // Setup in-memory database
        var services = new ServiceCollection();
        services.AddDbContext<ComicMaintainerDbContext>(opt =>
            opt.UseInMemoryDatabase(_dbName));
        services.AddDbContextFactory<ComicMaintainerDbContext>(opt =>
            opt.UseInMemoryDatabase(_dbName));
        _serviceProvider = services.BuildServiceProvider();
        
        var logger = new Mock<ILogger<FileStoreService>>().Object;
        var dbContextFactory = _serviceProvider.GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>();
        _service = new FileStoreService(options, logger, dbContextFactory);
    }

    [Fact]
    public async Task GetAllFilesAsync_InitiallyEmpty_ReturnsEmptyList()
    {
        // Act
        var files = await _service.GetAllFilesAsync();

        // Assert
        Assert.Empty(files);
    }

    [Fact]
    public async Task AddFileAsync_ValidFile_AddsToStore()
    {
        // Arrange
        var filePath = Path.Combine(_testDirectory, "test.cbz");
        File.WriteAllText(filePath, "test content");

        // Act
        await _service.AddFileAsync(filePath);
        var files = await _service.GetAllFilesAsync();

        // Assert
        Assert.Single(files);
        var file = files.First();
        Assert.Equal(filePath, file.FilePath);
        Assert.Equal("test.cbz", file.FileName);
    }

    [Fact]
    public async Task AddFileAsync_NonExistentFile_DoesNotAdd()
    {
        // Arrange
        var filePath = Path.Combine(_testDirectory, "nonexistent.cbz");

        // Act
        await _service.AddFileAsync(filePath);
        var files = await _service.GetAllFilesAsync();

        // Assert
        Assert.Empty(files);
    }

    [Fact]
    public async Task RemoveFileAsync_ExistingFile_RemovesFromStore()
    {
        // Arrange
        var filePath = Path.Combine(_testDirectory, "test.cbz");
        File.WriteAllText(filePath, "test content");
        await _service.AddFileAsync(filePath);

        // Act
        await _service.RemoveFileAsync(filePath);
        var files = await _service.GetAllFilesAsync();

        // Assert
        Assert.Empty(files);
    }

    [Fact]
    public async Task MarkFileProcessed_WhenBothRenamedAndNormalized_MarksAsProcessed()
    {
        // Arrange
        var filePath = Path.Combine(_testDirectory, "test.cbz");
        File.WriteAllText(filePath, "test content");
        await _service.AddFileAsync(filePath);

        // Act - processed is computed from renamed && normalized
        await _service.MarkFileRenamedAsync(filePath, true);
        await _service.MarkFileNormalizedAsync(filePath, true);
        var files = await _service.GetAllFilesAsync();

        // Assert
        var file = files.First();
        Assert.True(file.IsProcessed);
    }

    [Fact]
    public async Task MarkFileProcessed_WhenOnlyRenamed_NotMarkedAsProcessed()
    {
        // Arrange
        var filePath = Path.Combine(_testDirectory, "test.cbz");
        File.WriteAllText(filePath, "test content");
        await _service.AddFileAsync(filePath);

        // Act - only renamed, not normalized
        await _service.MarkFileRenamedAsync(filePath, true);
        var files = await _service.GetAllFilesAsync();

        // Assert
        var file = files.First();
        Assert.False(file.IsProcessed);
        Assert.True(file.IsRenamed);
        Assert.False(file.IsNormalized);
    }

    [Fact]
    public async Task MarkFileDuplicateAsync_SetTrue_MarksAsDuplicate()
    {
        // Arrange
        var filePath = Path.Combine(_testDirectory, "test.cbz");
        File.WriteAllText(filePath, "test content");
        await _service.AddFileAsync(filePath);

        // Act
        await _service.MarkFileDuplicateAsync(filePath, true);
        var files = await _service.GetAllFilesAsync();

        // Assert
        var file = files.First();
        Assert.True(file.IsDuplicate);
    }

    [Fact]
    public async Task MarkFileDuplicateAsync_SetFalse_UnmarksAsDuplicate()
    {
        // Arrange
        var filePath = Path.Combine(_testDirectory, "test.cbz");
        File.WriteAllText(filePath, "test content");
        await _service.AddFileAsync(filePath);
        await _service.MarkFileDuplicateAsync(filePath, true);

        // Act
        await _service.MarkFileDuplicateAsync(filePath, false);
        var files = await _service.GetAllFilesAsync();

        // Assert
        var file = files.First();
        Assert.False(file.IsDuplicate);
    }

    [Fact]
    public async Task GetFilteredFilesAsync_NoFilter_ReturnsAllFiles()
    {
        // Arrange
        var file1 = Path.Combine(_testDirectory, "test1.cbz");
        var file2 = Path.Combine(_testDirectory, "test2.cbz");
        File.WriteAllText(file1, "test");
        File.WriteAllText(file2, "test");
        await _service.AddFileAsync(file1);
        await _service.AddFileAsync(file2);

        // Act
        var files = await _service.GetFilteredFilesAsync();

        // Assert
        Assert.Equal(2, files.Count());
    }

    [Fact]
    public async Task GetFilteredFilesAsync_ProcessedFilter_ReturnsOnlyProcessed()
    {
        // Arrange
        var file1 = Path.Combine(_testDirectory, "test1.cbz");
        var file2 = Path.Combine(_testDirectory, "test2.cbz");
        File.WriteAllText(file1, "test");
        File.WriteAllText(file2, "test");
        await _service.AddFileAsync(file1);
        await _service.AddFileAsync(file2);
        // Mark as processed (renamed AND normalized)
        await _service.MarkFileRenamedAsync(file1, true);
        await _service.MarkFileNormalizedAsync(file1, true);

        // Act
        var files = await _service.GetFilteredFilesAsync("processed");

        // Assert
        Assert.Single(files);
        Assert.Equal(file1, files.First().FilePath);
    }

    [Fact]
    public async Task GetFilteredFilesAsync_UnprocessedFilter_ReturnsOnlyUnprocessed()
    {
        // Arrange
        var file1 = Path.Combine(_testDirectory, "test1.cbz");
        var file2 = Path.Combine(_testDirectory, "test2.cbz");
        File.WriteAllText(file1, "test");
        File.WriteAllText(file2, "test");
        await _service.AddFileAsync(file1);
        await _service.AddFileAsync(file2);
        // Mark as processed (renamed AND normalized)
        await _service.MarkFileRenamedAsync(file1, true);
        await _service.MarkFileNormalizedAsync(file1, true);

        // Act
        var files = await _service.GetFilteredFilesAsync("unprocessed");

        // Assert
        Assert.Single(files);
        Assert.Equal(file2, files.First().FilePath);
    }

    [Fact]
    public async Task GetFilteredFilesAsync_DuplicatesFilter_ReturnsOnlyDuplicates()
    {
        // Arrange
        var file1 = Path.Combine(_testDirectory, "test1.cbz");
        var file2 = Path.Combine(_testDirectory, "test2.cbz");
        File.WriteAllText(file1, "test");
        File.WriteAllText(file2, "test");
        await _service.AddFileAsync(file1);
        await _service.AddFileAsync(file2);
        await _service.MarkFileDuplicateAsync(file1, true);

        // Act
        var files = await _service.GetFilteredFilesAsync("duplicates");

        // Assert
        Assert.Single(files);
        Assert.Equal(file1, files.First().FilePath);
    }

    [Fact]
    public async Task GetFilteredFilesAsync_RenamedFilter_ReturnsOnlyRenamed()
    {
        // Arrange
        var file1 = Path.Combine(_testDirectory, "test1.cbz");
        var file2 = Path.Combine(_testDirectory, "test2.cbz");
        File.WriteAllText(file1, "test");
        File.WriteAllText(file2, "test");
        await _service.AddFileAsync(file1);
        await _service.AddFileAsync(file2);
        await _service.MarkFileRenamedAsync(file1, true);

        // Act
        var files = await _service.GetFilteredFilesAsync("renamed");

        // Assert
        Assert.Single(files);
        Assert.Equal(file1, files.First().FilePath);
    }

    [Fact]
    public async Task GetFilteredFilesAsync_NormalizedFilter_ReturnsOnlyNormalized()
    {
        // Arrange
        var file1 = Path.Combine(_testDirectory, "test1.cbz");
        var file2 = Path.Combine(_testDirectory, "test2.cbz");
        File.WriteAllText(file1, "test");
        File.WriteAllText(file2, "test");
        await _service.AddFileAsync(file1);
        await _service.AddFileAsync(file2);
        await _service.MarkFileNormalizedAsync(file1, true);

        // Act
        var files = await _service.GetFilteredFilesAsync("normalized");

        // Assert
        Assert.Single(files);
        Assert.Equal(file1, files.First().FilePath);
    }

    [Fact]
    public async Task GetFileCountsAsync_WithVariousFiles_ReturnsCorrectCounts()
    {
        // Arrange
        var file1 = Path.Combine(_testDirectory, "test1.cbz");
        var file2 = Path.Combine(_testDirectory, "test2.cbz");
        var file3 = Path.Combine(_testDirectory, "test3.cbz");
        File.WriteAllText(file1, "test");
        File.WriteAllText(file2, "test");
        File.WriteAllText(file3, "test");
        await _service.AddFileAsync(file1);
        await _service.AddFileAsync(file2);
        await _service.AddFileAsync(file3);
        // Mark as processed (renamed AND normalized)
        await _service.MarkFileRenamedAsync(file1, true);
        await _service.MarkFileNormalizedAsync(file1, true);
        await _service.MarkFileDuplicateAsync(file2, true);

        // Act
        var (total, processed, unprocessed, duplicates) = await _service.GetFileCountsAsync();

        // Assert
        Assert.Equal(3, total);
        Assert.Equal(1, processed);
        Assert.Equal(1, unprocessed);
        Assert.Equal(1, duplicates);
    }

    [Fact]
    public async Task InitializeFromDatabaseAsync_LoadsProcessedAndDuplicateStatus()
    {
        // Arrange
        var file1 = Path.Combine(_testDirectory, "test1.cbz");
        var file2 = Path.Combine(_testDirectory, "test2.cbz");
        var file3 = Path.Combine(_testDirectory, "test3.cbz");
        File.WriteAllText(file1, "test");
        File.WriteAllText(file2, "test");
        File.WriteAllText(file3, "test");
        
        // First, directly add entities to the database to simulate existing data
        using (var scope = _serviceProvider.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<ComicMaintainerDbContext>();
            
            var fileInfo1 = new FileInfo(file1);
            var fileInfo2 = new FileInfo(file2);
            var fileInfo3 = new FileInfo(file3);
            
            dbContext.ComicFiles.Add(new ComicFileEntity
            {
                FilePath = file1,
                FileName = fileInfo1.Name,
                Directory = fileInfo1.DirectoryName ?? string.Empty,
                FileSize = fileInfo1.Length,
                LastModified = fileInfo1.LastWriteTime,
                IsProcessed = true,
                IsRenamed = true,  // Both need to be true for IsProcessed = true
                IsNormalized = true,
                IsDuplicate = false
            });
            
            dbContext.ComicFiles.Add(new ComicFileEntity
            {
                FilePath = file2,
                FileName = fileInfo2.Name,
                Directory = fileInfo2.DirectoryName ?? string.Empty,
                FileSize = fileInfo2.Length,
                LastModified = fileInfo2.LastWriteTime,
                IsProcessed = false,
                IsDuplicate = true
            });
            
            dbContext.ComicFiles.Add(new ComicFileEntity
            {
                FilePath = file3,
                FileName = fileInfo3.Name,
                Directory = fileInfo3.DirectoryName ?? string.Empty,
                FileSize = fileInfo3.Length,
                LastModified = fileInfo3.LastWriteTime,
                IsProcessed = false,
                IsDuplicate = false
            });
            
            await dbContext.SaveChangesAsync();
        }
        
        // Create a new service instance (simulating restart)
        var settings = new AppSettings
        {
            WatchedDirectory = _testDirectory
        };
        var options = Options.Create(settings);
        var logger = new Mock<ILogger<FileStoreService>>().Object;
        var dbContextFactory = _serviceProvider.GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>();
        var newService = new FileStoreService(options, logger, dbContextFactory);
        
        // Verify data is in database before initialization
        using (var scope = _serviceProvider.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<ComicMaintainerDbContext>();
            var dbFiles = await dbContext.ComicFiles.ToListAsync();
            Assert.Equal(3, dbFiles.Count);
            Assert.Single(dbFiles, f => f.IsProcessed);
            Assert.Single(dbFiles, f => f.IsDuplicate);
        }
        
        // Act
        await newService.InitializeFromDatabaseAsync();
        
        // Add files again (as would happen on restart)
        await newService.AddFileAsync(file1);
        await newService.AddFileAsync(file2);
        await newService.AddFileAsync(file3);
        
        // Get all files and check their status
        var files = (await newService.GetAllFilesAsync()).ToList();
        
        // Assert
        var processedFile = files.FirstOrDefault(f => f.FilePath == file1);
        var duplicateFile = files.FirstOrDefault(f => f.FilePath == file2);
        var unprocessedFile = files.FirstOrDefault(f => f.FilePath == file3);
        
        Assert.NotNull(processedFile);
        Assert.True(processedFile.IsProcessed, "File 1 should be marked as processed");
        Assert.False(processedFile.IsDuplicate, "File 1 should not be marked as duplicate");
        
        Assert.NotNull(duplicateFile);
        Assert.False(duplicateFile.IsProcessed, "File 2 should not be marked as processed");
        Assert.True(duplicateFile.IsDuplicate, "File 2 should be marked as duplicate");
        
        Assert.NotNull(unprocessedFile);
        Assert.False(unprocessedFile.IsProcessed, "File 3 should not be marked as processed");
        Assert.False(unprocessedFile.IsDuplicate, "File 3 should not be marked as duplicate");
    }

    [Fact]
    public async Task IsFileProcessedAsync_WhenFileNotInStore_ReturnsFalse()
    {
        // Arrange
        var nonExistentFile = Path.Combine(_testDirectory, "nonexistent.cbz");
        
        // Act
        var isProcessed = await _service.IsFileProcessedAsync(nonExistentFile);
        
        // Assert
        Assert.False(isProcessed, "Non-existent file should not be marked as processed");
    }

    [Fact]
    public async Task IsFileProcessedAsync_WhenFileNotProcessed_ReturnsFalse()
    {
        // Arrange
        var file = Path.Combine(_testDirectory, "unprocessed.cbz");
        File.WriteAllText(file, "test content");
        await _service.AddFileAsync(file);
        
        // Act
        var isProcessed = await _service.IsFileProcessedAsync(file);
        
        // Assert
        Assert.False(isProcessed, "Unprocessed file should return false");
    }

    [Fact]
    public async Task IsFileProcessedAsync_WhenFileProcessed_ReturnsTrue()
    {
        // Arrange
        var file = Path.Combine(_testDirectory, "processed.cbz");
        File.WriteAllText(file, "test content");
        await _service.AddFileAsync(file);
        // Mark as processed (renamed AND normalized)
        await _service.MarkFileRenamedAsync(file, true);
        await _service.MarkFileNormalizedAsync(file, true);
        
        // Act
        var isProcessed = await _service.IsFileProcessedAsync(file);
        
        // Assert
        Assert.True(isProcessed, "Processed file should return true");
    }

    [Fact]
    public async Task IsFileProcessedAsync_AfterUnmarkingProcessed_ReturnsFalse()
    {
        // Arrange
        var file = Path.Combine(_testDirectory, "toggled.cbz");
        File.WriteAllText(file, "test content");
        await _service.AddFileAsync(file);
        // Mark as processed (renamed AND normalized)
        await _service.MarkFileRenamedAsync(file, true);
        await _service.MarkFileNormalizedAsync(file, true);
        
        // Verify it's marked as processed
        Assert.True(await _service.IsFileProcessedAsync(file));
        
        // Act - Unmark renamed (which makes it unprocessed)
        await _service.MarkFileRenamedAsync(file, false);
        var isProcessed = await _service.IsFileProcessedAsync(file);
        
        // Assert
        Assert.False(isProcessed, "File should not be marked as processed after unmarking renamed");
    }
    
    [Fact]
    public async Task InitializeFromDatabaseAsync_LoadsFileList_WithoutFilesystemScan()
    {
        // Arrange - Add files to the first service instance
        var file1 = Path.Combine(_testDirectory, "comic1.cbz");
        var file2 = Path.Combine(_testDirectory, "comic2.cbz");
        var file3 = Path.Combine(_testDirectory, "comic3.cbz");
        
        File.WriteAllText(file1, "test content 1");
        File.WriteAllText(file2, "test content 2");
        File.WriteAllText(file3, "test content 3");
        
        await _service.AddFileAsync(file1);
        await _service.AddFileAsync(file2);
        await _service.AddFileAsync(file3);
        // Mark file1 as processed (renamed AND normalized)
        await _service.MarkFileRenamedAsync(file1, true);
        await _service.MarkFileNormalizedAsync(file1, true);
        await _service.MarkFileDuplicateAsync(file2, true);
        
        // Verify files are in the database
        using (var scope = _serviceProvider.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<ComicMaintainerDbContext>();
            var dbFiles = await dbContext.ComicFiles.ToListAsync();
            Assert.Equal(3, dbFiles.Count);
        }
        
        // Act - Create a new service instance (simulating restart) and initialize from database
        var settings = new AppSettings { WatchedDirectory = _testDirectory };
        var options = Options.Create(settings);
        var logger = new Mock<ILogger<FileStoreService>>().Object;
        var dbContextFactory = _serviceProvider.GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>();
        var newService = new FileStoreService(options, logger, dbContextFactory);
        
        await newService.InitializeFromDatabaseAsync();
        
        // Assert - Files should be loaded from database without needing to add them again
        var files = (await newService.GetAllFilesAsync()).ToList();
        Assert.Equal(3, files.Count);
        
        // Verify file details are preserved
        var loadedFile1 = files.FirstOrDefault(f => f.FilePath == file1);
        var loadedFile2 = files.FirstOrDefault(f => f.FilePath == file2);
        var loadedFile3 = files.FirstOrDefault(f => f.FilePath == file3);
        
        Assert.NotNull(loadedFile1);
        Assert.Equal("comic1.cbz", loadedFile1.FileName);
        Assert.True(loadedFile1.IsProcessed, "File 1 should be marked as processed");
        Assert.False(loadedFile1.IsDuplicate);
        
        Assert.NotNull(loadedFile2);
        Assert.Equal("comic2.cbz", loadedFile2.FileName);
        Assert.False(loadedFile2.IsProcessed);
        Assert.True(loadedFile2.IsDuplicate, "File 2 should be marked as duplicate");
        
        Assert.NotNull(loadedFile3);
        Assert.Equal("comic3.cbz", loadedFile3.FileName);
        Assert.False(loadedFile3.IsProcessed);
        Assert.False(loadedFile3.IsDuplicate);
    }

    [Fact]
    public async Task CleanupStaleEntriesAsync_RemovesDeletedFiles_FromDatabase()
    {
        // Arrange - Add files to database through service
        var file1 = Path.Combine(_testDirectory, "comic1.cbz");
        var file2 = Path.Combine(_testDirectory, "comic2.cbz");
        var file3 = Path.Combine(_testDirectory, "comic3.cbz");
        
        File.WriteAllText(file1, "test content 1");
        File.WriteAllText(file2, "test content 2");
        File.WriteAllText(file3, "test content 3");
        
        await _service.AddFileAsync(file1);
        await _service.AddFileAsync(file2);
        await _service.AddFileAsync(file3);
        
        // Verify all files are in store
        var filesBeforeDelete = await _service.GetAllFilesAsync();
        Assert.Equal(3, filesBeforeDelete.Count());
        
        // Act - Delete files from filesystem (simulating external deletion)
        File.Delete(file1);
        File.Delete(file3);
        
        var removedCount = await _service.CleanupStaleEntriesAsync();
        
        // Assert
        Assert.Equal(2, removedCount);
        
        var filesAfterCleanup = await _service.GetAllFilesAsync();
        Assert.Single(filesAfterCleanup);
        Assert.Equal(file2, filesAfterCleanup.First().FilePath);
        
        // Verify database was actually updated
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ComicMaintainerDbContext>();
        var dbFiles = await dbContext.ComicFiles.ToListAsync();
        Assert.Single(dbFiles);
        Assert.Equal(file2, dbFiles.First().FilePath);
    }

    [Fact]
    public async Task CleanupStaleEntriesAsync_NoDeletedFiles_ReturnsZero()
    {
        // Arrange
        var file1 = Path.Combine(_testDirectory, "comic1.cbz");
        var file2 = Path.Combine(_testDirectory, "comic2.cbz");
        
        File.WriteAllText(file1, "test content 1");
        File.WriteAllText(file2, "test content 2");
        
        await _service.AddFileAsync(file1);
        await _service.AddFileAsync(file2);
        
        // Act - No files deleted
        var removedCount = await _service.CleanupStaleEntriesAsync();
        
        // Assert
        Assert.Equal(0, removedCount);
        
        var filesAfterCleanup = await _service.GetAllFilesAsync();
        Assert.Equal(2, filesAfterCleanup.Count());
    }

    [Fact]
    public async Task CleanupStaleEntriesAsync_EmptyDatabase_ReturnsZero()
    {
        // Arrange - No files added
        
        // Act
        var removedCount = await _service.CleanupStaleEntriesAsync();
        
        // Assert
        Assert.Equal(0, removedCount);
    }

    [Fact]
    public async Task MarkFileReadAsync_NewFile_MarksAsRead()
    {
        // Arrange
        var filePath = Path.Combine(_testDirectory, "test.cbz");
        File.WriteAllText(filePath, "test content");
        await _service.AddFileAsync(filePath);

        // Act
        await _service.MarkFileReadAsync(filePath, true);

        // Assert
        var files = await _service.GetAllFilesAsync();
        var file = files.First();
        Assert.True(file.IsRead);
    }

    [Fact]
    public async Task MarkFileReadAsync_UnmarkRead_RemovesReadStatus()
    {
        // Arrange
        var filePath = Path.Combine(_testDirectory, "test.cbz");
        File.WriteAllText(filePath, "test content");
        await _service.AddFileAsync(filePath);
        await _service.MarkFileReadAsync(filePath, true);

        // Act
        await _service.MarkFileReadAsync(filePath, false);

        // Assert
        var files = await _service.GetAllFilesAsync();
        var file = files.First();
        Assert.False(file.IsRead);
    }

    [Fact]
    public async Task SaveReadingProgressAsync_NewProgress_SavesPage()
    {
        // Arrange
        var filePath = Path.Combine(_testDirectory, "test.cbz");
        File.WriteAllText(filePath, "test content");
        await _service.AddFileAsync(filePath);

        // Act
        await _service.SaveReadingProgressAsync(filePath, 5);

        // Assert
        var progress = await _service.GetReadingProgressAsync(filePath);
        Assert.Equal(5, progress);
    }

    [Fact]
    public async Task SaveReadingProgressAsync_UpdateProgress_UpdatesPage()
    {
        // Arrange
        var filePath = Path.Combine(_testDirectory, "test.cbz");
        File.WriteAllText(filePath, "test content");
        await _service.AddFileAsync(filePath);
        await _service.SaveReadingProgressAsync(filePath, 5);

        // Act
        await _service.SaveReadingProgressAsync(filePath, 10);

        // Assert
        var progress = await _service.GetReadingProgressAsync(filePath);
        Assert.Equal(10, progress);
    }

    [Fact]
    public async Task GetReadingProgressAsync_NoProgress_ReturnsOne()
    {
        // Arrange
        var filePath = Path.Combine(_testDirectory, "test.cbz");
        File.WriteAllText(filePath, "test content");
        await _service.AddFileAsync(filePath);

        // Act
        var progress = await _service.GetReadingProgressAsync(filePath);

        // Assert
        Assert.Equal(1, progress);
    }

    [Fact]
    public async Task GetReadingProgressAsync_NonExistentFile_ReturnsOne()
    {
        // Arrange
        var filePath = Path.Combine(_testDirectory, "nonexistent.cbz");

        // Act
        var progress = await _service.GetReadingProgressAsync(filePath);

        // Assert
        Assert.Equal(1, progress);
    }

    [Fact]
    public async Task IsFileRenamedAsync_NotRenamed_ReturnsFalse()
    {
        // Arrange
        var filePath = Path.Combine(_testDirectory, "test.cbz");
        File.WriteAllText(filePath, "test content");
        await _service.AddFileAsync(filePath);

        // Act
        var isRenamed = await _service.IsFileRenamedAsync(filePath);

        // Assert
        Assert.False(isRenamed);
    }

    [Fact]
    public async Task IsFileRenamedAsync_AfterMarkingRenamed_ReturnsTrue()
    {
        // Arrange
        var filePath = Path.Combine(_testDirectory, "test.cbz");
        File.WriteAllText(filePath, "test content");
        await _service.AddFileAsync(filePath);
        await _service.MarkFileRenamedAsync(filePath, true);

        // Act
        var isRenamed = await _service.IsFileRenamedAsync(filePath);

        // Assert
        Assert.True(isRenamed);
    }

    [Fact]
    public async Task IsFileNormalizedAsync_NotNormalized_ReturnsFalse()
    {
        // Arrange
        var filePath = Path.Combine(_testDirectory, "test.cbz");
        File.WriteAllText(filePath, "test content");
        await _service.AddFileAsync(filePath);

        // Act
        var isNormalized = await _service.IsFileNormalizedAsync(filePath);

        // Assert
        Assert.False(isNormalized);
    }

    [Fact]
    public async Task IsFileNormalizedAsync_AfterMarkingNormalized_ReturnsTrue()
    {
        // Arrange
        var filePath = Path.Combine(_testDirectory, "test.cbz");
        File.WriteAllText(filePath, "test content");
        await _service.AddFileAsync(filePath);
        await _service.MarkFileNormalizedAsync(filePath, true);

        // Act
        var isNormalized = await _service.IsFileNormalizedAsync(filePath);

        // Assert
        Assert.True(isNormalized);
    }

    [Fact]
    public async Task FileExistsAsync_ExistingFile_ReturnsTrue()
    {
        // Arrange
        var filePath = Path.Combine(_testDirectory, "test.cbz");
        File.WriteAllText(filePath, "test content");
        await _service.AddFileAsync(filePath);

        // Act
        var exists = await _service.FileExistsAsync(filePath);

        // Assert
        Assert.True(exists);
    }

    [Fact]
    public async Task FileExistsAsync_NonExistentFile_ReturnsFalse()
    {
        // Arrange
        var filePath = Path.Combine(_testDirectory, "nonexistent.cbz");

        // Act
        var exists = await _service.FileExistsAsync(filePath);

        // Assert
        Assert.False(exists);
    }

    [Fact]
    public async Task MarkFilesReadAsync_MultipleFiles_MarksAllAsRead()
    {
        // Arrange
        var file1 = Path.Combine(_testDirectory, "test1.cbz");
        var file2 = Path.Combine(_testDirectory, "test2.cbz");
        var file3 = Path.Combine(_testDirectory, "test3.cbz");
        
        File.WriteAllText(file1, "test content 1");
        File.WriteAllText(file2, "test content 2");
        File.WriteAllText(file3, "test content 3");
        
        await _service.AddFileAsync(file1);
        await _service.AddFileAsync(file2);
        await _service.AddFileAsync(file3);

        // Act
        await _service.MarkFilesReadAsync(new[] { file1, file2, file3 }, true);

        // Assert
        var files = await _service.GetAllFilesAsync();
        Assert.All(files, f => Assert.True(f.IsRead));
    }

    [Fact]
    public async Task MarkFilesReadAsync_EmptyList_DoesNotThrow()
    {
        // Arrange
        var emptyList = Array.Empty<string>();

        // Act & Assert - Should not throw
        await _service.MarkFilesReadAsync(emptyList, true);
    }

    [Fact]
    public async Task MarkFileRenamedAsync_SetFalse_UnmarksRenamed()
    {
        // Arrange
        var filePath = Path.Combine(_testDirectory, "test.cbz");
        File.WriteAllText(filePath, "test content");
        await _service.AddFileAsync(filePath);
        await _service.MarkFileRenamedAsync(filePath, true);

        // Act
        await _service.MarkFileRenamedAsync(filePath, false);

        // Assert
        var isRenamed = await _service.IsFileRenamedAsync(filePath);
        Assert.False(isRenamed);
    }

    [Fact]
    public async Task MarkFileNormalizedAsync_SetFalse_UnmarksNormalized()
    {
        // Arrange
        var filePath = Path.Combine(_testDirectory, "test.cbz");
        File.WriteAllText(filePath, "test content");
        await _service.AddFileAsync(filePath);
        await _service.MarkFileNormalizedAsync(filePath, true);

        // Act
        await _service.MarkFileNormalizedAsync(filePath, false);

        // Assert
        var isNormalized = await _service.IsFileNormalizedAsync(filePath);
        Assert.False(isNormalized);
    }

    [Fact]
    public async Task AddFileAsync_WithEventBroadcaster_BroadcastsFileListUpdate()
    {
        // Arrange
        var filePath = Path.Combine(_testDirectory, "test.cbz");
        File.WriteAllText(filePath, "test content");
        
        var settings = new AppSettings { WatchedDirectory = _testDirectory };
        var options = Options.Create(settings);
        var logger = new Mock<ILogger<FileStoreService>>().Object;
        var dbContextFactory = _serviceProvider.GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>();
        var mockBroadcaster = new Mock<Core.Interfaces.IEventBroadcaster>();
        
        var service = new FileStoreService(options, logger, dbContextFactory, mockBroadcaster.Object);

        // Act
        await service.AddFileAsync(filePath);

        // Assert
        mockBroadcaster.Verify(b => b.BroadcastFileListUpdateAsync(), Times.Once);
    }

    [Fact]
    public async Task RemoveFileAsync_WithEventBroadcaster_BroadcastsFileListUpdate()
    {
        // Arrange
        var filePath = Path.Combine(_testDirectory, "test_remove.cbz");
        File.WriteAllText(filePath, "test content");
        
        var settings = new AppSettings { WatchedDirectory = _testDirectory };
        var options = Options.Create(settings);
        var logger = new Mock<ILogger<FileStoreService>>().Object;
        var dbContextFactory = _serviceProvider.GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>();
        
        // Add file without broadcaster first
        var serviceWithoutBroadcaster = new FileStoreService(options, logger, dbContextFactory, null);
        await serviceWithoutBroadcaster.AddFileAsync(filePath);
        
        // Test remove with broadcaster
        var mockBroadcaster = new Mock<Core.Interfaces.IEventBroadcaster>();
        var service = new FileStoreService(options, logger, dbContextFactory, mockBroadcaster.Object);

        // Act
        await service.RemoveFileAsync(filePath);

        // Assert
        mockBroadcaster.Verify(b => b.BroadcastFileListUpdateAsync(), Times.Once);
    }

    [Fact]
    public async Task AddFileAsync_WithoutEventBroadcaster_DoesNotThrow()
    {
        // Arrange
        var filePath = Path.Combine(_testDirectory, "test_no_broadcaster.cbz");
        File.WriteAllText(filePath, "test content");
        
        var settings = new AppSettings { WatchedDirectory = _testDirectory };
        var options = Options.Create(settings);
        var logger = new Mock<ILogger<FileStoreService>>().Object;
        var dbContextFactory = _serviceProvider.GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>();
        
        var service = new FileStoreService(options, logger, dbContextFactory, null);

        // Act & Assert - should not throw
        await service.AddFileAsync(filePath);
        var files = await service.GetAllFilesAsync();
        Assert.Contains(files, f => f.FilePath == filePath);
    }
}
