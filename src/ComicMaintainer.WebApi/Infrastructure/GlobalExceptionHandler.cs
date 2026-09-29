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
/// </summary>
public sealed class GlobalExceptionHandler : IExceptionHandler
{
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
        // response to write, and logging it as an error is pure noise.
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            _logger.LogDebug(
                "Request {Method} {Path} was aborted by the client",
                LoggingHelper.SanitizeForLog(httpContext.Request.Method),
                LoggingHelper.SanitizeForLog(httpContext.Request.Path.Value));
            return true;
        }

        var correlationId = httpContext.GetCorrelationId();

        _logger.LogError(
            exception,
            "Unhandled exception for {Method} {Path} (correlation id {CorrelationId})",
            LoggingHelper.SanitizeForLog(httpContext.Request.Method),
            LoggingHelper.SanitizeForLog(httpContext.Request.Path.Value),
            correlationId);

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
                : "The request could not be completed. Quote the reference below when reporting this.",
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
}
