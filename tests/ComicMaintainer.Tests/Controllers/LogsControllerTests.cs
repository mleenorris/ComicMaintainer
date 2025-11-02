using ComicMaintainer.Core.Configuration;
using ComicMaintainer.WebApi.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Controllers;

public class LogsControllerTests : IDisposable
{
    private readonly Mock<IOptions<AppSettings>> _mockSettings;
    private readonly Mock<ILogger<LogsController>> _mockLogger;
    private readonly LogsController _controller;
    private readonly string _testLogDir;

    public LogsControllerTests()
    {
        _testLogDir = Path.Combine(Path.GetTempPath(), $"logs_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testLogDir);

        var settings = new AppSettings
        {
            ConfigDirectory = _testLogDir
        };
        _mockSettings = new Mock<IOptions<AppSettings>>();
        _mockSettings.Setup(s => s.Value).Returns(settings);

        _mockLogger = new Mock<ILogger<LogsController>>();
        _controller = new LogsController(_mockSettings.Object, _mockLogger.Object);
    }

    [Fact]
    public void GetLogs_WithNoLogFiles_ReturnsNoFilesMessage()
    {
        // Act
        var result = _controller.GetLogs();

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(okResult.Value);
        var content = okResult.Value.GetType().GetProperty("content")?.GetValue(okResult.Value) as string;
        Assert.Contains("No log files found", content);
    }

    [Fact]
    public void GetLogs_WithDebugLogFile_ReturnsContent()
    {
        // Arrange
        var logFile = Path.Combine(_testLogDir, "debug.log");
        File.WriteAllLines(logFile, new[]
        {
            "Line 1",
            "Line 2",
            "Line 3"
        });

        // Act
        var result = _controller.GetLogs(lines: 10, type: "debug");

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(okResult.Value);
        var content = okResult.Value.GetType().GetProperty("content")?.GetValue(okResult.Value) as string;
        Assert.Contains("Line 1", content);
        Assert.Contains("Line 3", content);
    }

    [Fact]
    public void GetLogs_WithLinesParameter_ReturnsLimitedLines()
    {
        // Arrange
        var logFile = Path.Combine(_testLogDir, "debug.log");
        var allLines = Enumerable.Range(1, 100).Select(i => $"Line {i}").ToArray();
        File.WriteAllLines(logFile, allLines);

        // Act
        var result = _controller.GetLogs(lines: 10, type: "debug");

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(okResult.Value);
        var shownLines = okResult.Value.GetType().GetProperty("shown_lines")?.GetValue(okResult.Value);
        Assert.Equal(10, shownLines);
    }

    [Fact]
    public void GetLogs_WithAppType_SearchesForAppLogs()
    {
        // Arrange
        var logFile = Path.Combine(_testLogDir, "app.log");
        File.WriteAllLines(logFile, new[] { "App log line 1", "App log line 2" });

        // Act
        var result = _controller.GetLogs(type: "app");

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(okResult.Value);
        var content = okResult.Value.GetType().GetProperty("content")?.GetValue(okResult.Value) as string;
        Assert.Contains("App log line", content);
    }

    [Fact]
    public void GetLogs_WithWatcherType_SearchesForWatcherLogs()
    {
        // Arrange
        var logFile = Path.Combine(_testLogDir, "watcher.log");
        File.WriteAllLines(logFile, new[] { "Watcher log line 1", "Watcher log line 2" });

        // Act
        var result = _controller.GetLogs(type: "watcher");

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(okResult.Value);
        var content = okResult.Value.GetType().GetProperty("content")?.GetValue(okResult.Value) as string;
        Assert.Contains("Watcher log line", content);
    }

    [Fact]
    public void GetLogs_WithZeroLines_UsesMaxLimit()
    {
        // Arrange
        var logFile = Path.Combine(_testLogDir, "debug.log");
        var allLines = Enumerable.Range(1, 10000).Select(i => $"Line {i}").ToArray();
        File.WriteAllLines(logFile, allLines);

        // Act
        var result = _controller.GetLogs(lines: 0, type: "debug");

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(okResult.Value);
        var shownLines = okResult.Value.GetType().GetProperty("shown_lines")?.GetValue(okResult.Value);
        // Should be limited to MAX_LINES (10000)
        Assert.Equal(10000, shownLines);
    }

    [Fact]
    public void GetLogs_WithMultipleLogFiles_ReturnsNewest()
    {
        // Arrange
        var oldLogFile = Path.Combine(_testLogDir, "debug20241101.log");
        var newLogFile = Path.Combine(_testLogDir, "debug20241102.log");
        File.WriteAllLines(oldLogFile, new[] { "Old log" });
        Thread.Sleep(10); // Ensure different timestamps
        File.WriteAllLines(newLogFile, new[] { "New log" });

        // Act
        var result = _controller.GetLogs(type: "debug");

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(okResult.Value);
        var content = okResult.Value.GetType().GetProperty("content")?.GetValue(okResult.Value) as string;
        Assert.Contains("New log", content);
    }

    [Fact]
    public void GetLogs_WhenExceptionThrown_ReturnsInternalServerError()
    {
        // Arrange - Use invalid directory
        var settings = new AppSettings
        {
            ConfigDirectory = "/nonexistent/directory"
        };
        _mockSettings.Setup(s => s.Value).Returns(settings);
        var controller = new LogsController(_mockSettings.Object, _mockLogger.Object);

        // Act
        var result = controller.GetLogs();

        // Assert
        var statusCodeResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(500, statusCodeResult.StatusCode);
    }

    [Fact]
    public void GetLogs_WithDefaultParameters_ReturnsLast500Lines()
    {
        // Arrange
        var logFile = Path.Combine(_testLogDir, "debug.log");
        var allLines = Enumerable.Range(1, 1000).Select(i => $"Line {i}").ToArray();
        File.WriteAllLines(logFile, allLines);

        // Act
        var result = _controller.GetLogs(); // Default is 500 lines

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(okResult.Value);
        var shownLines = okResult.Value.GetType().GetProperty("shown_lines")?.GetValue(okResult.Value);
        Assert.Equal(500, shownLines);
        
        var content = okResult.Value.GetType().GetProperty("content")?.GetValue(okResult.Value) as string;
        // Should contain lines from 501 onwards (last 500 lines)
        Assert.Contains("Line 501", content);
        Assert.Contains("Line 1000", content);
        Assert.DoesNotContain("Line 500", content);
    }

    public void Dispose()
    {
        // Clean up test directory
        if (Directory.Exists(_testLogDir))
        {
            Directory.Delete(_testLogDir, true);
        }
    }
}
