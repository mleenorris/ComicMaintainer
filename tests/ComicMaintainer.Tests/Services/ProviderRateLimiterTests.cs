using ComicMaintainer.Core.Services;
using Microsoft.Extensions.Time.Testing;

namespace ComicMaintainer.Tests.Services;

public class ProviderRateLimiterTests
{
    [Fact]
    public async Task AcquireAsync_GrantsPermitsWithinThePaceLimit()
    {
        using var limiter = new ProviderRateLimiter("Test", permitLimit: 2, window: TimeSpan.FromMinutes(1));

        Assert.True((await limiter.AcquireAsync(TimeSpan.FromSeconds(1), CancellationToken.None)).IsGranted);
        Assert.True((await limiter.AcquireAsync(TimeSpan.FromSeconds(1), CancellationToken.None)).IsGranted);
    }

    [Fact]
    public async Task AcquireAsync_GivesUpAfterTheMaximumWait_InsteadOfQueueingIndefinitely()
    {
        var clock = new FakeTimeProvider();
        using var limiter = new ProviderRateLimiter("Test", permitLimit: 1, window: TimeSpan.FromMinutes(10), segmentsPerWindow: 1, timeProvider: clock);
        Assert.True((await limiter.AcquireAsync(TimeSpan.FromSeconds(1), CancellationToken.None)).IsGranted);

        var waiting = limiter.AcquireAsync(TimeSpan.FromSeconds(30), CancellationToken.None).AsTask();
        Assert.False(waiting.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(30));

        var permit = await waiting;
        Assert.Equal(ProviderPermitStatus.PaceWaitExpired, permit.Status);
    }

    [Fact]
    public async Task AcquireAsync_WaitsForTheNextWindow_WhenTheCurrentOneIsUsedUp()
    {
        using var limiter = new ProviderRateLimiter("Test", permitLimit: 2, window: TimeSpan.FromMilliseconds(600), segmentsPerWindow: 12);
        Assert.True((await limiter.AcquireAsync(limiter.MaxPermitWait, CancellationToken.None)).IsGranted);
        Assert.True((await limiter.AcquireAsync(limiter.MaxPermitWait, CancellationToken.None)).IsGranted);

        // The window is used up: the next caller waits for it rather than failing.
        Assert.True((await limiter.AcquireAsync(limiter.MaxPermitWait, CancellationToken.None)).IsGranted);
    }

    [Fact]
    public void MaxPermitWait_CoversAFullWindow_AndHasAFloorForShortWindows()
    {
        using var perMinute = new ProviderRateLimiter("Test", permitLimit: 28, window: TimeSpan.FromMinutes(1), segmentsPerWindow: 12);
        using var perSecond = new ProviderRateLimiter("Test", permitLimit: 5, window: TimeSpan.FromSeconds(1));

        Assert.Equal(TimeSpan.FromSeconds(65), perMinute.MaxPermitWait);
        Assert.Equal(TimeSpan.FromSeconds(20), perSecond.MaxPermitWait);
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
        Assert.True((await limiter.AcquireAsync(TimeSpan.FromSeconds(1), CancellationToken.None)).IsGranted);
        Assert.True((await limiter.AcquireAsync(TimeSpan.FromSeconds(1), CancellationToken.None)).IsGranted);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var permit = await limiter.AcquireAsync(TimeSpan.FromSeconds(20), CancellationToken.None);

        Assert.Equal(ProviderPermitStatus.BudgetSpent, permit.Status);
        Assert.InRange(permit.RetryAfter, TimeSpan.FromSeconds(1), TimeSpan.FromHours(1));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"waited {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task AcquireAsync_PaceWaitExpired_DoesNotSpendABudgetPermit()
    {
        var clock = new FakeTimeProvider();
        using var limiter = new ProviderRateLimiter(
            "Test",
            permitLimit: 1,
            window: TimeSpan.FromMinutes(1),
            segmentsPerWindow: 1,
            budgetPermitLimit: 2,
            budgetWindow: TimeSpan.FromHours(1),
            timeProvider: clock);
        Assert.True((await limiter.AcquireAsync(TimeSpan.FromSeconds(1), CancellationToken.None)).IsGranted);
        Assert.Equal(1, limiter.AvailableBudgetPermits);

        // Times out in the pace queue: nothing is sent, so no budget is used.
        var waiting = limiter.AcquireAsync(TimeSpan.FromSeconds(1), CancellationToken.None).AsTask();
        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(ProviderPermitStatus.PaceWaitExpired, (await waiting).Status);
        Assert.Equal(1, limiter.AvailableBudgetPermits);
    }

    [Fact]
    public async Task AcquireAsync_CallerCancellation_IsThrown()
    {
        using var limiter = new ProviderRateLimiter("Test", permitLimit: 1, window: TimeSpan.FromMinutes(10), segmentsPerWindow: 1);
        Assert.True((await limiter.AcquireAsync(TimeSpan.FromSeconds(1), CancellationToken.None)).IsGranted);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await limiter.AcquireAsync(TimeSpan.FromMinutes(1), cts.Token));
    }
}
