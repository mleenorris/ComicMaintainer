using ComicMaintainer.Core.ErrorReporting.Models;

namespace ComicMaintainer.Core.ErrorReporting.Interfaces;

/// <summary>
/// Entry point for the error-reporting pipeline: redact, fingerprint, suppress,
/// throttle, persist, and (when configured) transmit.
/// </summary>
public interface IErrorReportService
{
    /// <summary>
    /// Captures an exception raised inside the application.
    /// </summary>
    /// <param name="exception">The exception to report.</param>
    /// <param name="source">Which layer raised it.</param>
    /// <param name="origin">
    /// Route template or hosted-service name. Must not contain resolved route
    /// values, which can carry library paths.
    /// </param>
    /// <param name="correlationId">Identifier also returned to the client.</param>
    /// <returns>
    /// The stored report, or null when the error was suppressed or reporting is
    /// disabled.
    /// </returns>
    Task<ErrorReport?> CaptureAsync(
        Exception exception,
        ErrorReportSource source,
        string? origin = null,
        string? correlationId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Captures an error described by its parts rather than by an exception
    /// object. Used by the Serilog sink and by browser-submitted reports.
    /// </summary>
    Task<ErrorReport?> CaptureAsync(
        string exceptionType,
        string? message,
        string? stackTrace,
        ErrorReportSource source,
        string? origin = null,
        string? correlationId = null,
        string? lastUserAction = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the stored reports, newest occurrence first.
    /// </summary>
    Task<IReadOnlyList<ErrorReport>> GetReportsAsync(
        int limit = 50,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a single stored report by fingerprint.
    /// </summary>
    Task<ErrorReport?> GetReportAsync(
        string fingerprint,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Renders the exact issue body that would be filed for
    /// <paramref name="fingerprint"/>, so the user can review it before
    /// anything is sent.
    /// </summary>
    Task<string?> PreviewIssueBodyAsync(
        string fingerprint,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Delivers a stored report through the configured transport, honouring the
    /// per-fingerprint cooldown and the daily cap.
    /// </summary>
    Task<ErrorReportTransportResult> SubmitAsync(
        string fingerprint,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Discards a stored report and its suppression state, so a recurrence is
    /// captured and reported afresh.
    /// </summary>
    Task<bool> DismissAsync(string fingerprint, CancellationToken cancellationToken = default);
}
