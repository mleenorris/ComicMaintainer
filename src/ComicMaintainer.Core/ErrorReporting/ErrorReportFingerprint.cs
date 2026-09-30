using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ComicMaintainer.Core.ErrorReporting;

/// <summary>
/// Computes the stable identity of a failure, used to decide whether a new
/// GitHub issue is warranted or an existing one should simply have its
/// occurrence count bumped.
/// </summary>
/// <remarks>
/// The inputs are the exception type, the normalized leading stack frames and
/// the <em>message template</em> — never the rendered message. Rendering
/// substitutes the specific file, series or user that tripped the bug, so
/// fingerprinting the rendered text would file one issue per comic in the
/// library for a single defect.
/// </remarks>
public static class ErrorReportFingerprint
{
    /// <summary>
    /// Number of leading stack frames folded into the fingerprint. Enough to
    /// separate distinct call sites into the same helper, few enough that a
    /// deeper refactor of unrelated callers does not re-open a fixed issue.
    /// </summary>
    public const int SignificantFrameCount = 5;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Matches the trailing source-location of a frame:
    /// <c> in /src/Foo.cs:line 42</c>. Line numbers shift with every unrelated
    /// edit above them, so including them would churn fingerprints on each
    /// release.
    /// </summary>
    private static readonly Regex FrameSourceLocation = new(
        @"\s+in\s.*$",
        RegexOptions.CultureInvariant,
        RegexTimeout);

    /// <summary>
    /// Matches the generic/lambda/async state-machine decoration the compiler
    /// adds (<c>+&lt;MoveNext&gt;d__12</c>), which changes when unrelated
    /// members are added to the same type.
    /// </summary>
    private static readonly Regex CompilerDecoration = new(
        @"[+.]<[^>]*>[A-Za-z0-9_]*(__\d+)?",
        RegexOptions.CultureInvariant,
        RegexTimeout);

    /// <summary>
    /// Computes a lowercase hex SHA-256 fingerprint, truncated to 32 characters
    /// (128 bits — collision-free for any realistic number of distinct defects
    /// while staying short enough to sit in an issue title).
    /// </summary>
    public static string Compute(string? exceptionType, string? stackTrace, string? messageTemplate, string? sourceContext = null)
    {
        var builder = new StringBuilder();
        builder.Append(exceptionType?.Trim() ?? "(none)").Append('\n');

        // With no exception there are no frames to distinguish call sites, so
        // the logging type stands in for "where" the failure happened.
        builder.Append(sourceContext?.Trim() ?? "(none)").Append('\n');
        builder.Append(NormalizeTemplate(messageTemplate)).Append('\n');

        foreach (var frame in NormalizeFrames(stackTrace))
        {
            builder.Append(frame).Append('\n');
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..32];
    }

    /// <summary>
    /// Returns the normalized leading frames of a stack trace: method
    /// identities only, with source locations, line numbers and compiler
    /// decorations removed.
    /// </summary>
    public static IReadOnlyList<string> NormalizeFrames(string? stackTrace, int maxFrames = SignificantFrameCount)
    {
        if (string.IsNullOrWhiteSpace(stackTrace))
        {
            return Array.Empty<string>();
        }

        var frames = new List<string>(maxFrames);

        foreach (var rawLine in stackTrace.Split('\n'))
        {
            if (frames.Count >= maxFrames)
            {
                break;
            }

            var line = rawLine.Trim().TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            // "--- End of stack trace from previous location ---" and the
            // exception header line are not frames.
            if (!line.StartsWith("at ", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                line = FrameSourceLocation.Replace(line, string.Empty);
                line = CompilerDecoration.Replace(line, string.Empty);
            }
            catch (RegexMatchTimeoutException)
            {
                // Fall through with whatever normalization succeeded; a
                // pathological frame should not prevent fingerprinting.
            }

            frames.Add(line.Trim());
        }

        return frames;
    }

    /// <summary>
    /// Collapses whitespace in a message template so that reformatting a long
    /// template across source lines does not change the fingerprint.
    /// </summary>
    private static string NormalizeTemplate(string? messageTemplate)
    {
        if (string.IsNullOrWhiteSpace(messageTemplate))
        {
            return string.Empty;
        }

        var parts = messageTemplate.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', parts).ToLower(CultureInfo.InvariantCulture);
    }
}
