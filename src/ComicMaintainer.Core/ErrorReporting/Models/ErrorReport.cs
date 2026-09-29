namespace ComicMaintainer.Core.ErrorReporting.Models;

/// <summary>
/// A fully redacted, fingerprinted error ready to be shown to the user or filed
/// as a GitHub issue.
/// </summary>
/// <remarks>
/// Every string field on this type has already passed through
/// <c>IErrorReportRedactor</c>. Treat the type as the boundary: anything that
/// gets here is safe to display and to transmit.
/// </remarks>
public sealed class ErrorReport
{
    /// <summary>Stable identity of the defect. See <c>ErrorFingerprinter</c>.</summary>
    public string Fingerprint { get; init; } = string.Empty;

    /// <summary>Exception type name, or <c>Error</c> for frontend reports.</summary>
    public string ExceptionType { get; init; } = string.Empty;

    /// <summary>Redacted exception message.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Redacted stack trace, truncated to a bounded number of frames.</summary>
    public string? StackTrace { get; init; }

    /// <summary>
    /// Where the failure happened: an HTTP route <em>template</em> such as
    /// <c>POST /api/files/rename</c>, a hosted-service name, or a redacted page
    /// URL for frontend reports. Route templates are used rather than resolved
    /// URLs so that path parameters cannot carry library data into the report.
    /// </summary>
    public string? Origin { get; init; }

    /// <summary>Which layer produced the error.</summary>
    public ErrorReportSource Source { get; init; }

    /// <summary>
    /// Coarse product area used as an issue label, e.g. <c>reader</c>.
    /// </summary>
    public string Area { get; init; } = "core";

    /// <summary>Application version the trace corresponds to.</summary>
    public string AppVersion { get; init; } = string.Empty;

    /// <summary>Runtime and OS description, e.g. <c>.NET 10.0.0 / Linux</c>.</summary>
    public string Platform { get; init; } = string.Empty;

    /// <summary>How many times this fingerprint has been seen locally.</summary>
    public int OccurrenceCount { get; init; } = 1;

    /// <summary>When this fingerprint was first seen on this instance (UTC).</summary>
    public DateTime FirstSeenUtc { get; init; }

    /// <summary>When this fingerprint was last seen on this instance (UTC).</summary>
    public DateTime LastSeenUtc { get; init; }

    /// <summary>
    /// Correlation identifier shared with the HTTP response, so a user-reported
    /// symptom can be tied back to a specific request in the logs.
    /// </summary>
    public string? CorrelationId { get; init; }

    /// <summary>
    /// Bounded window of redacted log lines around the error. The full
    /// <c>debug.log</c> is never attached.
    /// </summary>
    public IReadOnlyList<string> LogExcerpt { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Redacted description of what the user was doing, when the UI supplied it.
    /// </summary>
    public string? LastUserAction { get; init; }

    /// <summary>
    /// Issue number this fingerprint was filed as, once a transport has
    /// reported it. Null while unreported.
    /// </summary>
    public int? ReportedIssueNumber { get; init; }
}
