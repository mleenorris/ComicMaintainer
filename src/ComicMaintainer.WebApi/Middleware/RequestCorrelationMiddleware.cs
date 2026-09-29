using Serilog.Context;

namespace ComicMaintainer.WebApi.Middleware;

/// <summary>
/// Gives every request a short, stable correlation id and makes that id visible
/// in all three places it is needed to actually diagnose a problem:
///
/// <list type="bullet">
///   <item>the <c>X-Correlation-Id</c> response header, so the browser (and the
///         UI's error toasts) can quote it back to the user;</item>
///   <item>the Serilog <see cref="LogContext"/>, so every log line written while
///         the request is in flight carries <c>{CorrelationId}</c> and the whole
///         request can be grepped out of debug.log;</item>
///   <item><see cref="HttpContext.Items"/>, so the global exception handler can
///         put the same id into the ProblemDetails body.</item>
/// </list>
///
/// A client-supplied <c>X-Correlation-Id</c> is honoured (so a reverse proxy or
/// the front end can tie a browser action to the server work it caused) but only
/// after sanitising: the value is echoed into a response header and written to
/// log files, so an unvalidated value would allow header and log injection.
/// </summary>
public class RequestCorrelationMiddleware
{
    /// <summary>Header used for both the inbound and the echoed correlation id.</summary>
    public const string HeaderName = "X-Correlation-Id";

    /// <summary><see cref="HttpContext.Items"/> key holding the resolved id.</summary>
    public const string ItemsKey = "CorrelationId";

    /// <summary>
    /// Longest client-supplied id accepted. Long enough for a GUID ("N" format
    /// is 32 chars) or a W3C trace id, short enough that it cannot be used to
    /// bloat every log line.
    /// </summary>
    public const int MaxLength = 64;

    private readonly RequestDelegate _next;

    public RequestCorrelationMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = ResolveCorrelationId(context);

        context.Items[ItemsKey] = correlationId;

        // The header has to be attached before anything downstream starts
        // writing the body, otherwise the response has already been flushed and
        // setting a header throws.
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        using (LogContext.PushProperty(ItemsKey, correlationId))
        {
            await _next(context);
        }
    }

    /// <summary>
    /// Reads the correlation id for the current request: the sanitised
    /// client-supplied header when it is usable, otherwise ASP.NET Core's own
    /// per-request trace identifier.
    /// </summary>
    private static string ResolveCorrelationId(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue(HeaderName, out var supplied))
        {
            var sanitized = Sanitize(supplied.ToString());
            if (!string.IsNullOrEmpty(sanitized))
            {
                return sanitized;
            }
        }

        // TraceIdentifier is always populated by the server and is already the
        // value ASP.NET Core uses for ProblemDetails' traceId, so reusing it
        // keeps the two consistent.
        return Sanitize(context.TraceIdentifier) is { Length: > 0 } id
            ? id
            : Guid.NewGuid().ToString("N");
    }

    /// <summary>
    /// Reduces a candidate id to characters that are safe in an HTTP header and
    /// in a single log line: ASCII letters, digits, '-', '_' and ':'. Anything
    /// else (including CR/LF, which is what makes header/log injection possible)
    /// is dropped rather than replaced, and the result is length-capped.
    /// </summary>
    public static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        Span<char> buffer = stackalloc char[MaxLength];
        var length = 0;

        foreach (var ch in value)
        {
            if (length == MaxLength)
            {
                break;
            }

            var isAllowed = ch is >= 'a' and <= 'z'
                || ch is >= 'A' and <= 'Z'
                || ch is >= '0' and <= '9'
                || ch is '-' or '_' or ':';

            if (isAllowed)
            {
                buffer[length++] = ch;
            }
        }

        return length == 0 ? string.Empty : new string(buffer[..length]);
    }
}

/// <summary>
/// Convenience accessors for the correlation id assigned by
/// <see cref="RequestCorrelationMiddleware"/>.
/// </summary>
public static class CorrelationIdExtensions
{
    /// <summary>
    /// Returns the correlation id for the request, falling back to the server's
    /// trace identifier when the middleware has not run (for example in unit
    /// tests that build a bare <see cref="HttpContext"/>).
    /// </summary>
    public static string GetCorrelationId(this HttpContext context)
    {
        if (context.Items.TryGetValue(RequestCorrelationMiddleware.ItemsKey, out var value)
            && value is string id
            && !string.IsNullOrEmpty(id))
        {
            return id;
        }

        return context.TraceIdentifier;
    }
}
