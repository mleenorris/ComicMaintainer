using ComicMaintainer.Tests.Helpers;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.WebApi.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ComicMaintainer.Tests.Middleware;

public class PathValidationMiddlewareTests
{
    private readonly Mock<ILogger<PathValidationMiddleware>> _mockLogger;
    private readonly AppSettings _appSettings;
    private readonly Mock<RequestDelegate> _mockNext;

    public PathValidationMiddlewareTests()
    {
        _mockLogger = new Mock<ILogger<PathValidationMiddleware>>();
        _appSettings = new AppSettings
        {
            WatchedDirectory = "/watched_dir",
            DuplicateDirectory = "/duplicates",
            ConfigDirectory = "/Config"
        };
        _mockNext = new Mock<RequestDelegate>();
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("..%2F..%2Fetc%2Fpasswd")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\System32\\config")]
    [InlineData("test.cbz%00.txt")]
    [InlineData("/watched_dir/../../../etc/passwd")]
    public async Task InvokeAsync_WithPathTraversal_Returns400(string maliciousPath)
    {
        // Arrange
        var middleware = new PathValidationMiddleware(
            _mockNext.Object,
            _mockLogger.Object,
            new TestOptionsMonitor<AppSettings>(_appSettings));

        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString($"?filePath={Uri.EscapeDataString(maliciousPath)}");
        context.Response.Body = new MemoryStream();

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        Assert.Equal(400, context.Response.StatusCode);
        _mockNext.Verify(x => x(It.IsAny<HttpContext>()), Times.Never);
    }

    [Theory]
    [InlineData("/watched_dir/comics/test.cbz")]
    [InlineData("/watched_dir/subfolder/test.cbr")]
    [InlineData("/duplicates/comics/duplicate.cbz")]
    [InlineData("/Config/settings.json")]
    public async Task InvokeAsync_WithValidPath_CallsNext(string validPath)
    {
        // Arrange
        var middleware = new PathValidationMiddleware(
            _mockNext.Object,
            _mockLogger.Object,
            new TestOptionsMonitor<AppSettings>(_appSettings));

        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString($"?filePath={Uri.EscapeDataString(validPath)}");
        
        _mockNext.Setup(x => x(It.IsAny<HttpContext>())).Returns(Task.CompletedTask);

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        _mockNext.Verify(x => x(It.IsAny<HttpContext>()), Times.Once);
    }

    [Fact]
    public async Task InvokeAsync_WithoutFilePathParameter_CallsNext()
    {
        // Arrange
        var middleware = new PathValidationMiddleware(
            _mockNext.Object,
            _mockLogger.Object,
            new TestOptionsMonitor<AppSettings>(_appSettings));

        var context = new DefaultHttpContext();
        _mockNext.Setup(x => x(It.IsAny<HttpContext>())).Returns(Task.CompletedTask);

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        _mockNext.Verify(x => x(It.IsAny<HttpContext>()), Times.Once);
    }

    [Fact]
    public async Task InvokeAsync_WithEmptyFilePath_CallsNext()
    {
        // Arrange
        var middleware = new PathValidationMiddleware(
            _mockNext.Object,
            _mockLogger.Object,
            new TestOptionsMonitor<AppSettings>(_appSettings));

        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?filePath=");
        _mockNext.Setup(x => x(It.IsAny<HttpContext>())).Returns(Task.CompletedTask);

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        _mockNext.Verify(x => x(It.IsAny<HttpContext>()), Times.Once);
    }

    [Theory]
    [InlineData("/watched_dir")]
    [InlineData("/duplicates")]
    [InlineData("/Config")]
    public async Task InvokeAsync_WithRootDirectory_CallsNext(string rootPath)
    {
        // Arrange
        var middleware = new PathValidationMiddleware(
            _mockNext.Object,
            _mockLogger.Object,
            new TestOptionsMonitor<AppSettings>(_appSettings));

        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString($"?filePath={Uri.EscapeDataString(rootPath)}");
        _mockNext.Setup(x => x(It.IsAny<HttpContext>())).Returns(Task.CompletedTask);

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        _mockNext.Verify(x => x(It.IsAny<HttpContext>()), Times.Once);
    }

    [Fact]
    public async Task InvokeAsync_WithInvalidPath_LogsSanitizedPath()
    {
        // Arrange
        var middleware = new PathValidationMiddleware(
            _mockNext.Object,
            _mockLogger.Object,
            new TestOptionsMonitor<AppSettings>(_appSettings));

        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?filePath=../../sensitive/data.txt");
        context.Response.Body = new MemoryStream();

        // Act
        await middleware.InvokeAsync(context);

        // Assert - verify logging was called (path should be sanitized in logs)
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("Invalid file path detected")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Theory]
    [InlineData("/tmp/outside")]
    [InlineData("/var/log/system.log")]
    [InlineData("/home/user/documents")]
    public async Task InvokeAsync_WithPathOutsideAllowedDirectories_Returns400(string outsidePath)
    {
        // Arrange
        var middleware = new PathValidationMiddleware(
            _mockNext.Object,
            _mockLogger.Object,
            new TestOptionsMonitor<AppSettings>(_appSettings));

        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString($"?filePath={Uri.EscapeDataString(outsidePath)}");
        context.Response.Body = new MemoryStream();

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        Assert.Equal(400, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_WithInvalidPathThrowingException_Returns400()
    {
        // Arrange
        var middleware = new PathValidationMiddleware(
            _mockNext.Object,
            _mockLogger.Object,
            new TestOptionsMonitor<AppSettings>(_appSettings));

        var context = new DefaultHttpContext();
        // Use null character which may cause exception in path operations
        context.Request.QueryString = new QueryString($"?filePath={Uri.EscapeDataString("\0invalid\0path")}");
        context.Response.Body = new MemoryStream();

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        Assert.Equal(400, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_WithMultipleFilePathParameters_UsesFirst()
    {
        // Arrange
        var middleware = new PathValidationMiddleware(
            _mockNext.Object,
            _mockLogger.Object,
            new TestOptionsMonitor<AppSettings>(_appSettings));

        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString($"?filePath={Uri.EscapeDataString("/watched_dir/test.cbz")}&filePath=../../etc/passwd");
        _mockNext.Setup(x => x(It.IsAny<HttpContext>())).Returns(Task.CompletedTask);

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        _mockNext.Verify(x => x(It.IsAny<HttpContext>()), Times.Once);
    }
}
