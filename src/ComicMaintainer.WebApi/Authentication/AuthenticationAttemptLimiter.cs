using System.Net;
using System.Threading.RateLimiting;

namespace ComicMaintainer.WebApi.Authentication;

/// <summary>
/// The per-client budget of <see cref="AuthenticationRateLimit"/>, held in one place so that every way of checking a
/// password draws on it. The endpoints that check one reach it through the rate-limiting middleware
/// (<see cref="PartitionFor"/>); the OPDS HTTP Basic handler, which checks credentials during authentication and so
/// runs before that middleware, takes a permit directly (<see cref="AttemptAcquire"/>). A client that has used up its
/// attempts at the sign-in page has used them up for OPDS, and the other way round.
/// </summary>
public sealed class AuthenticationAttemptLimiter : IDisposable
{
    private readonly PartitionedRateLimiter<string> _attempts = PartitionedRateLimiter.Create<string, string>(client =>
        RateLimitPartition.GetFixedWindowLimiter(client, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = AuthenticationRateLimit.PermitLimit,
            Window = AuthenticationRateLimit.Window,
            QueueLimit = 0,
        }));

    /// <summary>The partition a client's attempts are counted in.</summary>
    public static string PartitionKey(IPAddress? clientAddress) => $"ip:{clientAddress?.ToString() ?? "unknown"}";

    /// <summary>Takes one attempt from the client's budget; check <see cref="RateLimitLease.IsAcquired"/> on the result.</summary>
    public RateLimitLease AttemptAcquire(IPAddress? clientAddress) => _attempts.AttemptAcquire(PartitionKey(clientAddress));

    /// <summary>The rate-limiting middleware's view of the client's budget.</summary>
    public RateLimitPartition<string> PartitionFor(IPAddress? clientAddress)
    {
        var key = PartitionKey(clientAddress);
        return RateLimitPartition.Get(key, partition => new SharedBudgetLimiter(_attempts, partition));
    }

    public void Dispose() => _attempts.Dispose();

    /// <summary>
    /// Forwards to the shared budget. The middleware keeps one of these per partition and disposes it when it is
    /// idle; the budget lives on in <see cref="_attempts"/>, so an idle forwarder can be dropped at any time.
    /// </summary>
    private sealed class SharedBudgetLimiter(PartitionedRateLimiter<string> attempts, string partition) : RateLimiter
    {
        public override TimeSpan? IdleDuration => TimeSpan.MaxValue;

        public override RateLimiterStatistics? GetStatistics() => attempts.GetStatistics(partition);

        protected override RateLimitLease AttemptAcquireCore(int permitCount) => attempts.AttemptAcquire(partition, permitCount);

        protected override ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken) =>
            attempts.AcquireAsync(partition, permitCount, cancellationToken);
    }
}
