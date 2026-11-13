using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Net.Http.Headers;

namespace ComicMaintainer.WebApi.Middleware;

/// <summary>
/// Sends the app's HTML pages with their placeholders filled in (see
/// <see cref="HtmlVersionInjectionMiddleware.InjectPlaceholders"/>).
///
/// A page is the same for every request until the file, the app version or the base path changes, so it is
/// built once and kept in memory, and sent with a strong <c>ETag</c> and <c>Cache-Control: no-cache</c>: the
/// browser asks again on every navigation, so the versioned asset URLs inside are always current, but a page it
/// already has costs a 304 instead of the whole shell (index.html is over 200 KB).
/// </summary>
internal static class HtmlPageResponder
{
    internal const string CacheControl = "no-cache, private";

    private static readonly ConcurrentDictionary<string, BuiltPage> Pages = new(StringComparer.Ordinal);

    public static async Task WriteAsync(HttpContext context, string filePath, string version, string basePath)
    {
        var page = await GetAsync(filePath, version, basePath, context.RequestAborted);

        context.Response.Headers[HeaderNames.CacheControl] = CacheControl;
        context.Response.Headers[HeaderNames.ETag] = page.ETag.ToString();

        // If-None-Match compares weakly (RFC 9110 13.1.2): a proxy that compressed the page may have weakened it.
        var ifNoneMatch = context.Request.GetTypedHeaders().IfNoneMatch;
        if (ifNoneMatch.Any(tag => tag.Equals(EntityTagHeaderValue.Any) || tag.Compare(page.ETag, useStrongComparison: false)))
        {
            context.Response.StatusCode = StatusCodes.Status304NotModified;
            return;
        }

        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength = page.Body.Length;
        await context.Response.Body.WriteAsync(page.Body, context.RequestAborted);
    }

    private static async Task<BuiltPage> GetAsync(string filePath, string version, string basePath, CancellationToken cancellationToken)
    {
        var file = new FileInfo(filePath);
        if (Pages.TryGetValue(filePath, out var cached)
            && cached.LastWriteTimeUtc == file.LastWriteTimeUtc
            && cached.Length == file.Length
            && cached.Version == version
            && cached.BasePath == basePath)
        {
            return cached;
        }

        var content = await File.ReadAllTextAsync(filePath, cancellationToken);
        var body = Encoding.UTF8.GetBytes(HtmlVersionInjectionMiddleware.InjectPlaceholders(content, version, basePath));
        var etag = new EntityTagHeaderValue($"\"{Convert.ToHexString(SHA256.HashData(body), 0, 16)}\"");
        var page = new BuiltPage(file.LastWriteTimeUtc, file.Length, version, basePath, body, etag);
        Pages[filePath] = page;
        return page;
    }

    private sealed record BuiltPage(
        DateTime LastWriteTimeUtc,
        long Length,
        string Version,
        string BasePath,
        byte[] Body,
        EntityTagHeaderValue ETag);
}
