using ComicMaintainer.WebApi.Middleware;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Moq;

namespace ComicMaintainer.Tests.Middleware;

public class HtmlVersionInjectionMiddlewareCoverageTests : IDisposable
{
    private readonly string _webRoot;
    private readonly Mock<IWebHostEnvironment> _env = new();
    private readonly Mock<ILogger<HtmlVersionInjectionMiddleware>> _logger = new();

    public HtmlVersionInjectionMiddlewareCoverageTests()
    {
        _webRoot = Path.Combine(Path.GetTempPath(), "html_mw_cov_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_webRoot);
        File.WriteAllText(Path.Combine(_webRoot, "index.html"), "<script src=\"/js/app.js?v=__APP_VERSION__\"></script>");
        _env.SetupGet(e => e.WebRootPath).Returns(_webRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_webRoot))
        {
            Directory.Delete(_webRoot, recursive: true);
        }
    }

    private static DefaultHttpContext CreateContext(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static string ReadBody(HttpContext context) =>
        System.Text.Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());

    private (HtmlVersionInjectionMiddleware Middleware, Func<bool> NextCalled) CreateMiddleware()
    {
        var nextCalled = false;
        var middleware = new HtmlVersionInjectionMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            _env.Object,
            _logger.Object);
        return (middleware, () => nextCalled);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task WithoutWebRoot_PassesThroughToNext(string? webRoot)
    {
        _env.SetupGet(e => e.WebRootPath).Returns(webRoot!);
        var (middleware, nextCalled) = CreateMiddleware();
        var context = CreateContext("/");

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled());
        Assert.Equal(string.Empty, ReadBody(context));
    }

    [Fact]
    public async Task UnresolvablePath_IsLoggedAndPassedThroughToNext()
    {
        var (middleware, nextCalled) = CreateMiddleware();
        var context = CreateContext("/bad\0name.html");

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled());
        Assert.Equal(string.Empty, ReadBody(context));
        _logger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v!.ToString()!.StartsWith("Failed to resolve HTML path:")),
                It.IsAny<ArgumentException>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task WebRootWithTrailingSeparator_StillServesIndex()
    {
        _env.SetupGet(e => e.WebRootPath).Returns(_webRoot + Path.DirectorySeparatorChar);
        var (middleware, nextCalled) = CreateMiddleware();
        var context = CreateContext("/");

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled());
        var body = ReadBody(context);
        Assert.DoesNotContain("__APP_VERSION__", body);
        Assert.Contains("/js/app.js?v=", body);
    }

    [Fact]
    public async Task MissingRequestPath_IsTreatedAsRoot()
    {
        var (middleware, nextCalled) = CreateMiddleware();
        var context = new DefaultHttpContext();
        context.Features.Get<IHttpRequestFeature>()!.Path = null!;
        context.Response.Body = new MemoryStream();
        Assert.Null(context.Request.Path.Value);

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled());
        Assert.Equal("text/html; charset=utf-8", context.Response.ContentType);
        Assert.Contains("/js/app.js?v=", ReadBody(context));
    }
}
