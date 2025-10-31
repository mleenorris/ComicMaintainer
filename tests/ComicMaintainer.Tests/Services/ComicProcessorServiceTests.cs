using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using System.IO.Compression;

namespace ComicMaintainer.Tests.Services;

public class ComicProcessorServiceTests : IDisposable
{
    private readonly Mock<ILogger<ComicProcessorService>> _mockLogger;
    private readonly Mock<IFileStoreService> _mockFileStore;
    private readonly Mock<IOptions<AppSettings>> _mockOptions;
    private readonly AppSettings _settings;
    private readonly string _testDirectory;
    private readonly ComicProcessorService _service;

    public ComicProcessorServiceTests()
    {
        _mockLogger = new Mock<ILogger<ComicProcessorService>>();
        _mockFileStore = new Mock<IFileStoreService>();
        _mockOptions = new Mock<IOptions<AppSettings>>();
        
        _testDirectory = Path.Combine(Path.GetTempPath(), $"comic_tests_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDirectory);
        
        _settings = new AppSettings
        {
            WatchedDirectory = _testDirectory,
            DuplicateDirectory = Path.Combine(_testDirectory, "duplicates"),
            FilenameFormat = "{series} - Chapter {issue}",
            IssueNumberPadding = 4
        };
        
        _mockOptions.Setup(o => o.Value).Returns(_settings);
        
        _service = new ComicProcessorService(_mockOptions.Object, _mockLogger.Object, _mockFileStore.Object);
    }

    [Fact]
    public async Task ProcessFileAsync_NonExistentFile_ReturnsFalse()
    {
        // Arrange
        var filePath = Path.Combine(_testDirectory, "nonexistent.cbz");

        // Act
        var result = await _service.ProcessFileAsync(filePath);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task ProcessFileAsync_NonComicFile_ReturnsFalse()
    {
        // Arrange
        var filePath = Path.Combine(_testDirectory, "test.txt");
        await File.WriteAllTextAsync(filePath, "test content");

        // Act
        var result = await _service.ProcessFileAsync(filePath);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task ProcessFileAsync_ValidComicFile_ReturnsTrue()
    {
        // Arrange
        var filePath = CreateTestComicArchive("Test Series", "1");
        _mockFileStore.Setup(f => f.MarkFileProcessedAsync(It.IsAny<string>(), true, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockFileStore.Setup(f => f.GetFilteredFilesAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ComicFile>());

        // Act
        var result = await _service.ProcessFileAsync(filePath);

        // Assert
        Assert.True(result);
        _mockFileStore.Verify(f => f.MarkFileProcessedAsync(It.IsAny<string>(), true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetMetadataAsync_FileWithComicInfoXml_ReturnsMetadata()
    {
        // Arrange
        var filePath = CreateTestComicArchive("Batman", "12", year: 2023);

        // Act
        var metadata = await _service.GetMetadataAsync(filePath);

        // Assert
        Assert.NotNull(metadata);
        Assert.Equal("Batman", metadata.Series);
        Assert.Equal("12", metadata.Issue);
        Assert.Equal(2023, metadata.Year);
    }

    [Fact]
    public async Task GetMetadataAsync_NonExistentFile_ReturnsNull()
    {
        // Arrange
        var filePath = Path.Combine(_testDirectory, "nonexistent.cbz");

        // Act
        var metadata = await _service.GetMetadataAsync(filePath);

        // Assert
        Assert.Null(metadata);
    }

    [Fact]
    public async Task UpdateMetadataAsync_ValidFile_ReturnsTrue()
    {
        // Arrange
        var filePath = CreateTestComicArchive("Original Series", "1");
        var newMetadata = new ComicMetadata
        {
            Series = "Updated Series",
            Issue = "2",
            Title = "Updated Title",
            Year = 2024
        };

        // Act
        var result = await _service.UpdateMetadataAsync(filePath, newMetadata);

        // Assert
        Assert.True(result);
        
        // Verify metadata was actually updated
        var updatedMetadata = await _service.GetMetadataAsync(filePath);
        Assert.NotNull(updatedMetadata);
        Assert.Equal("Updated Series", updatedMetadata.Series);
        Assert.Equal("2", updatedMetadata.Issue);
    }

    [Fact]
    public async Task ProcessFilesAsync_MultipleFiles_CreatesJob()
    {
        // Arrange
        var file1 = CreateTestComicArchive("Series A", "1");
        var file2 = CreateTestComicArchive("Series B", "2");
        var filePaths = new List<string> { file1, file2 };
        
        _mockFileStore.Setup(f => f.MarkFileProcessedAsync(It.IsAny<string>(), true, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockFileStore.Setup(f => f.GetFilteredFilesAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ComicFile>());

        // Act
        var jobId = await _service.ProcessFilesAsync(filePaths);

        // Assert
        Assert.NotEqual(Guid.Empty, jobId);
        
        // Wait a bit for async processing
        await Task.Delay(1000);
        
        var job = _service.GetJob(jobId);
        Assert.NotNull(job);
        Assert.Equal(2, job.TotalFiles);
    }

    [Fact]
    public void GetJob_NonExistentJob_ReturnsNull()
    {
        // Arrange
        var jobId = Guid.NewGuid();

        // Act
        var job = _service.GetJob(jobId);

        // Assert
        Assert.Null(job);
    }

    private string CreateTestComicArchive(string series, string issue, int? year = null)
    {
        var fileName = $"{series} - Chapter {issue}.cbz";
        var filePath = Path.Combine(_testDirectory, fileName);

        // Create ComicInfo.xml content
        var comicInfoXml = $@"<?xml version=""1.0""?>
<ComicInfo>
    <Series>{series}</Series>
    <Number>{issue}</Number>
    {(year.HasValue ? $"<Year>{year.Value}</Year>" : "")}
    <Title>Test Issue</Title>
    <Publisher>Test Publisher</Publisher>
</ComicInfo>";

        // Create a CBZ (ZIP) archive with ComicInfo.xml
        using (var archive = ZipFile.Open(filePath, ZipArchiveMode.Create))
        {
            // Add ComicInfo.xml
            var comicInfoEntry = archive.CreateEntry("ComicInfo.xml");
            using (var writer = new StreamWriter(comicInfoEntry.Open()))
            {
                writer.Write(comicInfoXml);
            }

            // Add a dummy image file
            var imageEntry = archive.CreateEntry("page001.jpg");
            using (var writer = new StreamWriter(imageEntry.Open()))
            {
                writer.Write("dummy image content");
            }
        }

        return filePath;
    }

    public void Dispose()
    {
        // Clean up test directory
        if (Directory.Exists(_testDirectory))
        {
            try
            {
                Directory.Delete(_testDirectory, true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }
}
