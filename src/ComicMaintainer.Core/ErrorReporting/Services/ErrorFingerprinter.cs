using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ComicMaintainer.Core.ErrorReporting.Services;

/// <summary>
/// Derives a stable identity for an error so that the same defect recurring on
/// one instance, or occurring across many instances, collapses onto a single
/// GitHub issue.
/// </summary>
/// <remarks>
/// <para>
/// The fingerprint is a hash of four parts: the exception type, a normalised
/// message, the top frames that belong to this application, and the app version.
/// </para>
/// <para>
/// Normalisation removes the parts of a message that vary between occurrences of
/// the same defect — GUIDs, numbers, quoted values and redaction placeholders.
/// Without it, "Could not find page 7" and "Could not find page 8" would be two
/// issues.
/// </para>
/// <para>
/// Frames are restricted to <c>ComicMaintainer.*</c> because framework frames
/// are shared by unrelated defects: two different bugs that both end in
/// <c>System.Linq</c> must not collapse together, and the first application
/// frame is also the frame a fixer starts from.
/// </para>
/// <para>
/// The version is included deliberately: the same trace in a later release is a
/// regression worth a fresh report rather than a comment on a closed issue.
/// </para>
/// </remarks>
public static class ErrorFingerprinter
{
    /// <summary>Number of application stack frames folded into the hash.</summary>
    public const int SignificantFrameCount = 5;

    private const string ApplicationNamespacePrefix = "ComicMaintainer.";

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    private static readonly Regex GuidPattern = new(
        @"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b",
        RegexOptions.CultureInvariant, MatchTimeout);

    /// <summary>
    /// A redaction placeholder, optionally carrying the file extension the
    /// redactor preserved.
    /// </summary>
    /// <remarks>
    /// The extension has to be folded in with the placeholder. Two instances
    /// hitting the same defect on <c>.cbz</c> and <c>.cbr</c> files would
    /// otherwise produce different fingerprints and file two issues for one
    /// bug.
    /// </remarks>
    private static readonly Regex PlaceholderPattern = new(
        @"\[redacted\](?:\.[A-Za-z0-9]{1,8})?",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, MatchTimeout);

    /// <summary>
    /// Opaque identifiers: job ids, request trace ids, short hashes. Requiring
    /// both a digit and a letter keeps ordinary words out — <c>added</c> is
    /// valid hex, and normalising it would merge unrelated defects — while
    /// still catching <c>4e2a</c> and <c>0HN7GQ1A2B3C4</c>. Purely numeric
    /// values are already handled by <see cref="NumberPattern"/>.
    /// </summary>
    private static readonly Regex OpaqueIdentifierPattern = new(
        @"\b(?=[A-Za-z0-9]{4,64}\b)(?=[A-Za-z0-9]{0,63}\d)(?=[A-Za-z0-9]{0,63}[A-Za-z])[A-Za-z0-9]{4,64}\b",
        RegexOptions.CultureInvariant, MatchTimeout);

    private static readonly Regex QuotedPattern = new(
        @"'[^'\r\n]{0,512}'|""[^""\r\n]{0,512}""",
        RegexOptions.CultureInvariant, MatchTimeout);

    private static readonly Regex NumberPattern = new(
        @"\d+(?:\.\d+)*",
        RegexOptions.CultureInvariant, MatchTimeout);

    private static readonly Regex WhitespacePattern = new(
        @"\s+", RegexOptions.CultureInvariant, MatchTimeout);

    /// <summary>
    /// Matches a managed stack frame and captures the fully qualified member
    /// name, e.g. <c>ComicMaintainer.Core.Services.FileStoreService.GetAsync</c>.
    /// </summary>
    private static readonly Regex FramePattern = new(
        @"^\s{0,32}at\s+(?<member>[^\s(]{1,512})",
        RegexOptions.CultureInvariant | RegexOptions.Multiline, MatchTimeout);

    /// <summary>
    /// Computes the fingerprint for an error. The inputs should already be
    /// redacted; redaction placeholders are normalised away so a report whose
    /// message differs only in which path was scrubbed still collapses.
    /// </summary>
    public static string Compute(
        string? exceptionType,
        string? message,
        string? stackTrace,
        string? appVersion)
    {
        var parts = string.Join(
            "\n",
            Normalize(exceptionType),
            NormalizeMessage(message),
            string.Join("\n", ExtractSignificantFrames(stackTrace)),
            Normalize(appVersion));

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(parts));

        // 16 hex characters (64 bits) is short enough to paste into an issue
        // title and quote in conversation, and collision risk at the scale of a
        // single project's defect list is negligible.
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    /// <summary>
    /// Returns the application frames a fixer should look at first, in order,
    /// capped at <see cref="SignificantFrameCount"/>.
    /// </summary>
    /// <remarks>
    /// When a trace contains no application frame at all — a failure entirely
    /// inside the framework — the leading frames are used instead, so unrelated
    /// framework-only errors still fingerprint apart.
    /// </remarks>
    public static IReadOnlyList<string> ExtractSignificantFrames(string? stackTrace)
    {
        if (string.IsNullOrWhiteSpace(stackTrace))
        {
            return Array.Empty<string>();
        }

        List<string> all;
        try
        {
            all = FramePattern.Matches(stackTrace)
                .Select(m => m.Groups["member"].Value)
                .ToList();
        }
        catch (RegexMatchTimeoutException)
        {
            return Array.Empty<string>();
        }

        var applicationFrames = all
            .Where(frame => frame.StartsWith(ApplicationNamespacePrefix, StringComparison.Ordinal))
            .Take(SignificantFrameCount)
            .ToList();

        return applicationFrames.Count > 0
            ? applicationFrames
            : all.Take(SignificantFrameCount).ToList();
    }

    /// <summary>
    /// Maps an error onto a coarse product area, used to label the issue so the
    /// fixer starts with the right part of the codebase in view.
    /// </summary>
    /// <returns>
    /// One of <c>reader</c>, <c>metadata</c>, <c>email</c>, <c>watcher</c>,
    /// <c>auth</c>, <c>jobs</c>, <c>frontend</c> or <c>core</c>.
    /// </returns>
    public static string DeriveArea(string? stackTrace, string? origin = null)
    {
        var haystack = string.Join(
            " ",
            string.Join(" ", ExtractSignificantFrames(stackTrace)),
            origin ?? string.Empty);

        // Ordered most-specific first: a reader controller frame also contains
        // "Controller", and an email job also contains "Job".
        if (Contains(haystack, "Reader", "ComicReader")) return "reader";
        if (Contains(haystack, "Metadata", "AniList", "MangaDex", "ComicVine", "SeriesImage")) return "metadata";
        if (Contains(haystack, "Email", "Smtp", "Epub", "Ereader")) return "email";
        if (Contains(haystack, "Watcher", "FileWatcher")) return "watcher";
        if (Contains(haystack, "Auth", "Identity", "Authelia", "Jwt")) return "auth";
        if (Contains(haystack, "Job", "Scheduled", "Process")) return "jobs";
        if (Contains(haystack, "window.", "HTMLElement", "frontend")) return "frontend";
        return "core";
    }

    private static bool Contains(string haystack, params string[] needles) =>
        needles.Any(needle => haystack.Contains(needle, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Collapses the per-occurrence noise out of an exception message.
    /// </summary>
    public static string NormalizeMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return string.Empty;
        }

        try
        {
            var text = message;

            // A redaction placeholder already stands for variable data; folding
            // it into the same token as a path keeps a report fingerprinted the
            // same whether or not redaction fired.
            text = PlaceholderPattern.Replace(text, "<v>");
            text = GuidPattern.Replace(text, "<v>");
            text = QuotedPattern.Replace(text, "<v>");
            text = OpaqueIdentifierPattern.Replace(text, "<v>");
            text = NumberPattern.Replace(text, "<v>");
            text = WhitespacePattern.Replace(text, " ");

            return text.Trim().ToLowerInvariant();
        }
        catch (RegexMatchTimeoutException)
        {
            return string.Empty;
        }
    }

    private static string Normalize(string? value) =>
        (value ?? string.Empty).Trim().ToLower(CultureInfo.InvariantCulture);
}
