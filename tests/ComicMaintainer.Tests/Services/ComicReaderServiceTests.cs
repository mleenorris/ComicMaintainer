using System.IO.Compression;
using ComicMaintainer.Core.Services;
using Microsoft.Extensions.Logging;
using Moq;

namespace ComicMaintainer.Tests.Services;

public class ComicReaderServiceTests : IDisposable
{
    private readonly Mock<ILogger<ComicReaderService>> _mockLogger;
    private readonly ComicReaderService _service;
    private readonly string _testDir;

    public ComicReaderServiceTests()
    {
        _mockLogger = new Mock<ILogger<ComicReaderService>>();
        _service = new ComicReaderService(_mockLogger.Object);
        _testDir = Path.Combine(Path.GetTempPath(), "comic_reader_test_" + Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDir);
    }

    [Fact]
    public async Task GetPageCountAsync_WithValidCbz_ReturnsCorrectCount()
    {
        // Arrange
        var cbzPath = CreateTestCbz(3);

        // Act
        var count = await _service.GetPageCountAsync(cbzPath);

        // Assert
        Assert.Equal(3, count);
    }

    [Fact]
    public async Task GetPageCountAsync_WithNonExistentFile_ReturnsZero()
    {
        // Arrange
        var nonExistentPath = Path.Combine(_testDir, "nonexistent.cbz");

        // Act
        var count = await _service.GetPageCountAsync(nonExistentPath);

        // Assert
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task GetPageAsync_WithValidCbzAndPageNumber_ReturnsPage()
    {
        // Arrange
        var cbzPath = CreateTestCbz(3);

        // Act
        var result = await _service.GetPageAsync(cbzPath, 1);

        // Assert
        Assert.NotNull(result);
        Assert.NotNull(result.Value.Data);
        Assert.True(result.Value.Data.Length > 0);
        Assert.Equal("image/jpeg", result.Value.ContentType);
    }

    [Fact]
    public async Task GetPageAsync_WithNonExistentFile_ReturnsNull()
    {
        // Arrange
        var nonExistentPath = Path.Combine(_testDir, "nonexistent.cbz");

        // Act
        var result = await _service.GetPageAsync(nonExistentPath, 1);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetPageAsync_WithInvalidPageNumber_ReturnsNull()
    {
        // Arrange
        var cbzPath = CreateTestCbz(3);

        // Act
        var result = await _service.GetPageAsync(cbzPath, 10);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetPageAsync_WithUnsupportedExtension_ReturnsNull()
    {
        // Arrange
        var txtPath = Path.Combine(_testDir, "test.txt");
        File.WriteAllText(txtPath, "test");

        // Act
        var result = await _service.GetPageAsync(txtPath, 1);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetPageNamesAsync_WithValidCbz_ReturnsPageNames()
    {
        // Arrange
        var cbzPath = CreateTestCbz(3);

        // Act
        var pageNames = await _service.GetPageNamesAsync(cbzPath);

        // Assert
        Assert.NotNull(pageNames);
        Assert.Equal(3, pageNames.Count);
        Assert.All(pageNames, name => Assert.EndsWith(".jpg", name));
    }

    [Fact]
    public async Task GetPageNamesAsync_WithNonExistentFile_ReturnsEmptyList()
    {
        // Arrange
        var nonExistentPath = Path.Combine(_testDir, "nonexistent.cbz");

        // Act
        var pageNames = await _service.GetPageNamesAsync(nonExistentPath);

        // Assert
        Assert.NotNull(pageNames);
        Assert.Empty(pageNames);
    }

    [Fact]
    public async Task GetPageNamesAsync_WithUnsupportedExtension_ReturnsEmptyList()
    {
        // Arrange
        var txtPath = Path.Combine(_testDir, "test.txt");
        File.WriteAllText(txtPath, "test");

        // Act
        var pageNames = await _service.GetPageNamesAsync(txtPath);

        // Assert
        Assert.NotNull(pageNames);
        Assert.Empty(pageNames);
    }

    [Fact]
    public async Task GetPageAsync_WithDifferentImageTypes_ReturnsCorrectContentType()
    {
        // Arrange
        var cbzPath = CreateTestCbzWithMultipleTypes();

        // Act
        var jpgResult = await _service.GetPageAsync(cbzPath, 1);
        var pngResult = await _service.GetPageAsync(cbzPath, 2);

        // Assert
        Assert.NotNull(jpgResult);
        Assert.Equal("image/jpeg", jpgResult.Value.ContentType);
        Assert.NotNull(pngResult);
        Assert.Equal("image/png", pngResult.Value.ContentType);
    }

    [Fact]
    public async Task GetPageCountAsync_WithCbzContainingNonImageFiles_CountsOnlyImages()
    {
        // Arrange
        var cbzPath = CreateTestCbzWithMixedFiles();

        // Act
        var count = await _service.GetPageCountAsync(cbzPath);

        // Assert
        Assert.Equal(2, count); // Only 2 image files
    }

    private string CreateTestCbz(int pageCount)
    {
        var cbzPath = Path.Combine(_testDir, $"test_{Guid.NewGuid()}.cbz");
        
        using (var archive = ZipFile.Open(cbzPath, ZipArchiveMode.Create))
        {
            for (int i = 0; i < pageCount; i++)
            {
                var entry = archive.CreateEntry($"page{i:D3}.jpg");
                using var stream = entry.Open();
                var imageData = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }; // Minimal JPEG header
                stream.Write(imageData, 0, imageData.Length);
            }
        }
        
        return cbzPath;
    }

    private string CreateTestCbzWithMultipleTypes()
    {
        var cbzPath = Path.Combine(_testDir, $"test_multi_{Guid.NewGuid()}.cbz");
        
        using (var archive = ZipFile.Open(cbzPath, ZipArchiveMode.Create))
        {
            // Add JPG
            var jpgEntry = archive.CreateEntry("page001.jpg");
            using (var stream = jpgEntry.Open())
            {
                var imageData = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 };
                stream.Write(imageData, 0, imageData.Length);
            }
            
            // Add PNG
            var pngEntry = archive.CreateEntry("page002.png");
            using (var stream = pngEntry.Open())
            {
                var imageData = new byte[] { 0x89, 0x50, 0x4E, 0x47 }; // PNG signature
                stream.Write(imageData, 0, imageData.Length);
            }
        }
        
        return cbzPath;
    }

    private string CreateTestCbzWithMixedFiles()
    {
        var cbzPath = Path.Combine(_testDir, $"test_mixed_{Guid.NewGuid()}.cbz");
        
        using (var archive = ZipFile.Open(cbzPath, ZipArchiveMode.Create))
        {
            // Add images
            for (int i = 0; i < 2; i++)
            {
                var entry = archive.CreateEntry($"page{i:D3}.jpg");
                using var stream = entry.Open();
                var imageData = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 };
                stream.Write(imageData, 0, imageData.Length);
            }
            
            // Add non-image file
            var txtEntry = archive.CreateEntry("readme.txt");
            using (var stream = txtEntry.Open())
            {
                var textData = System.Text.Encoding.UTF8.GetBytes("test");
                stream.Write(textData, 0, textData.Length);
            }
        }
        
        return cbzPath;
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            Directory.Delete(_testDir, true);
        }
    }
}
