using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.ErrorReporting;
using ComicMaintainer.Core.Utilities;
using ComicMaintainer.WebApi.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ComicMaintainer.WebApi.Controllers;

/// <summary>
/// Operator view over the automated error-reporting ledger.
/// </summary>
/// <remarks>
/// Administrator-only in full, including the read endpoints: the records hold
/// redacted but still internal failure detail (stack traces, source types), and
/// there is no reason for an ordinary library user to see them. The write
/// actions are additionally covered by
/// <see cref="WriteOperationAuthorizationConvention"/>.
/// </remarks>
[ApiController]
[Route("api/error-reports")]
[Authorize(Policy = AuthorizationPolicies.CanAdminister)]
public class ErrorReportsController : ControllerBase
{
    private const int MaxPageSize = 200;

    private readonly IDbContextFactory<ComicMaintainerDbContext> _dbContextFactory;
    private readonly IErrorReportFactory _reportFactory;
    private readonly IErrorReportingService _reportingService;
    private readonly ILogger<ErrorReportsController> _logger;

    public ErrorReportsController(
        IDbContextFactory<ComicMaintainerDbContext> dbContextFactory,
        IErrorReportFactory reportFactory,
        IErrorReportingService reportingService,
        ILogger<ErrorReportsController> logger)
    {
        _dbContextFactory = dbContextFactory;
        _reportFactory = reportFactory;
        _reportingService = reportingService;
        _logger = logger;
    }

    /// <summary>Most recently seen failures, newest first.</summary>
    [HttpGet]
    public async Task<ActionResult<object>> GetReports(
        [FromQuery] int limit = 50,
        [FromQuery] string? state = null,
        CancellationToken cancellationToken = default)
    {
        var pageSize = Math.Clamp(limit, 1, MaxPageSize);

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var query = db.ErrorReports.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(state))
        {
            if (!ErrorReportState.IsKnown(state))
            {
                return BadRequest(new { error = $"Unknown state '{LoggingHelper.SanitizeForLog(state)}'" });
            }

            query = query.Where(e => e.State == state);
        }

        var total = await query.CountAsync(cancellationToken);

        var reports = await query
            .OrderByDescending(e => e.LastSeenAt)
            .Take(pageSize)
            .Select(e => new
            {
                fingerprint = e.Fingerprint,
                level = e.Level,
                exception_type = e.ExceptionType,
                source_context = e.SourceContext,
                message = e.RenderedMessage,
                occurrence_count = e.OccurrenceCount,
                first_seen_at = e.FirstSeenAt,
                last_seen_at = e.LastSeenAt,
                state = e.State,
                github_issue_number = e.GitHubIssueNumber,
                github_issue_url = e.GitHubIssueUrl,
                last_reported_at = e.LastReportedAt,
                last_error = e.LastError
            })
            .ToListAsync(cancellationToken);

        return Ok(new { total, reports });
    }

    /// <summary>Full detail (including the stack trace) for one fingerprint.</summary>
    [HttpGet("{fingerprint}")]
    public async Task<ActionResult<object>> GetReport(string fingerprint, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var report = await db.ErrorReports
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Fingerprint == fingerprint, cancellationToken);

        if (report is null)
        {
            return NotFound(new { error = "Error report not found" });
        }

        return Ok(new
        {
            fingerprint = report.Fingerprint,
            level = report.Level,
            exception_type = report.ExceptionType,
            source_context = report.SourceContext,
            message_template = report.MessageTemplate,
            message = report.RenderedMessage,
            stack_trace = report.StackTrace,
            app_version = report.AppVersion,
            correlation_id = report.CorrelationId,
            occurrence_count = report.OccurrenceCount,
            first_seen_at = report.FirstSeenAt,
            last_seen_at = report.LastSeenAt,
            state = report.State,
            github_issue_number = report.GitHubIssueNumber,
            github_issue_url = report.GitHubIssueUrl,
            last_reported_at = report.LastReportedAt,
            last_error = report.LastError
        });
    }

    /// <summary>
    /// Mutes a fingerprint so recurrences are counted but never filed. The
    /// escape hatch for a failure that is real, noisy, and already understood.
    /// </summary>
    [HttpPost("{fingerprint}/mute")]
    public Task<ActionResult> Mute(string fingerprint, CancellationToken cancellationToken = default)
        => SetStateAsync(fingerprint, ErrorReportState.Muted, cancellationToken);

    /// <summary>
    /// Unmutes a fingerprint. A fingerprint that already has an issue returns to
    /// <c>reported</c>; one that never did returns to <c>new</c> so it can be
    /// filed on its next occurrence.
    /// </summary>
    [HttpPost("{fingerprint}/unmute")]
    public Task<ActionResult> Unmute(string fingerprint, CancellationToken cancellationToken = default)
        => SetStateAsync(fingerprint, null, cancellationToken);

    /// <summary>
    /// Files a deliberately harmless report so an operator can confirm the
    /// token, repository and labels actually work — without waiting for a real
    /// failure, which is exactly when a misconfiguration is most costly.
    /// </summary>
    [HttpPost("test")]
    public async Task<ActionResult> SendTestReport(CancellationToken cancellationToken = default)
    {
        var report = _reportFactory.Create(
            "Error",
            "ComicMaintainer error reporting test report requested by an administrator",
            "ComicMaintainer error reporting test report requested by an administrator",
            exception: null,
            sourceContext: typeof(ErrorReportsController).FullName,
            correlationId: HttpContext.TraceIdentifier,
            timestampUtc: DateTime.UtcNow);

        var outcome = await _reportingService.ProcessAsync(report, cancellationToken);

        _logger.LogInformation("Error reporting test produced outcome {Outcome}", outcome);

        return outcome switch
        {
            ErrorReportOutcome.IssueCreated or ErrorReportOutcome.CommentAdded =>
                Ok(new { message = "Test report delivered to GitHub", outcome = outcome.ToString(), fingerprint = report.Fingerprint }),
            ErrorReportOutcome.Recorded =>
                Ok(new { message = "Test report was recorded; an issue already exists for it", outcome = outcome.ToString(), fingerprint = report.Fingerprint }),
            ErrorReportOutcome.Disabled =>
                BadRequest(new { error = "Error reporting is disabled" }),
            ErrorReportOutcome.NotConfigured =>
                BadRequest(new { error = "A GitHub owner, repository and token must be configured" }),
            ErrorReportOutcome.RateLimited =>
                BadRequest(new { error = "The daily issue cap has been reached" }),
            ErrorReportOutcome.Muted =>
                BadRequest(new { error = "The test report fingerprint is muted" }),
            _ =>
                StatusCode(502, new { error = "The test report could not be delivered to GitHub; check connectivity, the repository name and token permissions", outcome = outcome.ToString() })
        };
    }

    private async Task<ActionResult> SetStateAsync(string fingerprint, string? mutedState, CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var report = await db.ErrorReports
            .FirstOrDefaultAsync(e => e.Fingerprint == fingerprint, cancellationToken);

        if (report is null)
        {
            return NotFound(new { error = "Error report not found" });
        }

        report.State = mutedState
            ?? (report.GitHubIssueNumber is null ? ErrorReportState.New : ErrorReportState.Reported);

        await db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Error report {Fingerprint} state set to {State}",
            LoggingHelper.SanitizeForLog(report.Fingerprint),
            report.State);

        return Ok(new { message = "Error report updated", state = report.State });
    }
}
