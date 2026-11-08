using System.Globalization;
using System.Threading.RateLimiting;
using ComicMaintainer.WebApi.Authentication;
using Microsoft.AspNetCore.RateLimiting;

namespace ComicMaintainer.WebApi.Infrastructure;

/// <summary>
/// Answers a request the rate limiter refused: a <c>Retry-After</c> header and the <c>{ "error": ... }</c> body the web
/// UI shows, because an empty 429 reads as a generic failure and the user cannot tell that they only have to wait.
/// </summary>
/// <remarks>
/// A proxy that is not in <c>TRUSTED_PROXIES</c> makes every client behind it share one rate-limit partition, so one
/// client can use the limit up for all of them. The first rejection of such a request is logged, because that is when
/// an operator needs to know, and nothing at startup can tell that the app is behind a proxy.
/// </remarks>
public sealed class RateLimitRejection
{
    private const string ForwardedForHeader = "X-Forwarded-For";

    private readonly TrustedProxyAllowlist _trustedProxies;
    private int _untrustedProxyReported;

    public RateLimitRejection(TrustedProxyAllowlist trustedProxies)
    {
        _trustedProxies = trustedProxies;
    }

    /// <summary>For <see cref="RateLimiterOptions.OnRejected"/>. The rate limiter has already set the status code.</summary>
    public async ValueTask WriteAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        var httpContext = context.HttpContext;
        ReportAnUntrustedProxyOnce(httpContext);

        int? retryAfterSeconds = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
            ? (int)Math.Ceiling(retryAfter.TotalSeconds)
            : null;
        if (retryAfterSeconds is { } seconds)
        {
            httpContext.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        }

        await httpContext.Response.WriteAsJsonAsync(new { error = DescribeWait(retryAfterSeconds) }, cancellationToken);
    }

    /// <summary>The message for a client that has to wait <paramref name="seconds"/> before trying again, when known.</summary>
    public static string DescribeWait(int? seconds)
    {
        if (seconds is not { } wait)
        {
            return "Too many requests. Please try again shortly.";
        }

        if (wait < 60)
        {
            return $"Too many requests. Please try again in {wait} second{(wait == 1 ? string.Empty : "s")}.";
        }

        var minutes = (int)Math.Ceiling(wait / 60d);
        return $"Too many requests. Please try again in {minutes} minute{(minutes == 1 ? string.Empty : "s")}.";
    }

    private void ReportAnUntrustedProxyOnce(HttpContext context)
    {
        var peer = context.Connection.RemoteIpAddress;
        if (peer is null
            || !context.Request.Headers.ContainsKey(ForwardedForHeader)
            || _trustedProxies.Contains(peer)
            || Interlocked.Exchange(ref _untrustedProxyReported, 1) != 0)
        {
            return;
        }

        context.RequestServices.GetRequiredService<ILogger<RateLimitRejection>>().LogWarning(
            "A rate limit was reached by requests relayed through {Proxy}, which TRUSTED_PROXIES does not list. Every " +
            "client behind that proxy shares one limit, so one client can use it up for all of them. Add the proxy to " +
            "TRUSTED_PROXIES to count clients separately; see docs/REVERSE_PROXY.md.",
            TrustedProxyAllowlist.Normalize(peer));
    }
}
