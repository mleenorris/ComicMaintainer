namespace ComicMaintainer.Core.ErrorReporting;

/// <summary>
/// A single captured failure, already stripped of anything that identifies the
/// installation it came from.
/// </summary>
/// <remarks>
/// This is the only shape that ever leaves the machine. It deliberately carries
/// no request body, no headers, no environment variables and no connection
/// string — see <see cref="ErrorReportRedactor"/> for the scrubbing rules that
/// every string field is passed through before a report is constructed.
/// </remarks>
public sealed record ErrorReport
{
    /// <summary>Stable identity of the failure; see <see cref="ErrorReportFingerprint"/>.</summary>
    public required string Fingerprint { get; init; }

    /// <summary>Serilog level that produced the report ("Error" or "Fatal").</summary>
    public required string Level { get; init; }

    /// <summary>
    /// The structured message template (e.g. <c>"Failed to process {File}"</c>).
    /// Templates group failures far better than rendered text, which embeds the
    /// specific file or user that happened to trip the bug.
    /// </summary>
    public required string MessageTemplate { get; init; }

    /// <summary>The rendered message, redacted.</summary>
    public required string RenderedMessage { get; init; }

    /// <summary>Full name of the exception type, or null when the event had no exception.</summary>
    public string? ExceptionType { get; init; }

    /// <summary>Exception message, redacted.</summary>
    public string? ExceptionMessage { get; init; }

    /// <summary>Stack trace, redacted and truncated.</summary>
    public string? StackTrace { get; init; }

    /// <summary>Serilog <c>SourceContext</c> (the logging type's full name).</summary>
    public string? SourceContext { get; init; }

    /// <summary>Correlation id of the request that produced the failure, when there was one.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>Application version the failure was observed on.</summary>
    public string? AppVersion { get; init; }

    /// <summary>OS description, e.g. "Linux 6.8.0 #1 SMP".</summary>
    public string? OperatingSystem { get; init; }

    /// <summary>.NET runtime description.</summary>
    public string? RuntimeVersion { get; init; }

    /// <summary>When the failure occurred (UTC).</summary>
    public required DateTime TimestampUtc { get; init; }
}
