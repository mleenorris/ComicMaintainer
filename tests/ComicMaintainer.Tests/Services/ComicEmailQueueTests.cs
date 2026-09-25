using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Services;
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

    [Fact]
    public async Task ProcessingFailure_DoesNotStopTheConsumer()
    {
        var secondProcessed = new TaskCompletionSource();
        var service = new Mock<IComicEmailService>();
        service.Setup(s => s.ProcessDeliveryAsync(1, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
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
    public async Task StopAsync_WithoutStart_IsNoOp()
    {
        var queue = new ComicEmailQueue(
            () => new Mock<IComicEmailService>().Object,
            new Mock<ILogger<ComicEmailQueue>>().Object);

        await queue.StopAsync();
    }
}
