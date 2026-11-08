using System.Text.RegularExpressions;

namespace ComicMaintainer.WebApi.Startup;

/// <summary>
/// The path the app is served under behind a reverse proxy (<c>BASE_PATH</c>, e.g. <c>/comics</c>).
/// </summary>
public static partial class AppBasePath
{
    /// <summary>The placeholder the HTML pages, <c>sw.js</c> and <c>manifest.json</c> carry for the base path.</summary>
    public const string Placeholder = "__BASE_PATH__";

    // RFC 3986 unreserved characters only: the value is written into HTML attributes, JavaScript
    // strings and JSON, so anything that needs escaping in one of those is refused.
    [GeneratedRegex(@"^(?:/(?!\.{1,2}(?:/|$))[A-Za-z0-9._~-]+)+$", RegexOptions.CultureInvariant)]
    private static partial Regex ValidBasePath();

    /// <summary>
    /// The canonical form of a configured base path: a leading slash, no trailing slash, and the
    /// empty string for "served at the domain root". A value that cannot be used as a URL path
    /// is treated as not configured, and <see cref="IsInvalid"/> reports it so startup can say so.
    /// </summary>
    public static string Normalize(string? configured)
    {
        var candidate = Canonicalize(configured);
        return candidate.Length == 0 || ValidBasePath().IsMatch(candidate) ? candidate : string.Empty;
    }

    /// <summary>Whether a value was configured that <see cref="Normalize"/> had to discard.</summary>
    public static bool IsInvalid(string? configured)
    {
        var candidate = Canonicalize(configured);
        return candidate.Length > 0 && !ValidBasePath().IsMatch(candidate);
    }

    private static string Canonicalize(string? configured)
    {
        var trimmed = (configured ?? string.Empty).Trim().Trim('/');
        return trimmed.Length == 0 ? string.Empty : "/" + trimmed;
    }

    /// <summary>Replaces the base path placeholder in a page or script with the configured path.</summary>
    public static string Apply(string content, string? configured) =>
        content.Replace(Placeholder, Normalize(configured), StringComparison.Ordinal);
}
