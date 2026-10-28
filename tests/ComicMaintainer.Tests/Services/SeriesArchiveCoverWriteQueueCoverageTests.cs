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
    public async Task WriterCancellationTheQueueDidNotAskFor_IsLoggedAsAFailure_AndLaterRequestsStillRun()
    {
        var healthyProcessed = new TaskCompletionSource();
        var writerTimeout = new TaskCanceledException("writer timed out");
        _writer
            .Setup(w => w.WriteAsync("slow", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(writerTimeout);
        _writer
            .Setup(w => w.WriteAsync("healthy", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                healthyProcessed.TrySetResult();
                return Task.CompletedTask;
            });

        var queue = CreateQueue();
        await queue.StartAsync();

        queue.EnqueueWrite("slow", "/cache/slow.png", "image/png");
        queue.EnqueueWrite("healthy", "/cache/healthy.png", "image/png");

        // Only the queue's own stop may end the consumer.
        await healthyProcessed.Task.WaitAsync(WaitLimit);
        await queue.StopAsync().WaitAsync(WaitLimit);

        VerifyLog(LogLevel.Debug, "Failed to write embedded first-archive series cover for slow", writerTimeout, Times.Once());
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
                terminated.TrySetResult((invocation.Arguments[2]!.ToString()!, (Exception?)invocation.Arguments[3]))));
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

    [Fact]
    public async Task StartAsync_AfterStop_ProcessesWhatWasQueuedWhileStoppedAndAfterTheRestart()
    {
        var processed = new List<string>();
        _writer
            .Setup(w => w.WriteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns<string, string, string, bool, CancellationToken>((key, _, _, _, _) => Record("write:" + key));
        _writer
            .Setup(w => w.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((key, _) => Record("remove:" + key));

        var queue = CreateQueue();
        await queue.StartAsync();
        queue.EnqueueWrite("first-run", "/cache/a.png", "image/png");
        await queue.StopAsync().WaitAsync(WaitLimit);

        queue.EnqueueWrite("while-stopped", "/cache/b.png", "image/png");
        await queue.StartAsync();
        queue.EnqueueRemove("after-restart");

        // A graceful stop drains everything queued for the run.
        await queue.StopAsync().WaitAsync(WaitLimit);

        Assert.Equal(new[] { "write:first-run", "write:while-stopped", "remove:after-restart" }, processed);

        Task Record(string request)
        {
            lock (processed)
            {
                processed.Add(request);
            }

            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task StopAsync_OverlappingARestart_LeavesTheNewRunWorkingAndStoppable()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource<(CancellationToken Token, bool FirstFinished)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _writer
            .Setup(w => w.WriteAsync("first", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task;
                firstFinished.TrySetResult();
            });
        _writer
            .Setup(w => w.WriteAsync("second", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns<string, string, string, bool, CancellationToken>(async (_, _, _, _, ct) =>
            {
                secondStarted.TrySetResult((ct, firstFinished.Task.IsCompleted));
                await Task.Delay(Timeout.Infinite, ct);
            });

        var queue = CreateQueue();
        await queue.StartAsync();
        queue.EnqueueWrite("first", "/cache/first.png", "image/png");
        await firstStarted.Task.WaitAsync(WaitLimit);

        // The first stop waits for the write in progress; the queue is started
        // again meanwhile and the new run gets a write of its own.
        var firstStop = queue.StopAsync();
        Assert.False(firstStop.IsCompleted);
        await queue.StartAsync();
        queue.EnqueueWrite("second", "/cache/second.png", "image/png");

        releaseFirst.SetResult();
        await firstStop.WaitAsync(WaitLimit);
        var second = await secondStarted.Task.WaitAsync(WaitLimit);

        // The new run waited for the previous one, so the rewrites never overlapped.
        Assert.True(second.FirstFinished);
        Assert.False(second.Token.IsCancellationRequested);

        // Finishing the first stop must not have discarded the new run's token
        // source: a stop with an expired deadline still cancels its write.
        using var expired = new CancellationTokenSource();
        expired.Cancel();
        await queue.StopAsync(expired.Token).WaitAsync(WaitLimit);

        Assert.True(second.Token.IsCancellationRequested);
        _writer.Verify(
            w => w.WriteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
        VerifyLog(LogLevel.Debug, "Failed to", null, Times.Never());
        VerifyLog(LogLevel.Error, "terminated unexpectedly", null, Times.Never());
    }

    [Fact]
    public async Task StopAsync_WithExpiredDeadline_DoesNotWaitForThePreviousRunAfterARestart()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _writer
            .Setup(w => w.WriteAsync("first", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task;
            });

        var queue = CreateQueue();
        await queue.StartAsync();
        queue.EnqueueWrite("first", "/cache/first.png", "image/png");
        await firstStarted.Task.WaitAsync(WaitLimit);
        var firstStop = queue.StopAsync();
        await queue.StartAsync();
        queue.EnqueueRemove("second");

        // The new run is still waiting for the previous one; a stop whose deadline
        // has passed gives up on it instead of waiting for that write.
        using var expired = new CancellationTokenSource();
        expired.Cancel();
        await queue.StopAsync(expired.Token).WaitAsync(WaitLimit);
        Assert.False(firstStop.IsCompleted);

        releaseFirst.SetResult();
        await firstStop.WaitAsync(WaitLimit);
        _writer.Verify(
            w => w.WriteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _writer.Verify(w => w.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StartAsync_AfterARunStoppedWhileWaiting_StillWaitsForTheRunStillWriting()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thirdStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _writer
            .Setup(w => w.WriteAsync("first", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task;
                firstFinished.TrySetResult();
            });
        _writer
            .Setup(w => w.RemoveAsync("first", It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                thirdStarted.TrySetResult(firstFinished.Task.IsCompleted);
                return Task.CompletedTask;
            });

        var queue = CreateQueue();

        // Run A is writing; run B starts behind it and is stopped with an expired
        // deadline while still waiting, then run C starts.
        await queue.StartAsync();
        queue.EnqueueWrite("first", "/cache/first.png", "image/png");
        await firstStarted.Task.WaitAsync(WaitLimit);
        var firstStop = queue.StopAsync();
        await queue.StartAsync();
        queue.EnqueueRemove("second");
        using (var expired = new CancellationTokenSource())
        {
            expired.Cancel();
            await queue.StopAsync(expired.Token).WaitAsync(WaitLimit);
        }

        await queue.StartAsync();
        queue.EnqueueRemove("first");

        // C must not rewrite the archive alongside A even though B has already finished.
        await Task.Delay(200);
        Assert.False(thirdStarted.Task.IsCompleted);

        releaseFirst.SetResult();
        await firstStop.WaitAsync(WaitLimit);
        Assert.True(await thirdStarted.Task.WaitAsync(WaitLimit));
        await queue.StopAsync().WaitAsync(WaitLimit);

        _writer.Verify(w => w.RemoveAsync("second", It.IsAny<CancellationToken>()), Times.Never);
    }

    private void VerifyLog(LogLevel level, string messageFragment, Exception? exception, Times times) =>
        _logger.Verify(
            l => l.Log(
                level,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v!.ToString()!.Contains(messageFragment)),
                It.Is<Exception?>(e => exception == null || ReferenceEquals(e, exception)),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            times);
}
