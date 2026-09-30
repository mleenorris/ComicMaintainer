using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;

namespace ComicMaintainer.Core.ErrorReporting;

/// <summary>What happened to a captured report.</summary>
public enum ErrorReportOutcome
{
    /// <summary>Reporting is switched off.</summary>
    Disabled,

    /// <summary>Reporting is on but no GitHub token is configured.</summary>
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

    /// <summary>
    /// Backoff between delivery attempts for a transient failure. The report
    /// only exists in memory at this point — the dispatcher has already taken
    /// it off the queue — so a one-off timeout would otherwise lose it
    /// entirely. Deliberately short and bounded: this runs on the single
    /// background dispatcher, and a longer pause would stall every report
    /// behind it.
    /// </summary>
    private static readonly TimeSpan[] DefaultRetryDelays =
    {
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(10)
    };

    private readonly IDbContextFactory<ComicMaintainerDbContext> _dbContextFactory;
    private readonly IOptionsMonitor<AppSettings> _appSettings;
    private readonly IGitHubIssueClient _gitHubClient;
    private readonly ILogger<ErrorReportingService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly IReadOnlyList<TimeSpan> _retryDelays;

    private int _consecutiveTransientFailures;
    private DateTimeOffset _circuitOpenUntil = DateTimeOffset.MinValue;

    public ErrorReportingService(
        IDbContextFactory<ComicMaintainerDbContext> dbContextFactory,
        IOptionsMonitor<AppSettings> appSettings,
        IGitHubIssueClient gitHubClient,
        ILogger<ErrorReportingService> logger,
        TimeProvider? timeProvider = null,
        IReadOnlyList<TimeSpan>? retryDelays = null)
    {
        _dbContextFactory = dbContextFactory;
        _appSettings = appSettings;
        _gitHubClient = gitHubClient;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _retryDelays = retryDelays ?? DefaultRetryDelays;
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
                "Error reporting is enabled but no GitHub token is configured; report {Fingerprint} was recorded locally only",
                LoggingHelper.SanitizeForLog(report.Fingerprint));
            return ErrorReportOutcome.NotConfigured;
        }

        // An issue number is only meaningful on the repository it was filed
        // against. Rows carried over from a release where the destination was
        // an operator setting can be linked to some other repository, so that
        // linkage is dropped rather than reused — otherwise a recurrence would
        // comment on whatever unrelated issue happens to hold that number here,
        // and the defect would never be filed on the project repository.
        if (HasStaleIssueLinkage(entity, target))
        {
            _logger.LogInformation(
                "The linked GitHub issue belongs to another repository; clearing the issue linkage for fingerprint {Fingerprint}",
                LoggingHelper.SanitizeForLog(entity.Fingerprint));

            entity.GitHubIssueNumber = null;
            entity.GitHubIssueUrl = null;
            entity.GitHubOwner = null;
            entity.GitHubRepo = null;
            entity.IssueCreatedAt = null;
            entity.LastReportedAt = null;
            entity.PermanentFailureSignature = null;
            entity.State = ErrorReportState.New;
            await SaveAsync(db, cancellationToken);
        }

        // A permanent rejection is a configuration problem. Retrying it on
        // every occurrence would send one doomed GitHub request per logged
        // error, so the row stays parked until the configuration changes.
        var configurationSignature = ComputeConfigurationSignature(target, settings.ErrorReportingAssignee);
        if (entity.State == ErrorReportState.Failed
            && string.Equals(entity.PermanentFailureSignature, configurationSignature, StringComparison.Ordinal))
        {
            return ErrorReportOutcome.Recorded;
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

            return await CreateIssueAsync(db, entity, report, target, settings, now, isNew, configurationSignature, cancellationToken);
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

        return await AddRecurrenceCommentAsync(db, entity, report, target, now, configurationSignature, cancellationToken);
    }

    private async Task<ErrorReportOutcome> CreateIssueAsync(
        ComicMaintainerDbContext db,
        ErrorReportEntity entity,
        ErrorReport report,
        GitHubIssueTarget target,
        AppSettings settings,
        DateTime now,
        bool isNew,
        string configurationSignature,
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
                issue = await WithTransientRetriesAsync(
                    ct => _gitHubClient.CreateIssueAsync(target, title, body, labels, assignee, ct),
                    cancellationToken);
            }
            catch (GitHubIssuePermanentException ex) when (assignee is not null && ex.IsAssigneeRejection)
            {
                // GitHub rejects the whole request when an assignee cannot be
                // assigned (not a collaborator, coding agent not enabled). An
                // unassigned issue is far better than no issue, so retry once
                // without the assignee. Every other permanent rejection — bad
                // token, missing repository — would fail identically and is
                // left to propagate.
                _logger.LogWarning(
                    "GitHub rejected the assignee {Assignee}; retrying unassigned",
                    LoggingHelper.SanitizeForLog(assignee));
                issue = await WithTransientRetriesAsync(
                    ct => _gitHubClient.CreateIssueAsync(target, title, body, labels, null, ct),
                    cancellationToken);
            }

            entity.GitHubIssueNumber = issue.Number;
            entity.GitHubIssueUrl = issue.HtmlUrl;
            entity.GitHubOwner = target.Owner;
            entity.GitHubRepo = target.Repo;
            entity.IssueCreatedAt = now;
            entity.LastReportedAt = now;
            entity.State = ErrorReportState.Reported;
            entity.LastError = null;
            entity.PermanentFailureSignature = null;
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
            await RecordFailureAsync(db, entity, ex, configurationSignature, cancellationToken);
            return ErrorReportOutcome.Failed;
        }
    }

    private async Task<ErrorReportOutcome> AddRecurrenceCommentAsync(
        ComicMaintainerDbContext db,
        ErrorReportEntity entity,
        ErrorReport report,
        GitHubIssueTarget target,
        DateTime now,
        string configurationSignature,
        CancellationToken cancellationToken)
    {
        try
        {
            await WithTransientRetriesAsync(
                async ct =>
                {
                    await _gitHubClient.AddCommentAsync(
                        target,
                        entity.GitHubIssueNumber!.Value,
                        ErrorReportIssueFormatter.BuildRecurrenceComment(report, entity),
                        ct);
                    return true;
                },
                cancellationToken);

            entity.LastReportedAt = now;
            entity.LastError = null;
            entity.State = ErrorReportState.Reported;
            entity.PermanentFailureSignature = null;
            await SaveAsync(db, cancellationToken);

            OnReportingSucceeded();
            return ErrorReportOutcome.CommentAdded;
        }
        catch (Exception ex) when (ex is GitHubIssueTransientException or GitHubIssuePermanentException)
        {
            await RecordFailureAsync(db, entity, ex, configurationSignature, cancellationToken);
            return ErrorReportOutcome.Failed;
        }
    }

    /// <summary>
    /// Runs a delivery attempt, retrying transient failures with backoff.
    /// </summary>
    /// <remarks>
    /// By the time this runs the report exists only in memory — the dispatcher
    /// has already taken it off the queue — so without an in-place retry a
    /// single timeout silently discards it. Retries are bounded and honour
    /// cancellation so shutdown is never delayed, and permanent rejections are
    /// not retried at all.
    /// </remarks>
    private async Task<T> WithTransientRetriesAsync<T>(
        Func<CancellationToken, Task<T>> attempt,
        CancellationToken cancellationToken)
    {
        for (var i = 0; ; i++)
        {
            try
            {
                return await attempt(cancellationToken);
            }
            catch (GitHubIssueTransientException) when (i < _retryDelays.Count && !cancellationToken.IsCancellationRequested)
            {
                var delay = _retryDelays[i];
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, _timeProvider, cancellationToken);
                }
            }
        }
    }

    private async Task RecordFailureAsync(
        ComicMaintainerDbContext db,
        ErrorReportEntity entity,
        Exception exception,
        string configurationSignature,
        CancellationToken cancellationToken)
    {
        entity.LastError = ErrorReportRedactor.Truncate(exception.Message, 1024);

        // A permanent failure is a configuration problem the operator must fix.
        // Recording which configuration was rejected parks the row until that
        // configuration changes, so a bad token cannot produce one GitHub
        // request per occurrence indefinitely. Transient failures leave the
        // signature clear and are retried on the next occurrence.
        entity.State = ErrorReportState.Failed;
        entity.PermanentFailureSignature = exception is GitHubIssuePermanentException
            ? configurationSignature
            : null;
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

    /// <summary>
    /// True when the row is linked to an issue on a repository other than the
    /// project repository — either a row from a release where the destination
    /// was configurable, or one filed before the destination was recorded at
    /// all. Reusing such a number here is exactly the mistake this guards
    /// against.
    /// </summary>
    private static bool HasStaleIssueLinkage(ErrorReportEntity entity, GitHubIssueTarget target)
        => entity.GitHubIssueNumber is not null
            && !(string.Equals(entity.GitHubOwner, target.Owner, StringComparison.OrdinalIgnoreCase)
                && string.Equals(entity.GitHubRepo, target.Repo, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Identity of the reporting configuration, used to park a row that was
    /// permanently rejected until something the operator controls changes. The
    /// token is hashed with everything else and never stored in readable form.
    /// </summary>
    private static string ComputeConfigurationSignature(GitHubIssueTarget target, string? assignee)
    {
        var material = string.Join(
            '\n',
            target.Owner,
            target.Repo,
            target.Token,
            assignee ?? string.Empty);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexString(hash).ToLowerInvariant()[..32];
    }

    private static GitHubIssueTarget? BuildTarget(AppSettings settings)
    {
        var token = settings.ErrorReportingGitHubToken?.Trim();

        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        return new GitHubIssueTarget(ErrorReportingDestination.Owner, ErrorReportingDestination.Repo, token);
    }

    private static async Task SaveAsync(ComicMaintainerDbContext db, CancellationToken cancellationToken)
        => await db.SaveChangesAsync(cancellationToken);
}
