using ComicMaintainer.Core.Configuration;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.WebApi.Middleware;

/// <summary>
/// Middleware to validate and sanitize file paths to prevent path traversal attacks
/// </summary>
public class PathValidationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<PathValidationMiddleware> _logger;
    private readonly AppSettings _settings;

    public PathValidationMiddleware(
        RequestDelegate next, 
        ILogger<PathValidationMiddleware> logger,
        IOptions<AppSettings> settings)
    {
        _next = next;
        _logger = logger;
        _settings = settings.Value;
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
        try
        {
            // Get the full path
            var fullPath = Path.GetFullPath(filePath);
            
            // Check for path traversal attempts
            if (fullPath.Contains("..") || filePath.Contains(".."))
            {
                return false;
            }

            // Ensure path is within allowed directories
            var watchedDir = Path.GetFullPath(_settings.WatchedDirectory);
            var duplicateDir = Path.GetFullPath(_settings.DuplicateDirectory);
            var configDir = Path.GetFullPath(_settings.ConfigDirectory);

            return fullPath.StartsWith(watchedDir, StringComparison.OrdinalIgnoreCase) ||
                   fullPath.StartsWith(duplicateDir, StringComparison.OrdinalIgnoreCase) ||
                   fullPath.StartsWith(configDir, StringComparison.OrdinalIgnoreCase);
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
