using System.Diagnostics.Metrics;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Core.Telemetry;
using ComicMaintainer.WebApi.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Tests for the hosted runner of the scheduled-jobs framework: manual runs,
/// timer-driven runs, single-flight, cancellation at shutdown and re-arming when
/// a job's schedule changes.
/// </summary>
/// <remarks>
/// The runner fires its work with <c>Task.Run</c> and real <see cref="Timer"/>s, so
/// every wait here is on a signal (a log line written after the outcome was
/// persisted, or the persisted row itself) rather than on elapsed time.
/// </remarks>
public sealed class ScheduledJobsHostedServiceTests : IDisposable
{
    private const string JobKey = "test-job";
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(30);

    private readonly TestDbContextFactory _dbFactory = new();
    private readonly RecordingLogger _logger = new();
    private readonly List<ScheduledJobsHostedService> _hostedServices = new();
    private readonly List<TaskCompletionSource> _gates = new();
    private readonly List<IDisposable> _disposables = new();

    public void Dispose()
    {
        foreach (var gate in _gates)
        {
            gate.TrySetResult();
        }
        foreach (var hosted in _hostedServices)
        {
            hosted.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
            hosted.Dispose();
        }
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }
    }

    [Fact]
    public async Task RunNowAsync_ForUnknownJob_ReturnsFalseAndRunsNothing()
    {
        var handler = new TestJobHandler(JobKey, (_, _) => Task.FromResult("ok"));
        var (hosted, _) = CreateHostedService(handler);

        var queued = await hosted.RunNowAsync("no-such-job");

        Assert.False(queued);
        Assert.Equal(0, handler.Invocations);
    }

    [Fact]
    public async Task RunNowAsync_RunsTheJobAndRecordsSuccess()
    {
        SeedJob(enabled: false, optionsJson: "{\"depth\":2}");
        var handler = new TestJobHandler(JobKey, (_, _) => Task.FromResult("checked 3 files"));
        using var metrics = new ComicMaintainerMetrics();
        using var recorder = new MetricRecorder(metrics.Meter);
        var (hosted, _) = CreateHostedService(handler, metrics);
        await hosted.StartAsync(CancellationToken.None);

        var queued = await hosted.RunNowAsync(JobKey);
        await _logger.WaitForAsync(e => e.Message.Contains("completed in", StringComparison.Ordinal));

        Assert.True(queued);
        var call = Assert.Single(handler.Calls);
        Assert.Equal("{\"depth\":2}", call.OptionsJson);
        Assert.True(call.Token.CanBeCanceled, "Runs started by the host must observe its shutdown token.");

        var row = LoadJob();
        Assert.Equal(nameof(ScheduledJobStatus.Success), row.LastStatus);
        Assert.Equal("checked 3 files", row.LastMessage);
        Assert.NotNull(row.LastRunUtc);
        Assert.True(row.LastDurationMs >= 0);
        Assert.Contains(recorder.Snapshot(), m =>
            m.Instrument == "comicmaintainer.scheduled_job.runs"
            && (string?)m.Tags["job"] == JobKey
            && (string?)m.Tags["outcome"] == "success");
    }

    [Fact]
    public async Task RunNowAsync_BeforeTheHostHasStarted_StillRunsTheJob()
    {
        SeedJob(enabled: false);
        var handler = new TestJobHandler(JobKey, (_, _) => Task.FromResult("early run"));
        var (hosted, _) = CreateHostedService(handler);

        var queued = await hosted.RunNowAsync(JobKey);
        await _logger.WaitForAsync(e => e.Message.Contains("completed in", StringComparison.Ordinal));

        Assert.True(queued);
        Assert.Equal(1, handler.Invocations);
        var row = LoadJob();
        Assert.Equal(nameof(ScheduledJobStatus.Success), row.LastStatus);
        Assert.Equal("early run", row.LastMessage);
    }

    [Fact]
    public async Task RunNowAsync_WhenTheHandlerThrows_RecordsTheFailure()
    {
        SeedJob(enabled: false);
        var handler = new TestJobHandler(
            JobKey,
            (_, _) => Task.FromException<string>(new InvalidOperationException("disk full")));
        using var metrics = new ComicMaintainerMetrics();
        using var recorder = new MetricRecorder(metrics.Meter);
        var (hosted, _) = CreateHostedService(handler, metrics);
        await hosted.StartAsync(CancellationToken.None);

        await hosted.RunNowAsync(JobKey);
        var logged = await _logger.WaitForAsync(e => e.Level == LogLevel.Error);

        Assert.Contains("failed", logged.Message, StringComparison.Ordinal);
        Assert.IsType<InvalidOperationException>(logged.Exception);
        var row = LoadJob();
        Assert.Equal(nameof(ScheduledJobStatus.Failed), row.LastStatus);
        Assert.Equal("disk full", row.LastMessage);
        Assert.Contains(recorder.Snapshot(), m =>
            m.Instrument == "comicmaintainer.scheduled_job.runs" && (string?)m.Tags["outcome"] == "failed");
    }

    [Fact]
    public async Task RunNowAsync_HandlerCancellingItselfWithoutShutdown_IsRecordedAsAFailure()
    {
        SeedJob(enabled: false);
        var handler = new TestJobHandler(
            JobKey,
            (_, _) => Task.FromException<string>(new OperationCanceledException("provider gave up")));
        var (hosted, _) = CreateHostedService(handler);
        await hosted.StartAsync(CancellationToken.None);

        await hosted.RunNowAsync(JobKey);
        var logged = await _logger.WaitForAsync(e => e.Level == LogLevel.Error);

        Assert.IsType<OperationCanceledException>(logged.Exception);
        var row = LoadJob();
        Assert.Equal(nameof(ScheduledJobStatus.Failed), row.LastStatus);
        Assert.Equal("provider gave up", row.LastMessage);
    }

    [Fact]
    public async Task StopAsync_CancelsARunningJob_AndRecordsItAsCancelled()
    {
        SeedJob(enabled: false);
        var started = NewGate();
        var handler = new TestJobHandler(JobKey, async (_, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return "unreachable";
        });
        using var metrics = new ComicMaintainerMetrics();
        using var recorder = new MetricRecorder(metrics.Meter);
        var (hosted, _) = CreateHostedService(handler, metrics);
        await hosted.StartAsync(CancellationToken.None);

        await hosted.RunNowAsync(JobKey);
        await started.Task.WaitAsync(SignalTimeout);
        await hosted.StopAsync(CancellationToken.None);
        await _logger.WaitForAsync(e => e.Message.Contains("was cancelled", StringComparison.Ordinal));

        var row = LoadJob();
        Assert.Equal(nameof(ScheduledJobStatus.Cancelled), row.LastStatus);
        Assert.Equal("Run cancelled.", row.LastMessage);
        Assert.Contains(recorder.Snapshot(), m =>
            m.Instrument == "comicmaintainer.scheduled_job.runs" && (string?)m.Tags["outcome"] == "cancelled");
    }

    [Fact]
    public async Task RunNowAsync_WhileARunIsInProgress_ReturnsFalseAndSkipsTheOverlappingRun()
    {
        SeedJob(enabled: false);
        var started = NewGate();
        var release = NewGate();
        var handler = new TestJobHandler(JobKey, async (_, _) =>
        {
            if (started.TrySetResult())
            {
                await release.Task;
                return "first run";
            }
            return "second run";
        });
        var (hosted, _) = CreateHostedService(handler);
        await hosted.StartAsync(CancellationToken.None);

        var first = await hosted.RunNowAsync(JobKey);
        await started.Task.WaitAsync(SignalTimeout);
        var overlapping = await hosted.RunNowAsync(JobKey.ToUpperInvariant());

        // The overlapping request is refused up front, so the caller learns it was not
        // queued. It is not recorded as "Skipped": the in-flight run keeps its Running status.
        Assert.True(first);
        Assert.False(overlapping);
        Assert.Contains(_logger.Entries, e => e.Message.Contains(
            $"Skipping {JobKey.ToUpperInvariant()} run because previous run still in progress", StringComparison.Ordinal));
        Assert.Equal(nameof(ScheduledJobStatus.Running), LoadJob().LastStatus);

        release.TrySetResult();
        await _logger.WaitForAsync(e => e.Message.Contains("completed in", StringComparison.Ordinal));

        Assert.Equal(1, handler.Invocations);
        Assert.Equal(nameof(ScheduledJobStatus.Success), LoadJob().LastStatus);

        // The finished run released the job, so the next request runs it again.
        Assert.True(await hosted.RunNowAsync(JobKey));
        await _logger.WaitForAsync(e => e.Message.Contains("second run", StringComparison.Ordinal));
        Assert.Equal(2, handler.Invocations);
    }

    [Fact]
    public async Task RunNowAsync_WithAnAlreadyCancelledToken_StartsNothing()
    {
        SeedJob(enabled: false);
        var handler = new TestJobHandler(JobKey, (_, _) => Task.FromResult("ran"));
        var (hosted, _) = CreateHostedService(handler);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => hosted.RunNowAsync(JobKey, cancelled.Token));

        // Nothing was started and nothing holds the job: the next request runs it.
        Assert.Equal(nameof(ScheduledJobStatus.NeverRun), LoadJob().LastStatus);
        Assert.True(await hosted.RunNowAsync(JobKey));
        await _logger.WaitForAsync(e => e.Message.Contains("completed in", StringComparison.Ordinal));
        Assert.Equal(1, handler.Invocations);
    }

    [Fact]
    public async Task Timer_RunsADueJob_WithItsStoredOptions()
    {
        SeedJob(enabled: true, intervalMinutes: 60, nextRunUtc: DateTime.UtcNow.AddMilliseconds(100),
            optionsJson: "{\"mode\":\"timer\"}");
        var handler = new TestJobHandler(JobKey, (_, _) => Task.FromResult("timer run"));
        var (hosted, _) = CreateHostedService(handler);

        await hosted.StartAsync(CancellationToken.None);
        await _logger.WaitForAsync(e => e.Message.Contains("completed in", StringComparison.Ordinal));

        var call = Assert.Single(handler.Calls);
        Assert.Equal("{\"mode\":\"timer\"}", call.OptionsJson);
        var row = LoadJob();
        Assert.Equal(nameof(ScheduledJobStatus.Success), row.LastStatus);
        Assert.Equal("timer run", row.LastMessage);
        Assert.NotNull(row.NextRunUtc);
        Assert.True(row.NextRunUtc > DateTime.UtcNow.AddMinutes(30), "The next run is one interval after this one.");
    }

    [Fact]
    public async Task Timer_FiringWhileARunIsInProgress_RecordsASkippedRun()
    {
        SeedJob(enabled: true, intervalMinutes: 60, nextRunUtc: DateTime.UtcNow.AddMilliseconds(300));
        var started = NewGate();
        var release = NewGate();
        var handler = new TestJobHandler(JobKey, async (_, _) =>
        {
            started.TrySetResult();
            await release.Task;
            return "long run";
        });
        using var metrics = new ComicMaintainerMetrics();
        using var recorder = new MetricRecorder(metrics.Meter);
        var (hosted, _) = CreateHostedService(handler, metrics);
        var skipped = _dbFactory.WaitForSavedStatusAsync(JobKey, ScheduledJobStatus.Skipped);

        // Occupy the job's single-flight slot, then let the host arm the timer.
        await hosted.RunNowAsync(JobKey);
        await started.Task.WaitAsync(SignalTimeout);
        await hosted.StartAsync(CancellationToken.None);

        var skippedRow = await skipped.WaitAsync(SignalTimeout);
        Assert.Equal("Skipped: previous run still in progress.", skippedRow.LastMessage);
        Assert.Equal(0, skippedRow.LastDurationMs);
        Assert.Contains(recorder.Snapshot(), m =>
            m.Instrument == "comicmaintainer.scheduled_job.runs" && (string?)m.Tags["outcome"] == "skipped");

        release.TrySetResult();
        await _logger.WaitForAsync(e => e.Message.Contains("completed in", StringComparison.Ordinal));
        Assert.Equal(1, handler.Invocations);
    }

    [Fact]
    public async Task StartAsync_WithAnOverdueJob_ArmsItToRunShortly()
    {
        SeedJob(enabled: true, intervalMinutes: 60, nextRunUtc: DateTime.UtcNow.AddHours(-2));
        var handler = new TestJobHandler(JobKey, (_, _) => Task.FromResult("ok"));
        var (hosted, _) = CreateHostedService(handler);

        await hosted.StartAsync(CancellationToken.None);

        Assert.Contains(_logger.Entries, e => e.Message.Contains(
            $"Scheduled job {JobKey} armed: first run in 5s, interval 60m", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartAsync_WithoutANextRunTime_WaitsOneFullInterval()
    {
        SeedJob(enabled: true, intervalMinutes: 15, nextRunUtc: null);
        var handler = new TestJobHandler(JobKey, (_, _) => Task.FromResult("ok"));
        var (hosted, _) = CreateHostedService(handler);

        await hosted.StartAsync(CancellationToken.None);

        Assert.Contains(_logger.Entries, e => e.Message.Contains(
            $"Scheduled job {JobKey} armed: first run in 900s, interval 15m", StringComparison.Ordinal));
        Assert.Contains(_logger.Entries, e => e.Message.Contains("started with 1 job(s)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task JobChanged_RearmsTheTimerWithTheNewSchedule()
    {
        SeedJob(enabled: true, intervalMinutes: 60, nextRunUtc: DateTime.UtcNow.AddHours(1));
        var handler = new TestJobHandler(JobKey, (_, _) => Task.FromResult("ok"));
        var (hosted, service) = CreateHostedService(handler);
        await hosted.StartAsync(CancellationToken.None);

        await service.UpdateAsync(JobKey, enabled: true, intervalMinutes: 120, optionsJson: null);
        var rearmed = await _logger.WaitForAsync(e => e.Message.Contains("interval 120m", StringComparison.Ordinal));
        Assert.Contains($"Scheduled job {JobKey} armed", rearmed.Message, StringComparison.Ordinal);

        await service.UpdateAsync(JobKey, enabled: false, intervalMinutes: 120, optionsJson: null);
        await _logger.WaitForAsync(e => e.Message.Contains("disabled; timer not armed", StringComparison.Ordinal));
        Assert.Equal(0, handler.Invocations);
    }

    [Fact]
    public async Task JobChanged_WhenReloadingTheJobFails_LogsAndCarriesOn()
    {
        SeedJob(enabled: true, intervalMinutes: 60, nextRunUtc: DateTime.UtcNow.AddHours(1));
        var handler = new TestJobHandler(JobKey, (_, _) => Task.FromResult("ok"));
        var (hosted, service) = CreateHostedService(handler);
        // Subscribed before the host, so it runs first: the database goes away
        // between the update being saved and the host reloading the job.
        service.JobChanged += (_, _) => _dbFactory.FailCreation = true;
        await hosted.StartAsync(CancellationToken.None);

        await service.UpdateAsync(JobKey, enabled: true, intervalMinutes: 30, optionsJson: null);
        var logged = await _logger.WaitForAsync(e => e.Level == LogLevel.Error);

        Assert.Contains($"Failed to re-arm timer for {JobKey}", logged.Message, StringComparison.Ordinal);
        Assert.IsType<InvalidOperationException>(logged.Exception);
    }

    [Fact]
    public async Task JobChanged_ForAJobWhoseDefinitionVanished_DropsItsTimerQuietly()
    {
        SeedJob(enabled: true, intervalMinutes: 60, nextRunUtc: DateTime.UtcNow.AddHours(1), optionsJson: "{}");
        var handler = new TestJobHandler(JobKey, (_, _) => Task.FromResult("ran without a definition"));
        var (hosted, service) = CreateHostedService(handler);
        Task? reloadFinished = null;
        service.JobChanged += (_, _) =>
        {
            using (var db = _dbFactory.CreateDbContext())
            {
                db.ScheduledJobs.RemoveRange(db.ScheduledJobs);
                db.SaveChanges();
            }
            // The next context opened is the host reloading the job.
            reloadFinished = _dbFactory.WhenNextContextDisposedAsync();
        };
        await hosted.StartAsync(CancellationToken.None);

        await service.UpdateAsync(JobKey, enabled: true, intervalMinutes: 30, optionsJson: null);
        await reloadFinished!.WaitAsync(SignalTimeout);

        // The host is still usable: a manual run goes ahead without stored options.
        await hosted.RunNowAsync(JobKey);
        await _logger.WaitForAsync(e => e.Message.Contains("completed in", StringComparison.Ordinal));

        var call = Assert.Single(handler.Calls);
        Assert.Null(call.OptionsJson);
        Assert.DoesNotContain(_logger.Entries, e => e.Level == LogLevel.Error);
        using var verify = _dbFactory.CreateDbContext();
        Assert.Empty(verify.ScheduledJobs);
    }

    [Fact]
    public async Task JobChanged_ArrivingWhileTheHostStops_IsIgnored()
    {
        SeedJob(enabled: true, intervalMinutes: 60, nextRunUtc: DateTime.UtcNow.AddHours(1));
        var handler = new TestJobHandler(JobKey, (_, _) => Task.FromResult("ok"));
        var (hosted, service) = CreateHostedService(handler);
        // Runs before the host's own subscriber, simulating shutdown racing an
        // API update: the host has been stopped by the time it is notified.
        service.JobChanged += (_, _) => hosted.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        await hosted.StartAsync(CancellationToken.None);
        var armedBefore = _logger.Entries.Count(e => e.Message.Contains("armed", StringComparison.Ordinal));

        await service.UpdateAsync(JobKey, enabled: true, intervalMinutes: 45, optionsJson: null);
        // A later update is not even delivered: the host unsubscribed on stop.
        await service.UpdateAsync(JobKey, enabled: true, intervalMinutes: 50, optionsJson: null);

        Assert.Equal(1, armedBefore);
        Assert.DoesNotContain(_logger.Entries, e => e.Message.Contains("interval 45m", StringComparison.Ordinal));
        Assert.DoesNotContain(_logger.Entries, e => e.Message.Contains("interval 50m", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Dispose_AfterStart_ReleasesTimers_AndALaterStopIsHarmless()
    {
        SeedJob(enabled: true, intervalMinutes: 60, nextRunUtc: DateTime.UtcNow.AddHours(1));
        var handler = new TestJobHandler(JobKey, (_, _) => Task.FromResult("ok"));
        var (hosted, _) = CreateHostedService(handler);
        await hosted.StartAsync(CancellationToken.None);

        hosted.Dispose();
        var stop = await Record.ExceptionAsync(() => hosted.StopAsync(CancellationToken.None));

        Assert.Null(stop);
        Assert.Equal(0, handler.Invocations);
    }

    [Fact]
    public async Task Dispose_AfterStartWithADisabledJob_ReleasesItsUnarmedSlot()
    {
        SeedJob(enabled: false);
        var handler = new TestJobHandler(JobKey, (_, _) => Task.FromResult("ok"));
        var (hosted, _) = CreateHostedService(handler);
        await hosted.StartAsync(CancellationToken.None);

        var dispose = Record.Exception(hosted.Dispose);

        Assert.Null(dispose);
        Assert.Contains(_logger.Entries, e => e.Message.Contains("disabled; timer not armed", StringComparison.Ordinal));
        Assert.Equal(0, handler.Invocations);
    }

    [Fact]
    public async Task Timer_FiringWhileARunIsInProgress_WithoutMetrics_StillRecordsTheSkip()
    {
        SeedJob(enabled: true, intervalMinutes: 60, nextRunUtc: DateTime.UtcNow.AddMilliseconds(300));
        var started = NewGate();
        var release = NewGate();
        var handler = new TestJobHandler(JobKey, async (_, _) =>
        {
            started.TrySetResult();
            await release.Task;
            return "long run";
        });
        var (hosted, _) = CreateHostedService(handler);
        var skipped = _dbFactory.WaitForSavedStatusAsync(JobKey, ScheduledJobStatus.Skipped);

        await hosted.RunNowAsync(JobKey);
        await started.Task.WaitAsync(SignalTimeout);
        await hosted.StartAsync(CancellationToken.None);

        var skippedRow = await skipped.WaitAsync(SignalTimeout);
        Assert.Equal("Skipped: previous run still in progress.", skippedRow.LastMessage);

        release.TrySetResult();
        await _logger.WaitForAsync(e => e.Message.Contains("completed in", StringComparison.Ordinal));
        Assert.Equal(1, handler.Invocations);
    }

    [Fact]
    public async Task StopAsync_CancelsARunningJob_WithoutMetrics_StillRecordsTheCancellation()
    {
        SeedJob(enabled: false);
        var started = NewGate();
        var handler = new TestJobHandler(JobKey, async (_, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return "unreachable";
        });
        var (hosted, _) = CreateHostedService(handler);
        await hosted.StartAsync(CancellationToken.None);

        await hosted.RunNowAsync(JobKey);
        await started.Task.WaitAsync(SignalTimeout);
        await hosted.StopAsync(CancellationToken.None);
        await _logger.WaitForAsync(e => e.Message.Contains("was cancelled", StringComparison.Ordinal));

        var row = LoadJob();
        Assert.Equal(nameof(ScheduledJobStatus.Cancelled), row.LastStatus);
        Assert.Equal("Run cancelled.", row.LastMessage);
    }

    [Fact]
    public async Task StopAndDispose_WithoutStart_AreNoOps()
    {
        var handler = new TestJobHandler(JobKey, (_, _) => Task.FromResult("ok"));
        var hosted = new ScheduledJobsHostedService(
            new ScheduledJobService(_dbFactory, new[] { handler }, NullLogger<ScheduledJobService>.Instance),
            _logger);

        var stop = await Record.ExceptionAsync(() => hosted.StopAsync(CancellationToken.None));
        var dispose = Record.Exception(hosted.Dispose);

        Assert.Null(stop);
        Assert.Null(dispose);
    }

    private (ScheduledJobsHostedService Hosted, ScheduledJobService Service) CreateHostedService(
        IScheduledJobHandler handler,
        ComicMaintainerMetrics? metrics = null)
    {
        var service = new ScheduledJobService(_dbFactory, new[] { handler }, NullLogger<ScheduledJobService>.Instance);
        var hosted = new ScheduledJobsHostedService(service, _logger, metrics);
        _hostedServices.Add(hosted);
        return (hosted, service);
    }

    private TaskCompletionSource NewGate()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _gates.Add(gate);
        return gate;
    }

    private void SeedJob(bool enabled, int intervalMinutes = 60, DateTime? nextRunUtc = null, string? optionsJson = null)
    {
        using var db = _dbFactory.CreateDbContext();
        db.ScheduledJobs.Add(new ScheduledJobEntity
        {
            JobKey = JobKey,
            Enabled = enabled,
            IntervalMinutes = intervalMinutes,
            NextRunUtc = nextRunUtc,
            OptionsJson = optionsJson,
            LastStatus = nameof(ScheduledJobStatus.NeverRun)
        });
        db.SaveChanges();
    }

    private ScheduledJobEntity LoadJob()
    {
        using var db = _dbFactory.CreateDbContext();
        return db.ScheduledJobs.AsNoTracking().Single(j => j.JobKey == JobKey);
    }

    private sealed class TestJobHandler : IScheduledJobHandler
    {
        private readonly Func<string?, CancellationToken, Task<string>> _run;
        private readonly List<(string? OptionsJson, CancellationToken Token)> _calls = new();

        public TestJobHandler(string jobKey, Func<string?, CancellationToken, Task<string>> run)
        {
            JobKey = jobKey;
            _run = run;
        }

        public string JobKey { get; }
        public string DisplayName => "Test job";
        public string Description => "A job used by tests.";
        public ScheduledJobDefaults Defaults { get; } = new(Enabled: false, IntervalMinutes: 60);

        public int Invocations
        {
            get
            {
                lock (_calls)
                {
                    return _calls.Count;
                }
            }
        }

        public IReadOnlyList<(string? OptionsJson, CancellationToken Token)> Calls
        {
            get
            {
                lock (_calls)
                {
                    return _calls.ToList();
                }
            }
        }

        public Task<string> ExecuteAsync(string? optionsJson, CancellationToken cancellationToken)
        {
            lock (_calls)
            {
                _calls.Add((optionsJson, cancellationToken));
            }
            return _run(optionsJson, cancellationToken);
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    /// <summary>Captures log lines and lets a test await a specific one.</summary>
    private sealed class RecordingLogger : ILogger<ScheduledJobsHostedService>
    {
        private readonly object _gate = new();
        private readonly List<LogEntry> _entries = new();
        private readonly List<(Func<LogEntry, bool> Predicate, TaskCompletionSource<LogEntry> Signal)> _waiters = new();

        public IReadOnlyList<LogEntry> Entries
        {
            get
            {
                lock (_gate)
                {
                    return _entries.ToList();
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var entry = new LogEntry(logLevel, formatter(state, exception), exception);
            var matched = new List<TaskCompletionSource<LogEntry>>();
            lock (_gate)
            {
                _entries.Add(entry);
                for (var i = _waiters.Count - 1; i >= 0; i--)
                {
                    if (_waiters[i].Predicate(entry))
                    {
                        matched.Add(_waiters[i].Signal);
                        _waiters.RemoveAt(i);
                    }
                }
            }
            foreach (var signal in matched)
            {
                signal.TrySetResult(entry);
            }
        }

        public Task<LogEntry> WaitForAsync(Func<LogEntry, bool> predicate)
        {
            lock (_gate)
            {
                var existing = _entries.FirstOrDefault(predicate);
                if (existing is not null)
                {
                    return Task.FromResult(existing);
                }
                var signal = new TaskCompletionSource<LogEntry>(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((predicate, signal));
                return signal.Task.WaitAsync(SignalTimeout);
            }
        }
    }

    private sealed record SavedJob(string JobKey, string LastStatus, string? LastMessage, long? LastDurationMs);

    /// <summary>
    /// In-memory database whose contexts report every saved job row and their own
    /// disposal, so tests can wait for fire-and-forget work deterministically.
    /// </summary>
    private sealed class TestDbContextFactory : IDbContextFactory<ComicMaintainerDbContext>
    {
        private readonly DbContextOptions<ComicMaintainerDbContext> _options =
            new DbContextOptionsBuilder<ComicMaintainerDbContext>()
                .UseInMemoryDatabase($"ScheduledJobsHostedServiceTests_{Guid.NewGuid()}")
                .Options;
        private readonly object _gate = new();
        private readonly List<SavedJob> _saved = new();
        private readonly List<(string JobKey, string Status, TaskCompletionSource<SavedJob> Signal)> _statusWaiters = new();
        private TaskCompletionSource? _nextDisposal;

        public volatile bool FailCreation;

        public ComicMaintainerDbContext CreateDbContext()
        {
            if (FailCreation)
            {
                throw new InvalidOperationException("database unavailable");
            }

            TaskCompletionSource? disposal;
            lock (_gate)
            {
                disposal = _nextDisposal;
                _nextDisposal = null;
            }
            return new SignalingDbContext(_options, this, disposal);
        }

        public Task WhenNextContextDisposedAsync()
        {
            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                _nextDisposal = signal;
            }
            return signal.Task;
        }

        public Task<SavedJob> WaitForSavedStatusAsync(string jobKey, ScheduledJobStatus status)
        {
            lock (_gate)
            {
                var existing = _saved.FirstOrDefault(s => s.JobKey == jobKey && s.LastStatus == status.ToString());
                if (existing is not null)
                {
                    return Task.FromResult(existing);
                }
                var signal = new TaskCompletionSource<SavedJob>(TaskCreationOptions.RunContinuationsAsynchronously);
                _statusWaiters.Add((jobKey, status.ToString(), signal));
                return signal.Task;
            }
        }

        public void OnSaved(SavedJob saved)
        {
            var matched = new List<TaskCompletionSource<SavedJob>>();
            lock (_gate)
            {
                _saved.Add(saved);
                for (var i = _statusWaiters.Count - 1; i >= 0; i--)
                {
                    if (_statusWaiters[i].JobKey == saved.JobKey && _statusWaiters[i].Status == saved.LastStatus)
                    {
                        matched.Add(_statusWaiters[i].Signal);
                        _statusWaiters.RemoveAt(i);
                    }
                }
            }
            foreach (var signal in matched)
            {
                signal.TrySetResult(saved);
            }
        }
    }

    private sealed class SignalingDbContext : ComicMaintainerDbContext
    {
        private readonly TestDbContextFactory _factory;
        private readonly TaskCompletionSource? _disposal;

        public SignalingDbContext(
            DbContextOptions<ComicMaintainerDbContext> options,
            TestDbContextFactory factory,
            TaskCompletionSource? disposal)
            : base(options)
        {
            _factory = factory;
            _disposal = disposal;
        }

        public override async Task<int> SaveChangesAsync(
            bool acceptAllChangesOnSuccess,
            CancellationToken cancellationToken = default)
        {
            var changed = ChangeTracker.Entries<ScheduledJobEntity>()
                .Where(e => e.State is EntityState.Added or EntityState.Modified)
                .Select(e => e.Entity)
                .ToList();
            var result = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
            foreach (var job in changed)
            {
                _factory.OnSaved(new SavedJob(job.JobKey, job.LastStatus, job.LastMessage, job.LastDurationMs));
            }
            return result;
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            _disposal?.TrySetResult();
        }

        public override void Dispose()
        {
            base.Dispose();
            _disposal?.TrySetResult();
        }
    }

    private sealed class MetricRecorder : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly List<(string Instrument, Dictionary<string, object?> Tags)> _measurements = new();

        public MetricRecorder(Meter meter)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, meter))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) => Add(instrument, tags));
            _listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) => Add(instrument, tags));
            _listener.Start();
        }

        public IReadOnlyList<(string Instrument, Dictionary<string, object?> Tags)> Snapshot()
        {
            lock (_measurements)
            {
                return _measurements.ToList();
            }
        }

        public void Dispose() => _listener.Dispose();

        private void Add(Instrument instrument, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var copy = new Dictionary<string, object?>();
            foreach (var tag in tags)
            {
                copy[tag.Key] = tag.Value;
            }
            lock (_measurements)
            {
                _measurements.Add((instrument.Name, copy));
            }
        }
    }
}
