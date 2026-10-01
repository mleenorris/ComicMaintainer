using System.Text;
using System.Text.RegularExpressions;

namespace ComicMaintainer.Core.ErrorReporting;

/// <summary>
/// Turns the error details a browser reports into the stable, engine-independent
/// shape the rest of the reporting pipeline expects.
/// </summary>
/// <remarks>
/// <para>Normalization is what makes a client-side report actionable rather
/// than noise. The same defect produces different text in every engine —
/// Chromium writes <c>    at fn (https://host/js/main.js:120:7)</c> while
/// Firefox and Safari write <c>fn@https://host/js/main.js:120:7</c> — so
/// fingerprinting the raw stack would file one issue per browser for a single
/// bug.</para>
/// <para>Script URLs are reduced to their path for the same reason. The app
/// loads <c>/js/main.js?v=2.0.316</c>, so keeping the cache-busting query would
/// re-open every client-side issue on every release, and line/column numbers
/// shift with any edit above them.</para>
/// <para>Everything here is also a trust boundary: the values arrive from a
/// browser and end up in a public issue, so each one is stripped of control
/// characters and hard-bounded in length before it goes any further.</para>
/// </remarks>
public static class ClientErrorNormalizer
{
    /// <summary>Used when the browser reports no error type.</summary>
    public const string UnknownErrorName = "Error";

    /// <summary>Used when no script or page location could be determined.</summary>
    public const string UnknownLocation = "(unknown)";

    /// <summary>Longest accepted error type name.</summary>
    public const int MaxNameLength = 100;

    /// <summary>Longest accepted error message.</summary>
    public const int MaxMessageLength = 500;

    /// <summary>Longest accepted raw stack, before normalization.</summary>
    public const int MaxStackLength = 8000;

    /// <summary>Longest accepted URL.</summary>
    public const int MaxUrlLength = 500;

    /// <summary>
    /// Frames kept from a client stack. The top of the stack identifies the
    /// defect; the tail is event-dispatch plumbing that differs per browser.
    /// </summary>
    public const int MaxStackFrames = 10;

    private const RegexOptions Opts = RegexOptions.CultureInvariant;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    /// <summary>An error type name: a JavaScript identifier, nothing else.</summary>
    private static readonly Regex NameShape = new(@"^[A-Za-z_$][A-Za-z0-9_$.]*$", Opts, RegexTimeout);

    /// <summary>Chromium/Edge/Node frame: <c>at fn (loc)</c> or <c>at loc</c>.</summary>
    private static readonly Regex ChromiumFrame = new(
        @"^at\s+(?:(?<fn>.*?)\s+\((?<loc>.*)\)|(?<loc2>.*))$",
        Opts,
        RegexTimeout);

    /// <summary>Firefox/Safari frame: <c>fn@loc</c> (the name may be empty).</summary>
    private static readonly Regex SpiderMonkeyFrame = new(
        @"^(?<fn>[^@]*)@(?<loc>.*)$",
        Opts,
        RegexTimeout);

    /// <summary>Trailing <c>:line:column</c> (or just <c>:line</c>) of a location.</summary>
    private static readonly Regex LineAndColumn = new(@":\d+(?::\d+)?$", Opts, RegexTimeout);

    /// <summary>A URI scheme prefix of at least two characters.</summary>
    private static readonly Regex SchemePrefix = new(
        @"^[A-Za-z][A-Za-z0-9+.\-]+:",
        Opts,
        RegexTimeout);

    /// <summary>A UUID anywhere in a message — a build id, a job id, a device id.</summary>
    private static readonly Regex Uuid = new(
        @"\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        RegexTimeout);

    /// <summary>A standalone run of digits — an id, a status code, an offset.</summary>
    private static readonly Regex Number = new(@"(?<![A-Za-z0-9_.])\d+(?![A-Za-z0-9_])", Opts, RegexTimeout);

    /// <summary>
    /// The error type name, e.g. <c>SyntaxError</c>. Anything that is not a
    /// plain identifier is discarded rather than cleaned up: it did not come
    /// from <c>Error.prototype.name</c> and has no business in an issue title.
    /// </summary>
    public static string NormalizeName(string? name)
    {
        var collapsed = Collapse(name, MaxNameLength);
        return collapsed.Length > 0 && NameShape.IsMatch(collapsed) ? collapsed : UnknownErrorName;
    }

    /// <summary>The error message, collapsed onto one line and bounded.</summary>
    public static string NormalizeMessage(string? message)
    {
        var collapsed = Collapse(message, MaxMessageLength);
        return collapsed.Length > 0 ? collapsed : "(no message)";
    }

    /// <summary>
    /// The message with its variable parts replaced by placeholders, for use as
    /// the fingerprint's message template. Without this, one defect that
    /// mentions a build id or an HTTP status files a fresh issue per
    /// occurrence — exactly what the template/rendered-message split exists to
    /// prevent for server-side failures.
    /// </summary>
    public static string NormalizeMessageTemplate(string? message)
    {
        var normalized = NormalizeMessage(message);

        try
        {
            normalized = Uuid.Replace(normalized, "{id}");
            normalized = Number.Replace(normalized, "{n}");
        }
        catch (RegexMatchTimeoutException)
        {
            // A pathological message still deserves a report; it simply
            // fingerprints on its literal text.
        }

        return normalized;
    }

    /// <summary>
    /// The path of a script or page URL: no origin, no query string, no
    /// fragment. Relative and already-path-shaped values pass through.
    /// </summary>
    public static string NormalizeLocation(string? url)
    {
        var collapsed = Collapse(url, MaxUrlLength);
        if (collapsed.Length == 0)
        {
            return UnknownLocation;
        }

        // The query and fragment are cut from the raw text rather than via
        // Uri. A value that is already a path ("/js/main.js?v=2.0.316") must
        // never be handed to Uri: on Unix a leading slash parses as an absolute
        // file: URI, which percent-encodes the '?' into the path instead of
        // dropping it, and the result would differ by host platform.
        var trimmed = TrimQuery(collapsed);
        if (trimmed.Length == 0 || trimmed[0] == '/')
        {
            return trimmed.Length == 0 ? UnknownLocation : trimmed;
        }

        if (HasScheme(trimmed) && Uri.TryCreate(trimmed, UriKind.Absolute, out var absolute))
        {
            // Only web and file URLs have a meaningful path. A data: URL
            // carries the script source itself and a blob: URL an id minted
            // per page load, so neither may be echoed into an issue: the first
            // would copy source text, the second churns the fingerprint.
            if (!IsLocatableScheme(absolute.Scheme))
            {
                return $"{absolute.Scheme}:";
            }

            var path = absolute.AbsolutePath;
            return string.IsNullOrEmpty(path) ? "/" : path;
        }

        return trimmed;
    }

    /// <summary>
    /// Whether a value starts with a URI scheme. Two characters minimum, so a
    /// Windows drive letter is treated as the path it is rather than as a
    /// one-letter scheme.
    /// </summary>
    private static bool HasScheme(string value)
    {
        var match = Match(SchemePrefix, value);
        return match is { Success: true };
    }

    private static bool IsLocatableScheme(string scheme)
        => scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || scheme.Equals(Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Rewrites a browser stack into canonical <c>at member (location)</c>
    /// frames. The shape is not cosmetic:
    /// <see cref="ErrorReportFingerprint.NormalizeFrames"/> only recognises
    /// frames that start with <c>at </c>, so a stack left in Firefox's
    /// <c>fn@loc</c> form would contribute nothing to the fingerprint and every
    /// Firefox error would collapse into one issue.
    /// </summary>
    public static string NormalizeStack(string? stack)
    {
        if (string.IsNullOrWhiteSpace(stack))
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        var kept = 0;

        foreach (var rawLine in Truncate(stack, MaxStackLength).Split('\n'))
        {
            if (kept >= MaxStackFrames)
            {
                break;
            }

            var frame = NormalizeFrame(StripControlCharacters(rawLine).Trim());
            if (frame is null)
            {
                continue;
            }

            if (kept > 0)
            {
                builder.Append('\n');
            }

            builder.Append(frame);
            kept++;
        }

        return builder.ToString();
    }

    private static string? NormalizeFrame(string line)
    {
        if (line.Length == 0)
        {
            return null;
        }

        string? function;
        string? location;

        var chromium = Match(ChromiumFrame, line);
        if (chromium is { Success: true })
        {
            function = chromium.Groups["fn"].Success ? chromium.Groups["fn"].Value : null;
            location = chromium.Groups["loc"].Success
                ? chromium.Groups["loc"].Value
                : chromium.Groups["loc2"].Value;
        }
        else
        {
            var spiderMonkey = Match(SpiderMonkeyFrame, line);
            if (spiderMonkey is not { Success: true })
            {
                // The header line ("SyntaxError: Invalid or unexpected token")
                // and any prose the engine interleaves are not frames; the type
                // and message are reported separately.
                return null;
            }

            function = spiderMonkey.Groups["fn"].Value;
            location = spiderMonkey.Groups["loc"].Value;
        }

        function = Collapse(function, MaxNameLength);
        location = NormalizeFrameLocation(location);

        if (function.Length == 0 && location.Length == 0)
        {
            return null;
        }

        return function.Length == 0
            ? $"at {location}"
            : location.Length == 0
                ? $"at {function}"
                : $"at {function} ({location})";
    }

    /// <summary>
    /// A frame's location without its line/column numbers, origin or query —
    /// all three change for reasons that have nothing to do with the defect.
    /// </summary>
    private static string NormalizeFrameLocation(string? location)
    {
        var collapsed = Collapse(location, MaxUrlLength);
        if (collapsed.Length == 0)
        {
            return string.Empty;
        }

        try
        {
            collapsed = LineAndColumn.Replace(collapsed, string.Empty);
        }
        catch (RegexMatchTimeoutException)
        {
            // Keep whatever normalization succeeded.
        }

        var normalized = NormalizeLocation(collapsed);
        return normalized == UnknownLocation ? collapsed : normalized;
    }

    private static Match? Match(Regex regex, string input)
    {
        try
        {
            return regex.Match(input);
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    private static string TrimQuery(string value)
    {
        var cut = value.IndexOfAny(new[] { '?', '#' });
        return cut >= 0 ? value[..cut] : value;
    }

    /// <summary>
    /// Strips control characters, collapses all whitespace to single spaces and
    /// truncates. Control characters matter beyond tidiness: a newline in a
    /// value that is later written into a Markdown table would break out of its
    /// cell.
    /// </summary>
    private static string Collapse(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(Math.Min(value.Length, maxLength));
        var pendingSpace = false;

        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (char.IsControl(ch))
            {
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(ch);

            if (builder.Length >= maxLength)
            {
                break;
            }
        }

        return Truncate(builder.ToString(), maxLength);
    }

    private static string StripControlCharacters(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (!char.IsControl(ch) || ch == '\t')
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];
}
