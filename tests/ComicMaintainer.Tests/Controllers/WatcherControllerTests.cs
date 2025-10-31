using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.WebApi.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;

namespace ComicMaintainer.Tests.Controllers;

public class WatcherControllerTests
{
    private readonly Mock<IFileWatcherService> _mockWatcher;
    private readonly Mock<ILogger<WatcherController>> _mockLogger;
    private readonly WatcherController _controller;

    public WatcherControllerTests()
    {
        _mockWatcher = new Mock<IFileWatcherService>();
        _mockLogger = new Mock<ILogger<WatcherController>>();
        _controller = new WatcherController(_mockWatcher.Object, _mockLogger.Object);
    }

    [Fact]
    public void GetStatus_WatcherRunning_ReturnsOkWithEnabledTrue()
    {
        // Arrange
        _mockWatcher.Setup(w => w.IsRunning).Returns(true);

        // Act
        var result = _controller.GetStatus();

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var value = okResult.Value;
        Assert.NotNull(value);
        var enabledProperty = value.GetType().GetProperty("enabled");
        Assert.NotNull(enabledProperty);
        Assert.True((bool)enabledProperty.GetValue(value)!);
    }

    [Fact]
    public void GetStatus_WatcherNotRunning_ReturnsOkWithEnabledFalse()
    {
        // Arrange
        _mockWatcher.Setup(w => w.IsRunning).Returns(false);

        // Act
        var result = _controller.GetStatus();

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var value = okResult.Value;
        Assert.NotNull(value);
        var enabledProperty = value.GetType().GetProperty("enabled");
        Assert.NotNull(enabledProperty);
        Assert.False((bool)enabledProperty.GetValue(value)!);
    }

    [Fact]
    public void EnableWatcher_WithTrue_CallsSetEnabledAndReturnsOk()
    {
        // Arrange
        _mockWatcher.Setup(w => w.SetEnabled(true));

        // Act
        var result = _controller.EnableWatcher(true);

        // Assert
        Assert.IsType<OkResult>(result);
        _mockWatcher.Verify(w => w.SetEnabled(true), Times.Once);
    }

    [Fact]
    public void EnableWatcher_WithFalse_CallsSetEnabledAndReturnsOk()
    {
        // Arrange
        _mockWatcher.Setup(w => w.SetEnabled(false));

        // Act
        var result = _controller.EnableWatcher(false);

        // Assert
        Assert.IsType<OkResult>(result);
        _mockWatcher.Verify(w => w.SetEnabled(false), Times.Once);
    }

    [Fact]
    public void GetStatus_WhenExceptionThrown_ReturnsInternalServerError()
    {
        // Arrange
        _mockWatcher.Setup(w => w.IsRunning).Throws(new InvalidOperationException("Test error"));

        // Act
        var result = _controller.GetStatus();

        // Assert
        var statusCodeResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(500, statusCodeResult.StatusCode);
    }

    [Fact]
    public void EnableWatcher_WhenExceptionThrown_ReturnsInternalServerError()
    {
        // Arrange
        _mockWatcher.Setup(w => w.SetEnabled(It.IsAny<bool>())).Throws(new InvalidOperationException("Test error"));

        // Act
        var result = _controller.EnableWatcher(true);

        // Assert
        var statusCodeResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(500, statusCodeResult.StatusCode);
    }
}
