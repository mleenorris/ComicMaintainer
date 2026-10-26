using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.ErrorReporting;
using ComicMaintainer.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace ComicMaintainer.Tests.ErrorReporting;

public class ErrorReportingServiceCoverageTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly IDbContextFactory<ComicMaintainerDbContext> _dbContextFactory;
    private readonly FakeTimeProvider _timeProvider = new(Start);

    public ErrorReportingServiceCoverageTests()
    {
        var dbName = $"ErrorReportingCoverage_{Guid.NewGuid()}";
        var services = new ServiceCollection();
        services.AddDbContextFactory<ComicMaintainerDbContext>(opt => opt.UseInMemoryDatabase(dbName));
        _dbContextFactory = services.BuildServiceProvider()
            .GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public async Task ProcessAsync_TreatsANonPositiveDailyCapAsNoIssuesAllowed(int cap)
    {
        var settings = EnabledSettings();
        settings.ErrorReportingMaxIssuesPerDay = cap;
        var client = new FakeGitHubIssueClient(_timeProvider);

        var outcome = await CreateService(settings, client).ProcessAsync(CreateReport());

        Assert.Equal(ErrorReportOutcome.RateLimited, outcome);
        Assert.Equal(0, client.CreateAttempts);
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var entity = await db.ErrorReports.SingleAsync();
        Assert.Equal(ErrorReportState.New, entity.State);
        Assert.Null(entity.GitHubIssueNumber);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ProcessAsync_FilesTheIssueUnassignedWhenNoAssigneeIsConfigured(string? assignee)
    {
        var settings = EnabledSettings();
        settings.ErrorReportingAssignee = assignee;
        var client = new FakeGitHubIssueClient(_timeProvider);

        var outcome = await CreateService(settings, client).ProcessAsync(CreateReport());

        Assert.Equal(ErrorReportOutcome.IssueCreated, outcome);
        var created = Assert.Single(client.CreatedIssues);
        Assert.Null(created.Assignee);
        Assert.Equal(ErrorReportingDestination.Owner, created.Target.Owner);
        Assert.Equal("a-token-value", created.Target.Token);
    }

    [Fact]
    public async Task ProcessAsync_ParksAPermanentRejectionEvenWithoutAnAssignee()
    {
        var settings = EnabledSettings();
        settings.ErrorReportingAssignee = null;
        var client = new FakeGitHubIssueClient(_timeProvider) { CreateFailure = () => new GitHubIssuePermanentException("bad credentials") };
        var service = CreateService(settings, client);

        Assert.Equal(ErrorReportOutcome.Failed, await service.ProcessAsync(CreateReport()));
        Assert.Equal(ErrorReportOutcome.Recorded, await service.ProcessAsync(CreateReport()));

        Assert.Equal(1, client.CreateAttempts);
    }

    [Fact]
    public async Task ProcessAsync_RecordsAPermanentlyRejectedRecurrenceCommentAndParksTheRow()
    {
        await SeedReportedRowAsync(lastReportedAt: Start.UtcDateTime.AddDays(-2));
        var client = new FakeGitHubIssueClient(_timeProvider) { CommentFailure = () => new GitHubIssuePermanentException("issue is locked") };
        var service = CreateService(EnabledSettings(), client);

        var outcome = await service.ProcessAsync(CreateReport());

        Assert.Equal(ErrorReportOutcome.Failed, outcome);
        Assert.Equal(1, client.CommentAttempts);
        await using (var db = await _dbContextFactory.CreateDbContextAsync())
        {
            var entity = await db.ErrorReports.SingleAsync();
            Assert.Equal(ErrorReportState.Failed, entity.State);
            Assert.Equal("issue is locked", entity.LastError);
            Assert.NotNull(entity.PermanentFailureSignature);
            Assert.Equal(42, entity.GitHubIssueNumber);
            Assert.Equal(Start.UtcDateTime.AddDays(-2), entity.LastReportedAt);
        }

        // Parked until the configuration changes: no further comment attempts.
        Assert.Equal(ErrorReportOutcome.Recorded, await service.ProcessAsync(CreateReport()));
        Assert.Equal(1, client.CommentAttempts);
    }

    [Fact]
    public async Task ProcessAsync_RecordsARecurrenceCommentThatKeepsFailingTransiently()
    {
        await SeedReportedRowAsync(lastReportedAt: Start.UtcDateTime.AddDays(-2));
        var client = new FakeGitHubIssueClient(_timeProvider) { CommentFailure = () => new GitHubIssueTransientException("GitHub is down") };

        var outcome = await CreateService(EnabledSettings(), client).ProcessAsync(CreateReport());

        Assert.Equal(ErrorReportOutcome.Failed, outcome);
        // The first attempt plus one retry per configured delay.
        Assert.Equal(3, client.CommentAttempts);
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var entity = await db.ErrorReports.SingleAsync();
        Assert.Equal(ErrorReportState.Failed, entity.State);
        Assert.Equal("GitHub is down", entity.LastError);
        Assert.Null(entity.PermanentFailureSignature);
    }

    [Fact]
    public async Task ProcessAsync_WaitsOutTheBackoffDelayBeforeRetrying()
    {
        var clock = new ObservableTimeProvider(_timeProvider);
        var client = new FakeGitHubIssueClient(_timeProvider) { TransientCreateFailures = 1 };
        var service = new ErrorReportingService(
            _dbContextFactory,
            new TestOptionsMonitor<AppSettings>(EnabledSettings()),
            client,
            NullLogger<ErrorReportingService>.Instance,
            clock,
            new[] { TimeSpan.FromSeconds(30) });

        var processing = service.ProcessAsync(CreateReport());
        await clock.TimerCreated.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, client.CreateAttempts);
        _timeProvider.Advance(TimeSpan.FromSeconds(29));
        Assert.False(processing.IsCompleted);
        Assert.Equal(1, client.CreateAttempts);

        _timeProvider.Advance(TimeSpan.FromSeconds(1));
        var outcome = await processing.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(ErrorReportOutcome.IssueCreated, outcome);
        Assert.Equal(2, client.CreateAttempts);
        Assert.Equal(Start.AddSeconds(30), client.CreatedIssues.Single().AttemptedAt);
    }

    [Fact]
    public async Task ProcessAsync_UsesTheSystemClockWhenNoTimeProviderIsSupplied()
    {
        var settings = EnabledSettings();
        settings.ErrorReportingGitHubToken = null;
        var service = new ErrorReportingService(
            _dbContextFactory,
            new TestOptionsMonitor<AppSettings>(settings),
            new FakeGitHubIssueClient(_timeProvider),
            NullLogger<ErrorReportingService>.Instance);

        var before = DateTime.UtcNow;
        var outcome = await service.ProcessAsync(CreateReport());
        var after = DateTime.UtcNow;

        Assert.Equal(ErrorReportOutcome.NotConfigured, outcome);
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var entity = await db.ErrorReports.SingleAsync();
        Assert.InRange(entity.FirstSeenAt, before, after);
        Assert.Equal(entity.FirstSeenAt, entity.LastSeenAt);
    }

    private async Task SeedReportedRowAsync(DateTime lastReportedAt)
    {
        await using var seed = await _dbContextFactory.CreateDbContextAsync();
        seed.ErrorReports.Add(new ErrorReportEntity
        {
            Fingerprint = "fingerprint-1",
            Level = "Error",
            MessageTemplate = "Failed to process {File}",
            FirstSeenAt = lastReportedAt,
            LastSeenAt = lastReportedAt,
            OccurrenceCount = 3,
            State = ErrorReportState.Reported,
            GitHubIssueNumber = 42,
            GitHubOwner = ErrorReportingDestination.Owner,
            GitHubRepo = ErrorReportingDestination.Repo,
            IssueCreatedAt = lastReportedAt,
            LastReportedAt = lastReportedAt
        });
        await seed.SaveChangesAsync();
    }

    private ErrorReportingService CreateService(AppSettings settings, IGitHubIssueClient client)
        => new(
            _dbContextFactory,
            new TestOptionsMonitor<AppSettings>(settings),
            client,
            NullLogger<ErrorReportingService>.Instance,
            _timeProvider,
            new[] { TimeSpan.Zero, TimeSpan.Zero });

    private static AppSettings EnabledSettings() => new()
    {
        ErrorReportingEnabled = true,
        ErrorReportingGitHubToken = "a-token-value",
        ErrorReportingAssignee = "copilot-swe-agent",
        ErrorReportingMaxIssuesPerDay = 10,
        ErrorReportingDedupeWindowHours = 24,
        ErrorReportingCommentOnRecurrence = true
    };

    private ErrorReport CreateReport() => new()
    {
        Fingerprint = "fingerprint-1",
        Level = "Error",
        MessageTemplate = "Failed to process {File}",
        RenderedMessage = "Failed to process a file",
        ExceptionType = "System.IO.IOException",
        ExceptionMessage = "disk on fire",
        StackTrace = "at Ns.Type.Method()",
        SourceContext = "ComicMaintainer.Core.Services.ComicProcessorService",
        AppVersion = "2.0.0",
        TimestampUtc = _timeProvider.GetUtcNow().UtcDateTime
    };

    private sealed record CreatedIssue(GitHubIssueTarget Target, string? Assignee, DateTimeOffset AttemptedAt);

    private sealed class FakeGitHubIssueClient(TimeProvider clock) : IGitHubIssueClient
    {
        public List<CreatedIssue> CreatedIssues { get; } = new();

        public int CreateAttempts { get; private set; }

        public int CommentAttempts { get; private set; }

        public int TransientCreateFailures { get; set; }

        public Func<Exception>? CreateFailure { get; init; }

        public Func<Exception>? CommentFailure { get; init; }

        public Task<GitHubIssueReference> CreateIssueAsync(
            GitHubIssueTarget target,
            string title,
            string body,
            IReadOnlyCollection<string> labels,
            string? assignee,
            CancellationToken cancellationToken)
        {
            CreateAttempts++;
            if (CreateFailure is not null)
            {
                throw CreateFailure();
            }

            if (TransientCreateFailures > 0)
            {
                TransientCreateFailures--;
                throw new GitHubIssueTransientException("temporarily unreachable");
            }

            CreatedIssues.Add(new CreatedIssue(target, assignee, clock.GetUtcNow()));
            return Task.FromResult(new GitHubIssueReference(100 + CreatedIssues.Count, null));
        }

        public Task AddCommentAsync(GitHubIssueTarget target, int issueNumber, string body, CancellationToken cancellationToken)
        {
            CommentAttempts++;
            if (CommentFailure is not null)
            {
                throw CommentFailure();
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>Signals when the service starts a delay, so a test can advance the clock past it.</summary>
    private sealed class ObservableTimeProvider(FakeTimeProvider inner) : TimeProvider
    {
        private readonly TaskCompletionSource _timerCreated = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task TimerCreated => _timerCreated.Task;

        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

        public override long GetTimestamp() => inner.GetTimestamp();

        public override long TimestampFrequency => inner.TimestampFrequency;

        public override TimeZoneInfo LocalTimeZone => inner.LocalTimeZone;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = inner.CreateTimer(callback, state, dueTime, period);
            _timerCreated.TrySetResult();
            return timer;
        }
    }
}
