using System.Net;
using System.Threading.RateLimiting;
using ComicMaintainer.WebApi.Authentication;

namespace ComicMaintainer.Tests.Authentication;

/// <summary>
/// The per-client budget for credential checks: the OPDS handler's direct permits and the rate-limiting middleware's
/// partition draw on one count.
/// </summary>
public sealed class AuthenticationAttemptLimiterTests : IDisposable
{
    private static readonly IPAddress Client = IPAddress.Parse("198.51.100.1");
    private readonly AuthenticationAttemptLimiter _limiter = new();

    public void Dispose() => _limiter.Dispose();

    private RateLimiter MiddlewareView() => _limiter.PartitionFor(Client).Factory(AuthenticationAttemptLimiter.PartitionKey(Client));

    [Fact]
    public void PartitionKey_NamesAnUnknownClient()
    {
        Assert.Equal("ip:198.51.100.1", AuthenticationAttemptLimiter.PartitionKey(Client));
        Assert.Equal("ip:unknown", AuthenticationAttemptLimiter.PartitionKey(null));
    }

    [Fact]
    public void ADirectPermitIsCountedByTheMiddlewaresView()
    {
        for (var attempt = 0; attempt < AuthenticationRateLimit.PermitLimit; attempt++)
        {
            Assert.True(_limiter.AttemptAcquire(Client).IsAcquired);
        }

        using var view = MiddlewareView();

        Assert.False(view.AttemptAcquire().IsAcquired);
        Assert.Equal(0, view.GetStatistics()!.CurrentAvailablePermits);
    }

    [Fact]
    public async Task AMiddlewarePermitIsCountedByTheHandlersView()
    {
        using var view = MiddlewareView();

        for (var attempt = 0; attempt < AuthenticationRateLimit.PermitLimit; attempt++)
        {
            using var lease = await view.AcquireAsync();
            Assert.True(lease.IsAcquired);
        }

        Assert.False(_limiter.AttemptAcquire(Client).IsAcquired);
    }

    [Fact]
    public void ClientsHaveTheirOwnBudgets()
    {
        for (var attempt = 0; attempt < AuthenticationRateLimit.PermitLimit; attempt++)
        {
            _limiter.AttemptAcquire(Client);
        }

        Assert.True(_limiter.AttemptAcquire(IPAddress.Parse("198.51.100.2")).IsAcquired);
    }

    [Fact]
    public void AnIdleViewCanBeReclaimedWithoutLosingTheBudget()
    {
        Assert.Equal(TimeSpan.MaxValue, MiddlewareView().IdleDuration);

        _limiter.AttemptAcquire(Client);
        using var view = MiddlewareView();

        Assert.Equal(AuthenticationRateLimit.PermitLimit - 1, view.GetStatistics()!.CurrentAvailablePermits);
    }

    [Fact]
    public void ALimitedAttemptSaysHowLongToWait()
    {
        for (var attempt = 0; attempt < AuthenticationRateLimit.PermitLimit; attempt++)
        {
            _limiter.AttemptAcquire(Client);
        }

        using var lease = _limiter.AttemptAcquire(Client);

        Assert.False(lease.IsAcquired);
        Assert.True(lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter));
        Assert.InRange(retryAfter, TimeSpan.FromSeconds(1), AuthenticationRateLimit.Window);
    }
}
