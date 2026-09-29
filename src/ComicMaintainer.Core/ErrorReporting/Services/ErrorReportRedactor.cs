using System.Text.RegularExpressions;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.ErrorReporting.Interfaces;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.Core.ErrorReporting.Services;

/// <summary>
/// Scrubs deployment- and user-specific data out of text destined for a GitHub
/// issue.
/// </summary>
/// <remarks>
/// <para>
/// Redaction runs as a single left-to-right pass over one ordered alternation
/// (<see cref="SensitivePattern"/>) so every character is classified exactly
/// once. That matters because the categories overlap — <c>Authorization: ****** eyJhbGci...</c> is simultaneously a header, a credential pair and (thanks to
/// the <c>/</c> in base64url) path-like. Running separate passes would let a
/// later pass re-parse a placeholder emitted by an earlier one.
/// </para>
/// <para>
/// Literal secret values from <see cref="AppSettings"/> (SMTP password, provider
/// API key, GitHub token) are removed <em>before</em> the pattern pass, because
/// an opaque credential need not match any pattern.
/// </para>
/// <para>
/// The redactor deliberately errs towards over-redaction. Comic file names
/// contain spaces, so the pattern that catches them can also swallow the
/// sentence fragment preceding the name. Losing part of an exception message is
/// an acceptable price for never disclosing a subscriber's library; the
/// exception type, stack trace and fingerprint are unaffected.
/// </para>
/// <para>
/// Every pattern is linear and length-bounded — no nested or ambiguous
/// quantifiers — so a hostile log line cannot drive it into catastrophic
/// backtracking. A match timeout is applied as defence in depth, and a timeout
/// discards the input rather than emitting it unredacted.
/// </para>
/// </remarks>
public sealed class ErrorReportRedactor : IErrorReportRedactor
{
    /// <summary>Placeholder substituted for anything considered sensitive.</summary>
    public const string Placeholder = "[redacted]";

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Extensions that identify a file in this repository rather than in the
    /// user's library. Their names are public information and are the most
    /// useful part of a stack frame, so they survive path redaction.
    /// </summary>
    private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".js", ".html", ".css", ".razor", ".cshtml", ".ts", ".json", ".yml", ".yaml"
    };

    /// <summary>
    /// Library-content extensions. A bare file name ending in one of these is
    /// user data even without a directory component.
    /// </summary>
    private const string ContentExtensionAlternation =
        "cbz|cbr|cb7|cbt|cba|zip|rar|7z|epub|mobi|azw3|pdf|jpe?g|png|webp|gif|bmp|avif";

    // Ordered alternation. The engine scans by start position and, at a given
    // position, tries these branches in order; broader categories that can
    // contain narrower ones are therefore listed first.
    private static readonly Regex SensitivePattern = new(
        // 1. Headers carrying identity or credentials, including the Authelia
        //    forwarded-identity headers and anything cookie-shaped.
        @"(?<header>(?i:authorization|proxy-authorization|www-authenticate|set-cookie|cookie|x-api-key|api-key|remote-user|remote-groups|remote-email|remote-name|x-forwarded-user)\s{0,4}[:=]\s{0,4})[^\r\n]{1,4096}" +
        // 2. key=value / key: value pairs whose key names a credential.
        @"|(?<kv>(?i:passwords?|passwd|pwd|secrets?|tokens?|api[_\-]?keys?|access[_\-]?key|client[_\-]?secret|connectionstring|bearer)\s{0,4}[:=]\s{0,4})[^\s,;&""'\r\n]{1,4096}" +
        // 3. Well-known opaque credential shapes: GitHub tokens and JWTs.
        @"|(?<drop>\b(?:gh[pousr]_[A-Za-z0-9]{16,255}|github_pat_[A-Za-z0-9_]{20,255})\b" +
        @"|\beyJ[A-Za-z0-9_\-]{8,4096}\.[A-Za-z0-9_\-]{8,4096}\.[A-Za-z0-9_\-]{4,4096}" +
        // 4. E-mail addresses and network addresses. Listed before the URL and
        //    path branches only matters for shared start positions; they are
        //    grouped here because all three are dropped whole.
        @"|[A-Za-z0-9._%+\-]{1,128}@[A-Za-z0-9\-]{1,63}(?:\.[A-Za-z0-9\-]{1,63}){1,8}" +
        @"|\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b" +
        @"|\b(?:[0-9A-Fa-f]{1,4}:){2,7}[0-9A-Fa-f]{1,4}\b)" +
        // 5. URLs. Before the path branches because "https://host/a" starts
        //    before its first slash.
        @"|(?<url>\b[A-Za-z][A-Za-z0-9+.\-]{1,15}://[^\s""'<>\\]{0,2048})" +
        // 6. Paths: Windows drive-qualified, UNC, or absolute POSIX. The body
        //    excludes ':' so that a stack frame's ":line 42" suffix survives.
        @"|(?<path>(?:[A-Za-z]:[\\/]|\\\\|/)[^\s""'<>|:*?\r\n]{0,2048})" +
        // 7. Relative paths: at least one separator between name components.
        @"|(?<path>(?:[\w.\-~]{1,255}[\\/]){1,32}[\w.\-]{1,255})" +
        // 8. Bare library file names. Comic file names contain spaces, so the
        //    run may span words; '@', ':' and quote characters are excluded so
        //    it cannot swallow an adjacent e-mail address, URL or log delimiter.
        @"|(?<content>[A-Za-z0-9_~(\[#][A-Za-z0-9 _.,\-()\[\]#&'!+~]{0,160}\.(?i:" + ContentExtensionAlternation + @"))(?![A-Za-z0-9])",
        RegexOptions.ExplicitCapture | RegexOptions.CultureInvariant,
        MatchTimeout);

    private readonly IOptionsMonitor<AppSettings>? _settings;

    /// <summary>
    /// Creates a redactor that additionally scrubs the literal secret values
    /// currently configured on this installation.
    /// </summary>
    public ErrorReportRedactor(IOptionsMonitor<AppSettings> settings)
    {
        _settings = settings;
    }

    /// <summary>
    /// Creates a redactor with no configured secrets, for call sites that have
    /// no access to settings.
    /// </summary>
    public ErrorReportRedactor()
    {
        _settings = null;
    }

    /// <inheritdoc />
    public string Redact(string? input) => RedactText(input, CollectConfiguredSecrets());

    /// <inheritdoc />
    public IReadOnlyList<string> RedactLines(IEnumerable<string>? lines)
    {
        if (lines is null)
        {
            return Array.Empty<string>();
        }

        var secrets = CollectConfiguredSecrets();
        return lines.Select(line => RedactText(line, secrets)).ToList();
    }

    /// <summary>
    /// Pure redaction entry point. <paramref name="literalSecrets"/> holds exact
    /// values (passwords, API keys) that must be removed regardless of shape.
    /// </summary>
    public static string RedactText(string? input, IReadOnlyCollection<string>? literalSecrets = null)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        var text = input;

        // Literal secrets first: an opaque credential may match no pattern, and
        // removing it up front also stops it being carried along inside a longer
        // match. Longest-first so a secret containing another as a prefix is
        // fully removed.
        if (literalSecrets is { Count: > 0 })
        {
            foreach (var secret in literalSecrets.OrderByDescending(s => s.Length))
            {
                text = text.Replace(secret, Placeholder, StringComparison.Ordinal);
            }
        }

        try
        {
            return SensitivePattern.Replace(text, ReplaceMatch);
        }
        catch (RegexMatchTimeoutException)
        {
            // Redaction must never fail open: discard pathological input rather
            // than risk emitting it unredacted.
            return Placeholder;
        }
    }

    private static string ReplaceMatch(Match match)
    {
        // Headers and credential pairs keep the key, so the report still records
        // *what* was present, and drop only the value.
        if (match.Groups["header"].Success)
        {
            return match.Groups["header"].Value + Placeholder;
        }

        if (match.Groups["kv"].Success)
        {
            return match.Groups["kv"].Value + Placeholder;
        }

        if (match.Groups["url"].Success)
        {
            return RedactUrl(match.Groups["url"].Value);
        }

        if (match.Groups["path"].Success)
        {
            return RedactPath(match.Groups["path"].Value);
        }

        if (match.Groups["content"].Success)
        {
            var value = match.Groups["content"].Value;
            var dot = value.LastIndexOf('.');
            return dot > 0 ? Placeholder + value[dot..] : Placeholder;
        }

        // Tokens, JWTs, e-mail addresses and IP addresses carry no reusable
        // signal, so the whole match is dropped.
        return Placeholder;
    }

    /// <summary>
    /// Keeps the scheme and host of a URL — useful for identifying which
    /// external provider failed — and drops userinfo, path, query and fragment,
    /// any of which can carry an API key or a library path.
    /// </summary>
    private static string RedactUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            return Placeholder;
        }

        var hasDetail = !string.IsNullOrEmpty(parsed.UserInfo)
            || (!string.IsNullOrEmpty(parsed.AbsolutePath) && parsed.AbsolutePath != "/")
            || !string.IsNullOrEmpty(parsed.Query)
            || !string.IsNullOrEmpty(parsed.Fragment);

        // An IP-literal host identifies the deployment, so it is dropped too.
        var host = parsed.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6
            ? Placeholder
            : parsed.Host;

        return hasDetail
            ? $"{parsed.Scheme}://{host}/{Placeholder}"
            : $"{parsed.Scheme}://{host}";
    }

    /// <summary>
    /// Replaces a path with a placeholder. Files belonging to this repository
    /// keep their bare name, because a stack frame is near-useless without it
    /// and those names are already public; everything else keeps at most its
    /// extension.
    /// </summary>
    private static string RedactPath(string path)
    {
        var separator = path.LastIndexOfAny(['/', '\\']);
        var fileName = separator >= 0 && separator < path.Length - 1
            ? path[(separator + 1)..]
            : string.Empty;

        if (fileName.Length == 0)
        {
            return Placeholder;
        }

        var dot = fileName.LastIndexOf('.');
        var extension = dot > 0 && dot < fileName.Length - 1 ? fileName[dot..] : string.Empty;

        if (SourceExtensions.Contains(extension))
        {
            return fileName;
        }

        return extension.Length > 0 ? Placeholder + extension : Placeholder;
    }

    /// <summary>
    /// Snapshots the literal secrets configured on this instance. Values shorter
    /// than eight characters are skipped: they are too short to be a real
    /// credential and redacting them would corrupt unrelated text.
    /// </summary>
    private IReadOnlyCollection<string> CollectConfiguredSecrets()
    {
        if (_settings is null)
        {
            return Array.Empty<string>();
        }

        var current = _settings.CurrentValue;
        var candidates = new[]
        {
            current.SmtpPassword,
            current.SmtpUsername,
            current.ComicVineApiKey,
            current.GitHubToken,
            current.EmailFromAddress,
            current.WatchedDirectory,
            current.DuplicateDirectory,
            Environment.GetEnvironmentVariable("JWT_SECRET"),
        };

        return candidates
            .Where(value => !string.IsNullOrWhiteSpace(value) && value!.Trim().Length >= 8)
            .Select(value => value!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }
}
