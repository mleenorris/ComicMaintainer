using ComicMaintainer.Core.ErrorReporting.Interfaces;

namespace ComicMaintainer.WebApi.Logging;

/// <summary>
/// Drains <see cref="ErrorReportQueue"/> and runs each capture through the
/// error-reporting pipeline.
/// </summary>
/// <remarks>
/// <para>
/// The work is done here, off the logging thread, because capture touches the
/// database and may touch the network. It runs one item at a time: reports are
/// low-volume by design, and serialising them avoids racing two writes for the
/// same fingerprint.
/// </para>
/// <para>
/// Failures are logged at Debug. Anything higher would be picked up by
/// <see cref="ErrorReportingSink"/> and fed straight back into this queue.
/// </para>
/// </remarks>
public sealed class ErrorReportDispatchHostedService : BackgroundService
{
    private readonly ErrorReportQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ErrorReportDispatchHostedService> _logger;

    public ErrorReportDispatchHostedService(
        ErrorReportQueue queue,
        IServiceScopeFactory scopeFactory,
        ILogger<ErrorReportDispatchHostedService> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var pending in _queue.ReadAllAsync(stoppingToken))
            {
                await DispatchAsync(pending, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task DispatchAsync(PendingErrorCapture pending, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<IErrorReportService>();

            if (pending.Exception is { } exception)
            {
                await service.CaptureAsync(
                    exception,
                    pending.Source,
                    pending.Origin,
                    pending.CorrelationId,
                    cancellationToken);

                return;
            }

            await service.CaptureAsync(
                pending.ExceptionType,
                pending.Message,
                pending.StackTrace,
                pending.Source,
                pending.Origin,
                pending.CorrelationId,
                cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Error report dispatch failed: {Reason}", ex.GetType().Name);
        }
    }
}
