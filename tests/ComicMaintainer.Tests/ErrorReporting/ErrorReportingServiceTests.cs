using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.ErrorReporting;
using ComicMaintainer.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace ComicMaintainer.Tests.ErrorReporting;

/// <summary>
/// Covers the behaviour that protects the issue tracker: one issue per defect,
/// a hard daily cap, operator mutes, and never letting GitHub's availability
/// affect the application.
/// </summary>
public class ErrorReportingServiceTests
{
    private readonly IDbContextFactory<ComicMaintainerDbContext> _dbContextFactory;
    private readonly FakeTimeProvider _timeProvider = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));

    public ErrorReportingServiceTests()
    {
        var dbName = $"ErrorReportsTestDb_{Guid.NewGuid()}";
        var services = new ServiceCollection();
        services.AddDbContextFactory<ComicMaintainerDbContext>(opt => opt.UseInMemoryDatabase(dbName));
        _dbContextFactory = services.BuildServiceProvider()
            .GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>();
    }

    [Fact]
    public async Task ProcessAsync_DoesNothingWhenReportingIsDisabled()
    {
        var client = new RecordingGitHubIssueClient();
        // The default AppSettings is the shipped configuration; reporting must
        // be off in it, so a fresh install never phones home.
        var service = CreateService(new AppSettings(), client);

        var outcome = await service.ProcessAsync(CreateReport());

        Assert.Equal(ErrorReportOutcome.Disabled, outcome);
        Assert.Empty(client.CreatedIssues);
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        Assert.Empty(db.ErrorReports);
    }

    [Fact]
    public async Task ProcessAsync_ReportsMissingConfigurationWithoutCallingGitHub()
    {
        var client = new RecordingGitHubIssueClient();
        var settings = EnabledSettings();
        settings.ErrorReportingGitHubToken = null;

        var outcome = await CreateService(settings, client).ProcessAsync(CreateReport());

        Assert.Equal(ErrorReportOutcome.NotConfigured, outcome);
        Assert.Empty(client.CreatedIssues);
    }

    [Fact]
    public async Task ProcessAsync_CreatesOneIssueForTheFirstOccurrence()
    {
        var client = new RecordingGitHubIssueClient();
        var service = CreateService(EnabledSettings(), client);

        var outcome = await service.ProcessAsync(CreateReport());

        Assert.Equal(ErrorReportOutcome.IssueCreated, outcome);
        var created = Assert.Single(client.CreatedIssues);
        Assert.Contains("[field error]", created.Title);
        Assert.Contains(ErrorReportIssueFormatter.FieldErrorLabel, created.Labels);
        Assert.Contains(ErrorReportIssueFormatter.AutomatedLabel, created.Labels);
        Assert.Equal("copilot-swe-agent", created.Assignee);

        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var entity = Assert.Single(db.ErrorReports);
        Assert.Equal(ErrorReportState.Reported, entity.State);
        Assert.Equal(1, entity.OccurrenceCount);
        Assert.NotNull(entity.GitHubIssueNumber);
    }

    [Fact]
    public async Task ProcessAsync_SecondOccurrenceBumpsTheCountWithoutFilingAgain()
    {
        var client = new RecordingGitHubIssueClient();
        var service = CreateService(EnabledSettings(), client);

        await service.ProcessAsync(CreateReport());
        var outcome = await service.ProcessAsync(CreateReport());

        Assert.Equal(ErrorReportOutcome.Recorded, outcome);
        Assert.Single(client.CreatedIssues);
        Assert.Empty(client.Comments);

        await using var db = await _dbContextFactory.CreateDbContextAsync();
        Assert.Equal(2, (await db.ErrorReports.SingleAsync()).OccurrenceCount);
    }

    [Fact]
    public async Task ProcessAsync_CommentsOnRecurrenceOnceTheDedupeWindowHasElapsed()
    {
        var client = new RecordingGitHubIssueClient();
        var service = CreateService(EnabledSettings(), client);

        await service.ProcessAsync(CreateReport());
        _timeProvider.Advance(TimeSpan.FromHours(25));
        var outcome = await service.ProcessAsync(CreateReport());

        Assert.Equal(ErrorReportOutcome.CommentAdded, outcome);
        Assert.Single(client.CreatedIssues);
        Assert.Single(client.Comments);
    }

    [Fact]
    public async Task ProcessAsync_DoesNotCommentWhenRecurrenceCommentsAreDisabled()
    {
        var client = new RecordingGitHubIssueClient();
        var settings = EnabledSettings();
        settings.ErrorReportingCommentOnRecurrence = false;
        var service = CreateService(settings, client);

        await service.ProcessAsync(CreateReport());
        _timeProvider.Advance(TimeSpan.FromHours(25));
        var outcome = await service.ProcessAsync(CreateReport());

        Assert.Equal(ErrorReportOutcome.Recorded, outcome);
        Assert.Empty(client.Comments);
    }

    [Fact]
    public async Task ProcessAsync_StopsFilingOnceTheDailyCapIsReached()
    {
        var client = new RecordingGitHubIssueClient();
        var settings = EnabledSettings();
        settings.ErrorReportingMaxIssuesPerDay = 2;
        var service = CreateService(settings, client);

        Assert.Equal(ErrorReportOutcome.IssueCreated, await service.ProcessAsync(CreateReport("a")));
        Assert.Equal(ErrorReportOutcome.IssueCreated, await service.ProcessAsync(CreateReport("b")));
        var third = await service.ProcessAsync(CreateReport("c"));

        Assert.Equal(ErrorReportOutcome.RateLimited, third);
        Assert.Equal(2, client.CreatedIssues.Count);

        // The capped occurrence is still recorded locally, so nothing is lost.
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        Assert.Equal(3, await db.ErrorReports.CountAsync());
    }

    [Fact]
    public async Task ProcessAsync_AllowsFilingAgainOnceTheRollingDayHasPassed()
    {
        var client = new RecordingGitHubIssueClient();
        var settings = EnabledSettings();
        settings.ErrorReportingMaxIssuesPerDay = 1;
        var service = CreateService(settings, client);

        await service.ProcessAsync(CreateReport("a"));
        Assert.Equal(ErrorReportOutcome.RateLimited, await service.ProcessAsync(CreateReport("b")));

        _timeProvider.Advance(TimeSpan.FromHours(25));

        Assert.Equal(ErrorReportOutcome.IssueCreated, await service.ProcessAsync(CreateReport("c")));
    }

    [Fact]
    public async Task ProcessAsync_NeverFilesForAMutedFingerprint()
    {
        var client = new RecordingGitHubIssueClient();
        var service = CreateService(EnabledSettings(), client);
        var report = CreateReport();

        await using (var db = await _dbContextFactory.CreateDbContextAsync())
        {
            db.ErrorReports.Add(new ErrorReportEntity
            {
                Fingerprint = report.Fingerprint,
                State = ErrorReportState.Muted
            });
            await db.SaveChangesAsync();
        }

        var outcome = await service.ProcessAsync(report);

        Assert.Equal(ErrorReportOutcome.Muted, outcome);
        Assert.Empty(client.CreatedIssues);

        await using var verifyDb = await _dbContextFactory.CreateDbContextAsync();
        Assert.Equal(2, (await verifyDb.ErrorReports.SingleAsync()).OccurrenceCount);
    }

    [Fact]
    public async Task ProcessAsync_SurvivesGitHubBeingUnreachable()
    {
        var client = new ThrowingGitHubIssueClient(() => new GitHubIssueTransientException("unreachable"));
        var service = CreateService(EnabledSettings(), client);

        var outcome = await service.ProcessAsync(CreateReport());

        Assert.Equal(ErrorReportOutcome.Failed, outcome);

        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var entity = await db.ErrorReports.SingleAsync();
        Assert.Equal(ErrorReportState.Failed, entity.State);
        Assert.Null(entity.GitHubIssueNumber);
        Assert.NotNull(entity.LastError);
    }

    [Fact]
    public async Task ProcessAsync_OpensTheCircuitAfterRepeatedTransientFailures()
    {
        var client = new ThrowingGitHubIssueClient(() => new GitHubIssueTransientException("unreachable"));
        var service = CreateService(EnabledSettings(), client);

        for (var i = 0; i < 3; i++)
        {
            await service.ProcessAsync(CreateReport($"fp{i}"));
        }

        var attemptsBefore = client.Attempts;

        // With the breaker open, further reports are recorded but no call is made.
        var outcome = await service.ProcessAsync(CreateReport("fp-after-breaker"));

        Assert.Equal(ErrorReportOutcome.Recorded, outcome);
        Assert.Equal(attemptsBefore, client.Attempts);
    }

    [Fact]
    public async Task ProcessAsync_RetriesUnassignedWhenGitHubRejectsTheAssignee()
    {
        var client = new RecordingGitHubIssueClient { RejectAssignee = true };
        var service = CreateService(EnabledSettings(), client);

        var outcome = await service.ProcessAsync(CreateReport());

        Assert.Equal(ErrorReportOutcome.IssueCreated, outcome);
        var created = Assert.Single(client.CreatedIssues);
        Assert.Null(created.Assignee);
    }

    private ErrorReportingService CreateService(AppSettings settings, IGitHubIssueClient client)
        => new(
            _dbContextFactory,
            new TestOptionsMonitor<AppSettings>(settings),
            client,
            NullLogger<ErrorReportingService>.Instance,
            _timeProvider);

    private static AppSettings EnabledSettings() => new()
    {
        ErrorReportingEnabled = true,
        ErrorReportingGitHubOwner = "someone",
        ErrorReportingGitHubRepo = "their-fork",
        ErrorReportingGitHubToken = "a-token-value",
        ErrorReportingAssignee = "copilot-swe-agent",
        ErrorReportingMaxIssuesPerDay = 10,
        ErrorReportingDedupeWindowHours = 24
    };

    private ErrorReport CreateReport(string fingerprint = "fingerprint-1") => new()
    {
        Fingerprint = fingerprint,
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

    private sealed record CreatedIssue(string Title, string Body, IReadOnlyCollection<string> Labels, string? Assignee);

    private sealed class RecordingGitHubIssueClient : IGitHubIssueClient
    {
        private int _nextIssueNumber = 100;

        public List<CreatedIssue> CreatedIssues { get; } = new();
        public List<string> Comments { get; } = new();

        /// <summary>Simulates GitHub refusing an assignee that is not a collaborator.</summary>
        public bool RejectAssignee { get; set; }

        public Task<GitHubIssueReference> CreateIssueAsync(
            GitHubIssueTarget target,
            string title,
            string body,
            IReadOnlyCollection<string> labels,
            string? assignee,
            CancellationToken cancellationToken)
        {
            if (RejectAssignee && assignee is not null)
            {
                throw new GitHubIssuePermanentException("assignee not permitted");
            }

            CreatedIssues.Add(new CreatedIssue(title, body, labels, assignee));
            var number = _nextIssueNumber++;
            return Task.FromResult(new GitHubIssueReference(number, $"https://github.com/o/r/issues/{number}"));
        }

        public Task AddCommentAsync(GitHubIssueTarget target, int issueNumber, string body, CancellationToken cancellationToken)
        {
            Comments.Add(body);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingGitHubIssueClient : IGitHubIssueClient
    {
        private readonly Func<Exception> _exceptionFactory;

        public ThrowingGitHubIssueClient(Func<Exception> exceptionFactory) => _exceptionFactory = exceptionFactory;

        public int Attempts { get; private set; }

        public Task<GitHubIssueReference> CreateIssueAsync(
            GitHubIssueTarget target,
            string title,
            string body,
            IReadOnlyCollection<string> labels,
            string? assignee,
            CancellationToken cancellationToken)
        {
            Attempts++;
            throw _exceptionFactory();
        }

        public Task AddCommentAsync(GitHubIssueTarget target, int issueNumber, string body, CancellationToken cancellationToken)
        {
            Attempts++;
            throw _exceptionFactory();
        }
    }
}
