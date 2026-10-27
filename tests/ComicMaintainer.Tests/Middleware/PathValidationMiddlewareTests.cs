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

    [Theory]
    [InlineData("/watched_dir/What If...? 001.cbz")]
    [InlineData("/watched_dir/Batman... Again/Batman... Again 001.cbz")]
    [InlineData("/watched_dir/...And Justice for All/Issue 001.cbz")]
    [InlineData("/watched_dir/What If..Vol 1.cbz")]
    [InlineData("/watched_dir/..Prelude.cbz")]
    [InlineData("/watched_dir/Wait../Issue 001.cbz")]
    [InlineData("/duplicates/What If...? 001.cbz")]
    public async Task InvokeAsync_NameWithConsecutiveDotsInsideAnAllowedRoot_CallsNext(string path)
    {
        // Regression: a ".." substring check rejected legitimate names such as "What If...?", so
        // every API call carrying such a comic's path was answered with 400.
        var context = await InvokeWithFilePathAsync(path);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        _mockNext.Verify(x => x(It.IsAny<HttpContext>()), Times.Once);
    }

    [Theory]
    [InlineData("/watched_dir/..")]
    [InlineData("/watched_dir/comics/../../etc/passwd")]
    [InlineData("/watched_dir/comics/../Saga 001.cbz")]
    [InlineData("..\\..\\windows\\system32")]
    [InlineData("/watched_dir\\..\\etc\\passwd")]
    [InlineData("/watched_dir/comics\\..\\..\\secret.cbz")]
    public async Task InvokeAsync_ParentDirectorySegmentWithEitherSeparator_Returns400(string path)
    {
        // Any ".." segment is rejected, even one that would resolve back inside an allowed root.
        // On Linux "\" is not a separator, so "/watched_dir/comics\..\..\secret.cbz" is a single odd
        // file name inside /watched_dir that only the segment check (splitting on both) rejects.
        var context = await InvokeWithFilePathAsync(path);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        _mockNext.Verify(x => x(It.IsAny<HttpContext>()), Times.Never);
    }

    [Theory]
    [InlineData("/watched_dir2/comics/test.cbz")]
    [InlineData("/watched_dir-old/test.cbz")]
    [InlineData("/watched_dir.bak")]
    [InlineData("/duplicates2/duplicate.cbz")]
    [InlineData("/Config2/settings.json")]
    public async Task InvokeAsync_SiblingSharingARootsNameAsPrefix_Returns400(string path)
    {
        // Regression: containment was a bare prefix test, so "/watched_dir2/..." counted as being
        // inside "/watched_dir".
        var context = await InvokeWithFilePathAsync(path);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        _mockNext.Verify(x => x(It.IsAny<HttpContext>()), Times.Never);
    }

    [Theory]
    [InlineData("/comics", "/comics/Saga/Saga 001.cbz", true)]
    [InlineData("/comics", "/comics", true)]
    [InlineData("/comics", "/comics/", true)]
    [InlineData("/comics", "/comics2/Saga 001.cbz", false)]
    [InlineData("/comics/", "/comics/Saga/Saga 001.cbz", true)]
    [InlineData("/comics/", "/comics", true)]
    [InlineData("/comics/", "/comics2/Saga 001.cbz", false)]
    public async Task InvokeAsync_RootWithOrWithoutTrailingSeparator_ContainsOnlyItselfAndItsChildren(
        string watchedDirectory, string path, bool allowed)
    {
        var settings = new AppSettings
        {
            WatchedDirectory = watchedDirectory,
            DuplicateDirectory = "/duplicates",
            ConfigDirectory = "/Config",
        };

        var context = await InvokeWithFilePathAsync(path, settings);

        Assert.Equal(allowed ? StatusCodes.Status200OK : StatusCodes.Status400BadRequest, context.Response.StatusCode);
        _mockNext.Verify(x => x(It.IsAny<HttpContext>()), allowed ? Times.Once() : Times.Never());
    }

    [Theory]
    [InlineData("/WATCHED_DIR/comics/test.cbz")]
    [InlineData("/Watched_Dir/test.cbz")]
    [InlineData("/DUPLICATES/duplicate.cbz")]
    [InlineData("/config/settings.json")]
    public async Task InvokeAsync_RootDifferingOnlyInCase_IsRejectedExceptOnWindows(string path)
    {
        // Regression: containment ignored case on every platform, although "/watched_dir" and
        // "/WATCHED_DIR" are two different directories everywhere but Windows.
        var context = await InvokeWithFilePathAsync(path);

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
            _mockNext.Verify(x => x(It.IsAny<HttpContext>()), Times.Once);
        }
        else
        {
            Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
            _mockNext.Verify(x => x(It.IsAny<HttpContext>()), Times.Never);
        }
    }

    [Theory]
    [InlineData("", "/Config")]
    [InlineData("   ", "/Config")]
    [InlineData("/duplicates", "")]
    [InlineData("", " ")]
    public async Task InvokeAsync_BlankDuplicateOrConfigDirectory_StillAllowsLibraryPaths(
        string duplicateDirectory, string configDirectory)
    {
        // Regression: Path.GetFullPath threw for an empty root and the catch turned that into
        // "invalid", so every request carrying a filePath was answered with 400.
        var settings = new AppSettings
        {
            WatchedDirectory = "/watched_dir",
            DuplicateDirectory = duplicateDirectory,
            ConfigDirectory = configDirectory,
        };

        var context = await InvokeWithFilePathAsync("/watched_dir/comics/test.cbz", settings);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        _mockNext.Verify(x => x(It.IsAny<HttpContext>()), Times.Once);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task InvokeAsync_BlankDuplicateAndConfigDirectories_StillRejectPathsOutsideTheLibrary(string blank)
    {
        // A blank root means "not configured", never "everything is allowed".
        var settings = new AppSettings
        {
            WatchedDirectory = "/watched_dir",
            DuplicateDirectory = blank,
            ConfigDirectory = blank,
        };

        var context = await InvokeWithFilePathAsync("/elsewhere/test.cbz", settings);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        _mockNext.Verify(x => x(It.IsAny<HttpContext>()), Times.Never);
    }

    [Theory]
    [InlineData("/duplicates/comics/duplicate.cbz", true)]
    [InlineData("/Config/settings.json", true)]
    [InlineData("/watched_dir/comics/test.cbz", false)]
    [InlineData("/elsewhere/test.cbz", false)]
    public async Task InvokeAsync_BlankWatchedDirectory_IsSkippedLikeTheOtherRoots(string path, bool allowed)
    {
        // A blank watched directory used to throw, which rejected every path; now it simply
        // contributes no allowed root, while the duplicate and config roots keep working.
        var settings = new AppSettings
        {
            WatchedDirectory = "",
            DuplicateDirectory = "/duplicates",
            ConfigDirectory = "/Config",
        };

        var context = await InvokeWithFilePathAsync(path, settings);

        Assert.Equal(allowed ? StatusCodes.Status200OK : StatusCodes.Status400BadRequest, context.Response.StatusCode);
        _mockNext.Verify(x => x(It.IsAny<HttpContext>()), allowed ? Times.Once() : Times.Never());
    }

    [Theory]
    [InlineData("/watched_dir/comics/test.cbz", true)]
    [InlineData("/etc/passwd", false)]
    public async Task InvokeAsync_FileSystemRootAsAnAllowedDirectory_ContainsNothing(string path, bool allowed)
    {
        // The middleware shares LibraryPathValidator's containment rule, so a "/" root fails
        // closed like it does for file access instead of allowing every path.
        var settings = new AppSettings
        {
            WatchedDirectory = "/watched_dir",
            DuplicateDirectory = "/",
            ConfigDirectory = "/Config",
        };

        var context = await InvokeWithFilePathAsync(path, settings);

        Assert.Equal(allowed ? StatusCodes.Status200OK : StatusCodes.Status400BadRequest, context.Response.StatusCode);
        _mockNext.Verify(x => x(It.IsAny<HttpContext>()), allowed ? Times.Once() : Times.Never());
    }

    private async Task<HttpContext> InvokeWithFilePathAsync(string filePath, AppSettings? settings = null)
    {
        var middleware = new PathValidationMiddleware(
            _mockNext.Object,
            _mockLogger.Object,
            new TestOptionsMonitor<AppSettings>(settings ?? _appSettings));
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString($"?filePath={Uri.EscapeDataString(filePath)}");
        context.Response.Body = new MemoryStream();
        _mockNext.Setup(x => x(It.IsAny<HttpContext>())).Returns(Task.CompletedTask);

        await middleware.InvokeAsync(context);

        return context;
    }
}
