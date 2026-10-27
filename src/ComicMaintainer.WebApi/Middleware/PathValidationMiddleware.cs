using ComicMaintainer.Core.Configuration;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.WebApi.Middleware;

/// <summary>
/// Middleware to validate and sanitize file paths to prevent path traversal attacks
/// </summary>
public class PathValidationMiddleware
{
    /// <summary>
    /// Path comparison for allowed-root containment: case-insensitive on Windows and
    /// case-sensitive elsewhere, the same rule as <c>LibraryPathValidator.IsWithin</c>.
    /// </summary>
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

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
            var fullPath = Path.GetFullPath(filePath);

            // Ensure path is within allowed directories
            var settings = _settings.CurrentValue;
            return IsSameOrUnder(fullPath, settings.WatchedDirectory) ||
                   IsSameOrUnder(fullPath, settings.DuplicateDirectory) ||
                   IsSameOrUnder(fullPath, settings.ConfigDirectory);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Returns true when the normalized <paramref name="fullPath"/> is <paramref name="directory"/>
    /// itself or lives beneath it. Trailing separators are ignored, and a sibling that merely shares
    /// the directory's name as a prefix (<c>/comics2</c> next to <c>/comics</c>) is outside. A blank
    /// directory is not configured and contains nothing.
    /// </summary>
    private static bool IsSameOrUnder(string fullPath, string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(
                   fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), root, PathComparison)
               || fullPath.StartsWith(root + Path.DirectorySeparatorChar, PathComparison);
    }

    private static string SanitizePath(string path)
    {
        // Remove potentially sensitive information for logging
        return path.Replace("\\", "/").Split('/').LastOrDefault() ?? "unknown";
    }
}
