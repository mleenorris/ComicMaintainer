using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Services;
using Microsoft.Extensions.Logging;
using Moq;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Covers the lifecycle edges of <see cref="ComicEmailQueue"/>: repeated starts,
/// a shutdown deadline that interrupts a delivery, and a stop that overlaps a
/// restart.
/// </summary>
public class ComicEmailQueueCoverageTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task StartAsync_WhenAlreadyStarted_KeepsTheSingleConsumer()
    {
        var processed = new List<int>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new Mock<IComicEmailService>();
        service.Setup(s => s.ProcessDeliveryAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns<int, CancellationToken>((id, _) =>
            {
                lock (processed)
                {
                    processed.Add(id);
                    if (processed.Count == 2)
                    {
                        done.TrySetResult();
                    }
                }

                return Task.CompletedTask;
            });

        var queue = new ComicEmailQueue(() => service.Object, new Mock<ILogger<ComicEmailQueue>>().Object);

        var first = queue.StartAsync();
        var second = queue.StartAsync();
        Assert.True(first.IsCompletedSuccessfully);
        Assert.True(second.IsCompletedSuccessfully);

        queue.Enqueue(1);
        queue.Enqueue(2);
        await done.Task.WaitAsync(Timeout);
        await queue.StopAsync();

        // A second consumer would race the first one for the channel; with one
        // consumer each delivery is processed exactly once, in order.
        Assert.Equal(new[] { 1, 2 }, processed);
    }

    [Fact]
    public async Task StopAsync_WithAnExpiredDeadline_CancelsTheDeliveryInProgress()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observed = default;
        var service = new Mock<IComicEmailService>();
        service.Setup(s => s.ProcessDeliveryAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns<int, CancellationToken>(async (_, token) =>
            {
                observed = token;
                started.TrySetResult();
                await Task.Delay(System.Threading.Timeout.Infinite, token);
            });

        var logger = new Mock<ILogger<ComicEmailQueue>>();
        var queue = new ComicEmailQueue(() => service.Object, logger.Object);
        await queue.StartAsync();
        queue.Enqueue(7);
        queue.Enqueue(8);
        await started.Task.WaitAsync(Timeout);

        using var deadline = new CancellationTokenSource();
        deadline.Cancel();
        await queue.StopAsync(deadline.Token).WaitAsync(Timeout);

        Assert.True(observed.IsCancellationRequested);

        // The cancelled delivery is not reported as a failure, and the delivery
        // still queued behind it is left for the next run instead of processed.
        logger.Verify(
            l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
        service.Verify(s => s.ProcessDeliveryAsync(7, It.IsAny<CancellationToken>()), Times.Once);
        service.Verify(s => s.ProcessDeliveryAsync(8, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StopAsync_ToleratesARestartThatOverlappedThePreviousStop()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource<(CancellationToken Token, bool FirstFinished)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new Mock<IComicEmailService>();
        service.Setup(s => s.ProcessDeliveryAsync(1, It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                started.TrySetResult();
                await release.Task;
                firstFinished.TrySetResult();
            });
        service.Setup(s => s.ProcessDeliveryAsync(2, It.IsAny<CancellationToken>()))
            .Returns<int, CancellationToken>(async (_, token) =>
            {
                secondStarted.TrySetResult((token, firstFinished.Task.IsCompleted));
                await Task.Delay(System.Threading.Timeout.Infinite, token);
            });

        var logger = new Mock<ILogger<ComicEmailQueue>>();
        var queue = new ComicEmailQueue(() => service.Object, logger.Object);
        await queue.StartAsync();
        queue.Enqueue(1);
        await started.Task.WaitAsync(Timeout);

        // The first stop waits for the delivery in progress; the queue is
        // started again before that delivery finishes, and the new run gets
        // a delivery of its own.
        var firstStop = queue.StopAsync();
        Assert.False(firstStop.IsCompleted);
        await queue.StartAsync();
        queue.Enqueue(2);

        release.SetResult();
        await firstStop.WaitAsync(Timeout);
        var second = await secondStarted.Task.WaitAsync(Timeout);

        // The new run waited for the previous one, so the sends never overlapped.
        Assert.True(second.FirstFinished);
        Assert.False(second.Token.IsCancellationRequested);

        // Finishing the first stop must not have discarded the new run's token
        // source: a stop with an expired deadline still cancels its delivery.
        using var deadline = new CancellationTokenSource();
        deadline.Cancel();
        await queue.StopAsync(deadline.Token).WaitAsync(Timeout);

        Assert.True(second.Token.IsCancellationRequested);
        service.Verify(s => s.ProcessDeliveryAsync(1, It.IsAny<CancellationToken>()), Times.Once);
        service.Verify(s => s.ProcessDeliveryAsync(2, It.IsAny<CancellationToken>()), Times.Once);
        logger.Verify(
            l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
    }

    [Fact]
    public async Task StopAsync_WithExpiredDeadline_DoesNotWaitForThePreviousRunAfterARestart()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new Mock<IComicEmailService>();
        service.Setup(s => s.ProcessDeliveryAsync(1, It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                started.TrySetResult();
                await release.Task;
            });

        var queue = new ComicEmailQueue(() => service.Object, new Mock<ILogger<ComicEmailQueue>>().Object);
        await queue.StartAsync();
        queue.Enqueue(1);
        await started.Task.WaitAsync(Timeout);
        var firstStop = queue.StopAsync();
        await queue.StartAsync();
        queue.Enqueue(2);

        // The new run is still waiting for the previous one; a stop whose deadline
        // has passed gives up on it instead of waiting for that delivery.
        using var deadline = new CancellationTokenSource();
        deadline.Cancel();
        await queue.StopAsync(deadline.Token).WaitAsync(Timeout);
        Assert.False(firstStop.IsCompleted);

        release.SetResult();
        await firstStop.WaitAsync(Timeout);
        service.Verify(s => s.ProcessDeliveryAsync(1, It.IsAny<CancellationToken>()), Times.Once);
        service.Verify(s => s.ProcessDeliveryAsync(2, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StartAsync_AfterARunStoppedWhileWaiting_StillWaitsForTheRunStillSending()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thirdStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new Mock<IComicEmailService>();
        service.Setup(s => s.ProcessDeliveryAsync(1, It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task;
                firstFinished.TrySetResult();
            });
        service.Setup(s => s.ProcessDeliveryAsync(3, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                thirdStarted.TrySetResult(firstFinished.Task.IsCompleted);
                return Task.CompletedTask;
            });

        var queue = new ComicEmailQueue(() => service.Object, new Mock<ILogger<ComicEmailQueue>>().Object);

        // Run A is sending; run B starts behind it and is stopped with an expired
        // deadline while still waiting, then run C starts.
        await queue.StartAsync();
        queue.Enqueue(1);
        await firstStarted.Task.WaitAsync(Timeout);
        var firstStop = queue.StopAsync();
        await queue.StartAsync();
        queue.Enqueue(2);
        using (var deadline = new CancellationTokenSource())
        {
            deadline.Cancel();
            await queue.StopAsync(deadline.Token).WaitAsync(Timeout);
        }

        await queue.StartAsync();
        queue.Enqueue(3);

        // C must not send alongside A even though B has already finished.
        await Task.Delay(200);
        Assert.False(thirdStarted.Task.IsCompleted);

        releaseFirst.SetResult();
        await firstStop.WaitAsync(Timeout);
        Assert.True(await thirdStarted.Task.WaitAsync(Timeout));
        await queue.StopAsync().WaitAsync(Timeout);

        service.Verify(s => s.ProcessDeliveryAsync(2, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StartAsync_AfterStop_DeliversWhatWasQueuedWhileStoppedAndAfterTheRestart()
    {
        var processed = new List<int>();
        var service = new Mock<IComicEmailService>();
        service.Setup(s => s.ProcessDeliveryAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns<int, CancellationToken>((id, _) =>
            {
                lock (processed)
                {
                    processed.Add(id);
                }

                return Task.CompletedTask;
            });

        var queue = new ComicEmailQueue(() => service.Object, new Mock<ILogger<ComicEmailQueue>>().Object);
        await queue.StartAsync();
        queue.Enqueue(1);
        await queue.StopAsync().WaitAsync(Timeout);

        queue.Enqueue(2);
        await queue.StartAsync();
        queue.Enqueue(3);

        // A graceful stop drains everything queued for the run.
        await queue.StopAsync().WaitAsync(Timeout);

        Assert.Equal(new[] { 1, 2, 3 }, processed);
    }
}
