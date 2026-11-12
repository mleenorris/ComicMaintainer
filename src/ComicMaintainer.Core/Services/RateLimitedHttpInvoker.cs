using System.Net;
using ComicMaintainer.Core.Models;
using Microsoft.Extensions.Logging;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Shared helper that wraps outbound provider HTTP calls with three safeguards:
/// 1. Client-side throttling via <see cref="ProviderRateLimiter"/> so we stay
///    under published provider rate limits before they throttle us. The wait
///    for a permit covers the limiter's window
///    (<see cref="ProviderRateLimiter.MaxPermitWait"/>) unless the caller
///    allows less.
/// 2. A timeout per attempt, so a provider that accepts the connection but
///    never answers fails fast instead of holding the caller for the
///    <see cref="HttpClient"/> default of 100 seconds.
/// 3. Server-side back-off on throttling responses (HTTP 429, or the
///    provider's own status such as ComicVine's 420) by honoring the
///    <c>Retry-After</c> header (RFC 7231) plus the MangaDex-specific
///    <c>X-RateLimit-Retry-After</c> unix-timestamp header, retrying the
///    request once when the indicated delay is short
///    (<see cref="MaxInlineRetryAfter"/> or less).
/// </summary>
/// <remarks>
/// A spent request budget, a longer server-requested delay and an attempt
/// that times out are thrown as <see cref="ExternalMetadataUnavailableException"/>,
/// so callers can tell a provider failure from an answer. A permit that was
/// not granted in time is thrown with
/// <see cref="ExternalMetadataFailureKind.Paced"/>: nothing was sent, so it is
/// not held against the provider. Other transport errors propagate as is. Only
/// the caller's own cancellation surfaces as <see cref="OperationCanceledException"/>.
/// </remarks>
internal static class RateLimitedHttpInvoker
{
    /// <summary>
    /// Longest server-requested back-off waited out before the single retry.
    /// A longer one fails the request at once as rate limited, carrying the
    /// delay, so the caller can skip the provider until then.
    /// </summary>
    internal static readonly TimeSpan MaxInlineRetryAfter = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Longest wait for a client-side permit when a user is waiting for the
    /// answer (the manual-match and cover pickers), so the wait fits inside
    /// the request timeout along with the request itself.
    /// </summary>
    internal static readonly TimeSpan InteractivePermitWait = TimeSpan.FromSeconds(8);

    /// <summary>Floor applied to a 429 back-off so we don't hot-loop.</summary>
    private static readonly TimeSpan MinRetryAfter = TimeSpan.FromMilliseconds(250);

    /// <summary>Used when the server throttles with no usable header.</summary>
    private static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Acquires a permit from <paramref name="limiter"/>, sends the request
    /// with a timeout of <paramref name="attemptTimeout"/>, and retries once
    /// on a throttling response after waiting for the server-indicated
    /// <c>Retry-After</c> interval. The <paramref name="requestFactory"/> is
    /// invoked once per attempt because <see cref="HttpRequestMessage"/> may
    /// not be reused. The response of the last attempt is returned whatever
    /// its status code; the caller interprets it.
    /// </summary>
    /// <param name="isRateLimitStatus">
    /// Recognizes the provider's throttling statuses; defaults to HTTP 429.
    /// </param>
    /// <param name="maxPermitWait">
    /// Longest wait for a client-side permit; defaults to
    /// <see cref="ProviderRateLimiter.MaxPermitWait"/>.
    /// </param>
    /// <exception cref="ExternalMetadataUnavailableException">
    /// No permit was granted in time, the request budget is used up, the
    /// server asked for a longer back-off, or an attempt timed out.
    /// </exception>
    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        Func<HttpRequestMessage> requestFactory,
        ProviderRateLimiter limiter,
        ProviderHealthTracker health,
        ILogger logger,
        string providerLabel,
        HttpCompletionOption completionOption,
        TimeSpan attemptTimeout,
        CancellationToken cancellationToken,
        Func<HttpStatusCode, bool>? isRateLimitStatus = null,
        TimeSpan? maxPermitWait = null)
    {
        isRateLimitStatus ??= static status => status == HttpStatusCode.TooManyRequests;
        var permitWait = maxPermitWait ?? limiter.MaxPermitWait;
        for (var attempt = 0; ; attempt++)
        {
            var acquireStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            var permit = await limiter.AcquireAsync(permitWait, cancellationToken);
            health.RecordClientThrottleWait(System.Diagnostics.Stopwatch.GetElapsedTime(acquireStarted));
            if (permit.Status == ProviderPermitStatus.BudgetSpent)
            {
                throw new ExternalMetadataUnavailableException(
                    providerLabel,
                    ExternalMetadataFailureKind.RateLimited,
                    $"{providerLabel} client-side request budget is used up; retry in {permit.RetryAfter.TotalSeconds:F0}s",
                    permit.RetryAfter);
            }

            if (!permit.IsGranted)
            {
                throw new ExternalMetadataUnavailableException(
                    providerLabel,
                    ExternalMetadataFailureKind.Paced,
                    $"{providerLabel} is at its request pace limit (no slot within {permitWait.TotalSeconds:F0}s); try again shortly");
            }

            HttpResponseMessage response;
            using (var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                attemptCts.CancelAfter(attemptTimeout);
                using var request = requestFactory();
                try
                {
                    response = await client.SendAsync(request, completionOption, attemptCts.Token);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Report the caller's own token rather than the linked one.
                    cancellationToken.ThrowIfCancellationRequested();
                    throw;
                }
                catch (OperationCanceledException ex)
                {
                    // Our attempt timeout, or HttpClient.Timeout; not the caller.
                    throw new ExternalMetadataUnavailableException(
                        providerLabel,
                        ExternalMetadataFailureKind.Timeout,
                        $"{providerLabel} did not answer within {attemptTimeout.TotalSeconds:F0}s",
                        innerException: ex);
                }
            }

            if (!isRateLimitStatus(response.StatusCode) || attempt == 1)
            {
                return response;
            }

            var retryAfter = GetRetryAfter(response) ?? DefaultRetryAfter;
            if (retryAfter < MinRetryAfter) retryAfter = MinRetryAfter;
            if (retryAfter > MaxInlineRetryAfter)
            {
                // Too long to hold file processing for: report the throttle
                // (with its delay) so the caller can skip the provider.
                response.Dispose();
                throw new ExternalMetadataUnavailableException(
                    providerLabel,
                    ExternalMetadataFailureKind.RateLimited,
                    $"{providerLabel} rate limited the request (HTTP {(int)response.StatusCode}); retry in {retryAfter.TotalSeconds:F0}s",
                    retryAfter);
            }

            health.RecordRateLimited(retryAfter, $"HTTP {(int)response.StatusCode} from {providerLabel}");
            logger.LogWarning(
                "{Provider} rate limit hit (HTTP {StatusCode}); backing off for {RetryAfterSeconds:F1}s before retry",
                providerLabel,
                (int)response.StatusCode,
                retryAfter.TotalSeconds);

            response.Dispose();
            await Task.Delay(retryAfter, cancellationToken);
        }
    }

    /// <summary>
    /// Extracts a back-off interval from a throttling response. Supports the
    /// standard <c>Retry-After</c> (delta-seconds or HTTP-date) and the
    /// MangaDex <c>X-RateLimit-Retry-After</c> header (unix timestamp in seconds).
    /// Returns null when the response names no future delay.
    /// </summary>
    internal static TimeSpan? GetRetryAfter(HttpResponseMessage response)
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
            var delay = values
                .Select(raw => long.TryParse(raw, out var unixSeconds)
                    ? DateTimeOffset.FromUnixTimeSeconds(unixSeconds) - DateTimeOffset.UtcNow
                    : TimeSpan.Zero)
                .FirstOrDefault(diff => diff > TimeSpan.Zero);
            if (delay > TimeSpan.Zero)
            {
                return delay;
            }
        }

        return null;
    }
}
