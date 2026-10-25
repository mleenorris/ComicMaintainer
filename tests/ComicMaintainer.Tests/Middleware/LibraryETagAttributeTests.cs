using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Tests.Helpers;
using ComicMaintainer.WebApi.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Middleware;

public class LibraryETagAttributeTests
{
    private long _fileVersion = 1;
    private long _metadataVersion = 1;
    private readonly TestUserContextAccessor _user = new("alice");
    private readonly Mock<IOptionsMonitor<AppSettings>> _settings = new();
    private AppSettings _appSettings = new();

    public LibraryETagAttributeTests()
    {
        _settings.Setup(s => s.CurrentValue).Returns(() => _appSettings);
    }

    private IServiceProvider BuildServices(bool versioned = true)
    {
        var fileStore = new Mock<IFileStoreService>();
        var metadata = new Mock<ISeriesMetadataCacheService>();
        if (versioned)
        {
            fileStore.As<IChangeVersionSource>().SetupGet(v => v.ChangeVersion).Returns(() => _fileVersion);
            metadata.As<IChangeVersionSource>().SetupGet(v => v.ChangeVersion).Returns(() => _metadataVersion);
        }

        return new ServiceCollection()
            .AddSingleton(fileStore.Object)
            .AddSingleton(metadata.Object)
            .AddSingleton(_settings.Object)
            .AddSingleton<IUserContextAccessor>(_user)
            .BuildServiceProvider();
    }

    private async Task<(HttpContext Http, IActionResult? Result, bool Executed)> InvokeAsync(
        IServiceProvider services, string? ifNoneMatch = null, string query = "?page=1")
    {
        var http = new DefaultHttpContext { RequestServices = services };
        http.Request.Method = HttpMethods.Get;
        http.Request.Path = "/api/files/series";
        http.Request.QueryString = new QueryString(query);
        if (ifNoneMatch is not null)
        {
            http.Request.Headers.IfNoneMatch = ifNoneMatch;
        }

        var actionContext = new ActionContext(http, new RouteData(), new ActionDescriptor());
        var executing = new ActionExecutingContext(actionContext, new List<IFilterMetadata>(), new Dictionary<string, object?>(), controller: new object());
        var executed = false;

        await new LibraryETagAttribute().OnActionExecutionAsync(executing, () =>
        {
            executed = true;
            var result = new OkObjectResult(new { ok = true });
            return Task.FromResult(new ActionExecutedContext(actionContext, new List<IFilterMetadata>(), new object()) { Result = result });
        });

        return (http, executing.Result, executed);
    }

    [Fact]
    public async Task FirstRequest_RunsActionAndSetsPrivateRevalidatingETag()
    {
        var (http, _, executed) = await InvokeAsync(BuildServices());

        Assert.True(executed);
        Assert.StartsWith("W/\"", http.Response.Headers.ETag.ToString());
        Assert.Equal("private, no-cache", http.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task MatchingIfNoneMatch_Returns304WithoutRunningAction()
    {
        var services = BuildServices();
        var (first, _, _) = await InvokeAsync(services);
        var etag = first.Response.Headers.ETag.ToString();

        var (_, result, executed) = await InvokeAsync(services, etag);

        Assert.False(executed);
        Assert.Equal(StatusCodes.Status304NotModified, Assert.IsType<StatusCodeResult>(result).StatusCode);
    }

    [Theory]
    [InlineData("file")]
    [InlineData("metadata")]
    [InlineData("user")]
    [InlineData("settings")]
    [InlineData("query")]
    public async Task AnyInputChange_InvalidatesETag(string change)
    {
        var services = BuildServices();
        var (first, _, _) = await InvokeAsync(services);
        var etag = first.Response.Headers.ETag.ToString();

        var query = "?page=1";
        switch (change)
        {
            case "file": _fileVersion++; break;
            case "metadata": _metadataVersion++; break;
            case "user": _user.UserId = "bob"; break;
            case "settings": _appSettings = new AppSettings(); break;
            case "query": query = "?page=2"; break;
        }

        var (second, _, executed) = await InvokeAsync(services, etag, query);

        Assert.True(executed);
        Assert.NotEqual(etag, second.Response.Headers.ETag.ToString());
    }

    [Fact]
    public async Task UnversionedSources_AreNotCached()
    {
        var (http, _, executed) = await InvokeAsync(BuildServices(versioned: false), "*");

        Assert.True(executed);
        Assert.False(http.Response.Headers.ContainsKey("ETag"));
    }
}
