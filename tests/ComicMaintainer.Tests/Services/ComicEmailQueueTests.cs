using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Moq;

namespace ComicMaintainer.Tests.Services;

public class ComicEmailQueueTests
{
    [Fact]
    public async Task Enqueue_ProcessesDeliveriesInOrder()
    {
        var processed = new List<int>();
        var completed = new TaskCompletionSource();
        var service = new Mock<IComicEmailService>();
        service.Setup(s => s.ProcessDeliveryAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns<int, CancellationToken>((id, _) =>
            {
                processed.Add(id);
                if (processed.Count == 3)
                {
                    completed.TrySetResult();
                }

                return Task.CompletedTask;
            });

        var queue = new ComicEmailQueue(() => service.Object, new Mock<ILogger<ComicEmailQueue>>().Object);
        await queue.StartAsync();

        queue.Enqueue(1);
        queue.Enqueue(2);
        queue.Enqueue(3);

        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await queue.StopAsync();

        Assert.Equal(new[] { 1, 2, 3 }, processed);
    }

    [Fact]
    public async Task Enqueue_IgnoresNonPositiveIds()
    {
        var service = new Mock<IComicEmailService>();
        var queue = new ComicEmailQueue(() => service.Object, new Mock<ILogger<ComicEmailQueue>>().Object);
        await queue.StartAsync();

        queue.Enqueue(0);
        queue.Enqueue(-5);

        await queue.StopAsync();
        service.Verify(s => s.ProcessDeliveryAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    public static TheoryData<Exception> DeliveryFailures() => new()
    {
        new InvalidOperationException("boom"),
        new OutOfMemoryException(),
    };

    [Theory]
    [MemberData(nameof(DeliveryFailures))]
    public async Task ProcessingFailure_DoesNotStopTheConsumer(Exception failure)
    {
        var secondProcessed = new TaskCompletionSource();
        var service = new Mock<IComicEmailService>();
        service.Setup(s => s.ProcessDeliveryAsync(1, It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);
        service.Setup(s => s.ProcessDeliveryAsync(2, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                secondProcessed.TrySetResult();
                return Task.CompletedTask;
            });

        var queue = new ComicEmailQueue(() => service.Object, new Mock<ILogger<ComicEmailQueue>>().Object);
        await queue.StartAsync();

        queue.Enqueue(1);
        queue.Enqueue(2);

        await secondProcessed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await queue.StopAsync();
    }

    [Fact]
    public async Task StopAsync_DrainsAlreadyQueuedDeliveries()
    {
        var processed = 0;
        var service = new Mock<IComicEmailService>();
        service.Setup(s => s.ProcessDeliveryAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                Interlocked.Increment(ref processed);
                return Task.CompletedTask;
            });

        var queue = new ComicEmailQueue(() => service.Object, new Mock<ILogger<ComicEmailQueue>>().Object);
        await queue.StartAsync();

        for (var i = 1; i <= 5; i++)
        {
            queue.Enqueue(i);
        }

        await queue.StopAsync();

        Assert.Equal(5, processed);
    }

    [Fact]
    public async Task Enqueue_DoesNotQueueADeliveryTheQueueAlreadyHolds()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new Mock<IComicEmailService>();
        service.Setup(s => s.ProcessDeliveryAsync(1, It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                started.TrySetResult();
                await release.Task;
            });

        var queue = new ComicEmailQueue(() => service.Object, new Mock<ILogger<ComicEmailQueue>>().Object);
        queue.Enqueue(1);
        queue.Enqueue(1);
        Assert.True(queue.IsQueued(1));

        await queue.StartAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Still held while it is being processed.
        queue.Enqueue(1);
        Assert.True(queue.IsQueued(1));

        release.SetResult();
        await queue.StopAsync();

        service.Verify(s => s.ProcessDeliveryAsync(1, It.IsAny<CancellationToken>()), Times.Once);
        Assert.False(queue.IsQueued(1));
    }

    [Fact]
    public async Task StartAsync_ResumesPendingDeliveriesRightAwayAndThenPeriodically()
    {
        var time = new TimerTrackingTimeProvider(DateTimeOffset.UtcNow);
        var sweeps = 0;
        var sweptTwice = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new Mock<IComicEmailService>();
        service.Setup(s => s.ResumePendingDeliveriesAsync(It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                if (Interlocked.Increment(ref sweeps) == 2)
                {
                    sweptTwice.TrySetResult();
                }

                return Task.FromResult(0);
            });

        var queue = new ComicEmailQueue(
            () => service.Object,
            new Mock<ILogger<ComicEmailQueue>>().Object,
            timeProvider: time);
        await queue.StartAsync();

        await time.WaitForTimersAsync(1);
        Assert.Equal(1, Volatile.Read(ref sweeps));
        time.Advance(ComicEmailQueue.PendingSweepInterval - TimeSpan.FromSeconds(1));
        Assert.Equal(1, Volatile.Read(ref sweeps));

        time.Advance(TimeSpan.FromSeconds(1));
        await sweptTwice.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await queue.StopAsync();

        // Stopping ends the sweep.
        time.Advance(ComicEmailQueue.PendingSweepInterval * 3);
        Assert.Equal(2, Volatile.Read(ref sweeps));
    }

    [Theory]
    [MemberData(nameof(DeliveryFailures))]
    public async Task PendingSweep_KeepsRunningAfterAFailure(Exception failure)
    {
        var time = new TimerTrackingTimeProvider(DateTimeOffset.UtcNow);
        var sweeps = 0;
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new Mock<IComicEmailService>();
        service.Setup(s => s.ResumePendingDeliveriesAsync(It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                if (Interlocked.Increment(ref sweeps) == 1)
                {
                    return Task.FromException<int>(failure);
                }

                recovered.TrySetResult();
                return Task.FromResult(0);
            });

        var logger = new Mock<ILogger<ComicEmailQueue>>();
        var queue = new ComicEmailQueue(() => service.Object, logger.Object, timeProvider: time);
        await queue.StartAsync();

        await time.WaitForTimersAsync(1);
        time.Advance(ComicEmailQueue.PendingSweepInterval);
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await queue.StopAsync();

        logger.Verify(
            l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.Is<Exception?>(e => ReferenceEquals(e, failure)),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task StopAsync_ReleasesTheDeliveriesTheRunDidNotGetTo()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new Mock<IComicEmailService>();
        service.Setup(s => s.ProcessDeliveryAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns<int, CancellationToken>(async (_, token) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            });

        var queue = new ComicEmailQueue(() => service.Object, new Mock<ILogger<ComicEmailQueue>>().Object);
        await queue.StartAsync();
        queue.Enqueue(7);
        queue.Enqueue(8);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using var deadline = new CancellationTokenSource();
        deadline.Cancel();
        await queue.StopAsync(deadline.Token);

        // Neither delivery is held any more, so the next run's sweep finds both
        // pending rows again instead of treating them as already queued.
        Assert.False(queue.IsQueued(7));
        Assert.False(queue.IsQueued(8));
    }

    [Fact]
    public async Task Schedule_ProcessesTheDeliveryOnceItIsDueWithoutHoldingUpOthers()
    {
        var time = new TimerTrackingTimeProvider(DateTimeOffset.UtcNow);
        var processed = new List<int>();
        var scheduledProcessed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var otherProcessed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new Mock<IComicEmailService>();
        service.Setup(s => s.ProcessDeliveryAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns<int, CancellationToken>((id, _) =>
            {
                lock (processed)
                {
                    processed.Add(id);
                }

                (id == 1 ? scheduledProcessed : otherProcessed).TrySetResult();
                return Task.CompletedTask;
            });

        var queue = new ComicEmailQueue(() => service.Object, new Mock<ILogger<ComicEmailQueue>>().Object, timeProvider: time);
        await queue.StartAsync();
        await time.WaitForTimersAsync(1);

        queue.Schedule(1, time.GetUtcNow() + TimeSpan.FromMinutes(5));
        queue.Enqueue(2);
        await otherProcessed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(queue.IsQueued(1));
        lock (processed)
        {
            Assert.Equal(new[] { 2 }, processed);
        }

        time.Advance(TimeSpan.FromMinutes(5));
        await scheduledProcessed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await queue.StopAsync();

        Assert.Equal(new[] { 2, 1 }, processed);
        Assert.False(queue.IsQueued(1));
    }

    [Fact]
    public async Task Schedule_AgainMovesTheDeliveryToTheNewTime()
    {
        var time = new TimerTrackingTimeProvider(DateTimeOffset.UtcNow);
        var processedAt = new TaskCompletionSource<DateTimeOffset>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new Mock<IComicEmailService>();
        service.Setup(s => s.ProcessDeliveryAsync(1, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                processedAt.TrySetResult(time.GetUtcNow());
                return Task.CompletedTask;
            });

        var queue = new ComicEmailQueue(() => service.Object, new Mock<ILogger<ComicEmailQueue>>().Object, timeProvider: time);
        var start = time.GetUtcNow();
        queue.Schedule(1, start + TimeSpan.FromMinutes(1));
        queue.Schedule(1, start + TimeSpan.FromMinutes(10));
        await queue.StartAsync();

        time.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(100);
        Assert.False(processedAt.Task.IsCompleted);

        time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(start + TimeSpan.FromMinutes(10), await processedAt.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await queue.StopAsync();
    }

    [Fact]
    public async Task Schedule_WhileTheDeliveryIsBeingProcessed_TakesEffectAfterwards()
    {
        var time = new TimerTrackingTimeProvider(DateTimeOffset.UtcNow);
        var calls = 0;
        var secondCall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ComicEmailQueue? queue = null;
        var service = new Mock<IComicEmailService>();
        service.Setup(s => s.ProcessDeliveryAsync(1, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    // What the delivery service does after a temporary failure.
                    queue!.Schedule(1, time.GetUtcNow() + TimeSpan.FromMinutes(1));
                }
                else
                {
                    secondCall.TrySetResult();
                }

                return Task.CompletedTask;
            });

        queue = new ComicEmailQueue(() => service.Object, new Mock<ILogger<ComicEmailQueue>>().Object, timeProvider: time);
        await queue.StartAsync();
        queue.Enqueue(1);

        // One timer for the sweep, one for the retry once processing ended.
        await time.WaitForTimersAsync(2);
        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.True(queue.IsQueued(1));

        time.Advance(TimeSpan.FromMinutes(1));
        await secondCall.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await queue.StopAsync();

        Assert.Equal(2, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task Schedule_WithATimeThatHasPassed_QueuesTheDeliveryRightAway()
    {
        var processed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new Mock<IComicEmailService>();
        service.Setup(s => s.ProcessDeliveryAsync(3, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                processed.TrySetResult();
                return Task.CompletedTask;
            });

        var queue = new ComicEmailQueue(() => service.Object, new Mock<ILogger<ComicEmailQueue>>().Object);
        await queue.StartAsync();
        queue.Schedule(3, DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1));

        await processed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await queue.StopAsync();
    }

    [Fact]
    public async Task StopAsync_WithoutStart_IsNoOp()
    {
        var queue = new ComicEmailQueue(
            () => new Mock<IComicEmailService>().Object,
            new Mock<ILogger<ComicEmailQueue>>().Object);

        await queue.StopAsync();
    }
}
