using ComicMaintainer.WebApi.Middleware;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;

namespace ComicMaintainer.Tests.Middleware;

public class HtmlVersionInjectionMiddlewareTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly Mock<IWebHostEnvironment> _env;
    private readonly Mock<ILogger<HtmlVersionInjectionMiddleware>> _logger;

    public HtmlVersionInjectionMiddlewareTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "html_mw_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);

        _env = new Mock<IWebHostEnvironment>();
        _env.Setup(e => e.WebRootPath).Returns(_tempRoot);

        _logger = new Mock<ILogger<HtmlVersionInjectionMiddleware>>();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* ignore */ }
    }

    private static HttpContext CreateContext(string path)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static async Task<string> ReadBodyAsync(HttpContext ctx)
    {
        ctx.Response.Body.Position = 0;
        using var reader = new StreamReader(ctx.Response.Body);
        return await reader.ReadToEndAsync();
    }

    [Fact]
    public async Task RootPath_ServesIndexWithVersionSubstituted()
    {
        File.WriteAllText(
            Path.Combine(_tempRoot, "index.html"),
            "<link href=\"/css/main.css?v=__APP_VERSION__\"><script src=\"/js/main.js?v=__APP_VERSION__\"></script>");

        var nextCalled = false;
        var mw = new HtmlVersionInjectionMiddleware(_ => { nextCalled = true; return Task.CompletedTask; }, _env.Object, _logger.Object);
        var ctx = CreateContext("/");

        await mw.InvokeAsync(ctx);

        Assert.False(nextCalled);
        var body = await ReadBodyAsync(ctx);
        Assert.DoesNotContain("__APP_VERSION__", body);
        Assert.Matches("/css/main\\.css\\?v=[^\"']+", body);
        Assert.Matches("/js/main\\.js\\?v=[^\"']+", body);
        Assert.Equal("text/html; charset=utf-8", ctx.Response.ContentType);
        Assert.Equal("no-store, private", ctx.Response.Headers["Cache-Control"].ToString());
    }

    [Fact]
    public async Task DotHtmlPath_ServesFileWithVersionSubstituted()
    {
        File.WriteAllText(
            Path.Combine(_tempRoot, "reader.html"),
            "<link href=\"/css/main.css?v=__APP_VERSION__\">");

        var mw = new HtmlVersionInjectionMiddleware(_ => Task.CompletedTask, _env.Object, _logger.Object);
        var ctx = CreateContext("/reader.html");

        await mw.InvokeAsync(ctx);

        var body = await ReadBodyAsync(ctx);
        Assert.DoesNotContain("__APP_VERSION__", body);
        Assert.Contains("/css/main.css?v=", body);
    }

    [Fact]
    public async Task ApiPath_IsPassedThroughToNext()
    {
        File.WriteAllText(Path.Combine(_tempRoot, "index.html"), "<html>__APP_VERSION__</html>");

        var nextCalled = false;
        var mw = new HtmlVersionInjectionMiddleware(_ => { nextCalled = true; return Task.CompletedTask; }, _env.Object, _logger.Object);
        var ctx = CreateContext("/api/version");

        await mw.InvokeAsync(ctx);

        Assert.True(nextCalled);
        var body = await ReadBodyAsync(ctx);
        Assert.Empty(body);
    }

    [Fact]
    public async Task SwJsAndManifest_ArePassedThroughToNext()
    {
        var mw = new HtmlVersionInjectionMiddleware(_ => Task.CompletedTask, _env.Object, _logger.Object);

        foreach (var path in new[] { "/sw.js", "/manifest.json" })
        {
            var nextCalled = false;
            var localMw = new HtmlVersionInjectionMiddleware(_ => { nextCalled = true; return Task.CompletedTask; }, _env.Object, _logger.Object);
            var ctx = CreateContext(path);

            await localMw.InvokeAsync(ctx);

            Assert.True(nextCalled, $"Expected next() to be called for {path}");
        }
    }

    [Fact]
    public async Task NonHtmlPath_IsPassedThroughToNext()
    {
        var nextCalled = false;
        var mw = new HtmlVersionInjectionMiddleware(_ => { nextCalled = true; return Task.CompletedTask; }, _env.Object, _logger.Object);
        var ctx = CreateContext("/css/main.css");

        await mw.InvokeAsync(ctx);

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task MissingHtmlFile_FallsThroughToNext()
    {
        // Don't create the file.
        var nextCalled = false;
        var mw = new HtmlVersionInjectionMiddleware(_ => { nextCalled = true; return Task.CompletedTask; }, _env.Object, _logger.Object);
        var ctx = CreateContext("/missing.html");

        await mw.InvokeAsync(ctx);

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task PathTraversal_IsRefused()
    {
        // Try to escape the web root.
        var nextCalled = false;
        var mw = new HtmlVersionInjectionMiddleware(_ => { nextCalled = true; return Task.CompletedTask; }, _env.Object, _logger.Object);
        var ctx = CreateContext("/../etc/passwd.html");

        await mw.InvokeAsync(ctx);

        // Should fall through to next() because the path is outside the web root.
        Assert.True(nextCalled);
    }
}
