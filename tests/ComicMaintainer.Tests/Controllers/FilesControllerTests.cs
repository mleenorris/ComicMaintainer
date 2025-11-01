using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.WebApi.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;

namespace ComicMaintainer.Tests.Controllers;

public class FilesControllerTests
{
    private readonly Mock<IFileStoreService> _mockFileStore;
    private readonly Mock<IComicProcessorService> _mockProcessor;
    private readonly Mock<ILogger<FilesController>> _mockLogger;
    private readonly FilesController _controller;

    public FilesControllerTests()
    {
        _mockFileStore = new Mock<IFileStoreService>();
        _mockProcessor = new Mock<IComicProcessorService>();
        _mockLogger = new Mock<ILogger<FilesController>>();
        _controller = new FilesController(_mockFileStore.Object, _mockProcessor.Object, _mockLogger.Object);
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
        var returnedFiles = filesProperty.GetValue(resultValue) as List<ComicFile>;
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
        var returnedFiles = filesProperty.GetValue(resultValue) as List<ComicFile>;
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
        var metadata = new ComicMetadata { Series = "Batman", Issue = "12" };
        _mockProcessor.Setup(p => p.GetMetadataAsync("/test/file.cbz", It.IsAny<CancellationToken>()))
            .ReturnsAsync(metadata);

        // Act
        var result = await _controller.GetMetadata("/test/file.cbz");

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
        _mockProcessor.Setup(p => p.GetMetadataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ComicMetadata?)null);

        // Act
        var result = await _controller.GetMetadata("/test/file.cbz");

        // Assert
        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task UpdateMetadata_WithValidData_ReturnsOk()
    {
        // Arrange
        var metadata = new ComicMetadata { Series = "Superman", Issue = "5" };
        _mockProcessor.Setup(p => p.UpdateMetadataAsync("/test/file.cbz", metadata, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Act
        var result = await _controller.UpdateMetadata("/test/file.cbz", metadata);

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
        _mockProcessor.Setup(p => p.ProcessFileAsync("/test/file.cbz", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Act
        var result = await _controller.ProcessFile("/test/file.cbz");

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

    [Fact]
    public async Task MarkProcessed_WithValidPath_ReturnsOk()
    {
        // Arrange
        _mockFileStore.Setup(fs => fs.MarkFileProcessedAsync("/test/file.cbz", true, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _controller.MarkProcessed("/test/file.cbz", true);

        // Assert
        Assert.IsType<OkResult>(result);
    }

    [Fact]
    public async Task MarkProcessed_WithEmptyPath_ReturnsBadRequest()
    {
        // Act
        var result = await _controller.MarkProcessed("", true);

        // Assert
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task MarkProcessed_WhenExceptionThrown_ReturnsInternalServerError()
    {
        // Arrange
        _mockFileStore.Setup(fs => fs.MarkFileProcessedAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Test error"));

        // Act
        var result = await _controller.MarkProcessed("/test/file.cbz", true);

        // Assert
        var statusCodeResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(500, statusCodeResult.StatusCode);
    }
}
