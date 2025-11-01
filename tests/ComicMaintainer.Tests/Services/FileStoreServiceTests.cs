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
        _serviceProvider = services.BuildServiceProvider();
        
        var logger = new Mock<ILogger<FileStoreService>>().Object;
        _service = new FileStoreService(options, logger, _serviceProvider);
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
    public async Task MarkFileProcessedAsync_SetTrue_MarksAsProcessed()
    {
        // Arrange
        var filePath = Path.Combine(_testDirectory, "test.cbz");
        File.WriteAllText(filePath, "test content");
        await _service.AddFileAsync(filePath);

        // Act
        await _service.MarkFileProcessedAsync(filePath, true);
        var files = await _service.GetAllFilesAsync();

        // Assert
        var file = files.First();
        Assert.True(file.IsProcessed);
    }

    [Fact]
    public async Task MarkFileProcessedAsync_SetFalse_UnmarksAsProcessed()
    {
        // Arrange
        var filePath = Path.Combine(_testDirectory, "test.cbz");
        File.WriteAllText(filePath, "test content");
        await _service.AddFileAsync(filePath);
        await _service.MarkFileProcessedAsync(filePath, true);

        // Act
        await _service.MarkFileProcessedAsync(filePath, false);
        var files = await _service.GetAllFilesAsync();

        // Assert
        var file = files.First();
        Assert.False(file.IsProcessed);
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
        await _service.MarkFileProcessedAsync(file1, true);

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
        await _service.MarkFileProcessedAsync(file1, true);

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
        await _service.MarkFileProcessedAsync(file1, true);
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
        var newService = new FileStoreService(options, logger, _serviceProvider);
        
        // Verify data is in database before initialization
        using (var scope = _serviceProvider.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<ComicMaintainerDbContext>();
            var dbFiles = await dbContext.ComicFiles.ToListAsync();
            Assert.Equal(3, dbFiles.Count);
            Assert.Single(dbFiles.Where(f => f.IsProcessed));
            Assert.Single(dbFiles.Where(f => f.IsDuplicate));
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
}
