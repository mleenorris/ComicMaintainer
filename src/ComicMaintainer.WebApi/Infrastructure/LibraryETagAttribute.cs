using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace ComicMaintainer.WebApi.Infrastructure;

/// <summary>
/// Conditional-GET support for read-only library list endpoints. The ETag is
/// derived from the library change versions (file store incl. per-user read
/// state, series metadata cache), the live settings instance, the app build,
/// the current user and the full request URL — everything those endpoints'
/// responses depend on — so it can be checked BEFORE running the action. A
/// matching <c>If-None-Match</c> short-circuits with 304 and skips the work
/// entirely; otherwise the response carries the ETag with
/// <c>Cache-Control: private, no-cache</c> so the browser always revalidates
/// and never shares the cached copy across users.
/// </summary>
/// <remarks>
/// Only applies when both the file store and metadata cache report change
/// versions (<see cref="IChangeVersionSource"/>); otherwise the action runs
/// unchanged. Do not use on endpoints whose output depends on anything else
/// (e.g. reading progress or wall-clock time).
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class LibraryETagAttribute : Attribute, IAsyncActionFilter
{
    private static readonly string BuildVersion =
        typeof(LibraryETagAttribute).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(LibraryETagAttribute).Assembly.GetName().Version?.ToString()
        ?? "0";

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var httpContext = context.HttpContext;
        if (!HttpMethods.IsGet(httpContext.Request.Method) && !HttpMethods.IsHead(httpContext.Request.Method))
        {
            await next();
            return;
        }

        var etag = ComputeETag(httpContext);
        if (etag is null)
        {
            await next();
            return;
        }

        var wildcard = IfNoneMatchContainsWildcard(httpContext.Request);
        if (!wildcard && IfNoneMatchMatches(httpContext.Request, etag))
        {
            SetCacheHeaders(httpContext.Response, etag);
            context.Result = new StatusCodeResult(StatusCodes.Status304NotModified);
            return;
        }

        var executed = await next();
        if (executed.Exception is null || executed.ExceptionHandled)
        {
            var status = executed.Result switch
            {
                IStatusCodeActionResult { StatusCode: { } code } => code,
                _ => httpContext.Response.StatusCode
            };
            if (status == StatusCodes.Status200OK)
            {
                SetCacheHeaders(httpContext.Response, etag);
                if (wildcard)
                {
                    executed.Result = new StatusCodeResult(StatusCodes.Status304NotModified);
                }
            }
        }
    }

    internal static string? ComputeETag(HttpContext httpContext)
    {
        var services = httpContext.RequestServices;
        if (services.GetService(typeof(IFileStoreService)) is not IChangeVersionSource fileVersions
            || services.GetService(typeof(ISeriesMetadataCacheService)) is not IChangeVersionSource metadataVersions)
        {
            return null;
        }

        var settings = (services.GetService(typeof(IOptionsMonitor<AppSettings>)) as IOptionsMonitor<AppSettings>)?.CurrentValue;
        var userId = (services.GetService(typeof(IUserContextAccessor)) as IUserContextAccessor)?.UserId ?? string.Empty;

        // Settings are compared by identity: IOptionsMonitor hands out a new
        // instance on every reload, so any settings change yields a new tag.
        var settingsIdentity = settings is null ? 0 : RuntimeHelpers.GetHashCode(settings);

        var material = string.Join('\n',
            BuildVersion,
            fileVersions.ChangeVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            metadataVersions.ChangeVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            settingsIdentity.ToString(System.Globalization.CultureInfo.InvariantCulture),
            userId,
            httpContext.Request.Path.Value ?? string.Empty,
            httpContext.Request.QueryString.Value ?? string.Empty);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        // Weak: the representation may be re-encoded by response compression.
        return $"W/\"{Convert.ToHexString(hash, 0, 16)}\"";
    }

    private static bool IfNoneMatchMatches(HttpRequest request, string etag)
    {
        var header = request.Headers[HeaderNames.IfNoneMatch];
        if (header.Count == 0)
        {
            return false;
        }

        if (!EntityTagHeaderValue.TryParseList(header, out var tags))
        {
            return false;
        }

        var current = EntityTagHeaderValue.Parse(etag);
        foreach (var tag in tags)
        {
            if (tag.Compare(current, useStrongComparison: false))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IfNoneMatchContainsWildcard(HttpRequest request)
    {
        var header = request.Headers[HeaderNames.IfNoneMatch];
        return EntityTagHeaderValue.TryParseList(header, out var tags)
            && tags.Any(tag => tag.Equals(EntityTagHeaderValue.Any));
    }

    private static void SetCacheHeaders(HttpResponse response, string etag)
    {
        response.Headers[HeaderNames.ETag] = etag;
        response.Headers[HeaderNames.CacheControl] = "private, no-cache";
    }
}
