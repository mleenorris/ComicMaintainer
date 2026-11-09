using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Tests.Helpers;
using ComicMaintainer.WebApi.Middleware;
using Microsoft.AspNetCore.Http;

namespace ComicMaintainer.Tests.Middleware;

/// <summary>
/// The OPDS on/off switch, which answers before authentication: with the catalog off a client sees the same 404 as for
/// a path that does not exist, and no other route is affected.
/// </summary>
public sealed class OpdsFeatureGateMiddlewareTests
{
    private const string UnknownEndpointBody = "{\"error\":\"Not Found\",\"message\":\"The requested endpoint does not exist\"}";

    private bool _nextRan;

    [Theory]
    [InlineData("/opds")]
    [InlineData("/opds/v1.2/catalog")]
    [InlineData("/OPDS/v1.2/catalog")]
    [InlineData("/opds/v1.2/issues/7/file")]
    public async Task WhileTheCatalogIsOff_EveryOpdsRouteAnswersAsAnUnknownPath(string path)
    {
        var (context, body) = CreateContext(path);

        await InvokeAsync(context, opdsEnabled: false);

        Assert.False(_nextRan, "the request must not reach authentication");
        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", context.Response.ContentType);
        Assert.Equal(UnknownEndpointBody, await ReadAsync(body));
        Assert.False(context.Response.Headers.ContainsKey("WWW-Authenticate"), "the catalog must not reveal itself with a challenge");
    }

    [Fact]
    public async Task WhileTheCatalogIsOn_AnOpdsRouteGoesOn()
    {
        var (context, body) = CreateContext("/opds/v1.2/catalog");

        await InvokeAsync(context, opdsEnabled: true);

        Assert.True(_nextRan);
        Assert.Equal(0, body.Length);
    }

    [Theory]
    [InlineData("/api/settings")]
    [InlineData("/opdsearch")]
    [InlineData("/")]
    public async Task WhileTheCatalogIsOff_OtherRoutesGoOn(string path)
    {
        var (context, body) = CreateContext(path);

        await InvokeAsync(context, opdsEnabled: false);

        Assert.True(_nextRan);
        Assert.Equal(0, body.Length);
    }

    // The setting is read per request, so saving it takes effect without a restart.
    [Fact]
    public async Task TheToggleAppliesToTheNextRequest()
    {
        var settings = new TestOptionsMonitor<AppSettings>(new AppSettings { OpdsEnabled = true });
        var gate = new OpdsFeatureGateMiddleware(_ => { _nextRan = true; return Task.CompletedTask; }, settings);

        var (first, _) = CreateContext("/opds/v1.2/catalog");
        await gate.InvokeAsync(first);
        Assert.True(_nextRan);

        _nextRan = false;
        settings.Set(new AppSettings { OpdsEnabled = false });
        var (second, _) = CreateContext("/opds/v1.2/catalog");
        await gate.InvokeAsync(second);

        Assert.False(_nextRan);
        Assert.Equal(StatusCodes.Status404NotFound, second.Response.StatusCode);
    }

    private Task InvokeAsync(HttpContext context, bool opdsEnabled)
    {
        var gate = new OpdsFeatureGateMiddleware(
            _ => { _nextRan = true; return Task.CompletedTask; },
            new TestOptionsMonitor<AppSettings>(new AppSettings { OpdsEnabled = opdsEnabled }));
        return gate.InvokeAsync(context);
    }

    private static (DefaultHttpContext Context, MemoryStream Body) CreateContext(string path)
    {
        var body = new MemoryStream();
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Response.Body = body;
        return (context, body);
    }

    private static async Task<string> ReadAsync(Stream body)
    {
        body.Position = 0;
        using var reader = new StreamReader(body, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }
}
