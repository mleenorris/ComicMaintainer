using System.Threading.RateLimiting;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Client-side throttle for the outbound requests of one external metadata
/// provider, so the application stays under the provider's published quota
/// (MangaDex 5 req/s, AniList 30 req/min, ComicVine 200 req/hour) instead of
/// being throttled or blocked by it.
/// </summary>
/// <remarks>
/// <para>
/// The <em>pace</em> limit is a sliding window whose callers queue: a burst of
/// lookups is spread out instead of failing. A caller never queues longer than
/// the wait it passes to <see cref="AcquireAsync"/>; past that the acquisition
/// fails, so a saturated provider is reported as rate limited rather than
/// holding file processing indefinitely.
/// </para>
/// <para>
/// The optional <em>budget</em> is a second, longer window that never queues
/// (for ComicVine's hourly quota). Once it is used up every acquisition fails
/// at once, with the time until the window frees a permit, so the provider is
/// skipped until then instead of queueing lookups for up to an hour.
/// </para>
/// Each provider service owns a single instance for the lifetime of the process.
/// </remarks>
internal sealed class ProviderRateLimiter : IDisposable
{
    private const int BudgetSegments = 12;

    private readonly SlidingWindowRateLimiter _pace;
    private readonly SlidingWindowRateLimiter? _budget;
    private readonly TimeSpan _budgetSegment;

    /// <param name="name">Name used in diagnostics.</param>
    /// <param name="permitLimit">Requests allowed per <paramref name="window"/>; values below 1 become 1.</param>
    /// <param name="window">Length of the pace window.</param>
    /// <param name="segmentsPerWindow">Granularity of the pace window; values below 1 become 1.</param>
    /// <param name="budgetPermitLimit">Optional requests allowed per <paramref name="budgetWindow"/>, without queueing.</param>
    /// <param name="budgetWindow">Length of the budget window; required with <paramref name="budgetPermitLimit"/>.</param>
    public ProviderRateLimiter(
        string name,
        int permitLimit,
        TimeSpan window,
        int segmentsPerWindow = 4,
        int? budgetPermitLimit = null,
        TimeSpan? budgetWindow = null)
    {
        Name = name;
        // SlidingWindowRateLimiterOptions requires PermitLimit >= 1 and
        // SegmentsPerWindow >= 1. Clamp so misconfiguration produces a working
        // (if slow) limiter rather than throwing at startup.
        _pace = new SlidingWindowRateLimiter(new SlidingWindowRateLimiterOptions
        {
            PermitLimit = Math.Max(1, permitLimit),
            Window = window,
            SegmentsPerWindow = Math.Max(1, segmentsPerWindow),
            // Large queue so concurrent bulk scans wait their turn; the wait
            // itself is bounded per call by AcquireAsync's maxWait.
            QueueLimit = 10_000,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true,
        });

        if (budgetPermitLimit is { } budget && budgetWindow is { } length && length > TimeSpan.Zero)
        {
            _budgetSegment = length / BudgetSegments;
            _budget = new SlidingWindowRateLimiter(new SlidingWindowRateLimiterOptions
            {
                PermitLimit = Math.Max(1, budget),
                Window = length,
                SegmentsPerWindow = BudgetSegments,
                QueueLimit = 0,
                AutoReplenishment = true,
            });
        }
    }

    public string Name { get; }

    /// <summary>
    /// Takes one permit from the budget (when there is one) and then from the
    /// pace limit, waiting at most <paramref name="maxWait"/> in the pace queue.
    /// </summary>
    /// <returns>
    /// <c>null</c> when the request may be sent; otherwise how long the caller
    /// should wait before trying again (the budget is used up, or the pace
    /// queue did not free a permit within <paramref name="maxWait"/>).
    /// </returns>
    /// <exception cref="OperationCanceledException">The caller's token was cancelled.</exception>
    public async ValueTask<TimeSpan?> AcquireAsync(TimeSpan maxWait, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_budget is not null)
        {
            using var budgetLease = _budget.AttemptAcquire(1);
            if (!budgetLease.IsAcquired)
            {
                return budgetLease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter) && retryAfter > TimeSpan.Zero
                    ? retryAfter
                    : _budgetSegment;
            }
        }

        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        waitCts.CancelAfter(maxWait);
        try
        {
            // The permit counts against the window when it is granted; the
            // lease itself holds nothing, so it is released straight away.
            using var lease = await _pace.AcquireAsync(permitCount: 1, waitCts.Token);
            return lease.IsAcquired ? null : maxWait;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return maxWait;
        }
    }

    public void Dispose()
    {
        _pace.Dispose();
        _budget?.Dispose();
    }
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
