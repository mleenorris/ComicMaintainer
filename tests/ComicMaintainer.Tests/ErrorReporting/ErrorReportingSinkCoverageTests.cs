using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.ErrorReporting;
using ComicMaintainer.Tests.Helpers;
using ComicMaintainer.WebApi.Logging;
using Moq;
using Serilog.Events;
using Serilog.Parsing;

namespace ComicMaintainer.Tests.ErrorReporting;

public class ErrorReportingSinkCoverageTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 2, 3, 4, 5, 6, TimeSpan.Zero);

    private static LogEvent CreateEvent(LogEventLevel level, params LogEventProperty[] properties) =>
        new(Timestamp, level, exception: null, new MessageTemplateParser().Parse("Import of {File} failed"), properties);

    private static AppSettings Enabled(string? minimumLevel = "Error") => new()
    {
        ErrorReportingEnabled = true,
        ErrorReportingMinimumLevel = minimumLevel!
    };

    [Fact]
    public void ShouldReport_NullEvent_IsFalse()
    {
        Assert.False(ErrorReportingSink.ShouldReport(null!, Enabled()));
    }

    [Theory]
    [InlineData("Warning")]
    [InlineData("information")]
    [InlineData("Verbose")]
    public void ShouldReport_MinimumLevelBelowError_StillIgnoresWarnings(string minimumLevel)
    {
        var settings = Enabled(minimumLevel);

        Assert.False(ErrorReportingSink.ShouldReport(CreateEvent(LogEventLevel.Warning), settings));
        Assert.True(ErrorReportingSink.ShouldReport(CreateEvent(LogEventLevel.Error), settings));
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("")]
    [InlineData(null)]
    public void ShouldReport_UnparseableMinimumLevel_DefaultsToError(string? minimumLevel)
    {
        var settings = Enabled(minimumLevel);

        Assert.False(ErrorReportingSink.ShouldReport(CreateEvent(LogEventLevel.Warning), settings));
        Assert.True(ErrorReportingSink.ShouldReport(CreateEvent(LogEventLevel.Error), settings));
        Assert.True(ErrorReportingSink.ShouldReport(CreateEvent(LogEventLevel.Fatal), settings));
    }

    [Fact]
    public void ShouldReport_NonStringScalarSourceContext_IsMatchedOnItsRenderedValue()
    {
        var selfReported = CreateEvent(
            LogEventLevel.Error,
            new LogEventProperty("SourceContext", new ScalarValue(new Uri("https://example.test/ErrorReportingService"))));
        var unrelated = CreateEvent(LogEventLevel.Error, new LogEventProperty("SourceContext", new ScalarValue(42)));

        Assert.False(ErrorReportingSink.ShouldReport(selfReported, Enabled()));
        Assert.True(ErrorReportingSink.ShouldReport(unrelated, Enabled()));
    }

    [Fact]
    public void ShouldReport_StructuredSourceContext_IsMatchedOnItsRenderedValue()
    {
        var selfReported = CreateEvent(
            LogEventLevel.Error,
            new LogEventProperty(
                "SourceContext",
                new StructureValue(new[] { new LogEventProperty("Name", new ScalarValue("GitHubIssueClient")) })));
        var unrelated = CreateEvent(
            LogEventLevel.Error,
            new LogEventProperty("SourceContext", new SequenceValue(new[] { new ScalarValue("FilesController") })));

        Assert.False(ErrorReportingSink.ShouldReport(selfReported, Enabled()));
        Assert.True(ErrorReportingSink.ShouldReport(unrelated, Enabled()));
    }

    [Fact]
    public void Emit_PassesNonStringPropertiesToTheFactoryAsText()
    {
        var correlationId = Guid.Parse("6f1c2b0e-3d4a-4c5b-9e8f-0a1b2c3d4e5f");
        var report = new ErrorReport
        {
            Fingerprint = "fp",
            Level = "Error",
            MessageTemplate = "Import of {File} failed",
            RenderedMessage = "Import of \"a.cbz\" failed",
            TimestampUtc = Timestamp.UtcDateTime
        };
        var factory = new Mock<IErrorReportFactory>();
        factory
            .Setup(f => f.Create(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<Exception?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<DateTime>()))
            .Returns(report);
        var queue = new Mock<IErrorReportQueue>();
        var sink = new ErrorReportingSink(factory.Object, queue.Object, new TestOptionsMonitor<AppSettings>(Enabled()));

        sink.Emit(CreateEvent(
            LogEventLevel.Error,
            new LogEventProperty("File", new ScalarValue("a.cbz")),
            new LogEventProperty("SourceContext", new ScalarValue(1234)),
            new LogEventProperty("CorrelationId", new ScalarValue(correlationId))));

        factory.Verify(f => f.Create(
            "Error",
            "Import of {File} failed",
            "Import of \"a.cbz\" failed",
            null,
            "1234",
            correlationId.ToString(),
            Timestamp.UtcDateTime), Times.Once);
        queue.Verify(q => q.TryEnqueue(report), Times.Once);
    }

    [Fact]
    public void Emit_WithMinimumLevelBelowError_DoesNotReportWarnings()
    {
        var factory = new Mock<IErrorReportFactory>(MockBehavior.Strict);
        var queue = new Mock<IErrorReportQueue>(MockBehavior.Strict);
        var sink = new ErrorReportingSink(factory.Object, queue.Object, new TestOptionsMonitor<AppSettings>(Enabled("Debug")));

        sink.Emit(CreateEvent(LogEventLevel.Warning));

        factory.VerifyNoOtherCalls();
        queue.VerifyNoOtherCalls();
    }
}
