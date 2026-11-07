using ComicMaintainer.Core.Services;

namespace ComicMaintainer.Tests.Services;

public class ProviderRateLimiterTests
{
    [Fact]
    public async Task AcquireAsync_GrantsPermitsWithinThePaceLimit()
    {
        using var limiter = new ProviderRateLimiter("Test", permitLimit: 2, window: TimeSpan.FromMinutes(1));

        Assert.Null(await limiter.AcquireAsync(TimeSpan.FromSeconds(1), CancellationToken.None));
        Assert.Null(await limiter.AcquireAsync(TimeSpan.FromSeconds(1), CancellationToken.None));
    }

    [Fact]
    public async Task AcquireAsync_GivesUpAfterTheMaximumWait_InsteadOfQueueingIndefinitely()
    {
        using var limiter = new ProviderRateLimiter("Test", permitLimit: 1, window: TimeSpan.FromMinutes(10), segmentsPerWindow: 1);
        Assert.Null(await limiter.AcquireAsync(TimeSpan.FromSeconds(1), CancellationToken.None));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var wait = await limiter.AcquireAsync(TimeSpan.FromMilliseconds(200), CancellationToken.None);

        Assert.Equal(TimeSpan.FromMilliseconds(200), wait);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"waited {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task AcquireAsync_SpentBudget_FailsAtOnceWithTheTimeUntilItRefills()
    {
        using var limiter = new ProviderRateLimiter(
            "Test",
            permitLimit: 100,
            window: TimeSpan.FromSeconds(1),
            budgetPermitLimit: 2,
            budgetWindow: TimeSpan.FromHours(1));
        Assert.Null(await limiter.AcquireAsync(TimeSpan.FromSeconds(1), CancellationToken.None));
        Assert.Null(await limiter.AcquireAsync(TimeSpan.FromSeconds(1), CancellationToken.None));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var wait = await limiter.AcquireAsync(TimeSpan.FromSeconds(20), CancellationToken.None);

        Assert.NotNull(wait);
        Assert.InRange(wait.Value, TimeSpan.FromSeconds(1), TimeSpan.FromHours(1));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"waited {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task AcquireAsync_CallerCancellation_IsThrown()
    {
        using var limiter = new ProviderRateLimiter("Test", permitLimit: 1, window: TimeSpan.FromMinutes(10), segmentsPerWindow: 1);
        Assert.Null(await limiter.AcquireAsync(TimeSpan.FromSeconds(1), CancellationToken.None));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await limiter.AcquireAsync(TimeSpan.FromMinutes(1), cts.Token));
    }
}
