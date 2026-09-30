using ComicMaintainer.Core.ErrorReporting.Models;

namespace ComicMaintainer.Core.ErrorReporting.Interfaces;

/// <summary>
/// Outcome of asking a transport to deliver a report.
/// </summary>
/// <param name="Delivered">
/// True when the report reached GitHub. False for the consent-first transport,
/// which only prepares a link for the user to act on.
/// </param>
/// <param name="IssueNumber">Issue the report was filed as, when known.</param>
/// <param name="IssueUrl">
/// Either the filed issue, or the pre-filled <c>issues/new</c> URL the user must
/// review and submit.
/// </param>
/// <param name="Detail">Human-readable explanation, surfaced in the UI.</param>
public readonly record struct ErrorReportTransportResult(
    bool Delivered,
    int? IssueNumber,
    string? IssueUrl,
    string? Detail);

/// <summary>
/// Delivers a redacted <see cref="ErrorReport"/> to GitHub.
/// </summary>
/// <remarks>
/// Two implementations exist and are selected by
/// <c>AppSettings.ErrorReportingMode</c>: a consent-first transport that only
/// builds a pre-filled issue URL and transmits nothing, and an automatic
/// transport that posts through the GitHub API with a narrowly scoped token.
/// </remarks>
public interface IErrorReportTransport
{
    /// <summary>
    /// Identifier matching <c>AppSettings.ErrorReportingMode</c>, e.g.
    /// <c>manual</c> or <c>automatic</c>.
    /// </summary>
    string Mode { get; }

    /// <summary>
    /// True when this transport sends data off the instance without further user
    /// interaction. Used to decide whether an explicit opt-in is required.
    /// </summary>
    bool TransmitsAutomatically { get; }

    /// <summary>
    /// Delivers, or prepares delivery of, <paramref name="report"/>.
    /// </summary>
    Task<ErrorReportTransportResult> SendAsync(
        ErrorReport report,
        CancellationToken cancellationToken = default);
}
