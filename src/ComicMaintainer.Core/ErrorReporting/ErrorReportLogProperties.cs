namespace ComicMaintainer.Core.ErrorReporting;

/// <summary>
/// Log-scope properties understood by the error-reporting sink.
/// </summary>
/// <remarks>
/// Some failures are logged at <c>Error</c> because an operator genuinely needs
/// to see them, yet are not defects: a malformed comic archive a user dropped
/// in the watched folder, an unreachable third-party metadata provider, an SMTP
/// server rejecting a recipient. Filing a GitHub issue for those would bury
/// real bugs under field noise.
/// <para>Wrapping such a call in the scope returned by <see cref="Exclude"/>
/// suppresses the report while leaving the log line completely intact, which is
/// strictly better than downgrading the level to hide it.</para>
/// </remarks>
public static class ErrorReportLogProperties
{
    /// <summary>
    /// Name of the scope property that suppresses reporting. Present on the log
    /// event with the caller's reason as its value.
    /// </summary>
    public const string ExcludeFromErrorReports = "ExcludeFromErrorReports";

    /// <summary>
    /// Builds the scope state to pass to <c>ILogger.BeginScope</c> so that any
    /// error logged inside the scope is written to the log but never turned
    /// into a GitHub issue.
    /// </summary>
    /// <param name="reason">
    /// Why this failure is expected in the field. Recorded on the log event so
    /// the exclusion is auditable rather than invisible.
    /// </param>
    public static IReadOnlyDictionary<string, object> Exclude(string reason)
        => new Dictionary<string, object>
        {
            [ExcludeFromErrorReports] = string.IsNullOrWhiteSpace(reason) ? "unspecified" : reason
        };
}
