namespace ComicMaintainer.Core.ErrorReporting.Models;

/// <summary>
/// Where a captured error originated. Determines how much the report can be
/// trusted and which guardrails apply on the GitHub side.
/// </summary>
public enum ErrorReportSource
{
    /// <summary>An unhandled exception escaping an HTTP request.</summary>
    Api = 0,

    /// <summary>An error raised by a hosted/background service.</summary>
    Background = 1,

    /// <summary>An <c>Error</c>/<c>Fatal</c> log event observed by the Serilog sink.</summary>
    Log = 2,

    /// <summary>
    /// A browser error posted by the web UI. Field-supplied and therefore the
    /// least trusted source.
    /// </summary>
    Frontend = 3,
}
