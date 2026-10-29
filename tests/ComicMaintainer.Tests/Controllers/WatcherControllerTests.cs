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

    // Inspects recorded invocations rather than Verify(w => w.SetEnabled(...)): that lambda would
    // itself call the obsolete member outside any [Obsolete] context.
    private void AssertSetEnabledNeverCalled() =>
        Assert.DoesNotContain(_mockWatcher.Invocations, invocation => invocation.Method.Name == nameof(IFileWatcherService.SetEnabled));

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
    [Obsolete("Covers the deprecated POST /api/watcher/enable endpoint until it is removed in v3.0.")]
    public void EnableWatcher_WithTrue_IsDeprecatedAndReturnsOk()
    {
        // Arrange - no SetEnabled should be called since it's deprecated

        // Act
        var result = _controller.EnableWatcher(true);

        // Assert - Should return Ok but not call SetEnabled
        Assert.IsType<OkResult>(result);
        AssertSetEnabledNeverCalled();
    }

    [Fact]
    [Obsolete("Covers the deprecated POST /api/watcher/enable endpoint until it is removed in v3.0.")]
    public void EnableWatcher_WithFalse_IsDeprecatedAndReturnsOk()
    {
        // Arrange - no SetEnabled should be called since it's deprecated

        // Act
        var result = _controller.EnableWatcher(false);

        // Assert - Should return Ok but not call SetEnabled
        Assert.IsType<OkResult>(result);
        AssertSetEnabledNeverCalled();
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
    [Obsolete("Covers the deprecated POST /api/watcher/enable endpoint until it is removed in v3.0.")]
    public void EnableWatcher_IsDeprecatedAndDoesNotThrow()
    {
        // Arrange - deprecated endpoint should not throw even with exception
        // No need to setup exception since SetEnabled is never called

        // Act
        var result = _controller.EnableWatcher(true);

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
    [Obsolete("Covers the deprecated PUT /api/watcher endpoint until it is removed in v3.0.")]
    public void UpdateWatcher_IsDeprecatedAndReturnsCurrentState()
    {
        // Arrange
        var request = new WatcherController.WatcherUpdateRequest { Enabled = true };
        _mockWatcher.Setup(w => w.IsRunning).Returns(false); // Watcher is not running

        // Act
        var result = _controller.UpdateWatcher(request);

        // Assert - Should return current state, not requested state
        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(okResult.Value);
        var enabledProperty = okResult.Value.GetType().GetProperty("enabled");
        Assert.NotNull(enabledProperty);
        Assert.False((bool)enabledProperty.GetValue(okResult.Value)!); // Returns false (current state) not true (requested state)
        AssertSetEnabledNeverCalled();
    }

    [Fact]
    [Obsolete("Covers the deprecated PUT /api/watcher endpoint until it is removed in v3.0.")]
    public void UpdateWatcher_WithRunningWatcher_ReturnsRunningState()
    {
        // Arrange
        var request = new WatcherController.WatcherUpdateRequest { Enabled = false };
        _mockWatcher.Setup(w => w.IsRunning).Returns(true); // Watcher is running

        // Act
        var result = _controller.UpdateWatcher(request);

        // Assert - Should return current state (running), not requested state (disabled)
        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(okResult.Value);
        var enabledProperty = okResult.Value.GetType().GetProperty("enabled");
        Assert.NotNull(enabledProperty);
        Assert.True((bool)enabledProperty.GetValue(okResult.Value)!); // Returns true (current state) not false (requested state)
        AssertSetEnabledNeverCalled();
    }

    [Fact]
    [Obsolete("Covers the deprecated PUT /api/watcher endpoint until it is removed in v3.0.")]
    public void UpdateWatcher_WhenIsRunningThrows_ReturnsInternalServerError()
    {
        // Arrange
        var request = new WatcherController.WatcherUpdateRequest { Enabled = true };
        _mockWatcher.Setup(w => w.IsRunning).Throws(new InvalidOperationException("Test error"));

        // Act
        var result = _controller.UpdateWatcher(request);

        // Assert
        var statusCodeResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(500, statusCodeResult.StatusCode);
    }

    [Fact]
    public void GetWatcher_WhenIsRunningThrows_ReturnsInternalServerError()
    {
        // Arrange
        _mockWatcher.Setup(w => w.IsRunning).Throws(new InvalidOperationException("Test error"));

        // Act
        var result = _controller.GetWatcher();

        // Assert
        var statusCodeResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(500, statusCodeResult.StatusCode);
    }

    [Fact]
    [Obsolete("Covers the deprecated POST /api/watcher/enable endpoint until it is removed in v3.0.")]
    public void EnableWatcher_WhenExceptionThrown_ReturnsInternalServerError()
    {
        // Arrange - EnableWatcher is a no-op that never touches the watcher, so there is no
        // failure to inject; verify the deprecation warning is logged instead.

        // Act
        var result = _controller.EnableWatcher(true);

        // Assert - Should return OK and log warning
        Assert.IsType<OkResult>(result);
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("deprecated")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    [Obsolete("Covers the deprecated PUT /api/watcher endpoint until it is removed in v3.0.")]
    public void UpdateWatcher_LogsDeprecationWarning()
    {
        // Arrange
        var request = new WatcherController.WatcherUpdateRequest { Enabled = true };
        _mockWatcher.Setup(w => w.IsRunning).Returns(true);

        // Act
        _controller.UpdateWatcher(request);

        // Assert - Should log deprecation warning
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("deprecated")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void GetWatcher_ReturnsRunningProperty()
    {
        // Arrange
        _mockWatcher.Setup(w => w.IsRunning).Returns(true);

        // Act
        var result = _controller.GetWatcher();

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var value = okResult.Value;
        Assert.NotNull(value);
        var runningProperty = value.GetType().GetProperty("running");
        Assert.NotNull(runningProperty);
        Assert.True((bool)runningProperty.GetValue(value)!);
    }
}
