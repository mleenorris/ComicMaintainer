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
    public void EnableWatcher_WithTrue_IsDeprecatedAndReturnsOk()
    {
        // Arrange - no SetEnabled should be called since it's deprecated

        // Act
        #pragma warning disable CS0618 // Type or member is obsolete
        var result = _controller.EnableWatcher(true);
        #pragma warning restore CS0618 // Type or member is obsolete

        // Assert - Should return Ok but not call SetEnabled
        Assert.IsType<OkResult>(result);
        _mockWatcher.Verify(w => w.SetEnabled(It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public void EnableWatcher_WithFalse_IsDeprecatedAndReturnsOk()
    {
        // Arrange - no SetEnabled should be called since it's deprecated

        // Act
        #pragma warning disable CS0618 // Type or member is obsolete
        var result = _controller.EnableWatcher(false);
        #pragma warning restore CS0618 // Type or member is obsolete

        // Assert - Should return Ok but not call SetEnabled
        Assert.IsType<OkResult>(result);
        _mockWatcher.Verify(w => w.SetEnabled(It.IsAny<bool>()), Times.Never);
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
    public void EnableWatcher_IsDeprecatedAndDoesNotThrow()
    {
        // Arrange - deprecated endpoint should not throw even with exception
        // No need to setup exception since SetEnabled is never called

        // Act
        #pragma warning disable CS0618 // Type or member is obsolete
        var result = _controller.EnableWatcher(true);
        #pragma warning restore CS0618 // Type or member is obsolete

        // Assert - Should return Ok and not throw
        Assert.IsType<OkResult>(result);
    }

    // New RESTful endpoint tests

    [Fact]
    public void GetWatcher_WatcherRunning_ReturnsOkWithEnabledTrue()
    {
        // Arrange
        _mockWatcher.Setup(w => w.IsRunning).Returns(true);

        // Act
        var result = _controller.GetWatcher();

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var value = okResult.Value;
        Assert.NotNull(value);
        var enabledProperty = value.GetType().GetProperty("enabled");
        Assert.NotNull(enabledProperty);
        Assert.True((bool)enabledProperty.GetValue(value)!);
    }

    [Fact]
    public void GetWatcher_WatcherNotRunning_ReturnsOkWithEnabledFalse()
    {
        // Arrange
        _mockWatcher.Setup(w => w.IsRunning).Returns(false);

        // Act
        var result = _controller.GetWatcher();

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var value = okResult.Value;
        Assert.NotNull(value);
        var enabledProperty = value.GetType().GetProperty("enabled");
        Assert.NotNull(enabledProperty);
        Assert.False((bool)enabledProperty.GetValue(value)!);
    }

    [Fact]
    public void UpdateWatcher_IsDeprecatedAndReturnsCurrentState()
    {
        // Arrange
        var request = new WatcherController.WatcherUpdateRequest { Enabled = true };
        _mockWatcher.Setup(w => w.IsRunning).Returns(false); // Watcher is not running

        // Act
        #pragma warning disable CS0618 // Type or member is obsolete
        var result = _controller.UpdateWatcher(request);
        #pragma warning restore CS0618 // Type or member is obsolete

        // Assert - Should return current state, not requested state
        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(okResult.Value);
        var enabledProperty = okResult.Value.GetType().GetProperty("enabled");
        Assert.NotNull(enabledProperty);
        Assert.False((bool)enabledProperty.GetValue(okResult.Value)!); // Returns false (current state) not true (requested state)
        _mockWatcher.Verify(w => w.SetEnabled(It.IsAny<bool>()), Times.Never); // Should never call SetEnabled
    }

    [Fact]
    public void UpdateWatcher_WithRunningWatcher_ReturnsRunningState()
    {
        // Arrange
        var request = new WatcherController.WatcherUpdateRequest { Enabled = false };
        _mockWatcher.Setup(w => w.IsRunning).Returns(true); // Watcher is running

        // Act
        #pragma warning disable CS0618 // Type or member is obsolete
        var result = _controller.UpdateWatcher(request);
        #pragma warning restore CS0618 // Type or member is obsolete

        // Assert - Should return current state (running), not requested state (disabled)
        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(okResult.Value);
        var enabledProperty = okResult.Value.GetType().GetProperty("enabled");
        Assert.NotNull(enabledProperty);
        Assert.True((bool)enabledProperty.GetValue(okResult.Value)!); // Returns true (current state) not false (requested state)
        _mockWatcher.Verify(w => w.SetEnabled(It.IsAny<bool>()), Times.Never); // Should never call SetEnabled
    }

    [Fact]
    public void UpdateWatcher_WhenIsRunningThrows_ReturnsInternalServerError()
    {
        // Arrange
        var request = new WatcherController.WatcherUpdateRequest { Enabled = true };
        _mockWatcher.Setup(w => w.IsRunning).Throws(new InvalidOperationException("Test error"));

        // Act
        #pragma warning disable CS0618 // Type or member is obsolete
        var result = _controller.UpdateWatcher(request);
        #pragma warning restore CS0618 // Type or member is obsolete

        // Assert
        var statusCodeResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(500, statusCodeResult.StatusCode);
    }
}
