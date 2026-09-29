using System.Text;
using ComicMaintainer.Core.ErrorReporting.Interfaces;
using ComicMaintainer.Core.ErrorReporting.Models;
using Serilog.Core;
using Serilog.Events;

namespace ComicMaintainer.WebApi.Logging;

/// <summary>
/// Observes the Serilog pipeline: keeps a rolling window of recent lines and
/// forwards <see cref="LogEventLevel.Error"/> and
/// <see cref="LogEventLevel.Fatal"/> events to the error-reporting queue.
/// </summary>
/// <remarks>
/// <para>
/// Hooking the logging pipeline rather than individual call sites means every
/// controller, service and hosted service is covered without edits, including
/// code added later. The trade-off is that the sink sees log events, not
/// exceptions, so <see cref="LogEvent.Exception"/> may be null for errors that
/// were logged as plain messages; those are still captured, using the rendered
/// message as the identity.
/// </para>
/// <para>
/// The sink performs no I/O. Everything it does is in-memory and bounded, so a
/// slow database or an unreachable GitHub can never stall logging.
/// </para>
/// </remarks>
public sealed class ErrorReportingSink : ILogEventSink
{
    /// <summary>
    /// Source contexts whose errors are ignored to prevent a feedback loop: if
    /// reporting an error itself logs an error, the sink would re-enter and
    /// report that too, indefinitely.
    /// </summary>
    private static readonly string[] ExcludedSourceContexts =
    [
        "ComicMaintainer.Core.ErrorReporting",
        "ComicMaintainer.WebApi.Logging",
    ];

    private readonly IErrorReportLogBuffer _buffer;
    private readonly ErrorReportQueue _queue;

    public ErrorReportingSink(IErrorReportLogBuffer buffer, ErrorReportQueue queue)
    {
        _buffer = buffer;
        _queue = queue;
    }

    /// <inheritdoc />
    public void Emit(LogEvent logEvent)
    {
        if (logEvent is null)
        {
            return;
        }

        var sourceContext = ReadSourceContext(logEvent);

        _buffer.Add(Format(logEvent, sourceContext));

        if (logEvent.Level < LogEventLevel.Error || IsExcluded(sourceContext))
        {
            return;
        }

        var exception = logEvent.Exception;

        _queue.TryEnqueue(new PendingErrorCapture(
            ExceptionType: exception?.GetType().FullName ?? "LoggedError",
            Message: exception?.Message ?? logEvent.RenderMessage(),
            StackTrace: exception?.ToString(),
            Source: ErrorReportSource.Log,
            Origin: sourceContext));
    }

    private static bool IsExcluded(string? sourceContext) =>
        sourceContext is not null
        && ExcludedSourceContexts.Any(excluded =>
            sourceContext.Contains(excluded, StringComparison.Ordinal));

    private static string? ReadSourceContext(LogEvent logEvent) =>
        logEvent.Properties.TryGetValue("SourceContext", out var value)
            ? value.ToString().Trim('"')
            : null;

    private static string Format(LogEvent logEvent, string? sourceContext)
    {
        var builder = new StringBuilder(256)
            .Append('[').Append(logEvent.Timestamp.UtcDateTime.ToString("HH:mm:ss.fff")).Append("] [")
            .Append(logEvent.Level.ToString().ToUpperInvariant()).Append("] ");

        if (!string.IsNullOrEmpty(sourceContext))
        {
            builder.Append('[').Append(sourceContext).Append("] ");
        }

        builder.Append(logEvent.RenderMessage());

        if (logEvent.Exception is not null)
        {
            builder.Append(" | ").Append(logEvent.Exception.GetType().Name)
                   .Append(": ").Append(logEvent.Exception.Message);
        }

        return builder.ToString();
    }
}
