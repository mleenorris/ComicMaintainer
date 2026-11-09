using ComicMaintainer.Core.Utilities;
using ComicMaintainer.WebApi.Middleware;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace ComicMaintainer.WebApi.Infrastructure;

/// <summary>
/// Last-resort handler for exceptions that escape a controller.
///
/// Before this existed an unhandled exception produced an empty 500 with no
/// body, which meant the UI could only say "HTTP error! status: 500" and there
/// was nothing tying that failure to a line in the log. Every unhandled failure
/// now becomes an RFC 7807 <see cref="ProblemDetails"/> response carrying the
/// request's correlation id, and is logged once with the same id — so a user can
/// read a reference off the screen and an operator can find the stack trace by
/// grepping for it.
///
/// The exception message itself is deliberately not returned outside
/// Development: it can contain file-system paths, connection strings and SQL.
/// An action says what failed with <see cref="FailureMessageAttribute"/>; this
/// handler, not a catch block in the action, then produces the 500.
/// </summary>
public sealed class GlobalExceptionHandler : IExceptionHandler
{
    private const string GenericDetail = "The request could not be completed. Quote the reference below when reporting this.";

    private readonly IProblemDetailsService _problemDetailsService;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<GlobalExceptionHandler> _logger;

    public GlobalExceptionHandler(
        IProblemDetailsService problemDetailsService,
        IHostEnvironment environment,
        ILogger<GlobalExceptionHandler> logger)
    {
        _problemDetailsService = problemDetailsService;
        _environment = environment;
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        // A cancelled request is the client going away (navigation, reader
        // page turn, aborted upload). It is not a server fault, there is no
        // response to write, and logging it as an error is pure noise. Only the
        // request's own token counts: a timeout the server imposed on some inner
        // operation is a real failure, even though it is also a cancellation.
        // UseExceptionHandler already stops a cancellation or an IOException on
        // an aborted request before any handler runs, a dropped connection among
        // them, so this guards only a direct call.
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            _logger.LogDebug(
                "Request {Method} {Path} was aborted by the client",
                LoggingHelper.SanitizeForLog(httpContext.Request.Method),
                LoggingHelper.SanitizeForLog(httpContext.Request.Path.Value));
            return true;
        }

        var correlationId = httpContext.GetCorrelationId();

        LogUnhandled(httpContext, exception, correlationId);

        // If the response has already begun there is no way to replace it with
        // a problem document; let the server tear the connection down instead
        // of throwing a second exception inside the handler.
        if (httpContext.Response.HasStarted)
        {
            return false;
        }

        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;

        var problemDetails = new ProblemDetails
        {
            Status = StatusCodes.Status500InternalServerError,
            Title = "An unexpected error occurred.",
            Detail = _environment.IsDevelopment()
                ? exception.ToString()
                : FailureMessageOf(httpContext) ?? GenericDetail,
            Type = "https://datatracker.ietf.org/doc/html/rfc9110#section-15.6.1",
            Instance = httpContext.Request.Path
        };

        problemDetails.Extensions["correlationId"] = correlationId;

        // The UI reads `error` for every other failure response in this API, so
        // populate it too rather than making the front end special-case 500s.
        problemDetails.Extensions["error"] = problemDetails.Detail;

        return await _problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problemDetails,
            Exception = exception
        });
    }

    /// <summary>
    /// The failing action's <see cref="FailureMessageAttribute"/> text, if it has one. The
    /// exception handling middleware has cleared the request's endpoint by the time a handler
    /// runs, so it is read from the feature that records where the exception happened.
    /// </summary>
    private static string? FailureMessageOf(HttpContext httpContext) =>
        FailedEndpointOf(httpContext)?.Metadata.GetMetadata<FailureMessageAttribute>()?.Message;

    private static Endpoint? FailedEndpointOf(HttpContext httpContext) =>
        httpContext.Features.Get<IExceptionHandlerFeature>()?.Endpoint;

    private void LogUnhandled(HttpContext httpContext, Exception exception, string correlationId)
    {
        // The route is built into the template, as RequestTimeoutPolicies does, so that each
        // endpoint fingerprints and therefore files separately. A locked database, say, has only
        // framework frames at the top of its stack, so a single shared template would fold that
        // failure on every endpoint into one issue. The pattern is developer-authored text.
        var route = RequestTimeoutPolicies.EscapeTemplateText(
            RequestTimeoutPolicies.DescribeEndpoint(FailedEndpointOf(httpContext)));
        var method = LoggingHelper.SanitizeForLog(httpContext.Request.Method);
        var path = LoggingHelper.SanitizeForLog(httpContext.Request.Path.Value);

        // Most single-file endpoints name the file in the query string rather than the path, so
        // the path alone would not say which file failed. Only the file name is logged, matching
        // every other log line in the application.
        if (httpContext.Request.Query.TryGetValue("filePath", out var filePath) && !string.IsNullOrEmpty(filePath))
        {
            _logger.LogError(
                exception,
                "Unhandled exception on " + route + ": {Method} {Path} on {FilePath} (correlation id {CorrelationId})",
                method,
                path,
                LoggingHelper.SanitizePathForLog(filePath.ToString()),
                correlationId);
            return;
        }

        _logger.LogError(
            exception,
            "Unhandled exception on " + route + ": {Method} {Path} (correlation id {CorrelationId})",
            method,
            path,
            correlationId);
    }
}
