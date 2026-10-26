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

        // The first stop waits for the delivery in progress; the queue is
        // started again before that delivery finishes.
        var firstStop = queue.StopAsync();
        Assert.False(firstStop.IsCompleted);
        await queue.StartAsync();

        release.SetResult();
        await firstStop.WaitAsync(Timeout);

        await queue.StopAsync().WaitAsync(Timeout);
        service.Verify(s => s.ProcessDeliveryAsync(1, It.IsAny<CancellationToken>()), Times.Once);
    }
}
