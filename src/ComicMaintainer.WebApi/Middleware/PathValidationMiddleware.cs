using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.WebApi.Middleware;

/// <summary>
/// Middleware to validate and sanitize file paths to prevent path traversal attacks
/// </summary>
public class PathValidationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<PathValidationMiddleware> _logger;
    private readonly IOptionsMonitor<AppSettings> _settings;

    public PathValidationMiddleware(
        RequestDelegate next, 
        ILogger<PathValidationMiddleware> logger,
        IOptionsMonitor<AppSettings> settings)
    {
        _next = next;
        _logger = logger;
        _settings = settings;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Check if the request contains a filePath parameter
        if (context.Request.Query.TryGetValue("filePath", out var filePathValues))
        {
            var filePath = filePathValues.FirstOrDefault();
            if (!string.IsNullOrEmpty(filePath))
            {
                if (!IsValidPath(filePath))
                {
                    _logger.LogWarning("Invalid file path detected: {SanitizedPath}", SanitizePath(filePath));
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await context.Response.WriteAsJsonAsync(new { error = "Invalid file path" });
                    return;
                }
            }
        }

        await _next(context);
    }

    private bool IsValidPath(string filePath)
    {
        // Only a real ".." segment is a traversal attempt; names such as "What If...?" or
        // "Batman... Again" are legitimate comics. Both separators count, so "\..\" is rejected
        // even where "\" is an ordinary file-name character. Containment below is the real guard.
        if (filePath.Split('/', '\\').Any(segment => segment == ".."))
        {
            return false;
        }

        try
        {
            // Ensure path is within allowed directories
            var settings = _settings.CurrentValue;
            return LibraryPathValidator.IsLexicallyWithin(
                filePath, settings.WatchedDirectory, settings.DuplicateDirectory, settings.ConfigDirectory);
        }
        catch
        {
            return false;
        }
    }

    private static string SanitizePath(string path)
    {
        // Remove potentially sensitive information for logging
        return path.Replace("\\", "/").Split('/').LastOrDefault() ?? "unknown";
    }
}
