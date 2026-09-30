using System.Text;
using System.Text.RegularExpressions;

namespace ComicMaintainer.Core.ErrorReporting;

/// <summary>
/// Scrubs text that is about to leave the machine as part of an
/// <see cref="ErrorReport"/>.
/// </summary>
/// <remarks>
/// <para>This is the highest-risk part of automated error reporting: exception
/// messages and stack traces routinely contain the absolute path of a user's
/// comic library (which embeds their account name), SMTP credentials, API keys
/// and email addresses. A self-hosted user who opts in to filing issues is
/// agreeing to share a bug, not their library layout.</para>
/// <para>The redactor is deliberately aggressive and lossy. When there is a
/// choice between keeping a detail that might help debugging and dropping
/// something that might be sensitive, it drops it — the fingerprint and stack
/// frames are what make a report actionable, not the specific filename.</para>
/// </remarks>
public static class ErrorReportRedactor
{
    /// <summary>Marker substituted for any redacted run of text.</summary>
    public const string Mask = "[redacted]";

    /// <summary>Marker substituted for the directory portion of an absolute path.</summary>
    public const string PathMask = "<path>";

    /// <summary>Default cap applied to a single redacted field.</summary>
    public const int DefaultMaxLength = 4000;

    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Key names whose associated value is a secret. Matched against
    /// <c>key=value</c>, <c>key: value</c> and <c>"key": "value"</c> forms so
    /// that a setting name appearing in an exception message ("SmtpPassword
    /// rejected: hunter2") does not leak the value beside it.
    /// </summary>
    private static readonly string[] SecretKeyNames =
    {
        "SmtpPassword",
        "ComicVineApiKey",
        "GitHubToken",
        "ErrorReportingGitHubToken",
        "password",
        "passwd",
        "pwd",
        "secret",
        "token",
        "api[_-]?key",
        "apikey",
        "authorization",
        "auth",
        "bearer",
        "credential",
        "connectionstring",
        "jwt"
    };

    private static readonly Regex SecretAssignment = new(
        @"\b(?<key>" + string.Join("|", SecretKeyNames) + @")\b\s*(?<sep>[:=]|=>)\s*(?<quote>[""'])?(?<value>[^""'\s,;)\]}]+)(?<close>[""'])?",
        Opts,
        RegexTimeout);

    /// <summary>Bearer/Basic tokens that appear without a key name.</summary>
    private static readonly Regex AuthorizationScheme = new(
        @"\b(?<scheme>Bearer|Basic|Token)\s+(?<value>[A-Za-z0-9\-._~+/=]{8,})",
        Opts,
        RegexTimeout);

    /// <summary>GitHub personal access / app tokens, which have recognizable prefixes.</summary>
    private static readonly Regex GitHubToken = new(
        @"\b(gh[pousr]|github_pat)_[A-Za-z0-9_]{16,}",
        Opts,
        RegexTimeout);

    /// <summary>Credentials embedded in a URL: <c>******host</c>.</summary>
    private static readonly Regex UrlCredentials = new(
        @"(?<scheme>[a-z][a-z0-9+.\-]*://)(?<userinfo>[^/\s:@]+(:[^/\s@]*)?)@",
        Opts,
        RegexTimeout);

    private static readonly Regex EmailAddress = new(
        @"\b[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}\b",
        Opts,
        RegexTimeout);

    /// <summary>
    /// Unix absolute paths and Windows drive/UNC paths. Only the final segment
    /// survives, so <c>/home/alice/comics/Series/Issue 1.cbz</c> becomes
    /// <c>&lt;path&gt;/Issue 1.cbz</c> — enough to see it was a CBZ without
    /// revealing the user's name or library layout.
    /// </summary>
    private static readonly Regex UnixPath = new(
        @"(?<!\w)/(?:[^/\s""'<>|:*?]+/)+(?<leaf>[^/\s""'<>|:*?]*)",
        RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly Regex WindowsPath = new(
        @"(?<![\w:])(?:[A-Z]:\\|\\\\)(?:[^\\/\s""'<>|:*?]+\\)*(?<leaf>[^\\/\s""'<>|:*?]*)",
        Opts,
        RegexTimeout);

    /// <summary>
    /// Redacts a free-text field (message, exception message) and truncates it.
    /// </summary>
    public static string? Redact(string? value, int maxLength = DefaultMaxLength)
        => Redact(value, Array.Empty<string>(), maxLength);

    /// <summary>
    /// Redacts a free-text field, additionally masking any literal occurrence of
    /// <paramref name="literalSecrets"/>. Callers pass the configured secret
    /// values (SMTP password, API keys) so that a secret echoed verbatim by a
    /// third-party library is caught even when it appears without a key name.
    /// </summary>
    public static string? Redact(string? value, IReadOnlyCollection<string> literalSecrets, int maxLength = DefaultMaxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var result = value;

        try
        {
            // Literal secrets first: masking them before structural rules run
            // means a secret that also looks like a path or email is already gone.
            foreach (var secret in literalSecrets)
            {
                // Very short values would match far too much ordinary text.
                if (string.IsNullOrWhiteSpace(secret) || secret.Length < 6)
                {
                    continue;
                }

                result = result.Replace(secret, Mask, StringComparison.Ordinal);
            }

            result = GitHubToken.Replace(result, Mask);
            result = SecretAssignment.Replace(result, m =>
                $"{m.Groups["key"].Value}{m.Groups["sep"].Value}{Mask}");
            result = AuthorizationScheme.Replace(result, m => $"{m.Groups["scheme"].Value} {Mask}");
            result = UrlCredentials.Replace(result, m => $"{m.Groups["scheme"].Value}{Mask}@");
            result = EmailAddress.Replace(result, Mask);
            result = WindowsPath.Replace(result, m => $"{PathMask}\\{m.Groups["leaf"].Value}");
            result = UnixPath.Replace(result, m => $"{PathMask}/{m.Groups["leaf"].Value}");
        }
        catch (RegexMatchTimeoutException)
        {
            // A pathological input must never be reported half-scrubbed.
            return Mask;
        }

        return Truncate(result, maxLength);
    }

    /// <summary>
    /// Redacts a stack trace. Frames are processed individually so the
    /// per-field truncation does not cut the trace mid-frame, and only the
    /// leading frames are kept: the top of the stack is what identifies the
    /// bug, the tail is framework plumbing.
    /// </summary>
    public static string? RedactStackTrace(string? stackTrace, IReadOnlyCollection<string> literalSecrets, int maxFrames = 40, int maxLength = DefaultMaxLength)
    {
        if (string.IsNullOrWhiteSpace(stackTrace))
        {
            return stackTrace;
        }

        var frames = stackTrace.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var builder = new StringBuilder();
        var kept = 0;

        foreach (var frame in frames)
        {
            if (kept >= maxFrames)
            {
                builder.Append("   ... additional frames omitted");
                break;
            }

            var redacted = Redact(frame.TrimEnd('\r'), literalSecrets, maxLength);
            if (string.IsNullOrWhiteSpace(redacted))
            {
                continue;
            }

            if (builder.Length + redacted.Length + 1 > maxLength)
            {
                builder.Append("   ... additional frames omitted");
                break;
            }

            if (kept > 0)
            {
                builder.Append('\n');
            }

            builder.Append(redacted);
            kept++;
        }

        return builder.ToString();
    }

    /// <summary>Truncates with an explicit marker so a reader knows text was cut.</summary>
    public static string Truncate(string value, int maxLength)
    {
        if (maxLength <= 0 || value.Length <= maxLength)
        {
            return value;
        }

        const string suffix = "… [truncated]";
        if (maxLength <= suffix.Length)
        {
            return value[..maxLength];
        }

        return string.Concat(value.AsSpan(0, maxLength - suffix.Length), suffix);
    }
}
