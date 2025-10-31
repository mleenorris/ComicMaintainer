namespace ComicMaintainer.Core.Utilities;

/// <summary>
/// Helper class for sanitizing log entries to prevent log forging attacks
/// </summary>
public static class LoggingHelper
{
    /// <summary>
    /// Sanitize user input for safe logging by removing newlines and control characters
    /// </summary>
    public static string SanitizeForLog(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        // Remove newlines, carriage returns, and other control characters
        return input
            .Replace("\n", "\\n")
            .Replace("\r", "\\r")
            .Replace("\t", "\\t")
            .Replace("\0", "\\0");
    }

    /// <summary>
    /// Sanitize file path for logging, showing only the filename
    /// </summary>
    public static string SanitizePathForLog(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFileName(filePath) ?? string.Empty;
        }
        catch
        {
            return "invalid_path";
        }
    }

    /// <summary>
    /// Create a structured log message with sanitized parameters
    /// </summary>
    public static object CreateLogData(params (string key, string? value)[] parameters)
    {
        var data = new Dictionary<string, string>();
        foreach (var (key, value) in parameters)
        {
            data[key] = SanitizeForLog(value);
        }
        return data;
    }
}
