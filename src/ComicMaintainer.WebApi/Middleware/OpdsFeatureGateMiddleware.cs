using ComicMaintainer.Core.Configuration;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.WebApi.Middleware;

/// <summary>
/// While the OPDS catalog is off (<see cref="AppSettings.OpdsEnabled"/>), answers every <c>/opds</c> route with the
/// same 404 as a path that does not exist. It runs before authentication, so a client sending no credentials cannot
/// tell the catalog exists and no challenge is sent. The setting is read per request: the toggle applies at once.
/// </summary>
public sealed class OpdsFeatureGateMiddleware
{
    /// <summary>The path prefix the gate covers, after the base path has been removed.</summary>
    public const string PathPrefix = "/opds";

    private readonly RequestDelegate _next;
    private readonly IOptionsMonitor<AppSettings> _settings;

    public OpdsFeatureGateMiddleware(RequestDelegate next, IOptionsMonitor<AppSettings> settings)
    {
        _next = next;
        _settings = settings;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments(PathPrefix) && !_settings.CurrentValue.OpdsEnabled)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync(UnknownEndpointResponse.Body);
            return;
        }

        await _next(context);
    }
}

/// <summary>The body of a 404 for a path the app does not serve, shared by the fallback and <see cref="OpdsFeatureGateMiddleware"/>.</summary>
internal static class UnknownEndpointResponse
{
    public const string Body = "{\"error\":\"Not Found\",\"message\":\"The requested endpoint does not exist\"}";
}
