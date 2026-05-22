using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.WebApi.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;

namespace ComicMaintainer.Tests.Controllers;

public class ProcessControllerTests
{
    private readonly Mock<IComicProcessorService> _processorMock;
    private readonly Mock<IFileStoreService> _fileStoreMock;
    private readonly Mock<ILogger<ProcessController>> _loggerMock;
    private readonly ProcessController _controller;

    public ProcessControllerTests()
    {
        _processorMock = new Mock<IComicProcessorService>();
        _fileStoreMock = new Mock<IFileStoreService>();
        _loggerMock = new Mock<ILogger<ProcessController>>();
        _controller = new ProcessController(_processorMock.Object, _fileStoreMock.Object, _loggerMock.Object);
    }

    [Fact]
    public async Task ProcessAll_WithoutStreaming_ReturnsJobId()
    {
        // Arrange
        var testFiles = new List<ComicFile>
        {
            new() { FilePath = "/test/file1.cbz" },
            new() { FilePath = "/test/file2.cbz" }
        };
        var expectedJobId = Guid.NewGuid();
        
        _fileStoreMock
            .Setup(x => x.GetAllFilesAsync(default))
            .ReturnsAsync(testFiles);
            
        _processorMock
            .Setup(x => x.ProcessFilesAsync(It.IsAny<IEnumerable<string>>(), default))
            .ReturnsAsync(expectedJobId);

        // Act
        var result = await _controller.ProcessAll(stream: false);

        // Assert
        var okResult = Assert.IsType<ActionResult<object>>(result);
        var objectResult = Assert.IsType<OkObjectResult>(okResult.Result);
        Assert.NotNull(objectResult.Value);
        
        var jobIdProperty = objectResult.Value.GetType().GetProperty("jobId");
        Assert.Equal(expectedJobId, jobIdProperty?.GetValue(objectResult.Value));
        
        _fileStoreMock.Verify(x => x.GetAllFilesAsync(default), Times.Once);
        _processorMock.Verify(x => x.ProcessFilesAsync(It.IsAny<IEnumerable<string>>(), default), Times.Once);
    }

    [Fact]
    public async Task ProcessAll_WithStreaming_ReturnsJobIdAndStreamingFlag()
    {
        // Arrange
        var testFiles = new List<ComicFile>
        {
            new() { FilePath = "/test/file1.cbz" }
        };
        var expectedJobId = Guid.NewGuid();
        
        _fileStoreMock
            .Setup(x => x.GetAllFilesAsync(default))
            .ReturnsAsync(testFiles);
            
        _processorMock
            .Setup(x => x.ProcessFilesAsync(It.IsAny<IEnumerable<string>>(), default))
            .ReturnsAsync(expectedJobId);

        // Act
        var result = await _controller.ProcessAll(stream: true);

        // Assert
        var okResult = Assert.IsType<ActionResult<object>>(result);
        var objectResult = Assert.IsType<OkObjectResult>(okResult.Result);
        Assert.NotNull(objectResult.Value);
        
        var streamingProperty = objectResult.Value.GetType().GetProperty("streaming");
        Assert.Equal(true, streamingProperty?.GetValue(objectResult.Value));
    }

    [Fact]
    public async Task ProcessSelected_WithFiles_ReturnsJobId()
    {
        // Arrange
        var files = new List<string> { "test1.cbz", "test2.cbz" };
        var request = new ProcessController.ProcessRequest { Files = files };
        var expectedJobId = Guid.NewGuid();
        
        _processorMock
            .Setup(x => x.ProcessFilesAsync(It.IsAny<IEnumerable<string>>(), default))
            .ReturnsAsync(expectedJobId);

        // Act
        var result = await _controller.ProcessSelected(request, stream: false);

        // Assert
        var okResult = Assert.IsType<ActionResult<object>>(result);
        var objectResult = Assert.IsType<OkObjectResult>(okResult.Result);
        
        var jobIdProperty = objectResult.Value?.GetType().GetProperty("jobId");
        Assert.NotNull(jobIdProperty);
        Assert.Equal(expectedJobId, jobIdProperty.GetValue(objectResult.Value));
        
        _processorMock.Verify(x => x.ProcessFilesAsync(files, default), Times.Once);
    }

    [Fact]
    public async Task RenameAll_ReturnsJobId()
    {
        // Arrange
        var testFiles = new List<ComicFile>
        {
            new() { FilePath = "/test/file1.cbz" },
            new() { FilePath = "/test/file2.cbz" }
        };
        var expectedJobId = Guid.NewGuid();
        
        _fileStoreMock
            .Setup(x => x.GetAllFilesAsync(default))
            .ReturnsAsync(testFiles);
            
        _processorMock
            .Setup(x => x.RenameFilesAsync(It.IsAny<IEnumerable<string>>(), default))
            .ReturnsAsync(expectedJobId);

        // Act
        var result = await _controller.RenameAll(stream: false);

        // Assert
        var okResult = Assert.IsType<ActionResult<object>>(result);
        var objectResult = Assert.IsType<OkObjectResult>(okResult.Result);
        Assert.NotNull(objectResult.Value);
        
        var jobIdProperty = objectResult.Value.GetType().GetProperty("jobId");
        Assert.Equal(expectedJobId, jobIdProperty?.GetValue(objectResult.Value));
        
        _fileStoreMock.Verify(x => x.GetAllFilesAsync(default), Times.Once);
        _processorMock.Verify(x => x.RenameFilesAsync(It.IsAny<IEnumerable<string>>(), default), Times.Once);
    }

    [Fact]
    public async Task RenameSelected_WithFiles_ReturnsJobId()
    {
        // Arrange
        var files = new List<string> { "test.cbz" };
        var request = new ProcessController.ProcessRequest { Files = files };
        var expectedJobId = Guid.NewGuid();
        
        _processorMock
            .Setup(x => x.RenameFilesAsync(It.IsAny<IEnumerable<string>>(), default))
            .ReturnsAsync(expectedJobId);

        // Act
        var result = await _controller.RenameSelected(request, stream: false);

        // Assert
        var okResult = Assert.IsType<ActionResult<object>>(result);
        var objectResult = Assert.IsType<OkObjectResult>(okResult.Result);
        Assert.NotNull(objectResult.Value);
        
        var jobIdProperty = objectResult.Value.GetType().GetProperty("jobId");
        Assert.Equal(expectedJobId, jobIdProperty?.GetValue(objectResult.Value));
        
        _processorMock.Verify(x => x.RenameFilesAsync(files, default), Times.Once);
    }

    [Fact]
    public async Task NormalizeAll_ReturnsJobId()
    {
        // Arrange
        var testFiles = new List<ComicFile>
        {
            new() { FilePath = "/test/file1.cbz" },
            new() { FilePath = "/test/file2.cbz" }
        };
        var expectedJobId = Guid.NewGuid();
        
        _fileStoreMock
            .Setup(x => x.GetAllFilesAsync(default))
            .ReturnsAsync(testFiles);
            
        _processorMock
            .Setup(x => x.NormalizeFilesAsync(It.IsAny<IEnumerable<string>>(), false, default))
            .ReturnsAsync(expectedJobId);

        // Act
        var result = await _controller.NormalizeAll(stream: false);

        // Assert
        var okResult = Assert.IsType<ActionResult<object>>(result);
        var objectResult = Assert.IsType<OkObjectResult>(okResult.Result);
        Assert.NotNull(objectResult.Value);
        
        var jobIdProperty = objectResult.Value.GetType().GetProperty("jobId");
        Assert.Equal(expectedJobId, jobIdProperty?.GetValue(objectResult.Value));
        
        _fileStoreMock.Verify(x => x.GetAllFilesAsync(default), Times.Once);
        _processorMock.Verify(x => x.NormalizeFilesAsync(It.IsAny<IEnumerable<string>>(), false, default), Times.Once);
    }

    [Fact]
    public async Task NormalizeAll_WithForceReprocess_IncludesAlreadyNormalizedFiles()
    {
        // Arrange: half the files are already DB-marked normalized. Without
        // force they would be filtered out; with force every non-duplicate
        // file must be passed to the processor so callers can re-run
        // normalization after a cache change.
        var testFiles = new List<ComicFile>
        {
            new() { FilePath = "/test/file1.cbz", IsNormalized = true, IsDuplicate = false },
            new() { FilePath = "/test/file2.cbz", IsNormalized = false, IsDuplicate = false },
            new() { FilePath = "/test/dup.cbz", IsNormalized = false, IsDuplicate = true },
        };
        var expectedJobId = Guid.NewGuid();
        List<string>? capturedFiles = null;

        _fileStoreMock
            .Setup(x => x.GetAllFilesAsync(default))
            .ReturnsAsync(testFiles);

        _processorMock
            .Setup(x => x.NormalizeFilesAsync(It.IsAny<IEnumerable<string>>(), true, default))
            .Callback<IEnumerable<string>, bool, CancellationToken>((f, _, _) => capturedFiles = f.ToList())
            .ReturnsAsync(expectedJobId);

        // Act
        var result = await _controller.NormalizeAll(stream: false, forceReprocess: true);

        // Assert
        var okResult = Assert.IsType<ActionResult<object>>(result);
        var objectResult = Assert.IsType<OkObjectResult>(okResult.Result);
        var jobIdProperty = objectResult.Value!.GetType().GetProperty("jobId");
        Assert.Equal(expectedJobId, jobIdProperty?.GetValue(objectResult.Value));

        Assert.NotNull(capturedFiles);
        Assert.Equal(2, capturedFiles!.Count);
        Assert.Contains("/test/file1.cbz", capturedFiles);
        Assert.Contains("/test/file2.cbz", capturedFiles);
        Assert.DoesNotContain("/test/dup.cbz", capturedFiles);
    }

    [Fact]
    public async Task NormalizeSelected_WithFiles_ReturnsJobId()
    {
        // Arrange
        var files = new List<string> { "test.cbz" };
        var request = new ProcessController.ProcessRequest { Files = files };
        var expectedJobId = Guid.NewGuid();
        
        _processorMock
            .Setup(x => x.NormalizeFilesAsync(It.IsAny<IEnumerable<string>>(), false, default))
            .ReturnsAsync(expectedJobId);

        // Act
        var result = await _controller.NormalizeSelected(request, stream: false);

        // Assert
        var okResult = Assert.IsType<ActionResult<object>>(result);
        var objectResult = Assert.IsType<OkObjectResult>(okResult.Result);
        Assert.NotNull(objectResult.Value);
        
        var jobIdProperty = objectResult.Value.GetType().GetProperty("jobId");
        Assert.Equal(expectedJobId, jobIdProperty?.GetValue(objectResult.Value));
        
        _processorMock.Verify(x => x.NormalizeFilesAsync(files, false, default), Times.Once);
    }
}
