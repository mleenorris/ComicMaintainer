using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Services;
using Microsoft.Extensions.Logging;
using Moq;

namespace ComicMaintainer.Tests.Services;

public class SeriesArchiveCoverWriteQueueTests
{
    private readonly Mock<ISeriesArchiveCoverWriter> _writer = new();

    private SeriesArchiveCoverWriteQueue CreateQueue() =>
        new(_writer.Object, new Mock<ILogger<SeriesArchiveCoverWriteQueue>>().Object);

    [Fact]
    public async Task EnqueueWrite_InvokesWriterOnBackgroundConsumer()
    {
        var completed = new TaskCompletionSource();
        _writer
            .Setup(w => w.WriteAsync("batman", "/cache/batman.png", "image/png", true, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                completed.TrySetResult();
                return Task.CompletedTask;
            });

        var queue = CreateQueue();
        await queue.StartAsync();

        queue.EnqueueWrite("batman", "/cache/batman.png", "image/png", force: true);

        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _writer.Verify(w => w.WriteAsync("batman", "/cache/batman.png", "image/png", true, It.IsAny<CancellationToken>()), Times.Once);

        await queue.StopAsync();
    }

    [Fact]
    public async Task EnqueueRemove_InvokesWriterRemoveOnBackgroundConsumer()
    {
        var completed = new TaskCompletionSource();
        _writer
            .Setup(w => w.RemoveAsync("batman", It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                completed.TrySetResult();
                return Task.CompletedTask;
            });

        var queue = CreateQueue();
        await queue.StartAsync();

        queue.EnqueueRemove("batman");

        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _writer.Verify(w => w.RemoveAsync("batman", It.IsAny<CancellationToken>()), Times.Once);

        await queue.StopAsync();
    }

    [Fact]
    public async Task ProcessesRequestsSequentially_OnePerSeriesAtATime()
    {
        var inFlight = 0;
        var maxObserved = 0;
        var gate = new object();

        _writer
            .Setup(w => w.WriteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                lock (gate)
                {
                    inFlight++;
                    maxObserved = Math.Max(maxObserved, inFlight);
                }

                await Task.Delay(20);

                lock (gate)
                {
                    inFlight--;
                }
            });

        var queue = CreateQueue();
        await queue.StartAsync();

        for (var i = 0; i < 10; i++)
        {
            queue.EnqueueWrite($"series-{i}", "/cache/x.png", "image/png");
        }

        await queue.StopAsync();

        Assert.Equal(1, maxObserved);
        _writer.Verify(
            w => w.WriteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Exactly(10));
    }

    [Fact]
    public void EnqueueWrite_IgnoresBlankKey_AndDoesNotThrowWhenNotStarted()
    {
        var queue = CreateQueue();

        queue.EnqueueWrite("", "/cache/x.png", "image/png");
        queue.EnqueueRemove("   ");

        _writer.Verify(
            w => w.WriteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
