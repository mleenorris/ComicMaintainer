using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Services;
using Microsoft.Extensions.Logging;
using Moq;

namespace ComicMaintainer.Tests.Services;

public class SeriesArchiveCoverWriteQueueCoverageTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(5);

    private readonly Mock<ISeriesArchiveCoverWriter> _writer = new();
    private readonly Mock<ILogger<SeriesArchiveCoverWriteQueue>> _logger = new();

    private SeriesArchiveCoverWriteQueue CreateQueue() => new(_writer.Object, _logger.Object);

    [Fact]
    public async Task StartAsync_CalledTwice_KeepsSingleSequentialConsumer()
    {
        var gate = new object();
        var inFlight = 0;
        var maxObserved = 0;
        _writer
            .Setup(w => w.WriteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                lock (gate)
                {
                    inFlight++;
                    maxObserved = Math.Max(maxObserved, inFlight);
                }

                await Task.Yield();

                lock (gate)
                {
                    inFlight--;
                }
            });

        var queue = CreateQueue();
        await queue.StartAsync();
        var secondStart = queue.StartAsync();

        Assert.True(secondStart.IsCompletedSuccessfully);

        for (var i = 0; i < 5; i++)
        {
            queue.EnqueueWrite($"series-{i}", "/cache/x.png", "image/png");
        }

        await queue.StopAsync().WaitAsync(WaitLimit);

        Assert.Equal(1, maxObserved);
        _writer.Verify(
            w => w.WriteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Exactly(5));
    }

    [Fact]
    public async Task WriterFailures_AreLoggedAtDebugWithSanitizedKey_AndLaterRequestsStillRun()
    {
        var healthyProcessed = new TaskCompletionSource();
        var writeFailure = new InvalidOperationException("write failed");
        var removeFailure = new IOException("remove failed");
        _writer
            .Setup(w => w.WriteAsync("broken", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(writeFailure);
        _writer
            .Setup(w => w.RemoveAsync("broken\nseries", It.IsAny<CancellationToken>()))
            .ThrowsAsync(removeFailure);
        _writer
            .Setup(w => w.WriteAsync("healthy", "/cache/healthy.png", "image/png", true, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                healthyProcessed.TrySetResult();
                return Task.CompletedTask;
            });

        var queue = CreateQueue();
        await queue.StartAsync();

        queue.EnqueueWrite("broken", "/cache/broken.png", "image/png");
        queue.EnqueueRemove("broken\nseries");
        queue.EnqueueWrite("healthy", "/cache/healthy.png", "image/png", force: true);

        await healthyProcessed.Task.WaitAsync(WaitLimit);
        await queue.StopAsync().WaitAsync(WaitLimit);

        VerifyLog(LogLevel.Debug, "Failed to write embedded first-archive series cover for broken", writeFailure, Times.Once());
        VerifyLog(LogLevel.Debug, "Failed to remove embedded first-archive series cover for broken\\nseries", removeFailure, Times.Once());
        VerifyLog(LogLevel.Error, "terminated unexpectedly", null, Times.Never());
    }

    [Fact]
    public async Task StopAsync_WithExpiredDeadline_CancelsInFlightWriteAndSkipsPendingRequests()
    {
        var started = new TaskCompletionSource();
        var observedCancellation = new TaskCompletionSource();
        _writer
            .Setup(w => w.WriteAsync("slow", "/cache/slow.png", "image/png", false, It.IsAny<CancellationToken>()))
            .Returns<string, string, string, bool, CancellationToken>(async (_, _, _, _, ct) =>
            {
                started.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                    observedCancellation.TrySetResult();
                    throw;
                }
            });

        var queue = CreateQueue();
        await queue.StartAsync();
        queue.EnqueueWrite("slow", "/cache/slow.png", "image/png");
        await started.Task.WaitAsync(WaitLimit);
        queue.EnqueueRemove("pending");

        using var expired = new CancellationTokenSource();
        expired.Cancel();
        await queue.StopAsync(expired.Token).WaitAsync(WaitLimit);

        Assert.True(observedCancellation.Task.IsCompletedSuccessfully);
        _writer.Verify(w => w.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        VerifyLog(LogLevel.Debug, "Failed to", null, Times.Never());
        VerifyLog(LogLevel.Error, "terminated unexpectedly", null, Times.Never());
    }

    [Fact]
    public async Task Consumer_WhenFailureLoggingThrows_LogsTerminationAsErrorAndStopStillCompletes()
    {
        var sinkFailure = new InvalidOperationException("log sink unavailable");
        var terminated = new TaskCompletionSource<(string Message, Exception? Error)>();
        _logger
            .Setup(l => l.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Throws(sinkFailure);
        _logger
            .Setup(l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Callback(new InvocationAction(invocation =>
                terminated.TrySetResult((invocation.Arguments[2].ToString()!, (Exception?)invocation.Arguments[3]))));
        _writer
            .Setup(w => w.RemoveAsync("broken", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("remove failed"));

        var queue = CreateQueue();
        await queue.StartAsync();
        queue.EnqueueRemove("broken");

        var (message, error) = await terminated.Task.WaitAsync(WaitLimit);
        await queue.StopAsync().WaitAsync(WaitLimit);

        Assert.Contains("consumer terminated unexpectedly", message);
        Assert.Same(sinkFailure, error);
    }

    private void VerifyLog(LogLevel level, string messageFragment, Exception? exception, Times times) =>
        _logger.Verify(
            l => l.Log(
                level,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains(messageFragment)),
                It.Is<Exception?>(e => exception == null || ReferenceEquals(e, exception)),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            times);
}
