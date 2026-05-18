using System.Threading.RateLimiting;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Thin wrapper around <see cref="SlidingWindowRateLimiter"/> used by the
/// external metadata provider services to throttle outbound HTTP requests so
/// the application does not exceed published provider quotas (e.g. MangaDex
/// 5 req/s, AniList 30 req/min).
///
/// Each provider service owns a single instance for the lifetime of the
/// process. Acquisition is awaited per request and will transparently queue
/// (and back-pressure) callers if the configured rate is exceeded.
/// </summary>
internal sealed class ProviderRateLimiter : IDisposable
{
    private readonly SlidingWindowRateLimiter _limiter;
    private readonly string _name;

    public ProviderRateLimiter(string name, int permitLimit, TimeSpan window, int segmentsPerWindow = 4)
    {
        _name = name;
        // Defensive: SlidingWindowRateLimiterOptions requires PermitLimit >= 1
        // and SegmentsPerWindow >= 1. Clamp here so misconfiguration produces
        // a working (if slow) limiter rather than throwing at startup.
        var limit = permitLimit < 1 ? 1 : permitLimit;
        var segments = segmentsPerWindow < 1 ? 1 : segmentsPerWindow;
        _limiter = new SlidingWindowRateLimiter(new SlidingWindowRateLimiterOptions
        {
            PermitLimit = limit,
            Window = window,
            SegmentsPerWindow = segments,
            // Large queue so concurrent bulk scans block rather than fail.
            QueueLimit = 10_000,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true,
        });
    }

    public string Name => _name;

    /// <summary>
    /// Acquires one permit, awaiting until the limiter has capacity. Returns
    /// the lease so the caller can dispose it; callers should always use a
    /// <c>using</c> statement.
    /// </summary>
    public ValueTask<RateLimitLease> AcquireAsync(CancellationToken cancellationToken)
        => _limiter.AcquireAsync(permitCount: 1, cancellationToken);

    public void Dispose() => _limiter.Dispose();
}

/// <summary>
/// Process-wide holder for rate limiters that must be shared across multiple
/// services hitting the same provider endpoint. AniList's per-IP rate limit
/// covers both manga and manhwa queries against <c>graphql.anilist.co</c>, so
/// the two AniList services must share a single bucket to avoid double-counting
/// the budget client-side.
/// </summary>
internal static class SharedRateLimiters
{
    private static ProviderRateLimiter? _aniList;
    private static readonly object _aniListGate = new();

    public static ProviderRateLimiter GetOrCreateAniList(int requestsPerMinute)
    {
        if (_aniList is not null)
        {
            return _aniList;
        }
        lock (_aniListGate)
        {
            return _aniList ??= new ProviderRateLimiter(
                "AniList",
                Math.Max(1, requestsPerMinute),
                TimeSpan.FromMinutes(1),
                segmentsPerWindow: 12);
        }
    }
}
