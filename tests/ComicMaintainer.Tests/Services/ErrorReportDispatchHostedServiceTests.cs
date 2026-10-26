using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using ComicMaintainer.Core.ErrorReporting;
using ComicMaintainer.WebApi.Services;
using Microsoft.Extensions.Logging;
using Moq;

namespace ComicMaintainer.Tests.Services;

public class ErrorReportDispatchHostedServiceTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly ListLogger _logger = new();
    private readonly Mock<IErrorReportingService> _reporting = new(MockBehavior.Strict);
    private readonly ConcurrentQueue<string> _processed = new();

    private static ErrorReport Report(string fingerprint) => new()
    {
        Fingerprint = fingerprint,
        Level = "Error",
        MessageTemplate = "Something failed",
        RenderedMessage = "Something failed",
        TimestampUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc)
    };

    private ErrorReportDispatchHostedService CreateService(IErrorReportQueue queue) =>
        new(queue, _reporting.Object, _logger);

    [Fact]
    public async Task ExecuteAsync_ProcessesReportsInOrder_AndContinuesAfterAFailure()
    {
        var queue = new FiniteQueue([Report("fp-1"), Report("fp-2"), Report("fp-3")]);
        _reporting
            .Setup(s => s.ProcessAsync(It.IsAny<ErrorReport>(), It.IsAny<CancellationToken>()))
            .Returns<ErrorReport, CancellationToken>((report, _) =>
            {
                _processed.Enqueue(report.Fingerprint);
                return report.Fingerprint == "fp-2"
                    ? Task.FromException<ErrorReportOutcome>(new InvalidOperationException("github down"))
                    : Task.FromResult(ErrorReportOutcome.Recorded);
            });

        using var service = CreateService(queue);
        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!.WaitAsync(Timeout);

        Assert.Equal(new[] { "fp-1", "fp-2", "fp-3" }, _processed.ToArray());
        var warning = Assert.Single(_logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Equal("Failed to process error report fp-2", warning.Message);
        Assert.Equal("github down", warning.Exception?.Message);
        // A failure must never be logged as an error: the reporting sink would feed it back into the queue.
        Assert.DoesNotContain(_logger.Entries, e => e.Level >= LogLevel.Error);
        Assert.Equal("Error report dispatcher started", _logger.Entries.First().Message);
        Assert.Equal("Error report dispatcher stopped", _logger.Entries.Last().Message);
    }

    [Fact]
    public async Task ExecuteAsync_TreatsCancellationNotCausedByShutdownAsAFailure()
    {
        var queue = new FiniteQueue([Report("fp-cancelled"), Report("fp-ok")]);
        _reporting
            .Setup(s => s.ProcessAsync(It.IsAny<ErrorReport>(), It.IsAny<CancellationToken>()))
            .Returns<ErrorReport, CancellationToken>((report, _) =>
            {
                _processed.Enqueue(report.Fingerprint);
                return report.Fingerprint == "fp-cancelled"
                    ? Task.FromException<ErrorReportOutcome>(new OperationCanceledException("http timeout"))
                    : Task.FromResult(ErrorReportOutcome.IssueCreated);
            });

        using var service = CreateService(queue);
        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!.WaitAsync(Timeout);

        Assert.Equal(new[] { "fp-cancelled", "fp-ok" }, _processed.ToArray());
        var warning = Assert.Single(_logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Equal("Failed to process error report fp-cancelled", warning.Message);
        Assert.IsType<OperationCanceledException>(warning.Exception);
        Assert.Equal("Error report dispatcher stopped", _logger.Entries.Last().Message);
    }

    [Fact]
    public async Task ExecuteAsync_ReportsDroppedCount_WhenQueueOverflowed()
    {
        var queue = new FiniteQueue([Report("fp-1")], droppedCount: 3);
        _reporting
            .Setup(s => s.ProcessAsync(It.IsAny<ErrorReport>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ErrorReportOutcome.Recorded);

        using var service = CreateService(queue);
        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!.WaitAsync(Timeout);

        var warning = Assert.Single(_logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Equal("3 error report(s) were dropped because the report queue was full", warning.Message);
        Assert.Equal("Error report dispatcher stopped", _logger.Entries.Last().Message);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyCompletedQueue_StopsWithoutProcessing()
    {
        using var service = CreateService(new FiniteQueue([]));

        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!.WaitAsync(Timeout);

        _reporting.VerifyNoOtherCalls();
        Assert.Equal(
            new[] { "Error report dispatcher started", "Error report dispatcher stopped" },
            _logger.Entries.Select(e => e.Message).ToArray());
    }

    [Fact]
    public async Task StopAsync_WhileReportIsBeingProcessed_StopsWithoutProcessingTheRest()
    {
        var queue = new ErrorReportQueue(capacity: 1);
        Assert.True(queue.TryEnqueue(Report("fp-slow")));
        Assert.False(queue.TryEnqueue(Report("fp-overflow")));

        var processing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _reporting
            .Setup(s => s.ProcessAsync(It.IsAny<ErrorReport>(), It.IsAny<CancellationToken>()))
            .Returns<ErrorReport, CancellationToken>(async (report, ct) =>
            {
                _processed.Enqueue(report.Fingerprint);
                processing.TrySetResult();
                await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, ct);
                return ErrorReportOutcome.Recorded;
            });

        using var service = CreateService(queue);
        await service.StartAsync(CancellationToken.None);
        await processing.Task.WaitAsync(Timeout);
        Assert.True(queue.TryEnqueue(Report("fp-pending")));

        await service.StopAsync(CancellationToken.None).WaitAsync(Timeout);

        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
        Assert.Equal(new[] { "fp-slow" }, _processed.ToArray());
        // Shutdown cancellation is not a processing failure, so only the overflow warning is logged.
        var warning = Assert.Single(_logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Equal("1 error report(s) were dropped because the report queue was full", warning.Message);
        Assert.Equal("Error report dispatcher stopped", _logger.Entries.Last().Message);
    }

    [Fact]
    public async Task StopAsync_WhileWaitingForReports_StopsCleanly()
    {
        var queue = new ErrorReportQueue();
        using var service = CreateService(queue);

        await service.StartAsync(CancellationToken.None);
        // Stopping before ExecuteAsync has been scheduled would cancel it without ever running it.
        await _logger.Started.WaitAsync(Timeout);
        await service.StopAsync(CancellationToken.None).WaitAsync(Timeout);

        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
        _reporting.VerifyNoOtherCalls();
        Assert.DoesNotContain(_logger.Entries, e => e.Level >= LogLevel.Warning);
        Assert.Equal("Error report dispatcher stopped", _logger.Entries.Last().Message);
    }

    /// <summary>Queue that yields a fixed set of reports and then completes.</summary>
    private sealed class FiniteQueue(IReadOnlyList<ErrorReport> reports, long droppedCount = 0) : IErrorReportQueue
    {
        public long DroppedCount => droppedCount;

        public bool TryEnqueue(ErrorReport report) => throw new NotSupportedException();

        public async IAsyncEnumerable<ErrorReport> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var report in reports)
            {
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                yield return report;
            }
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    private sealed class ListLogger : ILogger<ErrorReportDispatchHostedService>
    {
        private readonly ConcurrentQueue<LogEntry> _entries = new();
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<LogEntry> Entries => _entries.ToArray();

        public Task Started => _started.Task;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var entry = new LogEntry(logLevel, formatter(state, exception), exception);
            _entries.Enqueue(entry);
            if (entry.Message == "Error report dispatcher started")
            {
                _started.TrySetResult();
            }
        }
    }
}
