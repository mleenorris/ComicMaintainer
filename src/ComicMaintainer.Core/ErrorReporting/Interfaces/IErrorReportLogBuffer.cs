namespace ComicMaintainer.Core.ErrorReporting.Interfaces;

/// <summary>
/// Holds the most recent log lines in memory so an error report can carry the
/// context leading up to the failure.
/// </summary>
/// <remarks>
/// This exists specifically so that <c>debug.log</c> is never attached to a
/// report. Shipping a log file would disclose the whole library — every path
/// and title the instance has touched — regardless of how carefully the
/// exception itself was redacted. A small ring buffer bounds the exposure to a
/// fixed number of lines, and those lines are still redacted before use.
/// </remarks>
public interface IErrorReportLogBuffer
{
    /// <summary>Records a formatted log line, evicting the oldest when full.</summary>
    void Add(string line);

    /// <summary>
    /// Returns up to <paramref name="maxLines"/> of the most recent lines, in
    /// chronological order.
    /// </summary>
    IReadOnlyList<string> Snapshot(int maxLines);
}
