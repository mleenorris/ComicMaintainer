using ComicMaintainer.Core.ErrorReporting;
using ComicMaintainer.Core.Utilities;

namespace ComicMaintainer.WebApi.Services;

/// <summary>
/// Drains the error-report queue and hands each report to
/// <see cref="IErrorReportingService"/>.
/// </summary>
/// <remarks>
/// Reports are processed strictly one at a time. Serial processing is what lets
/// the reporting service treat "is this fingerprint already reported?" as a
/// simple read-then-write without a distributed lock, and it keeps an error
/// storm from opening dozens of simultaneous connections to GitHub from a
/// machine that is already unhealthy.
/// </remarks>
public sealed class ErrorReportDispatchHostedService : BackgroundService
{
    private readonly IErrorReportQueue _queue;
    private readonly IErrorReportingService _reportingService;
    private readonly ILogger<ErrorReportDispatchHostedService> _logger;

    public ErrorReportDispatchHostedService(
        IErrorReportQueue queue,
        IErrorReportingService reportingService,
        ILogger<ErrorReportDispatchHostedService> logger)
    {
        _queue = queue;
        _reportingService = reportingService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Error report dispatcher started");

        try
        {
            await foreach (var report in _queue.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await _reportingService.ProcessAsync(report, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Logged as a warning: an error here would be picked up by
                    // the reporting sink and fed back into this same loop.
                    _logger.LogWarning(
                        ex,
                        "Failed to process error report {Fingerprint}",
                        LoggingHelper.SanitizeForLog(report.Fingerprint));
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }

        var dropped = _queue.DroppedCount;
        if (dropped > 0)
        {
            _logger.LogWarning("{Dropped} error report(s) were dropped because the report queue was full", dropped);
        }

        _logger.LogInformation("Error report dispatcher stopped");
    }
}
