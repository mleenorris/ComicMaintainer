using System.Reflection;

namespace ComicMaintainer.WebApi.Middleware;

/// <summary>
/// Middleware that serves HTML files (root and *.html under wwwroot) with the
/// <c>__APP_VERSION__</c> placeholder replaced by the current assembly version.
///
/// This is the critical part of the cache-busting strategy: it guarantees that
/// the very first request for the HTML shell already contains versioned URLs
/// for /css/*.css and /js/*.js, so the browser never issues a request for the
/// unversioned asset URL (which the service worker would otherwise cache
/// indefinitely and force users to hard-refresh to update).
/// </summary>
public class HtmlVersionInjectionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<HtmlVersionInjectionMiddleware> _logger;
    private readonly string _version;

    public HtmlVersionInjectionMiddleware(
        RequestDelegate next,
        IWebHostEnvironment env,
        ILogger<HtmlVersionInjectionMiddleware> logger)
    {
        _next = next;
        _env = env;
        _logger = logger;
        _version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;

        // Skip API and Hub requests entirely.
        if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/hubs/", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/sw.js", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/manifest.json", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        string? filePath = null;

        if (string.IsNullOrEmpty(path) || path == "/")
        {
            filePath = SafeResolve("index.html");
        }
        else if (path.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
        {
            // Strip leading slash so we resolve relative to the web root.
            var relative = path.TrimStart('/');
            filePath = SafeResolve(relative);
        }

        if (filePath != null && File.Exists(filePath))
        {
            var content = await File.ReadAllTextAsync(filePath);
            content = content.Replace("__APP_VERSION__", _version);

            // HTML must never be cached; the cache-busting query strings inside
            // depend on the HTML being re-fetched on every navigation.
            context.Response.Headers["Cache-Control"] = "no-store, private";
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync(content);
            return;
        }

        await _next(context);
    }

    /// <summary>
    /// Resolves a relative path under the web root, guarding against path
    /// traversal. Returns null if the resolved path falls outside the web root
    /// or the web root is not configured.
    /// </summary>
    private string? SafeResolve(string relative)
    {
        var webRoot = _env.WebRootPath;
        if (string.IsNullOrEmpty(webRoot))
        {
            return null;
        }

        try
        {
            var candidate = Path.GetFullPath(Path.Combine(webRoot, relative));
            var rootFull = Path.GetFullPath(webRoot);

            // Ensure the resolved path is inside the web root.
            var rootWithSep = rootFull.EndsWith(Path.DirectorySeparatorChar)
                ? rootFull
                : rootFull + Path.DirectorySeparatorChar;

            if (!candidate.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase) &&
                !candidate.Equals(rootFull, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Refusing to serve HTML outside web root: {Candidate}", candidate);
                return null;
            }

            return candidate;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve HTML path: {Relative}", relative);
            return null;
        }
    }
}
