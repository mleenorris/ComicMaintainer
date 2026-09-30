using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.ErrorReporting;
using Microsoft.Extensions.Options;
using Serilog.Core;
using Serilog.Events;

namespace ComicMaintainer.WebApi.Logging;

/// <summary>
/// Serilog sink that turns <c>Error</c>/<c>Fatal</c> log events into queued
/// <see cref="ErrorReport"/>s.
/// </summary>
/// <remarks>
/// <para>Attaching to the logging pipeline rather than to a single catch block
/// is what makes coverage automatic: <c>GlobalExceptionHandler</c> already logs
/// every unhandled request failure once, and the watcher, scheduled jobs and
/// queue consumers all log their failures through the same
/// <c>ILogger</c>, so they are captured without touching any of them.</para>
/// <para>The sink does the absolute minimum on the logging thread. It never
/// throws: an exception escaping <see cref="Emit"/> would surface inside
/// whatever code was in the middle of reporting its own failure, turning a
/// handled error into an unhandled one.</para>
/// </remarks>
public sealed class ErrorReportingSink : ILogEventSink
{
    private readonly IErrorReportFactory _reportFactory;
    private readonly IErrorReportQueue _queue;
    private readonly IOptionsMonitor<AppSettings> _appSettings;

    /// <summary>
    /// Types whose own failures must not be reported. Without this the first
    /// GitHub outage would log an error from the reporter, which would be
    /// captured, which would fail to report, which would log an error: a
    /// self-sustaining loop that both floods the log and never terminates.
    /// </summary>
    private static readonly string[] SelfExcludedSourceContexts =
    {
        nameof(ErrorReportingService),
        nameof(GitHubIssueClient),
        nameof(ErrorReportingSink),
        "ErrorReportDispatchHostedService"
    };

    public ErrorReportingSink(
        IErrorReportFactory reportFactory,
        IErrorReportQueue queue,
        IOptionsMonitor<AppSettings> appSettings)
    {
        _reportFactory = reportFactory;
        _queue = queue;
        _appSettings = appSettings;
    }

    public void Emit(LogEvent logEvent)
    {
        try
        {
            if (!ShouldReport(logEvent, _appSettings.CurrentValue))
            {
                return;
            }

            var report = _reportFactory.Create(
                logEvent.Level.ToString(),
                logEvent.MessageTemplate.Text,
                logEvent.RenderMessage(),
                logEvent.Exception,
                GetScalarProperty(logEvent, "SourceContext"),
                GetScalarProperty(logEvent, "CorrelationId"),
                logEvent.Timestamp.UtcDateTime);

            _queue.TryEnqueue(report);
        }
        catch
        {
            // Deliberately swallowed. A failure to capture a report is strictly
            // less important than the error that was being logged, which has
            // already been written to the log files by the other sinks.
        }
    }

    /// <summary>
    /// Decides whether an event is worth reporting. Exposed for tests so the
    /// filtering rules can be asserted without a live logging pipeline.
    /// </summary>
    public static bool ShouldReport(LogEvent logEvent, AppSettings settings)
    {
        if (logEvent is null || !settings.ErrorReportingEnabled)
        {
            return false;
        }

        if (logEvent.Level < ParseMinimumLevel(settings.ErrorReportingMinimumLevel))
        {
            return false;
        }

        // Warnings and below are routine in this application; reporting them
        // would bury real defects. Guards against a misconfigured minimum level.
        if (logEvent.Level < LogEventLevel.Error)
        {
            return false;
        }

        // A cancelled request is the client navigating away mid-response, not a
        // defect. GlobalExceptionHandler already filters the aborted-request
        // case, but background work cancelled at shutdown logs these too.
        if (IsCancellation(logEvent.Exception))
        {
            return false;
        }

        if (logEvent.Properties.ContainsKey(ErrorReportLogProperties.ExcludeFromErrorReports))
        {
            return false;
        }

        var sourceContext = GetScalarProperty(logEvent, "SourceContext");
        if (sourceContext is not null
            && SelfExcludedSourceContexts.Any(s => sourceContext.Contains(s, StringComparison.Ordinal)))
        {
            return false;
        }

        return true;
    }

    private static bool IsCancellation(Exception? exception)
    {
        while (exception is not null)
        {
            if (exception is OperationCanceledException)
            {
                return true;
            }

            exception = exception.InnerException;
        }

        return false;
    }

    private static LogEventLevel ParseMinimumLevel(string? configured)
        => Enum.TryParse<LogEventLevel>(configured, ignoreCase: true, out var level)
            ? level
            : LogEventLevel.Error;

    private static string? GetScalarProperty(LogEvent logEvent, string name)
    {
        if (!logEvent.Properties.TryGetValue(name, out var value))
        {
            return null;
        }

        return value is ScalarValue { Value: string s }
            ? s
            : value.ToString().Trim('"');
    }
}
