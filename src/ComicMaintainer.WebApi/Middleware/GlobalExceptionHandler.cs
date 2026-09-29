using System.Diagnostics;
using ComicMaintainer.Core.ErrorReporting.Models;
using ComicMaintainer.Core.Utilities;
using ComicMaintainer.WebApi.Logging;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace ComicMaintainer.WebApi.Middleware;

/// <summary>
/// Converts unhandled request exceptions into a consistent
/// <see cref="ProblemDetails"/> response and feeds the error reporter.
/// </summary>
/// <remarks>
/// <para>
/// Without this handler an unhandled exception produces either a bare 500 (in
/// production) or a developer exception page whose stack trace and file paths
/// are served to the client. Both are poor outcomes: the first gives the user
/// nothing to report, the second discloses the host's directory layout.
/// </para>
/// <para>
/// The correlation identifier is returned to the client and stored on the
/// report, so a user who says "I got error 4f2a1c" can be matched to the exact
/// captured trace without them having to send any logs.
/// </para>
/// <para>
/// Capture is handed to <see cref="ErrorReportQueue"/> rather than awaited. In
/// automatic mode capture talks to GitHub, and the client's 500 response must
/// not wait on telemetry; the queue is bounded and never blocks.
/// </para>
/// </remarks>
public sealed class GlobalExceptionHandler : IExceptionHandler
{
    private readonly ErrorReportQueue _queue;
    private readonly ILogger<GlobalExceptionHandler> _logger;

    public GlobalExceptionHandler(
        ErrorReportQueue queue,
        ILogger<GlobalExceptionHandler> logger)
    {
        // Capture is queued rather than awaited: in automatic mode it performs
        // a GitHub search and a POST, each with a 30-second timeout, and making
        // the 500 response wait on telemetry turns a handled failure into a
        // minute-long hang.
        _queue = queue;
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        // A cancelled request is the client going away, not a defect.
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            return false;
        }

        var correlationId = Activity.Current?.Id ?? httpContext.TraceIdentifier;
        var origin = DescribeOrigin(httpContext);

        // Both values reach the log from the request: the origin comes from the
        // matched endpoint and the correlation id from a client-influenced
        // trace header, so neither is trusted to be free of newlines or control
        // characters that would forge log entries.
        _logger.LogError(
            exception,
            "Unhandled exception while handling {Origin} (correlation {CorrelationId})",
            LoggingHelper.SanitizeForLog(origin),
            LoggingHelper.SanitizeForLog(correlationId));

        _queue.TryEnqueue(new PendingErrorCapture(
            ExceptionType: exception.GetType().FullName ?? exception.GetType().Name,
            Message: exception.Message,
            StackTrace: exception.ToString(),
            Source: ErrorReportSource.Api,
            Origin: origin,
            CorrelationId: correlationId,
            Exception: exception));

        if (httpContext.Response.HasStarted)
        {
            return false;
        }

        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;

        await httpContext.Response.WriteAsJsonAsync(
            new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "An unexpected error occurred.",
                // Deliberately generic: the exception message can contain file
                // paths and library data, and the client is not always the
                // instance owner.
                Detail = "The request could not be completed. If this keeps happening, "
                         + "report it from Settings and quote the correlation id.",
                Instance = origin,
                Extensions = { ["correlationId"] = correlationId },
            },
            cancellationToken);

        return true;
    }

    /// <summary>
    /// Describes the endpoint using its route <em>template</em>.
    /// </summary>
    /// <remarks>
    /// The resolved path is not used: routes such as
    /// <c>/api/comicreader/page/{filePath}</c> would otherwise carry the user's
    /// library path into the report and, from there, into a public issue.
    /// </remarks>
    private static string DescribeOrigin(HttpContext httpContext)
    {
        var endpoint = httpContext.GetEndpoint();
        var template = (endpoint as RouteEndpoint)?.RoutePattern.RawText;

        return template is null
            ? httpContext.Request.Method
            : $"{httpContext.Request.Method} /{template.TrimStart('/')}";
    }
}
