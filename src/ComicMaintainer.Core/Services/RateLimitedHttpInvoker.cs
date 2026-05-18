using System.Net;
using Microsoft.Extensions.Logging;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Shared helper that wraps outbound provider HTTP calls with two safeguards:
/// 1. Client-side throttling via <see cref="ProviderRateLimiter"/> so we stay
///    under published provider rate limits (MangaDex 5 req/s, AniList
///    30 req/min) before they 429 us.
/// 2. Server-side back-off on HTTP 429 responses by honoring the
///    <c>Retry-After</c> header (RFC 7231) plus the MangaDex-specific
///    <c>X-RateLimit-Retry-After</c> unix-timestamp header, retrying the
///    request once after the indicated delay.
/// </summary>
internal static class RateLimitedHttpInvoker
{
    /// <summary>Maximum 429 back-off we will block on before giving up.</summary>
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromMinutes(2);
    /// <summary>Floor applied to a 429 back-off so we don't hot-loop.</summary>
    private static readonly TimeSpan MinRetryAfter = TimeSpan.FromMilliseconds(250);
    /// <summary>Used when the server returns 429 with no usable header.</summary>
    private static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Acquires a slot from <paramref name="limiter"/>, sends the request, and
    /// retries once on HTTP 429 after waiting for the server-indicated
    /// <c>Retry-After</c> interval. The <paramref name="requestFactory"/> is
    /// invoked once per attempt because <see cref="HttpRequestMessage"/> may
    /// not be reused.
    /// </summary>
    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        Func<HttpRequestMessage> requestFactory,
        ProviderRateLimiter limiter,
        ProviderHealthTracker health,
        ILogger logger,
        string providerLabel,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage? response = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using (await limiter.AcquireAsync(cancellationToken))
            {
                // Lease is released as soon as we exit this scope; the actual
                // HTTP call below is not counted against the rate limit again,
                // matching the typical "one permit per outbound request" model.
            }

            var request = requestFactory();
            response = await client.SendAsync(request, completionOption, cancellationToken);
            if ((int)response.StatusCode != (int)HttpStatusCode.TooManyRequests || attempt == 1)
            {
                return response;
            }

            var retryAfter = ParseRetryAfter(response) ?? DefaultRetryAfter;
            if (retryAfter > MaxRetryAfter) retryAfter = MaxRetryAfter;
            if (retryAfter < MinRetryAfter) retryAfter = MinRetryAfter;

            health.RecordRateLimited(retryAfter, $"HTTP 429 from {providerLabel}");
            logger.LogWarning(
                "{Provider} rate limit hit (HTTP 429); backing off for {RetryAfterSeconds:F1}s before retry",
                providerLabel,
                retryAfter.TotalSeconds);

            response.Dispose();
            response = null;

            await Task.Delay(retryAfter, cancellationToken);
        }

        // Defensive: loop always returns or throws.
        return response ?? throw new InvalidOperationException("RateLimitedHttpInvoker exited without a response.");
    }

    /// <summary>
    /// Extracts a back-off interval from a 429 response. Supports the standard
    /// <c>Retry-After</c> (delta-seconds or HTTP-date) and the MangaDex
    /// <c>X-RateLimit-Retry-After</c> header (unix timestamp in seconds).
    /// </summary>
    private static TimeSpan? ParseRetryAfter(HttpResponseMessage response)
    {
        var ra = response.Headers.RetryAfter;
        if (ra is not null)
        {
            if (ra.Delta is { } delta)
            {
                return delta;
            }
            if (ra.Date is { } date)
            {
                var diff = date - DateTimeOffset.UtcNow;
                if (diff > TimeSpan.Zero)
                {
                    return diff;
                }
            }
        }

        if (response.Headers.TryGetValues("X-RateLimit-Retry-After", out var values))
        {
            foreach (var raw in values)
            {
                if (long.TryParse(raw, out var unixSeconds))
                {
                    var diff = DateTimeOffset.FromUnixTimeSeconds(unixSeconds) - DateTimeOffset.UtcNow;
                    if (diff > TimeSpan.Zero)
                    {
                        return diff;
                    }
                }
            }
        }

        return null;
    }
}
