using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.WebApi.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Controllers;

public class ComicReaderControllerTests
{
    private readonly Mock<IComicReaderService> _readerServiceMock;
    private readonly Mock<IFileStoreService> _fileStoreMock;
    private readonly Mock<ILogger<ComicReaderController>> _loggerMock;
    private readonly Mock<IOptions<AppSettings>> _settingsMock;
    private readonly ComicReaderController _controller;
    private readonly AppSettings _settings;

    public ComicReaderControllerTests()
    {
        _readerServiceMock = new Mock<IComicReaderService>();
        _fileStoreMock = new Mock<IFileStoreService>();
        _loggerMock = new Mock<ILogger<ComicReaderController>>();
        _settingsMock = new Mock<IOptions<AppSettings>>();
        
        _settings = new AppSettings
        {
            WatchedDirectory = "/test/watched"
        };
        _settingsMock.Setup(s => s.Value).Returns(_settings);

        _controller = new ComicReaderController(
            _readerServiceMock.Object,
            _fileStoreMock.Object,
            _loggerMock.Object,
            _settingsMock.Object
        );
    }

    [Fact]
    public async Task GetAdjacentFile_WithMissingFilePath_ReturnsBadRequest()
    {
        // Act
        var result = await _controller.GetAdjacentFile(null!, "next");

        // Assert
        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.NotNull(badRequestResult.Value);
    }

    [Fact]
    public async Task GetAdjacentFile_WithNextDirection_ReturnsNextFile()
    {
        // Arrange
        var currentFile = "/test/watched/comic1.cbz";
        var files = new List<ComicFile>
        {
            new() { FilePath = "/test/watched/comic1.cbz", FileName = "comic1.cbz" },
            new() { FilePath = "/test/watched/comic2.cbz", FileName = "comic2.cbz" },
            new() { FilePath = "/test/watched/comic3.cbz", FileName = "comic3.cbz" }
        };

        _fileStoreMock.Setup(f => f.GetFilteredFilesAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(files);

        // Act
        var result = await _controller.GetAdjacentFile(currentFile, "next");

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(okResult.Value);
        
        var response = okResult.Value;
        var hasAdjacentProperty = response.GetType().GetProperty("hasAdjacent");
        var filePathProperty = response.GetType().GetProperty("filePath");
        var fileNameProperty = response.GetType().GetProperty("fileName");
        
        Assert.NotNull(hasAdjacentProperty);
        Assert.NotNull(filePathProperty);
        Assert.NotNull(fileNameProperty);
        Assert.True((bool)hasAdjacentProperty.GetValue(response)!);
        Assert.Equal("/test/watched/comic2.cbz", filePathProperty.GetValue(response));
        Assert.Equal("comic2.cbz", fileNameProperty.GetValue(response));
    }

    [Fact]
    public async Task GetAdjacentFile_WithPrevDirection_ReturnsPreviousFile()
    {
        // Arrange
        var currentFile = "/test/watched/comic2.cbz";
        var files = new List<ComicFile>
        {
            new() { FilePath = "/test/watched/comic1.cbz", FileName = "comic1.cbz" },
            new() { FilePath = "/test/watched/comic2.cbz", FileName = "comic2.cbz" },
            new() { FilePath = "/test/watched/comic3.cbz", FileName = "comic3.cbz" }
        };

        _fileStoreMock.Setup(f => f.GetFilteredFilesAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(files);

        // Act
        var result = await _controller.GetAdjacentFile(currentFile, "prev");

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(okResult.Value);
        
        var response = okResult.Value;
        var hasAdjacentProperty = response.GetType().GetProperty("hasAdjacent");
        var filePathProperty = response.GetType().GetProperty("filePath");
        
        Assert.NotNull(hasAdjacentProperty);
        Assert.NotNull(filePathProperty);
        Assert.True((bool)hasAdjacentProperty.GetValue(response)!);
        Assert.Equal("/test/watched/comic1.cbz", filePathProperty.GetValue(response));
    }

    [Fact]
    public async Task GetAdjacentFile_AtEnd_ReturnsNoAdjacent()
    {
        // Arrange
        var currentFile = "/test/watched/comic3.cbz";
        var files = new List<ComicFile>
        {
            new() { FilePath = "/test/watched/comic1.cbz", FileName = "comic1.cbz" },
            new() { FilePath = "/test/watched/comic2.cbz", FileName = "comic2.cbz" },
            new() { FilePath = "/test/watched/comic3.cbz", FileName = "comic3.cbz" }
        };

        _fileStoreMock.Setup(f => f.GetFilteredFilesAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(files);

        // Act
        var result = await _controller.GetAdjacentFile(currentFile, "next");

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(okResult.Value);
        
        var response = okResult.Value;
        var hasAdjacentProperty = response.GetType().GetProperty("hasAdjacent");
        
        Assert.NotNull(hasAdjacentProperty);
        Assert.False((bool)hasAdjacentProperty.GetValue(response)!);
    }

    [Fact]
    public async Task GetAdjacentFile_AtBeginning_ReturnsNoAdjacent()
    {
        // Arrange
        var currentFile = "/test/watched/comic1.cbz";
        var files = new List<ComicFile>
        {
            new() { FilePath = "/test/watched/comic1.cbz", FileName = "comic1.cbz" },
            new() { FilePath = "/test/watched/comic2.cbz", FileName = "comic2.cbz" }
        };

        _fileStoreMock.Setup(f => f.GetFilteredFilesAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(files);

        // Act
        var result = await _controller.GetAdjacentFile(currentFile, "prev");

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(okResult.Value);
        
        var response = okResult.Value;
        var hasAdjacentProperty = response.GetType().GetProperty("hasAdjacent");
        
        Assert.NotNull(hasAdjacentProperty);
        Assert.False((bool)hasAdjacentProperty.GetValue(response)!);
    }

    [Fact]
    public async Task GetAdjacentFile_FileNotInLibrary_ReturnsNotFound()
    {
        // Arrange
        var currentFile = "/test/watched/nonexistent.cbz";
        var files = new List<ComicFile>
        {
            new() { FilePath = "/test/watched/comic1.cbz", FileName = "comic1.cbz" }
        };

        _fileStoreMock.Setup(f => f.GetFilteredFilesAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(files);

        // Act
        var result = await _controller.GetAdjacentFile(currentFile, "next");

        // Assert
        var notFoundResult = Assert.IsType<NotFoundObjectResult>(result.Result);
        Assert.NotNull(notFoundResult.Value);
    }

    [Fact]
    public async Task GetAdjacentFile_WithUnsafePath_ReturnsBadRequest()
    {
        // Arrange - Path outside watched directory
        var currentFile = "/other/directory/comic.cbz";

        // Act
        var result = await _controller.GetAdjacentFile(currentFile, "next");

        // Assert
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }
}
