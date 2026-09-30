using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.Core.ErrorReporting;

/// <summary>What happened to a captured report.</summary>
public enum ErrorReportOutcome
{
    /// <summary>Reporting is switched off.</summary>
    Disabled,

    /// <summary>Reporting is on but the target repository or token is missing.</summary>
    NotConfigured,

    /// <summary>Counted locally; no issue action taken (duplicate inside the dedupe window).</summary>
    Recorded,

    /// <summary>A new GitHub issue was filed.</summary>
    IssueCreated,

    /// <summary>A recurrence comment was added to the existing issue.</summary>
    CommentAdded,

    /// <summary>The fingerprint is muted by an operator; counted only.</summary>
    Muted,

    /// <summary>The per-day issue cap was reached; counted only.</summary>
    RateLimited,

    /// <summary>GitHub could not be reached or rejected the request.</summary>
    Failed
}

/// <summary>
/// Records a captured failure and, when warranted, files it as a GitHub issue.
/// </summary>
public interface IErrorReportingService
{
    Task<ErrorReportOutcome> ProcessAsync(ErrorReport report, CancellationToken cancellationToken = default);
}

/// <summary>
/// Deduplicating, rate-limited reporter.
/// </summary>
/// <remarks>
/// <para>Two properties matter more than throughput here. First, <b>one issue
/// per defect</b>: a fingerprint that already has an issue never gets another,
/// however many times it recurs or however often the process restarts, because
/// the ledger is in the database rather than in memory.</para>
/// <para>Second, <b>failure to report must never become a failure of the
/// application</b>. GitHub being down, rate-limiting, or handed a bad token
/// cannot be allowed to wedge the background dispatcher, so transient problems
/// trip a circuit breaker and permanent ones stop the attempt entirely.</para>
/// </remarks>
public sealed class ErrorReportingService : IErrorReportingService
{
    /// <summary>Consecutive transient failures that trip the breaker.</summary>
    private const int CircuitBreakerThreshold = 3;

    /// <summary>How long the breaker stays open once tripped.</summary>
    private static readonly TimeSpan CircuitBreakerDuration = TimeSpan.FromMinutes(15);

    private readonly IDbContextFactory<ComicMaintainerDbContext> _dbContextFactory;
    private readonly IOptionsMonitor<AppSettings> _appSettings;
    private readonly IGitHubIssueClient _gitHubClient;
    private readonly ILogger<ErrorReportingService> _logger;
    private readonly TimeProvider _timeProvider;

    private int _consecutiveTransientFailures;
    private DateTimeOffset _circuitOpenUntil = DateTimeOffset.MinValue;

    public ErrorReportingService(
        IDbContextFactory<ComicMaintainerDbContext> dbContextFactory,
        IOptionsMonitor<AppSettings> appSettings,
        IGitHubIssueClient gitHubClient,
        ILogger<ErrorReportingService> logger,
        TimeProvider? timeProvider = null)
    {
        _dbContextFactory = dbContextFactory;
        _appSettings = appSettings;
        _gitHubClient = gitHubClient;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ErrorReportOutcome> ProcessAsync(ErrorReport report, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);

        var settings = _appSettings.CurrentValue;
        if (!settings.ErrorReportingEnabled)
        {
            return ErrorReportOutcome.Disabled;
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var entity = await db.ErrorReports
            .FirstOrDefaultAsync(e => e.Fingerprint == report.Fingerprint, cancellationToken);

        var isNew = entity is null;
        if (entity is null)
        {
            entity = new ErrorReportEntity
            {
                Fingerprint = report.Fingerprint,
                Level = report.Level,
                ExceptionType = report.ExceptionType,
                MessageTemplate = report.MessageTemplate,
                SourceContext = report.SourceContext,
                StackTrace = report.StackTrace,
                FirstSeenAt = now,
                OccurrenceCount = 0,
                State = ErrorReportState.New
            };
            db.ErrorReports.Add(entity);
        }

        entity.OccurrenceCount++;
        entity.LastSeenAt = now;
        entity.RenderedMessage = report.RenderedMessage;
        entity.CorrelationId = report.CorrelationId;
        entity.AppVersion = report.AppVersion;

        await SaveAsync(db, cancellationToken);

        if (entity.State == ErrorReportState.Muted)
        {
            return ErrorReportOutcome.Muted;
        }

        var target = BuildTarget(settings);
        if (target is null)
        {
            _logger.LogWarning(
                "Error reporting is enabled but no GitHub owner/repository/token is configured; report {Fingerprint} was recorded locally only",
                LoggingHelper.SanitizeForLog(report.Fingerprint));
            return ErrorReportOutcome.NotConfigured;
        }

        if (IsCircuitOpen(now))
        {
            return ErrorReportOutcome.Recorded;
        }

        if (entity.GitHubIssueNumber is null)
        {
            if (await IsDailyCapReachedAsync(db, settings, now, cancellationToken))
            {
                _logger.LogWarning(
                    "Daily GitHub issue cap of {Cap} reached; report {Fingerprint} was recorded locally only",
                    settings.ErrorReportingMaxIssuesPerDay,
                    LoggingHelper.SanitizeForLog(report.Fingerprint));
                return ErrorReportOutcome.RateLimited;
            }

            return await CreateIssueAsync(db, entity, report, target, settings, now, isNew, cancellationToken);
        }

        if (!settings.ErrorReportingCommentOnRecurrence)
        {
            return ErrorReportOutcome.Recorded;
        }

        var window = TimeSpan.FromHours(Math.Max(1, settings.ErrorReportingDedupeWindowHours));
        if (entity.LastReportedAt is { } lastReported && now - lastReported < window)
        {
            return ErrorReportOutcome.Recorded;
        }

        return await AddRecurrenceCommentAsync(db, entity, report, target, now, cancellationToken);
    }

    private async Task<ErrorReportOutcome> CreateIssueAsync(
        ComicMaintainerDbContext db,
        ErrorReportEntity entity,
        ErrorReport report,
        GitHubIssueTarget target,
        AppSettings settings,
        DateTime now,
        bool isNew,
        CancellationToken cancellationToken)
    {
        var title = ErrorReportIssueFormatter.BuildTitle(report);
        var body = ErrorReportIssueFormatter.BuildBody(report, entity);
        var labels = ErrorReportIssueFormatter.BuildLabels(report);
        var assignee = string.IsNullOrWhiteSpace(settings.ErrorReportingAssignee)
            ? null
            : settings.ErrorReportingAssignee;

        try
        {
            GitHubIssueReference issue;
            try
            {
                issue = await _gitHubClient.CreateIssueAsync(target, title, body, labels, assignee, cancellationToken);
            }
            catch (GitHubIssuePermanentException) when (assignee is not null)
            {
                // GitHub rejects the whole request when an assignee cannot be
                // assigned (not a collaborator, coding agent not enabled). An
                // unassigned issue is far better than no issue, so retry once
                // without the assignee before giving up.
                _logger.LogWarning(
                    "GitHub rejected the issue with assignee {Assignee}; retrying unassigned",
                    LoggingHelper.SanitizeForLog(assignee));
                issue = await _gitHubClient.CreateIssueAsync(target, title, body, labels, null, cancellationToken);
            }

            entity.GitHubIssueNumber = issue.Number;
            entity.GitHubIssueUrl = issue.HtmlUrl;
            entity.IssueCreatedAt = now;
            entity.LastReportedAt = now;
            entity.State = ErrorReportState.Reported;
            entity.LastError = null;
            await SaveAsync(db, cancellationToken);

            OnReportingSucceeded();

            _logger.LogInformation(
                "Filed GitHub issue #{IssueNumber} for error fingerprint {Fingerprint} ({IsNew})",
                issue.Number,
                LoggingHelper.SanitizeForLog(entity.Fingerprint),
                isNew ? "first occurrence" : "previously unreported");

            return ErrorReportOutcome.IssueCreated;
        }
        catch (Exception ex) when (ex is GitHubIssueTransientException or GitHubIssuePermanentException)
        {
            await RecordFailureAsync(db, entity, ex, cancellationToken);
            return ErrorReportOutcome.Failed;
        }
    }

    private async Task<ErrorReportOutcome> AddRecurrenceCommentAsync(
        ComicMaintainerDbContext db,
        ErrorReportEntity entity,
        ErrorReport report,
        GitHubIssueTarget target,
        DateTime now,
        CancellationToken cancellationToken)
    {
        try
        {
            await _gitHubClient.AddCommentAsync(
                target,
                entity.GitHubIssueNumber!.Value,
                ErrorReportIssueFormatter.BuildRecurrenceComment(report, entity),
                cancellationToken);

            entity.LastReportedAt = now;
            entity.LastError = null;
            entity.State = ErrorReportState.Reported;
            await SaveAsync(db, cancellationToken);

            OnReportingSucceeded();
            return ErrorReportOutcome.CommentAdded;
        }
        catch (Exception ex) when (ex is GitHubIssueTransientException or GitHubIssuePermanentException)
        {
            await RecordFailureAsync(db, entity, ex, cancellationToken);
            return ErrorReportOutcome.Failed;
        }
    }

    private async Task RecordFailureAsync(
        ComicMaintainerDbContext db,
        ErrorReportEntity entity,
        Exception exception,
        CancellationToken cancellationToken)
    {
        entity.LastError = ErrorReportRedactor.Truncate(exception.Message, 1024);

        // A permanent failure is a configuration problem the operator must fix;
        // leaving the row "new" would make the dispatcher retry the same
        // rejected request for every subsequent occurrence.
        entity.State = ErrorReportState.Failed;
        await SaveAsync(db, cancellationToken);

        if (exception is GitHubIssueTransientException)
        {
            var failures = Interlocked.Increment(ref _consecutiveTransientFailures);
            if (failures >= CircuitBreakerThreshold)
            {
                _circuitOpenUntil = _timeProvider.GetUtcNow().Add(CircuitBreakerDuration);
                _logger.LogWarning(
                    "Pausing GitHub error reporting for {Minutes} minutes after {Failures} consecutive failures",
                    (int)CircuitBreakerDuration.TotalMinutes,
                    failures);
            }
        }

        // Logged as a warning on purpose: an error here would be captured by the
        // reporting sink and fed straight back into this method.
        _logger.LogWarning(
            "Could not report error fingerprint {Fingerprint} to GitHub: {Reason}",
            LoggingHelper.SanitizeForLog(entity.Fingerprint),
            LoggingHelper.SanitizeForLog(exception.Message));
    }

    private void OnReportingSucceeded()
    {
        Interlocked.Exchange(ref _consecutiveTransientFailures, 0);
        _circuitOpenUntil = DateTimeOffset.MinValue;
    }

    private bool IsCircuitOpen(DateTime now)
        => _circuitOpenUntil > new DateTimeOffset(now, TimeSpan.Zero);

    private async Task<bool> IsDailyCapReachedAsync(
        ComicMaintainerDbContext db,
        AppSettings settings,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var cap = settings.ErrorReportingMaxIssuesPerDay;
        if (cap <= 0)
        {
            return true;
        }

        var cutoff = now.AddDays(-1);
        var created = await db.ErrorReports
            .CountAsync(e => e.IssueCreatedAt != null && e.IssueCreatedAt >= cutoff, cancellationToken);

        return created >= cap;
    }

    private static GitHubIssueTarget? BuildTarget(AppSettings settings)
    {
        var owner = settings.ErrorReportingGitHubOwner?.Trim();
        var repo = settings.ErrorReportingGitHubRepo?.Trim();
        var token = settings.ErrorReportingGitHubToken?.Trim();

        if (string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(repo) || string.IsNullOrEmpty(token))
        {
            return null;
        }

        return new GitHubIssueTarget(owner, repo, token);
    }

    private static async Task SaveAsync(ComicMaintainerDbContext db, CancellationToken cancellationToken)
        => await db.SaveChangesAsync(cancellationToken);
}
