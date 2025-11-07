using ComicMaintainer.Core.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using System.Threading.Tasks;

namespace ComicMaintainer.WebApi.Middleware;

/// <summary>
/// Middleware that enforces HTTPS for authentication endpoints that handle passwords.
/// This prevents passwords from being transmitted in plain text over unencrypted connections.
/// </summary>
public class HttpsEnforcementMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<HttpsEnforcementMiddleware> _logger;
    private readonly bool _requireHttps;
    private readonly HashSet<string> _protectedEndpoints = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/auth/login",
        "/api/auth/register",
        "/api/auth/setup",
        "/api/auth/change-password"
    };

    public HttpsEnforcementMiddleware(
        RequestDelegate next, 
        ILogger<HttpsEnforcementMiddleware> logger,
        IOptions<AppSettings> appSettings)
    {
        _next = next;
        _logger = logger;
        _requireHttps = appSettings.Value.RequireHttpsForAuth;
        
        if (!_requireHttps)
        {
            _logger.LogWarning(
                "⚠️  SECURITY WARNING: HTTPS enforcement for authentication is DISABLED. " +
                "Passwords can be transmitted in plain text. This should ONLY be used in " +
                "isolated development/testing environments. NEVER in production.");
        }
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Skip enforcement if disabled (but log warning on first use)
        if (!_requireHttps)
        {
            await _next(context);
            return;
        }

        // Check if this is a protected endpoint that requires HTTPS
        if (_protectedEndpoints.Contains(context.Request.Path.Value ?? string.Empty))
        {
            // Check if the request is over HTTPS
            // Consider both direct HTTPS and proxied HTTPS (X-Forwarded-Proto header)
            var isHttps = context.Request.IsHttps;
            var forwardedProto = context.Request.Headers["X-Forwarded-Proto"].FirstOrDefault();
            var isProxiedHttps = forwardedProto?.Equals("https", StringComparison.OrdinalIgnoreCase) == true;

            if (!isHttps && !isProxiedHttps)
            {
                // Sanitize path to prevent log forging
                var sanitizedPath = context.Request.Path.Value?.Replace("\r", "").Replace("\n", "") ?? string.Empty;
                
                _logger.LogWarning(
                    "SECURITY: Blocked password transmission over insecure connection. " +
                    "Endpoint: {Endpoint}, Remote IP: {RemoteIp}",
                    sanitizedPath,
                    context.Connection.RemoteIpAddress);

                context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
                context.Response.ContentType = "application/json";
                
                var errorResponse = System.Text.Json.JsonSerializer.Serialize(new
                {
                    error = "HTTPS Required",
                    message = "This endpoint requires HTTPS to protect password transmission. " +
                             "Please configure HTTPS either directly or via a reverse proxy with " +
                             "proper X-Forwarded-Proto headers.",
                    statusCode = 426
                });

                await context.Response.WriteAsync(errorResponse);
                return;
            }
        }

        await _next(context);
    }
}
