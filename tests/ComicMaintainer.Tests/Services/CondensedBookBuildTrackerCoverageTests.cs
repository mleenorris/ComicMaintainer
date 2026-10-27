using System.Collections.Concurrent;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Covers the queueing, pruning, progress and shutdown edges of
/// <see cref="CondensedBookBuildTracker"/> that <see cref="CondensedBookBuildTrackerTests"/>
/// does not reach.
/// </summary>
public class CondensedBookBuildTrackerCoverageTests : IDisposable
{
    private const string Owner = "user-1";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly string _workDir;
    private readonly Mock<IComicEmailService> _email = new(MockBehavior.Strict);
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
    private readonly CapturingLogger _logger = new();
    private readonly CondensedBookBuildTracker _tracker;

    public CondensedBookBuildTrackerCoverageTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"condense-build-cov-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);

        _email
            .Setup(e => e.ResolveCondensedFormatAsync(
                It.IsAny<string?>(),
                It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .Returns((string? format, int? _, CancellationToken _) =>
                Task.FromResult(EmailDeliveryFormat.NormalizeCondensedOrThrow(format, null, nameof(format))));

        _email
            .Setup(e => e.PlanCondensedDeliveryAsync(
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<string?>(),
                It.IsAny<int?>(),
                It.IsAny<bool>(),
                It.IsAny<int?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult(new CondensePlanDto(
                "all",
                2,
                2,
                25 * 1024 * 1024,
                new[] { new CondensedBookDto("Series 001-002", Files, 1024, false) },
                new Dictionary<string, string>())));

        _tracker = new CondensedBookBuildTracker(_email.Object, _logger, _clock, TimeSpan.FromMinutes(30));
    }

    private static IReadOnlyList<string> Files { get; } = new[]
    {
        "/comics/Series/Series - Chapter 0001.cbz",
        "/comics/Series/Series - Chapter 0002.cbz"
    };

    [Fact]
    public async Task ProductionConstructor_KeepsFinishedBuildsForTheDefaultRetention()
    {
        Assert.Equal(TimeSpan.FromMinutes(30), CondensedBookBuildTracker.Retention);

        var epub = CreateBook("Default retention");
        SetupBuild((_, _) => Task.FromResult(new CondensedBookFile(epub, Path.GetFileName(epub))));

        using var tracker = new CondensedBookBuildTracker(_email.Object, _logger);
        var started = await tracker.StartAsync(Request(), Owner);
        var finished = await WaitForAsync(tracker, started.BuildId, Owner, IsTerminal);

        Assert.Equal(CondensedBookBuildStatus.Completed, finished.Status);
        Assert.Equal(CondensedBookBuildTracker.Retention, finished.ExpiresAt!.Value - finished.CompletedAt!.Value);
        Assert.NotNull(tracker.GetCompletedFile(started.BuildId, Owner));
    }

    [Fact]
    public async Task Cancel_StopsABuildThatIsStillQueuedBehindAnother()
    {
        var epub = CreateBook("Series 001-002");
        var entered = NewSignal();
        var release = NewSignal();
        var calls = 0;
        SetupBuild(async (_, token) =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return new CondensedBookFile(epub, Path.GetFileName(epub));
        });

        var running = await _tracker.StartAsync(Request(), Owner);
        await entered.Task.WaitAsync(Timeout);

        // A different format is a different book, so it queues behind the
        // running build instead of joining it.
        var queued = await _tracker.StartAsync(Request(EmailDeliveryFormat.Azw3), Owner);
        Assert.NotEqual(running.BuildId, queued.BuildId);
        Assert.Equal(CondensedBookBuildStatus.Queued, queued.Status);

        Assert.True(_tracker.Cancel(queued.BuildId, Owner));

        var cancelled = await WaitForAsync(_tracker, queued.BuildId, Owner, IsTerminal);
        Assert.Equal(CondensedBookBuildStatus.Cancelled, cancelled.Status);
        Assert.Null(cancelled.StartedAt);
        Assert.NotNull(cancelled.ExpiresAt);

        release.SetResult();
        var finished = await WaitForAsync(_tracker, running.BuildId, Owner, IsTerminal);
        Assert.Equal(CondensedBookBuildStatus.Completed, finished.Status);
        Assert.Equal(1, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task Discard_WhileRunning_DeletesTheFileTheBuildStillProduced()
    {
        var epub = CreateBook("Late book");
        var entered = NewSignal();
        var release = NewSignal();
        SetupBuild(async (_, _) =>
        {
            entered.TrySetResult();

            // Ignores the cancellation on purpose: the book is finished just as
            // the discard arrives.
            await release.Task;
            return new CondensedBookFile(epub, Path.GetFileName(epub));
        });

        var started = await _tracker.StartAsync(Request(), Owner);
        await entered.Task.WaitAsync(Timeout);

        Assert.True(_tracker.Discard(started.BuildId, Owner));
        Assert.Null(_tracker.Get(started.BuildId, Owner));
        Assert.False(_tracker.Cancel(started.BuildId, Owner));
        Assert.True(File.Exists(epub));

        release.SetResult();

        // Dispose waits for the worker, whose cleanup removes the orphaned book.
        _tracker.Dispose();

        Assert.False(Directory.Exists(Path.GetDirectoryName(epub)));
        Assert.Contains(_logger.Entries, e => e.Message.Contains("finished", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FinishedBuildsBeyondThePerUserLimitAreDroppedOldestFirst()
    {
        var books = new List<string>();
        SetupBuild((_, _) =>
        {
            var path = CreateBook($"Book {books.Count}");
            books.Add(path);
            return Task.FromResult(new CondensedBookFile(path, Path.GetFileName(path)));
        });

        // Another user's build is older than all of them but does not count
        // towards this user's limit.
        var otherUsers = await _tracker.StartAsync(Request(), "user-2");
        await WaitForAsync(_tracker, otherUsers.BuildId, "user-2", IsTerminal);
        _clock.Advance(TimeSpan.FromSeconds(1));

        var ids = new List<Guid>();
        for (var i = 0; i < 21; i++)
        {
            var started = await _tracker.StartAsync(Request(), Owner);
            await WaitForAsync(_tracker, started.BuildId, Owner, IsTerminal);
            ids.Add(started.BuildId);
            _clock.Advance(TimeSpan.FromSeconds(1));
        }

        var remaining = _tracker.List(Owner);

        Assert.Equal(20, remaining.Count);
        Assert.DoesNotContain(remaining, b => b.BuildId == ids[0]);
        Assert.Null(_tracker.Get(ids[0], Owner));
        Assert.Equal(ids.Skip(1).Reverse(), remaining.Select(b => b.BuildId));

        // books[0] belongs to the other user; books[1] is this user's oldest.
        Assert.True(File.Exists(books[0]));
        Assert.False(Directory.Exists(Path.GetDirectoryName(books[1])));
        Assert.True(File.Exists(books[2]));
        Assert.Single(_tracker.List("user-2"));
    }

    [Fact]
    public async Task Progress_TranslatesEveryPhaseAndIsIgnoredOnceTheBuildFinished()
    {
        var epub = CreateBook("Series 001-002");
        var steps = new (EpubConversionPhase Phase, string Expected)[]
        {
            (EpubConversionPhase.Reading, "reading"),
            (EpubConversionPhase.Validating, "validating"),
            (EpubConversionPhase.Recompressing, "recompressing"),
            ((EpubConversionPhase)99, "writing")
        };
        var reported = new SemaphoreSlim(0);
        var proceed = new SemaphoreSlim(0);
        IProgress<EpubConversionProgress>? captured = null;

        SetupBuild(async (progress, token) =>
        {
            captured = progress;
            for (var i = 0; i < steps.Length; i++)
            {
                progress!.Report(new EpubConversionProgress(steps[i].Phase, i + 1, 8, 1, 2, "Series - Chapter 0001.cbz", 2, 4));
                reported.Release();
                await proceed.WaitAsync(token);
            }

            return new CondensedBookFile(epub, Path.GetFileName(epub));
        });

        var started = await _tracker.StartAsync(Request(), Owner);

        for (var i = 0; i < steps.Length; i++)
        {
            Assert.True(await reported.WaitAsync(Timeout));
            var snapshot = _tracker.Get(started.BuildId, Owner)!;
            Assert.Equal(steps[i].Expected, snapshot.Progress.Phase);
            Assert.Equal(i + 1, snapshot.Progress.CompletedPages);
            Assert.Equal(2, snapshot.Progress.Pass);
            Assert.Equal(4, snapshot.Progress.TotalPasses);
            proceed.Release();
        }

        var finished = await WaitForAsync(_tracker, started.BuildId, Owner, IsTerminal);
        Assert.Equal("completed", finished.Progress.Phase);
        Assert.Equal(8, finished.Progress.CompletedPages);

        // A late report (e.g. from a converter that kept a reference to the
        // sink) must not move a finished build back into a working phase.
        captured!.Report(new EpubConversionProgress(EpubConversionPhase.Writing, 1, 99, 0, 2, "late.cbz", 1, 1));

        var after = _tracker.Get(started.BuildId, Owner)!;
        Assert.Equal("completed", after.Progress.Phase);
        Assert.Equal(8, after.Progress.TotalPages);
        Assert.Equal(CondensedBookBuildStatus.Completed, after.Status);
    }

    [Fact]
    public async Task StartAsync_WithoutAnOwnerOrCondenseMode_TracksTheBuildForAnonymousCallers()
    {
        var epub = CreateBook("Anonymous");
        var release = NewSignal();
        SetupBuild(async (_, token) =>
        {
            await release.Task.WaitAsync(token);
            return new CondensedBookFile(epub, Path.GetFileName(epub));
        });

        var request = new CondensedBookBuildRequest(Files, null, 3, 0);
        var started = await _tracker.StartAsync(request, null);
        var joined = await _tracker.StartAsync(request, null);

        Assert.Equal(started.BuildId, joined.BuildId);
        Assert.Null(_tracker.Get(started.BuildId, Owner));
        Assert.Contains(_tracker.List(null), b => b.BuildId == started.BuildId);
        Assert.DoesNotContain(_tracker.List(Owner), b => b.BuildId == started.BuildId);

        release.SetResult();
        var finished = await WaitForAsync(_tracker, started.BuildId, null, IsTerminal);
        Assert.Equal(CondensedBookBuildStatus.Completed, finished.Status);
    }

    [Fact]
    public async Task Dispose_LogsACancellationCallbackThatThrowsAndStillStopsTheBuild()
    {
        var entered = NewSignal();
        SetupBuild(async (_, token) =>
        {
            token.Register(() => throw new InvalidOperationException("callback failed"));
            entered.TrySetResult();
            await Task.Delay(System.Threading.Timeout.Infinite, token);
            throw new InvalidOperationException("The build should have been cancelled.");
        });

        var started = await _tracker.StartAsync(Request(), Owner);
        await entered.Task.WaitAsync(Timeout);

        _tracker.Dispose();

        Assert.Contains(_logger.Entries, e =>
            e.Level == LogLevel.Warning
            && e.Message.Contains("while cancelling condensed book builds during shutdown", StringComparison.Ordinal)
            && e.Exception is AggregateException);
        Assert.Contains(_logger.Entries, e =>
            e.Level == LogLevel.Information
            && e.Message.Contains($"{started.BuildId} was cancelled", StringComparison.Ordinal));
        Assert.Empty(_tracker.List(Owner));
    }

    [Fact]
    public async Task Dispose_LogsAWorkerThatFailedWhileShuttingDown()
    {
        var logger = new CapturingLogger(message => message.Contains("was cancelled", StringComparison.Ordinal));
        var tracker = new CondensedBookBuildTracker(_email.Object, logger, _clock, TimeSpan.FromMinutes(30));
        var entered = NewSignal();
        SetupBuild(async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(System.Threading.Timeout.Infinite, token);
            throw new InvalidOperationException("The build should have been cancelled.");
        });

        await tracker.StartAsync(Request(), Owner);
        await entered.Task.WaitAsync(Timeout);

        tracker.Dispose();

        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Error
            && e.Message.Contains("worker failed during shutdown", StringComparison.Ordinal)
            && e.Exception is InvalidOperationException);
        Assert.Empty(tracker.List(Owner));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => tracker.StartAsync(Request(), Owner));
    }

    private static bool IsTerminal(CondensedBookBuildDto build) =>
        CondensedBookBuildStatus.IsTerminal(build.Status);

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static CondensedBookBuildRequest Request(string? deliveryFormat = null) =>
        new(Files, "all", null, 0, DeliveryFormat: deliveryFormat);

    private void SetupBuild(
        Func<IProgress<EpubConversionProgress>?, CancellationToken, Task<CondensedBookFile>> build)
    {
        _email
            .Setup(e => e.CreateCondensedBookAsync(
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<string?>(),
                It.IsAny<int?>(),
                It.IsAny<int>(),
                It.IsAny<bool>(),
                It.IsAny<int?>(),
                It.IsAny<bool>(),
                It.IsAny<IProgress<EpubConversionProgress>?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Returns((
                IEnumerable<string> _,
                string? _,
                int? _,
                int _,
                bool _,
                int? _,
                bool _,
                IProgress<EpubConversionProgress>? progress,
                string? _,
                CancellationToken token) => build(progress, token));
    }

    private string CreateBook(string name)
    {
        var directory = Path.Combine(_workDir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{name}.epub");
        File.WriteAllText(path, "epub bytes");
        return path;
    }

    private static async Task<CondensedBookBuildDto> WaitForAsync(
        CondensedBookBuildTracker tracker,
        Guid buildId,
        string? owner,
        Func<CondensedBookBuildDto, bool> predicate)
    {
        var deadline = DateTime.UtcNow.Add(Timeout);
        while (DateTime.UtcNow < deadline)
        {
            var build = tracker.Get(buildId, owner);
            if (build is not null && predicate(build))
            {
                return build;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"Build {buildId} never reached the expected state.");
    }

    public void Dispose()
    {
        _tracker.Dispose();

        try
        {
            if (Directory.Exists(_workDir))
            {
                Directory.Delete(_workDir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort cleanup.
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    /// <summary>Records every log call and optionally fails on selected messages.</summary>
    private sealed class CapturingLogger : ILogger<CondensedBookBuildTracker>
    {
        private readonly Func<string, bool>? _throwWhen;
        private readonly ConcurrentQueue<LogEntry> _entries = new();

        public CapturingLogger(Func<string, bool>? throwWhen = null) => _throwWhen = throwWhen;

        public IReadOnlyCollection<LogEntry> Entries => _entries.ToArray();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            _entries.Enqueue(new LogEntry(logLevel, message, exception));
            if (_throwWhen?.Invoke(message) == true)
            {
                throw new InvalidOperationException($"Logging '{message}' failed.");
            }
        }
    }
}
