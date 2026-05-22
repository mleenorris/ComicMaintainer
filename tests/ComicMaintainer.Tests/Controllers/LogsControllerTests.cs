using ComicMaintainer.Core.Configuration;
using ComicMaintainer.WebApi.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Controllers;

public class LogsControllerTests : IDisposable
{
    private readonly Mock<IOptionsMonitor<AppSettings>> _mockSettings;
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
        _mockSettings = new Mock<IOptionsMonitor<AppSettings>>();
        _mockSettings.Setup(s => s.CurrentValue).Returns(settings);

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
        _mockSettings.Setup(s => s.CurrentValue).Returns(settings);
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

    [Fact]
    public void GetLogFiles_WithDebugType_ReturnsAllDebugLogFiles()
    {
        // Arrange
        var log1 = Path.Combine(_testLogDir, "debug20241101.log");
        var log2 = Path.Combine(_testLogDir, "debug20241102.log");
        var log3 = Path.Combine(_testLogDir, "debug.log");
        File.WriteAllText(log1, "Old log 1");
        Thread.Sleep(10);
        File.WriteAllText(log2, "Old log 2");
        Thread.Sleep(10);
        File.WriteAllText(log3, "Current log");

        // Act
        var result = _controller.GetLogFiles(type: "debug");

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(okResult.Value);
        var count = okResult.Value.GetType().GetProperty("count")?.GetValue(okResult.Value);
        Assert.Equal(3, count);
    }

    [Fact]
    public void GetLogFiles_WithAppType_ReturnsOnlyAppLogFiles()
    {
        // Arrange
        var appLog = Path.Combine(_testLogDir, "app.log");
        var debugLog = Path.Combine(_testLogDir, "debug.log");
        File.WriteAllText(appLog, "App log");
        File.WriteAllText(debugLog, "Debug log");

        // Act
        var result = _controller.GetLogFiles(type: "app");

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(okResult.Value);
        var count = okResult.Value.GetType().GetProperty("count")?.GetValue(okResult.Value);
        Assert.Equal(1, count);
    }

    [Fact]
    public void GetLogFiles_WithNoFiles_ReturnsEmptyList()
    {
        // Act
        var result = _controller.GetLogFiles(type: "debug");

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(okResult.Value);
        var count = okResult.Value.GetType().GetProperty("count")?.GetValue(okResult.Value);
        Assert.Equal(0, count);
    }

    [Fact]
    public void GetLogs_WithSpecificFilename_ReturnsContentFromThatFile()
    {
        // Arrange
        var oldLog = Path.Combine(_testLogDir, "debug20241101.log");
        var newLog = Path.Combine(_testLogDir, "debug20241102.log");
        File.WriteAllLines(oldLog, new[] { "Old log line 1", "Old log line 2" });
        Thread.Sleep(10);
        File.WriteAllLines(newLog, new[] { "New log line 1", "New log line 2" });

        // Act - Request the old log specifically
        var result = _controller.GetLogs(type: "debug", filename: "debug20241101.log");

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(okResult.Value);
        var content = okResult.Value.GetType().GetProperty("content")?.GetValue(okResult.Value) as string;
        Assert.Contains("Old log line 1", content);
        Assert.DoesNotContain("New log line", content);
    }

    [Fact]
    public void GetLogs_WithInvalidFilename_ReturnsNotFoundMessage()
    {
        // Arrange
        var logFile = Path.Combine(_testLogDir, "debug.log");
        File.WriteAllText(logFile, "Some content");

        // Act
        var result = _controller.GetLogs(type: "debug", filename: "nonexistent.log");

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(okResult.Value);
        var content = okResult.Value.GetType().GetProperty("content")?.GetValue(okResult.Value) as string;
        Assert.Contains("not found", content);
    }

    [Fact]
    public void GetLogs_WithFilenameNotMatchingType_ReturnsErrorMessage()
    {
        // Arrange
        var appLog = Path.Combine(_testLogDir, "app.log");
        File.WriteAllText(appLog, "App content");

        // Act - Try to access app log with debug type
        var result = _controller.GetLogs(type: "debug", filename: "app.log");

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(okResult.Value);
        var content = okResult.Value.GetType().GetProperty("content")?.GetValue(okResult.Value) as string;
        Assert.Contains("does not match", content);
    }

    [Fact]
    public void GetLogs_WithFilenameContainingPathTraversal_IsSanitized()
    {
        // Arrange
        var logFile = Path.Combine(_testLogDir, "debug.log");
        File.WriteAllText(logFile, "Safe content");

        // Act - Try directory traversal attack
        var result = _controller.GetLogs(type: "debug", filename: "../../../etc/passwd");

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(okResult.Value);
        var content = okResult.Value.GetType().GetProperty("content")?.GetValue(okResult.Value) as string;
        // Should be sanitized to just "passwd" which won't exist or won't match pattern
        Assert.True(content.Contains("not found") || content.Contains("does not match"));
    }

    [Fact]
    public void GetLogs_ReturnsFilename()
    {
        // Arrange
        var logFile = Path.Combine(_testLogDir, "debug.log");
        File.WriteAllLines(logFile, new[] { "Line 1", "Line 2" });

        // Act
        var result = _controller.GetLogs(type: "debug");

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(okResult.Value);
        var filename = okResult.Value.GetType().GetProperty("filename")?.GetValue(okResult.Value) as string;
        Assert.Equal("debug.log", filename);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("UNKNOWN")]
    [InlineData("")]
    public void GetLogs_WithInvalidType_DefaultsToDebug(string invalidType)
    {
        // Arrange
        var logFile = Path.Combine(_testLogDir, "debug.log");
        File.WriteAllLines(logFile, new[] { "Debug log content" });

        // Act
        var result = _controller.GetLogs(type: invalidType);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(okResult.Value);
        var content = okResult.Value.GetType().GetProperty("content")?.GetValue(okResult.Value) as string;
        Assert.Contains("Debug log content", content);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("UNKNOWN")]
    [InlineData("")]
    public void GetLogFiles_WithInvalidType_DefaultsToDebug(string invalidType)
    {
        // Arrange
        var debugLog = Path.Combine(_testLogDir, "debug.log");
        var appLog = Path.Combine(_testLogDir, "app.log");
        File.WriteAllText(debugLog, "Debug log");
        File.WriteAllText(appLog, "App log");

        // Act
        var result = _controller.GetLogFiles(type: invalidType);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(okResult.Value);
        var count = okResult.Value.GetType().GetProperty("count")?.GetValue(okResult.Value);
        Assert.Equal(1, count);
    }

    [Fact]
    public void GetLogFiles_WhenExceptionThrown_ReturnsInternalServerError()
    {
        // Arrange - Use invalid directory
        var settings = new AppSettings
        {
            ConfigDirectory = "/nonexistent/directory"
        };
        _mockSettings.Setup(s => s.CurrentValue).Returns(settings);
        var controller = new LogsController(_mockSettings.Object, _mockLogger.Object);

        // Act
        var result = controller.GetLogFiles();

        // Assert
        var statusCodeResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(500, statusCodeResult.StatusCode);
    }

    [Fact]
    public void GetLogFiles_WithWatcherType_ReturnsOnlyWatcherLogFiles()
    {
        // Arrange
        var watcherLog = Path.Combine(_testLogDir, "watcher.log");
        var debugLog = Path.Combine(_testLogDir, "debug.log");
        File.WriteAllText(watcherLog, "Watcher log");
        File.WriteAllText(debugLog, "Debug log");

        // Act
        var result = _controller.GetLogFiles(type: "watcher");

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(okResult.Value);
        var count = okResult.Value.GetType().GetProperty("count")?.GetValue(okResult.Value);
        Assert.Equal(1, count);
    }

    [Fact]
    public void GetLogFiles_OrdersByLastWriteTimeDescending()
    {
        // Arrange
        var log1 = Path.Combine(_testLogDir, "debug1.log");
        var log2 = Path.Combine(_testLogDir, "debug2.log");
        var log3 = Path.Combine(_testLogDir, "debug3.log");
        
        File.WriteAllText(log1, "Old log");
        Thread.Sleep(10);
        File.WriteAllText(log2, "Middle log");
        Thread.Sleep(10);
        File.WriteAllText(log3, "New log");

        // Act
        var result = _controller.GetLogFiles(type: "debug");

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(okResult.Value);
        
        // Get files array through reflection
        var filesProperty = okResult.Value.GetType().GetProperty("files");
        var files = filesProperty?.GetValue(okResult.Value) as Array;
        Assert.NotNull(files);
        Assert.Equal(3, files.Length);
        
        // First file should be the newest (debug3.log)
        var firstFile = files.GetValue(0);
        var filename = firstFile?.GetType().GetProperty("filename")?.GetValue(firstFile) as string;
        Assert.Equal("debug3.log", filename);
    }

    [Fact]
    public void GetLogs_WithExceedingMaxLines_LimitsTo10000()
    {
        // Arrange
        var logFile = Path.Combine(_testLogDir, "debug.log");
        var allLines = Enumerable.Range(1, 15000).Select(i => $"Line {i}").ToArray();
        File.WriteAllLines(logFile, allLines);

        // Act
        var result = _controller.GetLogs(lines: 15000, type: "debug");

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(okResult.Value);
        var shownLines = okResult.Value.GetType().GetProperty("shown_lines")?.GetValue(okResult.Value);
        // Should be limited to MAX_LINES (10000)
        Assert.Equal(10000, shownLines);
    }

    [Fact]
    public void GetLogs_WithNullConfigDirectory_UsesDefault()
    {
        // Arrange - Set ConfigDirectory to null
        var settings = new AppSettings
        {
            ConfigDirectory = null
        };
        _mockSettings.Setup(s => s.CurrentValue).Returns(settings);
        var controller = new LogsController(_mockSettings.Object, _mockLogger.Object);

        // Act
        var result = controller.GetLogs();

        // Assert - Should not throw, will return error or no files message
        var okOrErrorResult = result.Result;
        Assert.True(okOrErrorResult is OkObjectResult || okOrErrorResult is ObjectResult);
    }

    [Fact]
    public void GetLogs_WithTimestampedEntries_ReturnsNewestFirst()
    {
        // Arrange - use the real Serilog file output template format so the
        // controller can detect entry boundaries.
        var logFile = Path.Combine(_testLogDir, "debug.log");
        File.WriteAllLines(logFile, new[]
        {
            "[2025-05-22 10:00:00.000] [INF] First entry",
            "[2025-05-22 10:00:01.000] [INF] Second entry",
            "[2025-05-22 10:00:02.000] [INF] Third entry",
        });

        // Act
        var result = _controller.GetLogs(lines: 100, type: "debug");

        // Assert - newest entry must come first in the rendered content.
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(okResult.Value);
        var content = okResult.Value.GetType().GetProperty("content")?.GetValue(okResult.Value) as string;
        Assert.NotNull(content);
        var indexFirst = content!.IndexOf("First entry", StringComparison.Ordinal);
        var indexSecond = content.IndexOf("Second entry", StringComparison.Ordinal);
        var indexThird = content.IndexOf("Third entry", StringComparison.Ordinal);
        Assert.True(indexThird >= 0 && indexSecond > indexThird && indexFirst > indexSecond,
            $"Expected newest-first ordering, got: {content}");
    }

    [Fact]
    public void GetLogs_WithMultiLineException_KeepsContinuationLinesWithOwningEntry()
    {
        // Arrange - exception stack trace lines do not start with a timestamp
        // and must travel with the preceding log entry when the order is reversed.
        var logFile = Path.Combine(_testLogDir, "debug.log");
        File.WriteAllLines(logFile, new[]
        {
            "[2025-05-22 10:00:00.000] [INF] Older entry",
            "[2025-05-22 10:00:01.000] [ERR] Boom",
            "System.Exception: Boom",
            "   at Foo.Bar() in Foo.cs:line 42",
            "[2025-05-22 10:00:02.000] [INF] Newest entry",
        });

        // Act
        var result = _controller.GetLogs(lines: 100, type: "debug");

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var content = okResult.Value!.GetType().GetProperty("content")!.GetValue(okResult.Value) as string;
        Assert.NotNull(content);
        var lines = content!.Split(Environment.NewLine);
        // "Newest entry" should be first; the error entry should appear next
        // with its exception lines intact and in original order.
        Assert.StartsWith("[2025-05-22 10:00:02.000]", lines[0]);
        var boomIndex = Array.FindIndex(lines, l => l.Contains("Boom", StringComparison.Ordinal) && l.StartsWith("["));
        Assert.True(boomIndex > 0);
        Assert.Equal("System.Exception: Boom", lines[boomIndex + 1]);
        Assert.Equal("   at Foo.Bar() in Foo.cs:line 42", lines[boomIndex + 2]);
        Assert.StartsWith("[2025-05-22 10:00:00.000]", lines[^1]);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Clean up test directory
            try
            {
                if (Directory.Exists(_testLogDir))
                {
                    Directory.Delete(_testLogDir, true);
                }
            }
            catch (Exception)
            {
                // Ignore cleanup errors to prevent test failures
            }
        }
    }
}
