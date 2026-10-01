using System.Text.Json;
using ComicMaintainer.Core.Utilities;
using ComicMaintainer.WebApi.Middleware;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.WebApi.Infrastructure;

/// <summary>
/// The request-timeout policies the API runs under, and the response a timed-out
/// request produces.
/// </summary>
/// <remarks>
/// <para>A timeout used to be the quietest failure in the application. ASP.NET
/// Core handles it itself: it logs <c>"Timeout exception handled."</c> at
/// <c>Warning</c> — with no method, path or correlation id — and writes a bare
/// 504 with an empty body. Automated error reporting only captures
/// <c>Error</c>/<c>Fatal</c>, so nothing was ever fingerprinted, queued or filed,
/// and the UI could only report "HTTP error! status: 504". Work that legitimately
/// runs for minutes (condensing a whole series into one book, then streaming the
/// result) therefore failed silently on both ends.</para>
/// <para><see cref="WriteTimedOutResponseAsync"/> fixes both halves: it logs the
/// timeout at <c>Error</c> — which is what feeds the GitHub issue pipeline — and
/// returns the <c>error</c>/<c>correlationId</c> body the front end already knows
/// how to display.</para>
/// </remarks>
public static class RequestTimeoutPolicies
{
    /// <summary>
    /// Policy for endpoints that do real work before they can answer: planning a
    /// condensed book over a whole series, queueing a bulk send, starting a
    /// build. Minutes rather than seconds, but still bounded — an endpoint that
    /// genuinely has no upper bound (a download, a stream) uses
    /// <see cref="DisableRequestTimeoutAttribute"/> instead.
    /// </summary>
    public const string LongRunning = "LongRunning";

    /// <summary>Applies to every endpoint that does not ask for something else.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Timeout of the <see cref="LongRunning"/> policy.</summary>
    public static readonly TimeSpan LongRunningTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The source context automated reports are filed under, so a timeout is
    /// titled "[field error] Error in RequestTimeoutPolicies" rather than being
    /// attributed to whichever controller happened to be running.
    /// </summary>
    private static readonly string LoggerCategory = typeof(RequestTimeoutPolicies).FullName!;

    public static RequestTimeoutPolicy Create(TimeSpan timeout) => new()
    {
        Timeout = timeout,
        TimeoutStatusCode = StatusCodes.Status504GatewayTimeout,
        WriteTimeoutResponse = WriteTimedOutResponseAsync
    };

    /// <summary>
    /// Records a timed-out request and answers with the same JSON shape every
    /// other failure in this API uses.
    /// </summary>
    /// <remarks>
    /// Logged without an exception on purpose: the only exception available here
    /// is the <see cref="OperationCanceledException"/> the timeout raised, and
    /// <c>ErrorReportingSink</c> deliberately drops cancellations so that work
    /// abandoned at shutdown is not reported as a defect. Attaching it would put
    /// this report straight back into the hole it exists to close.
    /// </remarks>
    public static async Task WriteTimedOutResponseAsync(HttpContext context)
    {
        var correlationId = context.GetCorrelationId();
        var route = DescribeEndpoint(context);
        var timeout = ResolveTimeout(context);

        context.RequestServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger(LoggerCategory)
            // The route is built into the template rather than passed as an
            // argument so that each endpoint fingerprints — and therefore files
            // — separately; a shared template would collapse every timeout in
            // the application into one issue. The route pattern is
            // developer-authored text, so nothing user-supplied reaches it.
            .LogError(
                "Request timed out after {TimeoutSeconds}s on " + EscapeTemplateText(route)
                    + ": {Method} {Path} (correlation id {CorrelationId})",
                timeout.TotalSeconds,
                LoggingHelper.SanitizeForLog(context.Request.Method),
                LoggingHelper.SanitizeForLog(context.Request.Path.Value),
                correlationId);

        context.Response.ContentType = "application/json; charset=utf-8";

        await context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            error = $"The request took longer than {timeout.TotalSeconds:0}s and was cancelled. Quote the reference below when reporting this.",
            correlationId
        }));
    }

    /// <summary>
    /// The matched endpoint's route pattern, which is stable per endpoint and
    /// free of user data. Falls back to a literal when the request never reached
    /// routing, so an arbitrary request path can never become part of a
    /// fingerprint.
    /// </summary>
    private static string DescribeEndpoint(HttpContext context)
        => context.GetEndpoint() is RouteEndpoint endpoint
            ? endpoint.RoutePattern.RawText ?? "(unrouted)"
            : "(unrouted)";

    /// <summary>
    /// Escapes literal text that is being spliced into a message template.
    /// </summary>
    /// <remarks>
    /// Route patterns carry their parameters in braces —
    /// <c>condense-builds/{buildId:guid}/download</c> — and a brace in a message
    /// template is a property hole. Left as-is the logger sees more holes than
    /// it was given arguments for, binding fails and the event is dropped
    /// entirely: the timeout would go unreported again, and only for the routes
    /// with parameters.
    /// </remarks>
    private static string EscapeTemplateText(string text)
        => text.Replace("{", "{{", StringComparison.Ordinal)
               .Replace("}", "}}", StringComparison.Ordinal);

    /// <summary>
    /// The limit this request was actually held to, resolved the same way
    /// <c>RequestTimeoutsMiddleware</c> resolves it. Reported so an issue names
    /// the limit that was hit rather than implying every timeout is the default
    /// one, which would send whoever reads it looking in the wrong place.
    /// </summary>
    private static TimeSpan ResolveTimeout(HttpContext context)
    {
        var options = context.RequestServices
            .GetService<IOptionsMonitor<RequestTimeoutOptions>>()
            ?.CurrentValue;

        var attribute = context.GetEndpoint()?.Metadata.GetMetadata<RequestTimeoutAttribute>();

        if (attribute?.Timeout is { } explicitTimeout)
        {
            return explicitTimeout;
        }

        if (attribute?.PolicyName is { } policyName
            && options is not null
            && options.Policies.TryGetValue(policyName, out var policy)
            && policy.Timeout is { } policyTimeout)
        {
            return policyTimeout;
        }

        return options?.DefaultPolicy?.Timeout ?? DefaultTimeout;
    }
}
