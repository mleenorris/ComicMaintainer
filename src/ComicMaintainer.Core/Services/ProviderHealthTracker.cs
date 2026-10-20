using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Telemetry;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Thread-safe rolling counters used by <see cref="Interfaces.IExternalSeriesMetadataService"/>
/// implementations to expose runtime health to the UI.
/// </summary>
internal sealed class ProviderHealthTracker
{
    private readonly object _gate = new();
    private readonly string _name;
    private long _successCount;
    private long _failureCount;
    private string? _lastError;
    private DateTime? _lastSuccessUtc;
    private DateTime? _lastFailureUtc;
    private DateTime? _rateLimitedUntilUtc;
    private readonly ComicMaintainerMetrics? _metrics;

    public ProviderHealthTracker(string name, ComicMaintainerMetrics? metrics = null)
    {
        _name = name;
        _metrics = metrics;
    }

    public void RecordSuccess()
    {
        _metrics?.RecordProviderRequest(_name, success: true);
        lock (_gate)
        {
            _successCount++;
            _lastSuccessUtc = DateTime.UtcNow;
        }
    }

    public void RecordFailure(string? error)
    {
        _metrics?.RecordProviderRequest(_name, success: false);
        lock (_gate)
        {
            _failureCount++;
            _lastFailureUtc = DateTime.UtcNow;
            _lastError = string.IsNullOrWhiteSpace(error) ? _lastError : error;
        }
    }

    /// <summary>
    /// Records that the provider rejected a request with HTTP 429 (or that the
    /// client-side limiter is throttling). <paramref name="retryAfter"/> is the
    /// duration the caller intends to back off; the tracker remembers the
    /// resulting wall-clock deadline so <see cref="Snapshot"/> can surface a
    /// "Degraded - rate limited" indicator until it passes.
    /// </summary>
    public void RecordRateLimited(TimeSpan retryAfter, string? error)
    {
        _metrics?.RecordProviderRateLimitWait(_name, "server", retryAfter);
        lock (_gate)
        {
            _failureCount++;
            _lastFailureUtc = DateTime.UtcNow;
            _lastError = string.IsNullOrWhiteSpace(error) ? _lastError : error;
            // Clamp retry-after to a sane window so a misbehaving server
            // can't pin the indicator on indefinitely.
            var clamped = retryAfter;
            if (clamped < TimeSpan.Zero) clamped = TimeSpan.Zero;
            if (clamped > TimeSpan.FromMinutes(10)) clamped = TimeSpan.FromMinutes(10);
            var deadline = DateTime.UtcNow + clamped;
            // Extend (don't shorten) any existing window so back-to-back 429s
            // don't reset to a smaller value.
            if (!_rateLimitedUntilUtc.HasValue || deadline > _rateLimitedUntilUtc.Value)
            {
                _rateLimitedUntilUtc = deadline;
            }
        }
    }

    /// <summary>
    /// Records time a request spent queued behind the client-side
    /// <see cref="ProviderRateLimiter"/>. Only surfaced as a metric; it is not a
    /// failure and does not affect the health snapshot.
    /// </summary>
    public void RecordClientThrottleWait(TimeSpan waited)
    {
        if (waited >= ComicMaintainerMetrics.RateLimitWaitThreshold)
        {
            _metrics?.RecordProviderRateLimitWait(_name, "client", waited);
        }
    }

    /// <summary>
    /// Returns a populated <see cref="ProviderHealth"/> with the rolling
    /// counters captured atomically. Caller is expected to fill in
    /// <c>Enabled</c>, <c>Configured</c>, <c>Reachable</c>, and
    /// <c>StatusMessage</c> with provider-specific information.
    /// </summary>
    public ProviderHealth Snapshot()
    {
        lock (_gate)
        {
            var rateLimited = _rateLimitedUntilUtc.HasValue && _rateLimitedUntilUtc.Value > DateTime.UtcNow;
            return new ProviderHealth
            {
                Name = _name,
                SuccessCount = _successCount,
                FailureCount = _failureCount,
                LastError = _lastError,
                LastSuccessUtc = _lastSuccessUtc,
                LastFailureUtc = _lastFailureUtc,
                RateLimited = rateLimited,
                RateLimitedUntilUtc = rateLimited ? _rateLimitedUntilUtc : null,
            };
        }
    }
}
