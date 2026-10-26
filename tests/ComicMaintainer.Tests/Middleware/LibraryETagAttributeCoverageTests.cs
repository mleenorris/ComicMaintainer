using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Tests.Helpers;
using ComicMaintainer.WebApi.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace ComicMaintainer.Tests.Middleware;

public class LibraryETagAttributeCoverageTests
{
    private sealed record Outcome(DefaultHttpContext Http, IActionResult? Result, bool Executed);

    private static IServiceProvider BuildServices(
        bool withSettings = true,
        bool withUserAccessor = true,
        string? userId = "alice",
        bool withTimeProvider = true)
    {
        var fileStore = new Mock<IFileStoreService>();
        fileStore.As<IChangeVersionSource>().SetupGet(v => v.ChangeVersion).Returns(7);
        var metadata = new Mock<ISeriesMetadataCacheService>();
        metadata.As<IChangeVersionSource>().SetupGet(v => v.ChangeVersion).Returns(3);

        var services = new ServiceCollection()
            .AddSingleton(fileStore.Object)
            .AddSingleton(metadata.Object);
        if (withSettings)
        {
            services.AddSingleton<IOptionsMonitor<AppSettings>>(new TestOptionsMonitor<AppSettings>(new AppSettings()));
        }

        if (withUserAccessor)
        {
            services.AddSingleton<IUserContextAccessor>(new TestUserContextAccessor(userId));
        }

        if (withTimeProvider)
        {
            services.AddSingleton<TimeProvider>(new FakeTimeProvider(new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero)));
        }

        return services.BuildServiceProvider();
    }

    private static DefaultHttpContext CreateHttpContext(IServiceProvider services, string method = "GET", string? ifNoneMatch = null)
    {
        var http = new DefaultHttpContext { RequestServices = services };
        http.Request.Method = method;
        http.Request.Path = "/api/files";
        http.Request.QueryString = new QueryString("?page=1");
        if (ifNoneMatch is not null)
        {
            http.Request.Headers.IfNoneMatch = ifNoneMatch;
        }

        return http;
    }

    private static async Task<Outcome> InvokeAsync(
        DefaultHttpContext http,
        Action<ActionExecutedContext>? configureExecuted = null)
    {
        var actionContext = new ActionContext(http, new RouteData(), new ActionDescriptor());
        var executing = new ActionExecutingContext(actionContext, new List<IFilterMetadata>(), new Dictionary<string, object?>(), controller: new object());
        var ran = false;
        ActionExecutedContext? executed = null;

        await new LibraryETagAttribute().OnActionExecutionAsync(executing, () =>
        {
            ran = true;
            executed = new ActionExecutedContext(actionContext, new List<IFilterMetadata>(), new object())
            {
                Result = new OkObjectResult(new { ok = true })
            };
            configureExecuted?.Invoke(executed);
            return Task.FromResult(executed);
        });

        return new Outcome(http, executing.Result ?? executed?.Result, ran);
    }

    private static async Task<string> GetETagAsync(IServiceProvider services, Action<DefaultHttpContext>? configure = null)
    {
        var http = CreateHttpContext(services);
        configure?.Invoke(http);
        var outcome = await InvokeAsync(http);
        var etag = outcome.Http.Response.Headers.ETag.ToString();
        Assert.StartsWith("W/\"", etag);
        return etag;
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task NonReadRequests_PassThroughWithoutCaching(string method)
    {
        var services = BuildServices();
        var etag = await GetETagAsync(services);

        var outcome = await InvokeAsync(CreateHttpContext(services, method, ifNoneMatch: etag));

        Assert.True(outcome.Executed);
        Assert.IsType<OkObjectResult>(outcome.Result);
        Assert.False(outcome.Http.Response.Headers.ContainsKey("ETag"));
        Assert.False(outcome.Http.Response.Headers.ContainsKey("Cache-Control"));
    }

    [Fact]
    public async Task HeadRequest_IsValidatedLikeGet()
    {
        var services = BuildServices();
        var etag = await GetETagAsync(services);

        var outcome = await InvokeAsync(CreateHttpContext(services, HttpMethods.Head, ifNoneMatch: etag));

        Assert.False(outcome.Executed);
        Assert.Equal(StatusCodes.Status304NotModified, Assert.IsType<StatusCodeResult>(outcome.Result).StatusCode);
        Assert.Equal(etag, outcome.Http.Response.Headers.ETag.ToString());
        Assert.Equal("private, no-cache", outcome.Http.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task MalformedIfNoneMatch_IsIgnoredAndActionRuns()
    {
        var outcome = await InvokeAsync(CreateHttpContext(BuildServices(), ifNoneMatch: "not-a-quoted-etag"));

        Assert.True(outcome.Executed);
        Assert.IsType<OkObjectResult>(outcome.Result);
        Assert.StartsWith("W/\"", outcome.Http.Response.Headers.ETag.ToString());
    }

    [Fact]
    public async Task NonMatchingIfNoneMatch_RunsActionAndReturnsCurrentETag()
    {
        var services = BuildServices();
        var etag = await GetETagAsync(services);

        var outcome = await InvokeAsync(CreateHttpContext(services, ifNoneMatch: "W/\"0123\", \"abcd\""));

        Assert.True(outcome.Executed);
        Assert.Equal(etag, outcome.Http.Response.Headers.ETag.ToString());
    }

    [Fact]
    public async Task ResultWithoutStatusCode_UsesResponseStatusCode()
    {
        var outcome = await InvokeAsync(
            CreateHttpContext(BuildServices()),
            executed => executed.Result = new ObjectResult(new { ok = true }));

        Assert.True(outcome.Executed);
        Assert.StartsWith("W/\"", outcome.Http.Response.Headers.ETag.ToString());
    }

    [Fact]
    public async Task NonStatusCodeResult_WithFailedResponseStatus_IsNotTagged()
    {
        var outcome = await InvokeAsync(
            CreateHttpContext(BuildServices()),
            executed =>
            {
                executed.HttpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
                executed.Result = new EmptyResult();
            });

        Assert.True(outcome.Executed);
        Assert.IsType<EmptyResult>(outcome.Result);
        Assert.False(outcome.Http.Response.Headers.ContainsKey("ETag"));
    }

    [Fact]
    public async Task WildcardIfNoneMatch_WithNullResultAndOkResponse_Returns304()
    {
        var outcome = await InvokeAsync(
            CreateHttpContext(BuildServices(), ifNoneMatch: "*"),
            executed => executed.Result = null);

        Assert.True(outcome.Executed);
        Assert.Equal(StatusCodes.Status304NotModified, Assert.IsType<StatusCodeResult>(outcome.Result).StatusCode);
        Assert.True(outcome.Http.Response.Headers.ContainsKey("ETag"));
    }

    [Fact]
    public async Task UnhandledActionException_LeavesResponseUntagged()
    {
        var outcome = await InvokeAsync(
            CreateHttpContext(BuildServices(), ifNoneMatch: "*"),
            executed => executed.Exception = new InvalidOperationException("boom"));

        Assert.True(outcome.Executed);
        Assert.IsType<OkObjectResult>(outcome.Result);
        Assert.False(outcome.Http.Response.Headers.ContainsKey("ETag"));
        Assert.False(outcome.Http.Response.Headers.ContainsKey("Cache-Control"));
    }

    [Fact]
    public async Task HandledActionException_WithSuccessfulResult_IsTagged()
    {
        var outcome = await InvokeAsync(
            CreateHttpContext(BuildServices()),
            executed =>
            {
                executed.Exception = new InvalidOperationException("recovered");
                executed.ExceptionHandled = true;
            });

        Assert.True(outcome.Executed);
        Assert.StartsWith("W/\"", outcome.Http.Response.Headers.ETag.ToString());
        Assert.Equal("private, no-cache", outcome.Http.Response.Headers.CacheControl.ToString());
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task MissingSettingsOrUserAccessor_StillProduceAStableETag(bool withSettings, bool withUserAccessor)
    {
        var services = BuildServices(withSettings, withUserAccessor);
        var etag = await GetETagAsync(services);

        var outcome = await InvokeAsync(CreateHttpContext(services, ifNoneMatch: etag));

        Assert.False(outcome.Executed);
        Assert.Equal(StatusCodes.Status304NotModified, Assert.IsType<StatusCodeResult>(outcome.Result).StatusCode);
    }

    [Fact]
    public async Task MissingTimeProvider_FallsBackToTheSystemClock()
    {
        var outcome = await InvokeAsync(CreateHttpContext(BuildServices(withTimeProvider: false)));

        Assert.True(outcome.Executed);
        Assert.StartsWith("W/\"", outcome.Http.Response.Headers.ETag.ToString());
        Assert.Equal("private, no-cache", outcome.Http.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task AnonymousUser_IsTaggedLikeAMissingUserAccessor()
    {
        // Settings are omitted so the (identity-based) settings component is the same for every provider.
        var anonymous = await GetETagAsync(BuildServices(withSettings: false, userId: null));
        var noAccessor = await GetETagAsync(BuildServices(withSettings: false, withUserAccessor: false));
        var alice = await GetETagAsync(BuildServices(withSettings: false, userId: "alice"));

        Assert.Equal(anonymous, noAccessor);
        Assert.NotEqual(anonymous, alice);
    }

    [Fact]
    public async Task MissingPathAndQuery_AreTreatedAsEmpty()
    {
        var services = BuildServices();

        var empty = await GetETagAsync(services, http =>
        {
            http.Request.Path = PathString.Empty;
            http.Request.QueryString = QueryString.Empty;
        });
        var missing = await GetETagAsync(services, http =>
        {
            var request = http.Features.Get<IHttpRequestFeature>()!;
            request.Path = null!;
            request.QueryString = null!;
        });
        var populated = await GetETagAsync(services);

        Assert.Equal(empty, missing);
        Assert.NotEqual(empty, populated);
    }
}
