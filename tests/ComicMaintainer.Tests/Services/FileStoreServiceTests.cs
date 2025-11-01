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

    public FileStoreServiceTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"filestore_tests_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDirectory);

        var settings = new AppSettings
        {
            WatchedDirectory = _testDirectory
        };
        var options = Options.Create(settings);
        
        // Setup in-memory database
        var services = new ServiceCollection();
        services.AddDbContext<ComicMaintainerDbContext>(opt =>
            opt.UseInMemoryDatabase($"TestDb_{Guid.NewGuid()}"));
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
}
