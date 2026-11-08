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
/// Filing an issue is not idempotent: a request that timed out may still have
/// created the issue, so it must not be repeated before the repository has
/// been checked. And when GitHub says how long to wait, reporting must wait.
/// </summary>
public class ErrorReportingServiceIssueDedupeTests
{
    private static readonly DateTimeOffset Start = new(2026, 5, 4, 8, 30, 0, TimeSpan.Zero);

    private readonly IDbContextFactory<ComicMaintainerDbContext> _dbContextFactory;
    private readonly FakeTimeProvider _time = new(Start);

    public ErrorReportingServiceIssueDedupeTests()
    {
        var dbName = $"ErrorReportDedupeTestDb_{Guid.NewGuid()}";
        var services = new ServiceCollection();
        services.AddDbContextFactory<ComicMaintainerDbContext>(opt => opt.UseInMemoryDatabase(dbName));
        _dbContextFactory = services.BuildServiceProvider()
            .GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>();
    }

    [Fact]
    public async Task ATimedOutCreateThatGitHubCarriedOut_IsLinkedInsteadOfFiledAgain()
    {
        var github = new SimulatedGitHub(_time);
        github.CreateResults.Enqueue(CreateResult.CreatedButResponseLost);
        var service = CreateService(github);

        var outcome = await service.ProcessAsync(CreateReport());

        Assert.Equal(ErrorReportOutcome.IssueCreated, outcome);
        var issue = Assert.Single(github.Issues);
        Assert.Equal(1, github.CreateAttempts);
        Assert.Equal(Start - TimeSpan.FromMinutes(5), Assert.Single(github.Lookups));

        var entity = await GetEntityAsync();
        Assert.Equal(issue.Number, entity.GitHubIssueNumber);
        Assert.Equal(ErrorReportState.Reported, entity.State);
        Assert.Null(entity.IssueCreationAttemptedAt);
    }

    [Fact]
    public async Task ATimedOutCreateThatGitHubDidNotCarryOut_IsRetriedOnceTheRepositoryWasChecked()
    {
        var github = new SimulatedGitHub(_time);
        github.CreateResults.Enqueue(CreateResult.FailedResponseLost);
        var service = CreateService(github);

        var outcome = await service.ProcessAsync(CreateReport());

        Assert.Equal(ErrorReportOutcome.IssueCreated, outcome);
        Assert.Single(github.Issues);
        Assert.Equal(2, github.CreateAttempts);
        Assert.Single(github.Lookups);
    }

    [Fact]
    public async Task ATimedOutCreate_IsLookedUpOnlyAfterTheBackoffJustBeforeItIsRepeated()
    {
        var backoff = TimeSpan.FromSeconds(2);
        var github = new SimulatedGitHub(_time);
        github.CreateResults.Enqueue(CreateResult.FailedResponseLost);
        var service = CreateService(github, new[] { backoff });

        var processing = service.ProcessAsync(CreateReport());
        while (!processing.IsCompleted)
        {
            _time.Advance(TimeSpan.FromMilliseconds(500));
            await Task.Delay(1);
        }

        Assert.Equal(ErrorReportOutcome.IssueCreated, await processing);

        // The first request gets the whole backoff to land before it is looked
        // for, and the repeat comes straight after the lookup found nothing.
        var lookedUpAt = Assert.Single(github.LookupTimes);
        Assert.True(lookedUpAt >= Start + backoff, $"Looked up {lookedUpAt - Start} after the first request.");
        Assert.Equal(new[] { Start, lookedUpAt }, github.CreateTimes);
    }

    [Fact]
    public async Task AFailureThatNeverReachedGitHub_IsRetriedWithoutLookingFirst()
    {
        var github = new SimulatedGitHub(_time);
        github.CreateResults.Enqueue(CreateResult.ConnectionRefused);
        var service = CreateService(github);

        var outcome = await service.ProcessAsync(CreateReport());

        Assert.Equal(ErrorReportOutcome.IssueCreated, outcome);
        Assert.Single(github.Issues);
        Assert.Equal(2, github.CreateAttempts);
        Assert.Empty(github.Lookups);
    }

    [Fact]
    public async Task WhenTheLookupFails_TheNextOccurrenceLinksTheIssueTheLostRequestCreated()
    {
        var github = new SimulatedGitHub(_time);
        github.CreateResults.Enqueue(CreateResult.CreatedButResponseLost);
        github.LookupFailure = () => new GitHubIssueTransientException("lookup timed out");
        var service = CreateService(github);

        Assert.Equal(ErrorReportOutcome.Failed, await service.ProcessAsync(CreateReport()));
        Assert.Single(github.Issues);
        var afterFailure = await GetEntityAsync();
        Assert.Null(afterFailure.GitHubIssueNumber);
        Assert.Equal(Start.UtcDateTime, afterFailure.IssueCreationAttemptedAt);

        // The next occurrence looks for the issue before filing anything.
        github.LookupFailure = null;
        _time.Advance(TimeSpan.FromHours(3));
        Assert.Equal(ErrorReportOutcome.IssueCreated, await service.ProcessAsync(CreateReport()));

        Assert.Single(github.Issues);
        Assert.Equal(1, github.CreateAttempts);
        Assert.Equal(Start - TimeSpan.FromMinutes(5), github.Lookups[^1]);
        var linked = await GetEntityAsync();
        Assert.Equal(github.Issues[0].Number, linked.GitHubIssueNumber);
        Assert.Null(linked.IssueCreationAttemptedAt);
    }

    [Fact]
    public async Task WhenEveryAttemptsOutcomeIsUnknown_TheNextOccurrenceChecksBeforeFilingAgain()
    {
        var github = new SimulatedGitHub(_time);
        for (var i = 0; i < 3; i++)
        {
            github.CreateResults.Enqueue(CreateResult.FailedResponseLost);
        }

        var service = CreateService(github);

        Assert.Equal(ErrorReportOutcome.Failed, await service.ProcessAsync(CreateReport()));
        Assert.Equal(3, github.CreateAttempts);
        Assert.Empty(github.Issues);
        Assert.NotNull((await GetEntityAsync()).IssueCreationAttemptedAt);

        Assert.Equal(ErrorReportOutcome.IssueCreated, await service.ProcessAsync(CreateReport()));
        Assert.Single(github.Issues);
        Assert.Equal(4, github.CreateAttempts);
        Assert.Null((await GetEntityAsync()).IssueCreationAttemptedAt);
    }

    [Fact]
    public async Task ARequestGitHubRefused_LeavesNothingToReconcile()
    {
        var github = new SimulatedGitHub(_time);
        github.CreateResults.Enqueue(CreateResult.Rejected);
        var service = CreateService(github);

        Assert.Equal(ErrorReportOutcome.Failed, await service.ProcessAsync(CreateReport()));

        var entity = await GetEntityAsync();
        Assert.Null(entity.IssueCreationAttemptedAt);
        Assert.Empty(github.Lookups);
    }

    [Fact]
    public async Task RetryAfter_IsHonouredInsteadOfRetryingInPlace()
    {
        var github = new SimulatedGitHub(_time);
        github.CreateResults.Enqueue(CreateResult.SecondaryRateLimit(TimeSpan.FromSeconds(90)));
        var service = CreateService(github);

        Assert.Equal(ErrorReportOutcome.Failed, await service.ProcessAsync(CreateReport()));
        Assert.Equal(1, github.CreateAttempts);
        Assert.Null((await GetEntityAsync()).IssueCreationAttemptedAt);

        // Nothing reaches GitHub until the wait it asked for is over.
        _time.Advance(TimeSpan.FromSeconds(89));
        Assert.Equal(ErrorReportOutcome.Recorded, await service.ProcessAsync(CreateReport()));
        Assert.Equal(ErrorReportOutcome.Recorded, await service.ProcessAsync(CreateReport("another-fingerprint")));
        Assert.Equal(1, github.CreateAttempts);

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(ErrorReportOutcome.IssueCreated, await service.ProcessAsync(CreateReport()));
        Assert.Equal(2, github.CreateAttempts);
    }

    [Fact]
    public async Task RecurrenceComments_AreNeitherRepeatedAfterALostResponseNorRetriedDuringARateLimit()
    {
        var github = new SimulatedGitHub(_time);
        var service = CreateService(github);
        await service.ProcessAsync(CreateReport());

        github.CommentFailures.Enqueue(() => new GitHubIssueTransientException(
            "Request to GitHub timed out", null, mayHaveSucceeded: true, retryAfter: null));
        _time.Advance(TimeSpan.FromHours(25));
        Assert.Equal(ErrorReportOutcome.Failed, await service.ProcessAsync(CreateReport()));
        Assert.Equal(1, github.CommentAttempts);

        github.CommentFailures.Enqueue(() => new GitHubIssueTransientException(
            "GitHub returned 429 (TooManyRequests)", null, mayHaveSucceeded: false, retryAfter: TimeSpan.FromMinutes(2)));
        _time.Advance(TimeSpan.FromHours(25));
        Assert.Equal(ErrorReportOutcome.Failed, await service.ProcessAsync(CreateReport()));
        Assert.Equal(2, github.CommentAttempts);

        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(ErrorReportOutcome.Recorded, await service.ProcessAsync(CreateReport()));
        Assert.Equal(2, github.CommentAttempts);
    }

    private ErrorReportingService CreateService(IGitHubIssueClient client, IReadOnlyList<TimeSpan>? retryDelays = null)
        => new(
            _dbContextFactory,
            new TestOptionsMonitor<AppSettings>(new AppSettings
            {
                ErrorReportingEnabled = true,
                ErrorReportingGitHubToken = "a-token-value",
                ErrorReportingMaxIssuesPerDay = 10,
                ErrorReportingDedupeWindowHours = 24
            }),
            client,
            NullLogger<ErrorReportingService>.Instance,
            _time,
            retryDelays ?? new[] { TimeSpan.Zero, TimeSpan.Zero });

    private ErrorReport CreateReport(string fingerprint = "0123456789abcdef") => new()
    {
        Fingerprint = fingerprint,
        Level = "Error",
        MessageTemplate = "Failed to process {File}",
        RenderedMessage = "Failed to process a file",
        ExceptionType = "System.IO.IOException",
        ExceptionMessage = "disk on fire",
        StackTrace = "at Ns.Type.Method()",
        SourceContext = "ComicMaintainer.Core.Services.ComicProcessorService",
        AppVersion = "3.0.0",
        TimestampUtc = _time.GetUtcNow().UtcDateTime
    };

    private async Task<ErrorReportEntity> GetEntityAsync(string fingerprint = "0123456789abcdef")
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        return await db.ErrorReports.AsNoTracking().SingleAsync(e => e.Fingerprint == fingerprint);
    }

    private sealed record CreateResult(string Kind, TimeSpan? RetryAfter = null)
    {
        /// <summary>GitHub filed the issue, but the response never arrived.</summary>
        public static CreateResult CreatedButResponseLost { get; } = new("created-lost");

        /// <summary>The request timed out before GitHub filed anything.</summary>
        public static CreateResult FailedResponseLost { get; } = new("failed-lost");

        /// <summary>The connection could not even be opened.</summary>
        public static CreateResult ConnectionRefused { get; } = new("refused");

        /// <summary>A permanent rejection, such as a bad token.</summary>
        public static CreateResult Rejected { get; } = new("rejected");

        public static CreateResult SecondaryRateLimit(TimeSpan retryAfter) => new("rate-limited", retryAfter);
    }

    private sealed record SimulatedIssue(int Number, string Body, DateTimeOffset CreatedAt);

    /// <summary>
    /// A repository whose issue creation can be told to lose its response,
    /// with an issue listing the lookup reads.
    /// </summary>
    private sealed class SimulatedGitHub(TimeProvider clock) : IGitHubIssueClient
    {
        private int _nextNumber = 500;

        public List<SimulatedIssue> Issues { get; } = new();

        public Queue<CreateResult> CreateResults { get; } = new();

        public Queue<Func<Exception>> CommentFailures { get; } = new();

        public List<DateTimeOffset> Lookups { get; } = new();

        /// <summary>When each lookup was made.</summary>
        public List<DateTimeOffset> LookupTimes { get; } = new();

        /// <summary>When each create request was made.</summary>
        public List<DateTimeOffset> CreateTimes { get; } = new();

        public Func<Exception>? LookupFailure { get; set; }

        public int CreateAttempts { get; private set; }

        public int CommentAttempts { get; private set; }

        public Task<GitHubIssueReference> CreateIssueAsync(
            GitHubIssueTarget target,
            string title,
            string body,
            IReadOnlyCollection<string> labels,
            string? assignee,
            CancellationToken cancellationToken)
        {
            CreateAttempts++;
            CreateTimes.Add(clock.GetUtcNow());
            var result = CreateResults.Count > 0 ? CreateResults.Dequeue() : null;
            switch (result?.Kind)
            {
                case "created-lost":
                    File(body);
                    throw new GitHubIssueTransientException("Request to GitHub timed out", null, mayHaveSucceeded: true, retryAfter: null);
                case "failed-lost":
                    throw new GitHubIssueTransientException("GitHub returned 502 (BadGateway)", null, mayHaveSucceeded: true, retryAfter: null);
                case "refused":
                    throw new GitHubIssueTransientException("Could not reach GitHub", null, mayHaveSucceeded: false, retryAfter: null);
                case "rejected":
                    throw new GitHubIssuePermanentException("GitHub rejected the request with 401 (Unauthorized).");
                case "rate-limited":
                    throw new GitHubIssueTransientException("GitHub returned 403 (Forbidden)", null, mayHaveSucceeded: false, retryAfter: result.RetryAfter);
                default:
                    var issue = File(body);
                    return Task.FromResult(new GitHubIssueReference(issue.Number, $"https://github.test/issues/{issue.Number}"));
            }
        }

        public Task AddCommentAsync(GitHubIssueTarget target, int issueNumber, string body, CancellationToken cancellationToken)
        {
            CommentAttempts++;
            if (CommentFailures.Count > 0)
            {
                throw CommentFailures.Dequeue()();
            }

            return Task.CompletedTask;
        }

        public Task<GitHubIssueReference?> FindIssueByFingerprintAsync(
            GitHubIssueTarget target,
            string fingerprint,
            DateTimeOffset createdSince,
            CancellationToken cancellationToken)
        {
            Lookups.Add(createdSince);
            LookupTimes.Add(clock.GetUtcNow());
            if (LookupFailure is not null)
            {
                throw LookupFailure();
            }

            var match = Issues.FirstOrDefault(i =>
                i.CreatedAt >= createdSince &&
                i.Body.Contains(ErrorReportIssueFormatter.BuildFingerprintBlock(fingerprint), StringComparison.Ordinal));
            return Task.FromResult(match is null ? null : new GitHubIssueReference(match.Number, null));
        }

        private SimulatedIssue File(string body)
        {
            var issue = new SimulatedIssue(_nextNumber++, body, clock.GetUtcNow());
            Issues.Add(issue);
            return issue;
        }
    }
}
