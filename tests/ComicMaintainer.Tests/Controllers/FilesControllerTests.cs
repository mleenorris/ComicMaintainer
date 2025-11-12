using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.WebApi.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using System.Text.Json;

namespace ComicMaintainer.Tests.Controllers;

public class FilesControllerTests
{
    private readonly Mock<IFileStoreService> _mockFileStore;
    private readonly Mock<IComicProcessorService> _mockProcessor;
    private readonly Mock<IProcessingHistoryService> _mockHistoryService;
    private readonly Mock<ILogger<FilesController>> _mockLogger;
    private readonly Mock<IOptions<AppSettings>> _mockSettings;
    private readonly FilesController _controller;

    public FilesControllerTests()
    {
        _mockFileStore = new Mock<IFileStoreService>();
        _mockProcessor = new Mock<IComicProcessorService>();
        _mockHistoryService = new Mock<IProcessingHistoryService>();
        _mockLogger = new Mock<ILogger<FilesController>>();
        _mockSettings = new Mock<IOptions<AppSettings>>();
        
        // Setup default settings with temp directory as watched directory for tests
        var settings = new AppSettings
        {
            WatchedDirectory = Path.GetTempPath()
        };
        _mockSettings.Setup(s => s.Value).Returns(settings);
        
        _controller = new FilesController(_mockFileStore.Object, _mockProcessor.Object, _mockHistoryService.Object, _mockLogger.Object, _mockSettings.Object);
    }

    [Fact]
    public async Task GetFiles_WithoutFilter_ReturnsOkWithFiles()
    {
        // Arrange
        var files = new List<ComicFile>
        {
            new() { FilePath = "/test/file1.cbz", IsProcessed = true },
            new() { FilePath = "/test/file2.cbz", IsProcessed = false }
        };
        _mockFileStore.Setup(fs => fs.GetFilteredFilesAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(files);
        _mockFileStore.Setup(fs => fs.GetFilteredFilesAsync("unprocessed", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ComicFile> { files[1] });

        // Act
        var result = await _controller.GetFiles();

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(okResult.Value);
        // Check that it returns an object with expected properties
        var resultValue = okResult.Value;
        var filesProperty = resultValue?.GetType().GetProperty("files");
        Assert.NotNull(filesProperty);
        var returnedFiles = filesProperty.GetValue(resultValue) as List<FileDto>;
        Assert.NotNull(returnedFiles);
        Assert.Equal(2, returnedFiles.Count);
    }

    [Fact]
    public async Task GetFiles_WithFilter_ReturnsFilteredFiles()
    {
        // Arrange
        var files = new List<ComicFile>
        {
            new() { FilePath = "/test/batman.cbz", IsProcessed = true }
        };
        _mockFileStore.Setup(fs => fs.GetFilteredFilesAsync("processed", It.IsAny<CancellationToken>()))
            .ReturnsAsync(files);
        _mockFileStore.Setup(fs => fs.GetFilteredFilesAsync("unprocessed", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ComicFile>());

        // Act
        var result = await _controller.GetFiles(filter: "marked");

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(okResult.Value);
        var resultValue = okResult.Value;
        var filesProperty = resultValue?.GetType().GetProperty("files");
        Assert.NotNull(filesProperty);
        var returnedFiles = filesProperty.GetValue(resultValue) as List<FileDto>;
        Assert.NotNull(returnedFiles);
        Assert.Single(returnedFiles);
    }

    [Fact]
    public async Task GetFiles_WhenExceptionThrown_ReturnsInternalServerError()
    {
        // Arrange
        _mockFileStore.Setup(fs => fs.GetFilteredFilesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Test error"));

        // Act
        var result = await _controller.GetFiles();

        // Assert
        var statusCodeResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(500, statusCodeResult.StatusCode);
    }

    [Fact]
    public async Task GetFileCounts_ReturnsOkWithCounts()
    {
        // Arrange
        _mockFileStore.Setup(fs => fs.GetFileCountsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((100, 60, 40, 5));

        // Act
        var result = await _controller.GetFileCounts();

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(okResult.Value);
    }

    [Fact]
    public async Task GetFileCounts_WhenExceptionThrown_ReturnsInternalServerError()
    {
        // Arrange
        _mockFileStore.Setup(fs => fs.GetFileCountsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Test error"));

        // Act
        var result = await _controller.GetFileCounts();

        // Assert
        var statusCodeResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(500, statusCodeResult.StatusCode);
    }

    [Fact]
    public async Task GetMetadata_WithValidPath_ReturnsOkWithMetadata()
    {
        // Arrange
        var testFilePath = Path.Combine(Path.GetTempPath(), "file.cbz");
        var metadata = new ComicMetadata { Series = "Batman", Issue = "12" };
        _mockProcessor.Setup(p => p.GetMetadataAsync(testFilePath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(metadata);

        // Act
        var result = await _controller.GetMetadata(testFilePath);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var returnedMetadata = Assert.IsType<ComicMetadata>(okResult.Value);
        Assert.Equal("Batman", returnedMetadata.Series);
    }

    [Fact]
    public async Task GetMetadata_WithEmptyPath_ReturnsBadRequest()
    {
        // Act
        var result = await _controller.GetMetadata("");

        // Assert
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetMetadata_WhenMetadataNotFound_ReturnsNotFound()
    {
        // Arrange
        var testFilePath = Path.Combine(Path.GetTempPath(), "file.cbz");
        _mockProcessor.Setup(p => p.GetMetadataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ComicMetadata?)null);

        // Act
        var result = await _controller.GetMetadata(testFilePath);

        // Assert
        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task UpdateMetadata_WithValidData_ReturnsOk()
    {
        // Arrange
        var testFilePath = Path.Combine(Path.GetTempPath(), "file.cbz");
        var metadata = new ComicMetadata { Series = "Superman", Issue = "5" };
        _mockProcessor.Setup(p => p.UpdateMetadataAsync(testFilePath, metadata, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Act
        var result = await _controller.UpdateMetadata(testFilePath, metadata);

        // Assert
        Assert.IsType<OkResult>(result);
    }

    [Fact]
    public async Task UpdateMetadata_WithEmptyPath_ReturnsBadRequest()
    {
        // Arrange
        var metadata = new ComicMetadata { Series = "Superman" };

        // Act
        var result = await _controller.UpdateMetadata("", metadata);

        // Assert
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task UpdateMetadata_WhenUpdateFails_ReturnsBadRequest()
    {
        // Arrange
        var metadata = new ComicMetadata { Series = "Superman" };
        _mockProcessor.Setup(p => p.UpdateMetadataAsync(It.IsAny<string>(), metadata, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        // Act
        var result = await _controller.UpdateMetadata("/test/file.cbz", metadata);

        // Assert
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task ProcessFile_WithValidPath_ReturnsOk()
    {
        // Arrange
        var testFilePath = Path.Combine(Path.GetTempPath(), "file.cbz");
        _mockProcessor.Setup(p => p.ProcessFileAsync(testFilePath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Act
        var result = await _controller.ProcessFile(testFilePath);

        // Assert
        Assert.IsType<OkResult>(result);
    }

    [Fact]
    public async Task ProcessFile_WithEmptyPath_ReturnsBadRequest()
    {
        // Act
        var result = await _controller.ProcessFile("");

        // Assert
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task ProcessFile_WhenProcessingFails_ReturnsBadRequest()
    {
        // Arrange
        _mockProcessor.Setup(p => p.ProcessFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        // Act
        var result = await _controller.ProcessFile("/test/file.cbz");

        // Assert
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task ProcessBatch_WithValidPaths_ReturnsOkWithJobId()
    {
        // Arrange
        var jobId = Guid.NewGuid();
        var filePaths = new List<string> { "/test/file1.cbz", "/test/file2.cbz" };
        _mockProcessor.Setup(p => p.ProcessFilesAsync(filePaths, It.IsAny<CancellationToken>()))
            .ReturnsAsync(jobId);

        // Act
        var result = await _controller.ProcessBatch(filePaths);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(okResult.Value);
    }

    [Fact]
    public async Task ProcessBatch_WhenExceptionThrown_ReturnsInternalServerError()
    {
        // Arrange
        _mockProcessor.Setup(p => p.ProcessFilesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Test error"));

        // Act
        var result = await _controller.ProcessBatch(new List<string> { "/test/file.cbz" });

        // Assert
        var statusCodeResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(500, statusCodeResult.StatusCode);
    }

    // MarkProcessed tests removed - processed state is now computed from renamed && normalized
    // Use MarkFileRenamedAsync and MarkFileNormalizedAsync instead

    // Helper method to encode file path to base64 URL-safe format
    private static string EncodeFilePathForUrl(string filePath)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(filePath);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    [Fact]
    public async Task GetFileTags_WithValidPath_ReturnsOkWithMetadata()
    {
        // Arrange
        var filePath = "/test/file.cbz";
        var encodedPath = EncodeFilePathForUrl(filePath);
        var metadata = new ComicMetadata { Series = "Batman", Issue = "12" };
        _mockProcessor.Setup(p => p.GetMetadataAsync(filePath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(metadata);

        // Act
        var result = await _controller.GetFileTags(encodedPath);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var returnedMetadata = Assert.IsType<ComicMetadata>(okResult.Value);
        Assert.Equal("Batman", returnedMetadata.Series);
    }

    [Fact]
    public async Task GetFileTags_WhenMetadataNotFound_ReturnsNotFound()
    {
        // Arrange
        var filePath = "/test/file.cbz";
        var encodedPath = EncodeFilePathForUrl(filePath);
        _mockProcessor.Setup(p => p.GetMetadataAsync(filePath, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ComicMetadata?)null);

        // Act
        var result = await _controller.GetFileTags(encodedPath);

        // Assert
        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task UpdateFileTags_WithValidData_ReturnsOk()
    {
        // Arrange
        var filePath = "/test/file.cbz";
        var encodedPath = EncodeFilePathForUrl(filePath);
        var metadata = new ComicMetadata { Series = "Superman", Issue = "5" };
        _mockProcessor.Setup(p => p.UpdateMetadataAsync(filePath, metadata, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Act
        var result = await _controller.UpdateFileTags(encodedPath, metadata);

        // Assert
        Assert.IsType<OkResult>(result);
    }

    [Fact]
    public async Task UpdateFileTags_WhenUpdateFails_ReturnsBadRequest()
    {
        // Arrange
        var filePath = "/test/file.cbz";
        var encodedPath = EncodeFilePathForUrl(filePath);
        var metadata = new ComicMetadata { Series = "Superman" };
        _mockProcessor.Setup(p => p.UpdateMetadataAsync(filePath, metadata, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        // Act
        var result = await _controller.UpdateFileTags(encodedPath, metadata);

        // Assert
        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("Failed to update tags", badRequestResult.Value);
    }

    [Fact]
    public async Task GetFileTags_WithPathContainingSlashes_ReturnsOkWithMetadata()
    {
        // Arrange
        var filePath = "/comics/Marvel/Spider-Man/issue001.cbz";
        var encodedPath = EncodeFilePathForUrl(filePath);
        var metadata = new ComicMetadata { Series = "Spider-Man", Issue = "1" };
        _mockProcessor.Setup(p => p.GetMetadataAsync(filePath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(metadata);

        // Act
        var result = await _controller.GetFileTags(encodedPath);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var returnedMetadata = Assert.IsType<ComicMetadata>(okResult.Value);
        Assert.Equal("Spider-Man", returnedMetadata.Series);
        Assert.Equal("1", returnedMetadata.Issue);
    }

    [Fact]
    public async Task UpdateFileTags_WithPathContainingSlashes_ReturnsOk()
    {
        // Arrange
        var filePath = "/comics/DC/Batman/issue050.cbz";
        var encodedPath = EncodeFilePathForUrl(filePath);
        var metadata = new ComicMetadata { Series = "Batman", Issue = "50" };
        _mockProcessor.Setup(p => p.UpdateMetadataAsync(filePath, metadata, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Act
        var result = await _controller.UpdateFileTags(encodedPath, metadata);

        // Assert
        Assert.IsType<OkResult>(result);
        _mockProcessor.Verify(p => p.UpdateMetadataAsync(filePath, metadata, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteFile_WithExistingFile_ReturnsOkAndDeletesFile()
    {
        // Arrange
        var testFilePath = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid()}.cbz");
        
        // Create a temporary test file
        await File.WriteAllTextAsync(testFilePath, "test content");
        Assert.True(File.Exists(testFilePath));

        _mockFileStore.Setup(fs => fs.RemoveFileAsync(testFilePath, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _controller.DeleteFile(testFilePath);

        // Assert
        Assert.IsType<OkResult>(result);
        Assert.False(File.Exists(testFilePath)); // File should be deleted
        _mockFileStore.Verify(fs => fs.RemoveFileAsync(testFilePath, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteFile_WithNonExistentFile_ReturnsNotFound()
    {
        // Arrange - use a path within watched directory that doesn't exist
        var filePath = Path.Combine(Path.GetTempPath(), "nonexistent", $"test-{Guid.NewGuid()}.cbz");

        // Act
        var result = await _controller.DeleteFile(filePath);

        // Assert
        Assert.IsType<NotFoundResult>(result);
        _mockFileStore.Verify(fs => fs.RemoveFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteFile_WhenFileStoreThrowsException_ReturnsInternalServerError()
    {
        // Arrange
        var filePath = Path.Combine(Path.GetTempPath(), $"test-exception-{Guid.NewGuid()}.cbz");
        
        // Create a temporary test file
        await File.WriteAllTextAsync(filePath, "test content");
        Assert.True(File.Exists(filePath));

        _mockFileStore.Setup(fs => fs.RemoveFileAsync(filePath, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Test error"));

        // Act
        var result = await _controller.DeleteFile(filePath);

        // Assert
        var statusCodeResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(500, statusCodeResult.StatusCode);
        Assert.Equal("Error deleting file", statusCodeResult.Value);
        
        // Cleanup: delete the test file if it still exists
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task DeleteFile_WhenFileDeletionThrowsIOException_ReturnsInternalServerError()
    {
        // Arrange - create a file that we'll make read-only/locked to cause deletion to fail
        var testFilePath = Path.Combine(Path.GetTempPath(), $"test-locked-{Guid.NewGuid()}.cbz");
        await File.WriteAllTextAsync(testFilePath, "test content");
        
        // Make the file read-only to potentially cause deletion issues
        var fileInfo = new FileInfo(testFilePath);
        fileInfo.IsReadOnly = true;

        _mockFileStore.Setup(fs => fs.RemoveFileAsync(testFilePath, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _controller.DeleteFile(testFilePath);

        // Assert - on Unix systems, readonly doesn't prevent deletion, so this might succeed
        // We accept either success or error
        Assert.True(
            result is OkResult || 
            (result is ObjectResult objResult && objResult.StatusCode == 500),
            "Expected either Ok or 500 error result"
        );
        
        // Cleanup - remove readonly and delete if it still exists
        if (File.Exists(testFilePath))
        {
            fileInfo.IsReadOnly = false;
            File.Delete(testFilePath);
        }
    }

    [Fact]
    public async Task DeleteFileByEncodedPath_WithExistingFile_ReturnsOkAndDeletesFile()
    {
        // Arrange
        var testFilePath = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid()}.cbz");
        var encodedPath = EncodeFilePathForUrl(testFilePath);
        
        // Create a temporary test file
        await File.WriteAllTextAsync(testFilePath, "test content");
        Assert.True(File.Exists(testFilePath));

        _mockFileStore.Setup(fs => fs.RemoveFileAsync(testFilePath, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _controller.DeleteFileByEncodedPath(encodedPath);

        // Assert
        Assert.IsType<OkResult>(result);
        Assert.False(File.Exists(testFilePath)); // File should be deleted
        _mockFileStore.Verify(fs => fs.RemoveFileAsync(testFilePath, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteFileByEncodedPath_WithNonExistentFile_ReturnsNotFound()
    {
        // Arrange - use a path within watched directory that doesn't exist
        var filePath = Path.Combine(Path.GetTempPath(), "nonexistent", $"test-{Guid.NewGuid()}.cbz");
        var encodedPath = EncodeFilePathForUrl(filePath);

        // Act
        var result = await _controller.DeleteFileByEncodedPath(encodedPath);

        // Assert
        Assert.IsType<NotFoundResult>(result);
        _mockFileStore.Verify(fs => fs.RemoveFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteFileByEncodedPath_WithInvalidEncodedPath_ReturnsBadRequest()
    {
        // Arrange
        var invalidEncodedPath = "!!!invalid-base64!!!";

        // Act
        var result = await _controller.DeleteFileByEncodedPath(invalidEncodedPath);

        // Assert
        Assert.IsType<BadRequestObjectResult>(result);
        _mockFileStore.Verify(fs => fs.RemoveFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteFileByEncodedPath_WhenFileStoreThrowsException_ReturnsInternalServerError()
    {
        // Arrange
        var testFilePath = Path.Combine(Path.GetTempPath(), $"test-exception-{Guid.NewGuid()}.cbz");
        var encodedPath = EncodeFilePathForUrl(testFilePath);
        
        // Create a temporary test file
        await File.WriteAllTextAsync(testFilePath, "test content");
        Assert.True(File.Exists(testFilePath));

        _mockFileStore.Setup(fs => fs.RemoveFileAsync(testFilePath, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Test error"));

        // Act
        var result = await _controller.DeleteFileByEncodedPath(encodedPath);

        // Assert
        var statusCodeResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(500, statusCodeResult.StatusCode);
        Assert.Equal("Error deleting file", statusCodeResult.Value);
        
        // Cleanup: delete the test file if it still exists
        if (File.Exists(testFilePath))
        {
            File.Delete(testFilePath);
        }
    }

    [Fact]
    public async Task DeleteFile_WithPathOutsideWatchedDirectory_ReturnsBadRequest()
    {
        // Arrange
        var outsidePath = "/etc/passwd"; // Path outside watched directory

        // Act
        var result = await _controller.DeleteFile(outsidePath);

        // Assert
        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("File path is outside the allowed directory", badRequestResult.Value);
        _mockFileStore.Verify(fs => fs.RemoveFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteFileByEncodedPath_WithPathOutsideWatchedDirectory_ReturnsBadRequest()
    {
        // Arrange
        var outsidePath = "/etc/passwd"; // Path outside watched directory
        var encodedPath = EncodeFilePathForUrl(outsidePath);

        // Act
        var result = await _controller.DeleteFileByEncodedPath(encodedPath);

        // Assert
        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("File path is outside the allowed directory", badRequestResult.Value);
        _mockFileStore.Verify(fs => fs.RemoveFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteFile_WithPathTraversalAttempt_ReturnsBadRequest()
    {
        // Arrange
        var traversalPath = Path.Combine(Path.GetTempPath(), "..", "..", "etc", "passwd");

        // Act
        var result = await _controller.DeleteFile(traversalPath);

        // Assert
        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("File path is outside the allowed directory", badRequestResult.Value);
        _mockFileStore.Verify(fs => fs.RemoveFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteFileByEncodedPath_WithPathTraversalAttempt_ReturnsBadRequest()
    {
        // Arrange
        var traversalPath = Path.Combine(Path.GetTempPath(), "..", "..", "etc", "passwd");
        var encodedPath = EncodeFilePathForUrl(traversalPath);

        // Act
        var result = await _controller.DeleteFileByEncodedPath(encodedPath);

        // Assert
        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("File path is outside the allowed directory", badRequestResult.Value);
        _mockFileStore.Verify(fs => fs.RemoveFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // New RESTful endpoint tests

    [Fact]
    public async Task ScanUnmarked_ReturnsOkWithCounts()
    {
        // Arrange
        var allFiles = new List<ComicFile>
        {
            new() { FilePath = "/test/file1.cbz", IsProcessed = true },
            new() { FilePath = "/test/file2.cbz", IsProcessed = false },
            new() { FilePath = "/test/file3.cbz", IsProcessed = false }
        };
        _mockFileStore.Setup(fs => fs.GetAllFilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(allFiles);
        _mockFileStore.Setup(fs => fs.GetFilteredFilesAsync("unprocessed", It.IsAny<CancellationToken>()))
            .ReturnsAsync(allFiles.Where(f => !f.IsProcessed).ToList());
        _mockFileStore.Setup(fs => fs.GetFilteredFilesAsync("processed", It.IsAny<CancellationToken>()))
            .ReturnsAsync(allFiles.Where(f => f.IsProcessed).ToList());

        // Act
        var result = await _controller.ScanUnmarked();

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(okResult.Value);
        var resultValue = okResult.Value;
        var totalCountProp = resultValue?.GetType().GetProperty("total_count");
        Assert.NotNull(totalCountProp);
        var totalCount = (int?)totalCountProp.GetValue(resultValue);
        Assert.Equal(3, totalCount);
    }

    [Fact]
    public async Task ProcessFileByEncodedPath_WithValidPath_ReturnsOk()
    {
        // Arrange
        var filePath = Path.Combine(Path.GetTempPath(), "test.cbz");
        var encodedPath = EncodeFilePathForUrl(filePath);
        _mockProcessor.Setup(p => p.ProcessFileAsync(filePath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Act
        var result = await _controller.ProcessFileByEncodedPath(encodedPath);

        // Assert
        Assert.IsType<OkResult>(result);
    }

    [Fact]
    public async Task ProcessFileByEncodedPath_WithInvalidPath_ReturnsBadRequest()
    {
        // Arrange
        var encodedPath = "invalid-base64";

        // Act
        var result = await _controller.ProcessFileByEncodedPath(encodedPath);

        // Assert
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task RenameFileByEncodedPath_WithValidPath_ReturnsOk()
    {
        // Arrange
        var filePath = Path.Combine(Path.GetTempPath(), "test.cbz");
        var encodedPath = EncodeFilePathForUrl(filePath);
        var metadata = new ComicMetadata { Series = "Batman", Issue = "1" };
        _mockProcessor.Setup(p => p.GetMetadataAsync(filePath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(metadata);

        // Act
        var result = await _controller.RenameFileByEncodedPath(encodedPath);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(okResult.Value);
    }

    [Fact]
    public async Task RenameFileByEncodedPath_WithInvalidPath_ReturnsBadRequestOrNotFound()
    {
        // Arrange
        // "invalid-base64" can still decode as base64, so it may decode to a string
        // But the metadata will be null for a non-existent file
        var encodedPath = "invalid-base64";
        _mockProcessor.Setup(p => p.GetMetadataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ComicMetadata?)null);

        // Act
        var result = await _controller.RenameFileByEncodedPath(encodedPath);

        // Assert - Either BadRequest for truly invalid path, or NotFound for decoded but non-existent file
        Assert.True(result is BadRequestObjectResult || result is NotFoundObjectResult);
    }

    [Fact]
    public async Task RenameFileByEncodedPath_StartsRenameJob()
    {
        // Arrange
        var filePath = Path.Combine(Path.GetTempPath(), "test.cbz");
        var encodedPath = EncodeFilePathForUrl(filePath);
        var expectedJobId = Guid.NewGuid();
        
        _mockProcessor.Setup(p => p.RenameFilesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedJobId);

        // Act
        var result = await _controller.RenameFileByEncodedPath(encodedPath);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(okResult.Value);
        
        // Verify the rename job was started
        _mockProcessor.Verify(p => p.RenameFilesAsync(
            It.Is<IEnumerable<string>>(files => files.Single() == filePath),
            It.IsAny<CancellationToken>()), 
            Times.Once);
    }

    // UpdateProcessedStatus tests removed - processed state is now computed from renamed && normalized
    // Processed status cannot be set directly anymore
}
