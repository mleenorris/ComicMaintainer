namespace ComicMaintainer.Core.ErrorReporting.Interfaces;

/// <summary>
/// Removes user- and deployment-specific data from text before it can leave the
/// installation as part of an error report.
/// </summary>
/// <remarks>
/// ComicMaintainer is self-hosted, so an unredacted stack trace or log line can
/// disclose the owner's library layout, comic titles, account names, mail server
/// credentials and provider API keys. Every string that reaches a transport must
/// pass through this interface first; see <c>docs/ERROR_REPORTING.md</c>.
/// </remarks>
public interface IErrorReportRedactor
{
    /// <summary>
    /// Returns <paramref name="input"/> with paths, filenames, credentials,
    /// tokens, e-mail addresses and IP addresses replaced by placeholders.
    /// </summary>
    string Redact(string? input);

    /// <summary>
    /// Redacts each line independently, preserving line order.
    /// </summary>
    IReadOnlyList<string> RedactLines(IEnumerable<string>? lines);
}
