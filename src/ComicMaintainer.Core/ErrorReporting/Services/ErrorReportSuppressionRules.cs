namespace ComicMaintainer.Core.ErrorReporting.Services;

/// <summary>
/// Decides whether an error is a defect worth a GitHub issue, or an
/// environmental condition that a code change cannot fix.
/// </summary>
/// <remarks>
/// <para>
/// This is the single most important piece of noise control in the pipeline. A
/// self-hosted instance routinely logs errors that say nothing about the code:
/// a user navigating away mid-request, a NAS volume filling up, SQLite reporting
/// a busy database under concurrent writes, or an external metadata provider
/// rate-limiting. Filing issues for those would bury real defects and, in
/// automatic mode, spam the tracker.
/// </para>
/// <para>
/// Matching is done on type <em>names</em> and message text rather than on
/// exception types, so the rules stay testable without constructing real
/// provider exceptions and keep working for types the Core project does not
/// reference.
/// </para>
/// </remarks>
public static class ErrorReportSuppressionRules
{
    /// <summary>
    /// Exception type names that are always environmental. Compared against both
    /// the full name and the simple name so callers may pass either.
    /// </summary>
    private static readonly HashSet<string> AlwaysSuppressedTypes = new(StringComparer.Ordinal)
    {
        // Shutdown, request abort and cooperative cancellation.
        "OperationCanceledException",
        "TaskCanceledException",
        "ConnectionResetException",
        "ConnectionAbortedException",

        // The client hung up or sent something malformed: not a server defect.
        "BadHttpRequestException",

        // Transient network and remote-service failures, principally from the
        // external metadata providers.
        "HttpRequestException",
        "SocketException",
        "TimeoutException",
        "WebException",
        "AuthenticationException",

        // Host filesystem conditions the operator must fix.
        "UnauthorizedAccessException",
        "DriveNotFoundException",
        "PathTooLongException",
    };

    /// <summary>
    /// Message fragments that mark an otherwise-generic exception as
    /// environmental. Matched case-insensitively.
    /// </summary>
    private static readonly string[] SuppressedMessageFragments =
    [
        // SQLite contention. Concurrent writers are expected on a busy library
        // and are handled by retry/backoff, not by a code fix.
        "database is locked",
        "database table is locked",
        "sqlite_busy",
        "sqlite_locked",

        // Disk exhaustion and quota.
        "no space left on device",
        "there is not enough space on the disk",
        "disk full",
        "disk quota exceeded",

        // Client disconnects surfacing through IO.
        "the client has disconnected",
        "broken pipe",
        "connection reset by peer",
        "the response has already started",

        // Read-only mounts and permission problems.
        "read-only file system",
        "permission denied",
        "access to the path",

        // Upstream provider throttling and outages.
        "too many requests",
        "service unavailable",
        "gateway timeout",
        "name or service not known",
        "no such host is known",
    ];

    /// <summary>
    /// Returns <c>true</c> when the error must not be reported.
    /// </summary>
    /// <param name="exceptionTypeName">
    /// Full or simple exception type name. May be null for frontend reports.
    /// </param>
    /// <param name="message">Exception or error message.</param>
    public static bool ShouldSuppress(string? exceptionTypeName, string? message)
    {
        if (MatchesSuppressedType(exceptionTypeName))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        foreach (var fragment in SuppressedMessageFragments)
        {
            if (message.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="exception"/>, or any exception
    /// it wraps, is environmental.
    /// </summary>
    /// <remarks>
    /// The inner chain is walked because ASP.NET Core and EF Core routinely wrap
    /// a cancellation or a SQLite busy error inside a generic outer exception;
    /// judging only the outer type would let all of them through.
    /// </remarks>
    public static bool ShouldSuppress(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (ShouldSuppress(current.GetType().FullName, current.Message))
            {
                return true;
            }

            if (current is AggregateException aggregate
                && aggregate.InnerExceptions.Any(ShouldSuppress))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesSuppressedType(string? exceptionTypeName)
    {
        if (string.IsNullOrWhiteSpace(exceptionTypeName))
        {
            return false;
        }

        var name = exceptionTypeName.Trim();
        if (AlwaysSuppressedTypes.Contains(name))
        {
            return true;
        }

        // Accept a namespace-qualified name by comparing the final segment.
        var lastDot = name.LastIndexOf('.');
        return lastDot >= 0
            && lastDot < name.Length - 1
            && AlwaysSuppressedTypes.Contains(name[(lastDot + 1)..]);
    }
}
