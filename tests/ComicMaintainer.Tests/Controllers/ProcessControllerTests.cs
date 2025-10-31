using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.WebApi.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;

namespace ComicMaintainer.Tests.Controllers;

public class ProcessControllerTests
{
    private readonly Mock<IComicProcessorService> _processorMock;
    private readonly Mock<ILogger<ProcessController>> _loggerMock;
    private readonly ProcessController _controller;

    public ProcessControllerTests()
    {
        _processorMock = new Mock<IComicProcessorService>();
        _loggerMock = new Mock<ILogger<ProcessController>>();
        _controller = new ProcessController(_processorMock.Object, _loggerMock.Object);
    }

    [Fact]
    public void ProcessAll_WithoutStreaming_ReturnsJobId()
    {
        // Act
        var result = _controller.ProcessAll(stream: false);

        // Assert
        var okResult = Assert.IsType<ActionResult<object>>(result);
        var objectResult = Assert.IsType<OkObjectResult>(okResult.Result);
        Assert.NotNull(objectResult.Value);
        
        var jobIdProperty = objectResult.Value.GetType().GetProperty("jobId");
        Assert.NotNull(jobIdProperty?.GetValue(objectResult.Value));
    }

    [Fact]
    public void ProcessAll_WithStreaming_ReturnsJobIdAndStreamingFlag()
    {
        // Act
        var result = _controller.ProcessAll(stream: true);

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
        
        var jobIdProperty = objectResult.Value.GetType().GetProperty("jobId");
        Assert.Equal(expectedJobId, jobIdProperty?.GetValue(objectResult.Value));
        
        _processorMock.Verify(x => x.ProcessFilesAsync(files, default), Times.Once);
    }

    [Fact]
    public void RenameAll_ReturnsJobId()
    {
        // Act
        var result = _controller.RenameAll(stream: false);

        // Assert
        var okResult = Assert.IsType<ActionResult<object>>(result);
        var objectResult = Assert.IsType<OkObjectResult>(okResult.Result);
        Assert.NotNull(objectResult.Value);
    }

    [Fact]
    public void RenameSelected_WithFiles_ReturnsJobId()
    {
        // Arrange
        var request = new ProcessController.ProcessRequest 
        { 
            Files = new List<string> { "test.cbz" } 
        };

        // Act
        var result = _controller.RenameSelected(request, stream: false);

        // Assert
        var okResult = Assert.IsType<ActionResult<object>>(result);
        var objectResult = Assert.IsType<OkObjectResult>(okResult.Result);
        Assert.NotNull(objectResult.Value);
    }

    [Fact]
    public void NormalizeAll_ReturnsJobId()
    {
        // Act
        var result = _controller.NormalizeAll(stream: false);

        // Assert
        var okResult = Assert.IsType<ActionResult<object>>(result);
        var objectResult = Assert.IsType<OkObjectResult>(okResult.Result);
        Assert.NotNull(objectResult.Value);
    }

    [Fact]
    public void NormalizeSelected_WithFiles_ReturnsJobId()
    {
        // Arrange
        var request = new ProcessController.ProcessRequest 
        { 
            Files = new List<string> { "test.cbz" } 
        };

        // Act
        var result = _controller.NormalizeSelected(request, stream: false);

        // Assert
        var okResult = Assert.IsType<ActionResult<object>>(result);
        var objectResult = Assert.IsType<OkObjectResult>(okResult.Result);
        Assert.NotNull(objectResult.Value);
    }
}
