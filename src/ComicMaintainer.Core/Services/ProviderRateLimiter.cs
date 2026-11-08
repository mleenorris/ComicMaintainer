using System.Threading.RateLimiting;

namespace ComicMaintainer.Core.Services;

/// <summary>What became of a request for a <see cref="ProviderRateLimiter"/> permit.</summary>
internal enum ProviderPermitStatus
{
    /// <summary>The request may be sent.</summary>
    Granted,

    /// <summary>The pace limit had no room within the time the caller could wait.</summary>
    PaceWaitExpired,

    /// <summary>The longer-window budget is used up.</summary>
    BudgetSpent
}

/// <summary>Result of <see cref="ProviderRateLimiter.AcquireAsync"/>.</summary>
/// <param name="Status">Whether the request may be sent, and if not, why.</param>
/// <param name="RetryAfter">
/// For <see cref="ProviderPermitStatus.BudgetSpent"/>, how long until the
/// budget frees a permit; otherwise zero.
/// </param>
internal readonly record struct ProviderPermit(ProviderPermitStatus Status, TimeSpan RetryAfter)
{
    public static ProviderPermit Granted => new(ProviderPermitStatus.Granted, TimeSpan.Zero);

    public static ProviderPermit PaceWaitExpired => new(ProviderPermitStatus.PaceWaitExpired, TimeSpan.Zero);

    public bool IsGranted => Status == ProviderPermitStatus.Granted;
}

/// <summary>
/// Client-side throttle for the outbound requests of one external metadata
/// provider, so the application stays under the provider's published quota
/// (MangaDex 5 req/s, AniList 30 req/min, ComicVine 200 req/hour) instead of
/// being throttled or blocked by it.
/// </summary>
/// <remarks>
/// <para>
/// The <em>pace</em> limit is a sliding window whose callers queue: a burst of
/// lookups is spread out instead of failing. A lookup can wait
/// <see cref="MaxPermitWait"/>, which always covers a full window, so a window
/// used up by earlier lookups delays the next one rather than failing it. A
/// caller that cannot wait that long passes a shorter limit. Running out of
/// that wait is our own pacing, not a provider problem, and is reported as
/// <see cref="ProviderPermitStatus.PaceWaitExpired"/>.
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

    /// <summary>Least time a lookup can wait, for limiters with a window of a few seconds.</summary>
    private static readonly TimeSpan MinPermitWait = TimeSpan.FromSeconds(20);

    private readonly TimeProvider _timeProvider;
    private readonly SlidingWindowRateLimiter _pace;
    private readonly SlidingWindowRateLimiter? _budget;
    private readonly TimeSpan _budgetSegment;

    /// <param name="name">Name used in diagnostics.</param>
    /// <param name="permitLimit">Requests allowed per <paramref name="window"/>; values below 1 become 1.</param>
    /// <param name="window">Length of the pace window.</param>
    /// <param name="segmentsPerWindow">Granularity of the pace window; values below 1 become 1.</param>
    /// <param name="budgetPermitLimit">Optional requests allowed per <paramref name="budgetWindow"/>, without queueing.</param>
    /// <param name="budgetWindow">Length of the budget window; required with <paramref name="budgetPermitLimit"/>.</param>
    /// <param name="timeProvider">
    /// Clock that times the wait for a permit; the system clock when null. The
    /// windows themselves always run on real time.
    /// </param>
    public ProviderRateLimiter(
        string name,
        int permitLimit,
        TimeSpan window,
        int segmentsPerWindow = 4,
        int? budgetPermitLimit = null,
        TimeSpan? budgetWindow = null,
        TimeProvider? timeProvider = null)
    {
        Name = name;
        _timeProvider = timeProvider ?? TimeProvider.System;

        // SlidingWindowRateLimiterOptions requires PermitLimit >= 1 and
        // SegmentsPerWindow >= 1. Clamp so misconfiguration produces a working
        // (if slow) limiter rather than throwing at startup.
        var segments = Math.Max(1, segmentsPerWindow);
        _pace = new SlidingWindowRateLimiter(new SlidingWindowRateLimiterOptions
        {
            PermitLimit = Math.Max(1, permitLimit),
            Window = window,
            SegmentsPerWindow = segments,
            // Large queue so concurrent bulk scans wait their turn; the wait
            // itself is bounded per call by AcquireAsync's maxWait.
            QueueLimit = 10_000,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true,
        });

        // A permit used at the start of the window comes back one window
        // later, at the first replenishment after that.
        var fullWindow = window + (window / segments);
        MaxPermitWait = fullWindow > MinPermitWait ? fullWindow : MinPermitWait;

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
    /// How long a lookup waits for a pace permit before giving up: at least one
    /// full window, so a lookup is never failed by requests that are merely
    /// using up the current window.
    /// </summary>
    public TimeSpan MaxPermitWait { get; }

    /// <summary>Permits currently waiting for room in the pace window (diagnostics and tests).</summary>
    internal long QueuedPermits => _pace.GetStatistics()?.CurrentQueuedCount ?? 0;

    /// <summary>Permits left in the budget; null without a budget (diagnostics and tests).</summary>
    internal long? AvailableBudgetPermits => _budget?.GetStatistics()?.CurrentAvailablePermits;

    /// <summary>
    /// Waits at most <paramref name="maxWait"/> for a pace permit and then
    /// takes one from the budget (when there is one). A budget permit is only
    /// spent once the request is about to go out; a budget that is already
    /// used up fails at once, without waiting for the pace limit.
    /// </summary>
    /// <exception cref="OperationCanceledException">The caller's token was cancelled.</exception>
    public async ValueTask<ProviderPermit> AcquireAsync(TimeSpan maxWait, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_budget is not null)
        {
            // Zero permits only asks whether any are left.
            using var available = _budget.AttemptAcquire(0);
            if (!available.IsAcquired)
            {
                return BudgetSpent(available);
            }
        }

        using var waitLimit = new CancellationTokenSource(maxWait, _timeProvider);
        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, waitLimit.Token);
        try
        {
            // The permit counts against the window when it is granted; the
            // lease itself holds nothing, so it is released straight away.
            using var lease = await _pace.AcquireAsync(permitCount: 1, waitCts.Token);
            if (!lease.IsAcquired)
            {
                return ProviderPermit.PaceWaitExpired;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ProviderPermit.PaceWaitExpired;
        }

        if (_budget is not null)
        {
            using var budgetLease = _budget.AttemptAcquire(1);
            if (!budgetLease.IsAcquired)
            {
                return BudgetSpent(budgetLease);
            }
        }

        return ProviderPermit.Granted;
    }

    public void Dispose()
    {
        _pace.Dispose();
        _budget?.Dispose();
    }

    private ProviderPermit BudgetSpent(RateLimitLease lease)
        => new(
            ProviderPermitStatus.BudgetSpent,
            lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter) && retryAfter > TimeSpan.Zero
                ? retryAfter
                : _budgetSegment);
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
