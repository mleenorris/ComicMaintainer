using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.ErrorReporting.Interfaces;
using ComicMaintainer.Core.ErrorReporting.Models;
using ComicMaintainer.Core.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.Core.ErrorReporting.Services;

/// <summary>
/// Coordinates the error-reporting pipeline: suppress, redact, fingerprint,
/// de-duplicate, throttle, persist and (when configured) transmit.
/// </summary>
/// <remarks>
/// <para>
/// Capture must never make a failure worse. Every public method therefore
/// swallows its own exceptions and logs them at Debug level: a reporter that
/// throws while reporting would turn a handled error into an outage, and
/// logging its own failure at Error level would feed the Serilog sink back into
/// itself.
/// </para>
/// <para>
/// Nothing leaves the instance unless <c>ErrorReportingMode</c> selects a
/// transmitting transport. With the default <c>manual</c> mode, capture only
/// writes a redacted row to the local database.
/// </para>
/// </remarks>
public class ErrorReportService : IErrorReportService
{
    /// <summary>
    /// Maximum stack-trace characters retained. Long enough for the frames that
    /// matter, short enough to keep an issue body readable and to stay inside
    /// the entity's column limit.
    /// </summary>
    private const int MaxStackTraceLength = 8000;

    private const int MaxMessageLength = 2000;

    private readonly IDbContextFactory<ComicMaintainerDbContext> _dbContextFactory;
    private readonly IOptionsMonitor<AppSettings> _settings;
    private readonly IErrorReportRedactor _redactor;
    private readonly IEnumerable<IErrorReportTransport> _transports;
    private readonly IErrorReportLogBuffer _logBuffer;
    private readonly ILogger<ErrorReportService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly string _appVersion;
    private readonly string _platform;

    public ErrorReportService(
        IDbContextFactory<ComicMaintainerDbContext> dbContextFactory,
        IOptionsMonitor<AppSettings> settings,
        IErrorReportRedactor redactor,
        IEnumerable<IErrorReportTransport> transports,
        IErrorReportLogBuffer logBuffer,
        ILogger<ErrorReportService> logger,
        TimeProvider? timeProvider = null)
    {
        _dbContextFactory = dbContextFactory;
        _settings = settings;
        _redactor = redactor;
        _transports = transports;
        _logBuffer = logBuffer;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;

        _appVersion = typeof(ErrorReportService).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        _platform = $"{System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription} / " +
                    $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription}";
    }

    /// <inheritdoc />
    public Task<ErrorReport?> CaptureAsync(
        Exception exception,
        ErrorReportSource source,
        string? origin = null,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exception);

        // Judge the whole exception chain before redaction: the suppression
        // rules match on real type names and original messages, and redaction
        // would rewrite the very fragments they look for.
        if (ErrorReportSuppressionRules.ShouldSuppress(exception))
        {
            return Task.FromResult<ErrorReport?>(null);
        }

        return CaptureCoreAsync(
            exception.GetType().FullName ?? exception.GetType().Name,
            BuildMessageChain(exception),
            exception.ToString(),
            source,
            origin,
            correlationId,
            lastUserAction: null,
            alreadyScreened: true,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<ErrorReport?> CaptureAsync(
        string exceptionType,
        string? message,
        string? stackTrace,
        ErrorReportSource source,
        string? origin = null,
        string? correlationId = null,
        string? lastUserAction = null,
        CancellationToken cancellationToken = default)
        => CaptureCoreAsync(
            exceptionType,
            message,
            stackTrace,
            source,
            origin,
            correlationId,
            lastUserAction,
            alreadyScreened: false,
            cancellationToken);

    private async Task<ErrorReport?> CaptureCoreAsync(
        string exceptionType,
        string? message,
        string? stackTrace,
        ErrorReportSource source,
        string? origin,
        string? correlationId,
        string? lastUserAction,
        bool alreadyScreened,
        CancellationToken cancellationToken)
    {
        try
        {
            var settings = _settings.CurrentValue;
            if (!settings.EnableErrorReporting)
            {
                return null;
            }

            if (!alreadyScreened && ErrorReportSuppressionRules.ShouldSuppress(exceptionType, message))
            {
                return null;
            }

            // Redaction is the hard gate: everything below this line is safe to
            // persist, display and transmit.
            var safeType = Truncate(_redactor.Redact(exceptionType), 512);
            var safeMessage = Truncate(_redactor.Redact(message), MaxMessageLength);
            var safeStack = Truncate(_redactor.Redact(stackTrace), MaxStackTraceLength);
            var safeOrigin = Truncate(_redactor.Redact(origin), 512);
            var safeAction = Truncate(_redactor.Redact(lastUserAction), 1024);
            var safeCorrelation = Truncate(_redactor.Redact(correlationId), 64);

            var fingerprint = ErrorFingerprinter.Compute(safeType, safeMessage, safeStack, _appVersion);
            var area = ErrorFingerprinter.DeriveArea(safeStack, safeOrigin);
            var excerpt = _redactor.RedactLines(_logBuffer.Snapshot(settings.ErrorReportLogContextLines));

            var now = _timeProvider.GetUtcNow().UtcDateTime;

            await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

            var entity = await db.ErrorReports
                .FirstOrDefaultAsync(e => e.Fingerprint == fingerprint, cancellationToken);

            if (entity is null)
            {
                entity = new ErrorReportEntity
                {
                    Fingerprint = fingerprint,
                    ExceptionType = safeType,
                    Message = safeMessage,
                    StackTrace = safeStack,
                    Origin = safeOrigin,
                    Source = (int)source,
                    Area = area,
                    AppVersion = _appVersion,
                    Platform = Truncate(_platform, 256),
                    OccurrenceCount = 1,
                    FirstSeenAt = now,
                    LastSeenAt = now,
                    CorrelationId = safeCorrelation,
                    LogExcerpt = string.Join("\n", excerpt),
                    LastUserAction = safeAction,
                };
                db.ErrorReports.Add(entity);
            }
            else
            {
                // A recurrence only advances the counter and the timestamps. The
                // original trace is kept: the first occurrence is the one whose
                // log excerpt has not yet been overwritten by later noise.
                entity.OccurrenceCount++;
                entity.LastSeenAt = now;
                if (!string.IsNullOrEmpty(safeCorrelation))
                {
                    entity.CorrelationId = safeCorrelation;
                }
            }

            await db.SaveChangesAsync(cancellationToken);

            var report = ToReport(entity);

            // In automatic mode the report is delivered immediately, subject to
            // the cooldown and the daily cap enforced by SubmitAsync.
            if (IsAutomatic(settings))
            {
                await SubmitAsync(fingerprint, cancellationToken);
            }

            return report;
        }
        catch (Exception ex)
        {
            // Debug, not Error: an Error here would be captured by the Serilog
            // sink and fed straight back into this method.
            _logger.LogDebug(ex, "Error reporting pipeline failed while capturing an error");
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ErrorReport>> GetReportsAsync(
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var take = Math.Clamp(limit, 1, 200);

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var entities = await db.ErrorReports
            .AsNoTracking()
            .OrderByDescending(e => e.LastSeenAt)
            .Take(take)
            .ToListAsync(cancellationToken);

        return entities.Select(ToReport).ToList();
    }

    /// <inheritdoc />
    public async Task<ErrorReport?> GetReportAsync(
        string fingerprint,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var entity = await db.ErrorReports
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Fingerprint == fingerprint, cancellationToken);

        return entity is null ? null : ToReport(entity);
    }

    /// <inheritdoc />
    public async Task<string?> PreviewIssueBodyAsync(
        string fingerprint,
        CancellationToken cancellationToken = default)
    {
        var report = await GetReportAsync(fingerprint, cancellationToken);
        return report is null ? null : ErrorReportIssueBuilder.BuildBody(report);
    }

    /// <inheritdoc />
    public async Task<ErrorReportTransportResult> SubmitAsync(
        string fingerprint,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var settings = _settings.CurrentValue;
            if (!settings.EnableErrorReporting)
            {
                return new ErrorReportTransportResult(false, null, null, "Error reporting is disabled.");
            }

            await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

            var entity = await db.ErrorReports
                .FirstOrDefaultAsync(e => e.Fingerprint == fingerprint, cancellationToken);

            if (entity is null)
            {
                return new ErrorReportTransportResult(false, null, null, "No such report.");
            }

            var transport = ResolveTransport(settings);
            var now = _timeProvider.GetUtcNow().UtcDateTime;

            // Throttles apply only to transports that actually transmit. The
            // consent-first transport just builds a link, so rate-limiting it
            // would only stop the user from seeing their own report.
            if (transport.TransmitsAutomatically)
            {
                var cooldown = TimeSpan.FromHours(Math.Max(0, settings.ErrorReportCooldownHours));
                if (entity.LastReportedAt is { } last && now - last < cooldown)
                {
                    return new ErrorReportTransportResult(
                        false,
                        entity.ReportedIssueNumber,
                        null,
                        $"Already reported at {last:u}; cooling down for {settings.ErrorReportCooldownHours}h.");
                }

                var since = now - TimeSpan.FromDays(1);
                var reportedToday = await db.ErrorReports
                    .CountAsync(e => e.LastReportedAt != null && e.LastReportedAt >= since, cancellationToken);

                if (reportedToday >= Math.Max(1, settings.ErrorReportMaxPerDay))
                {
                    return new ErrorReportTransportResult(
                        false,
                        null,
                        null,
                        $"Daily report cap of {settings.ErrorReportMaxPerDay} reached.");
                }
            }

            var result = await transport.SendAsync(ToReport(entity), cancellationToken);

            if (result.Delivered)
            {
                entity.LastReportedAt = now;
                entity.ReportedIssueNumber = result.IssueNumber;
                entity.ReportedIssueState = "open";
                await db.SaveChangesAsync(cancellationToken);
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error reporting pipeline failed while submitting {Fingerprint}",
                LoggingHelper.SanitizeForLog(fingerprint));
            return new ErrorReportTransportResult(false, null, null, "Submission failed; see debug log.");
        }
    }

    /// <inheritdoc />
    public async Task<bool> DismissAsync(string fingerprint, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var entity = await db.ErrorReports
            .FirstOrDefaultAsync(e => e.Fingerprint == fingerprint, cancellationToken);

        if (entity is null)
        {
            return false;
        }

        db.ErrorReports.Remove(entity);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private bool IsAutomatic(AppSettings settings) =>
        ResolveTransport(settings).TransmitsAutomatically;

    /// <summary>
    /// Picks the transport named by <c>ErrorReportingMode</c>, falling back to
    /// the non-transmitting one. The fallback direction matters: an unrecognised
    /// mode must never be interpreted as permission to send data.
    /// </summary>
    private IErrorReportTransport ResolveTransport(AppSettings settings)
    {
        var mode = settings.ErrorReportingMode?.Trim();

        var selected = _transports.FirstOrDefault(
            t => string.Equals(t.Mode, mode, StringComparison.OrdinalIgnoreCase));

        return selected
            ?? _transports.First(t => !t.TransmitsAutomatically);
    }

    /// <summary>
    /// Flattens an exception chain into one message, so the report shows the
    /// root cause and not just the outermost wrapper.
    /// </summary>
    private static string BuildMessageChain(Exception exception)
    {
        var parts = new List<string>();
        for (var current = exception; current is not null && parts.Count < 5; current = current.InnerException)
        {
            parts.Add(current.Message);
        }
        return string.Join(" --> ", parts);
    }

    private static ErrorReport ToReport(ErrorReportEntity entity) => new()
    {
        Fingerprint = entity.Fingerprint,
        ExceptionType = entity.ExceptionType,
        Message = entity.Message,
        StackTrace = entity.StackTrace,
        Origin = entity.Origin,
        Source = (ErrorReportSource)entity.Source,
        Area = entity.Area,
        AppVersion = entity.AppVersion,
        Platform = entity.Platform ?? string.Empty,
        OccurrenceCount = entity.OccurrenceCount,
        FirstSeenUtc = entity.FirstSeenAt,
        LastSeenUtc = entity.LastSeenAt,
        CorrelationId = entity.CorrelationId,
        LogExcerpt = string.IsNullOrEmpty(entity.LogExcerpt)
            ? Array.Empty<string>()
            : entity.LogExcerpt.Split('\n'),
        LastUserAction = entity.LastUserAction,
        ReportedIssueNumber = entity.ReportedIssueNumber,
    };

    private static string Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }
        return value.Length <= max ? value : value[..max];
    }
}
