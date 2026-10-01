using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.ErrorReporting;
using ComicMaintainer.Tests.Helpers;
using ComicMaintainer.WebApi.Logging;
using Serilog.Events;
using Serilog.Parsing;

namespace ComicMaintainer.Tests.ErrorReporting;

/// <summary>
/// The sink runs inside a logging call, so the two things it must guarantee are
/// that it filters correctly and that it cannot throw.
/// </summary>
public class ErrorReportingSinkTests
{
    [Fact]
    public void ShouldReport_IsFalseByDefaultBecauseReportingIsOptIn()
    {
        Assert.False(ErrorReportingSink.ShouldReport(CreateEvent(LogEventLevel.Error), new AppSettings()));
    }

    [Theory]
    [InlineData(LogEventLevel.Verbose, false)]
    [InlineData(LogEventLevel.Debug, false)]
    [InlineData(LogEventLevel.Information, false)]
    [InlineData(LogEventLevel.Warning, false)]
    [InlineData(LogEventLevel.Error, true)]
    [InlineData(LogEventLevel.Fatal, true)]
    public void ShouldReport_OnlyCapturesErrorAndAbove(LogEventLevel level, bool expected)
    {
        Assert.Equal(expected, ErrorReportingSink.ShouldReport(CreateEvent(level), EnabledSettings()));
    }

    [Fact]
    public void ShouldReport_HonoursAFatalOnlyMinimumLevel()
    {
        var settings = EnabledSettings();
        settings.ErrorReportingMinimumLevel = "Fatal";

        Assert.False(ErrorReportingSink.ShouldReport(CreateEvent(LogEventLevel.Error), settings));
        Assert.True(ErrorReportingSink.ShouldReport(CreateEvent(LogEventLevel.Fatal), settings));
    }

    [Fact]
    public void ShouldReport_IgnoresCancellation()
    {
        var logEvent = CreateEvent(LogEventLevel.Error, exception: new OperationCanceledException());

        Assert.False(ErrorReportingSink.ShouldReport(logEvent, EnabledSettings()));
    }

    [Fact]
    public void ShouldReport_IgnoresCancellationWrappedInAnotherException()
    {
        var logEvent = CreateEvent(
            LogEventLevel.Error,
            exception: new InvalidOperationException("outer", new OperationCanceledException()));

        Assert.False(ErrorReportingSink.ShouldReport(logEvent, EnabledSettings()));
    }

    [Fact]
    public void ShouldReport_RespectsTheOptOutScopeMarker()
    {
        var logEvent = CreateEvent(
            LogEventLevel.Error,
            properties: new[]
            {
                new LogEventProperty(
                    ErrorReportLogProperties.ExcludeFromErrorReports,
                    new ScalarValue("corrupt archive supplied by the user"))
            });

        Assert.False(ErrorReportingSink.ShouldReport(logEvent, EnabledSettings()));
    }

    [Fact]
    public void ShouldReport_IgnoresTheReporterSOwnFailuresToAvoidAFeedbackLoop()
    {
        var logEvent = CreateEvent(
            LogEventLevel.Error,
            properties: new[]
            {
                new LogEventProperty("SourceContext", new ScalarValue("ComicMaintainer.Core.ErrorReporting.ErrorReportingService"))
            });

        Assert.False(ErrorReportingSink.ShouldReport(logEvent, EnabledSettings()));
    }

    [Fact]
    public void Emit_EnqueuesARedactedReport()
    {
        var queue = new ErrorReportQueue();
        var sink = new ErrorReportingSink(
            new ErrorReportFactory(new TestOptionsMonitor<AppSettings>(EnabledSettings())),
            queue,
            new TestOptionsMonitor<AppSettings>(EnabledSettings()));

        sink.Emit(CreateEvent(LogEventLevel.Error, exception: new InvalidOperationException("boom")));

        Assert.Equal(0, queue.DroppedCount);
    }

    [Fact]
    public void Emit_SwallowsFailuresSoLoggingIsNeverBroken()
    {
        var sink = new ErrorReportingSink(
            new ThrowingReportFactory(),
            new ErrorReportQueue(),
            new TestOptionsMonitor<AppSettings>(EnabledSettings()));

        // No assertion beyond "does not throw": an exception here would escape
        // into whatever code was logging its own failure.
        sink.Emit(CreateEvent(LogEventLevel.Error, exception: new InvalidOperationException("boom")));
    }

    private static AppSettings EnabledSettings() => new()
    {
        ErrorReportingEnabled = true,
        ErrorReportingGitHubToken = "a-token-value"
    };

    private static LogEvent CreateEvent(
        LogEventLevel level,
        Exception? exception = null,
        IEnumerable<LogEventProperty>? properties = null)
        => new(
            DateTimeOffset.UtcNow,
            level,
            exception,
            new MessageTemplateParser().Parse("Something failed"),
            properties ?? Array.Empty<LogEventProperty>());

    private sealed class ThrowingReportFactory : IErrorReportFactory
    {
        public ErrorReport Create(
            string level,
            string? messageTemplate,
            string? renderedMessage,
            Exception? exception,
            string? sourceContext,
            string? correlationId,
            DateTime timestampUtc)
            => throw new InvalidOperationException("factory exploded");
    }
}
