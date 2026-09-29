namespace ComicMaintainer.Core.ErrorReporting;

/// <summary>
/// Well-known logging properties that let a call site opt a specific log event
/// out of error reporting.
/// </summary>
/// <remarks>
/// The Serilog sink captures every <c>Error</c> event, which is what makes
/// coverage automatic. Some failures are nonetheless known not to be defects —
/// a damaged comic file that fails to process is bad input, already recorded in
/// the processing history — and turning those into public issues would drown
/// the real reports. Marking the individual event is preferred over excluding a
/// whole source context, which would also hide genuine defects raised by the
/// same service.
/// </remarks>
public static class ErrorReportLogProperties
{
    /// <summary>
    /// Property whose presence on a log event means "do not report this".
    /// </summary>
    public const string ExclusionPropertyName = "ErrorReportExclusion";

    /// <summary>
    /// Scope state that marks the events written inside it as excluded.
    /// </summary>
    /// <param name="reason">
    /// Short, non-sensitive explanation recorded alongside the event.
    /// </param>
    public static IReadOnlyList<KeyValuePair<string, object>> Exclude(string reason) =>
        [new KeyValuePair<string, object>(ExclusionPropertyName, reason)];
}
