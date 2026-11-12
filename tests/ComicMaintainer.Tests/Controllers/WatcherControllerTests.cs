using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.WebApi.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Moq;

namespace ComicMaintainer.Tests.Controllers;

public class WatcherControllerTests
{
    private readonly Mock<IFileWatcherService> _mockWatcher;
    private readonly WatcherController _controller;

    public WatcherControllerTests()
    {
        _mockWatcher = new Mock<IFileWatcherService>();
        _controller = new WatcherController(_mockWatcher.Object);
    }

    /// <summary>
    /// The watcher follows the rename and normalize settings, so the status endpoints are all
    /// that is left: the former PUT /api/watcher and POST /api/watcher/enable no-ops are gone.
    /// </summary>
    [Fact]
    public void Controller_OnlyExposesReadEndpoints()
    {
        var httpMethods = typeof(WatcherController)
            .GetMethods()
            .SelectMany(method => method.GetCustomAttributes(typeof(HttpMethodAttribute), inherit: true))
            .Cast<HttpMethodAttribute>()
            .SelectMany(attribute => attribute.HttpMethods)
            .Distinct()
            .ToList();

        Assert.Equal(new[] { "GET" }, httpMethods);
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
    public void GetStatus_WhenExceptionThrown_Escapes()
    {
        // Arrange
        _mockWatcher.Setup(w => w.IsRunning).Throws(new InvalidOperationException("Test error"));

        // Act and assert
        Assert.Throws<InvalidOperationException>(() => _controller.GetStatus());
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
    public void GetWatcher_WhenIsRunningThrows_Escapes()
    {
        // Arrange
        _mockWatcher.Setup(w => w.IsRunning).Throws(new InvalidOperationException("Test error"));

        // Act and assert
        Assert.Throws<InvalidOperationException>(() => _controller.GetWatcher());
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
