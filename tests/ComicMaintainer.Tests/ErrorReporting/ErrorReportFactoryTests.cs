using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.ErrorReporting;
using ComicMaintainer.Tests.Helpers;

namespace ComicMaintainer.Tests.ErrorReporting;

public class ErrorReportFactoryTests
{
    private static readonly DateTime Timestamp = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly ErrorReportFactory _factory = new(new TestOptionsMonitor<AppSettings>(new AppSettings()));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateExternal_TreatsABlankTypeAsNoExceptionType(string? type)
    {
        var report = CreateExternal(new ExternalFailure(type, "boom", "at render (main.js)"));

        Assert.Null(report.ExceptionType);
        Assert.Equal("boom", report.ExceptionMessage);
        Assert.Equal("boom\nat render (main.js)", report.StackTrace);
        Assert.Equal(
            ErrorReportFingerprint.Compute(null, "at render (main.js)", "Uncaught client error", "Browser"),
            report.Fingerprint);
    }

    [Theory]
    [InlineData("TypeError", "x is undefined", "at render (main.js)", "TypeError: x is undefined\nat render (main.js)")]
    [InlineData(" TypeError ", " x is undefined ", "  at render (main.js)  ", "TypeError: x is undefined\nat render (main.js)")]
    [InlineData("TypeError", null, "at render (main.js)", "TypeError\nat render (main.js)")]
    [InlineData("TypeError", "   ", "at render (main.js)", "TypeError\nat render (main.js)")]
    [InlineData("TypeError", "x is undefined", null, "TypeError: x is undefined")]
    [InlineData("TypeError", "x is undefined", "   ", "TypeError: x is undefined")]
    [InlineData(null, "x is undefined", null, "x is undefined")]
    [InlineData(null, null, "at render (main.js)", "at render (main.js)")]
    [InlineData("", "   ", " at render (main.js) ", "at render (main.js)")]
    public void CreateExternal_ComposesTheReportedStackLikeExceptionToString(
        string? type,
        string? message,
        string? frames,
        string expected)
    {
        var report = CreateExternal(new ExternalFailure(type, message, frames));

        Assert.Equal(expected, report.StackTrace);
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData(null, "   ", null)]
    [InlineData("", null, "   ")]
    public void CreateExternal_ReportsNoStackWhenThereIsNothingToShow(string? type, string? message, string? frames)
    {
        var report = CreateExternal(new ExternalFailure(type, message, frames));

        Assert.Null(report.StackTrace);
        Assert.Null(report.ExceptionType);
    }

    [Fact]
    public void Create_UsesEmptyTextForAMissingTemplateAndMessage()
    {
        var report = _factory.Create("Error", null, null, null, null, null, Timestamp);

        Assert.Equal(string.Empty, report.MessageTemplate);
        Assert.Equal(string.Empty, report.RenderedMessage);
        Assert.Null(report.ExceptionType);
        Assert.Null(report.ExceptionMessage);
        Assert.Null(report.StackTrace);
        Assert.Null(report.SourceContext);
        Assert.Null(report.CorrelationId);
        Assert.Equal(Timestamp, report.TimestampUtc);
        Assert.False(string.IsNullOrWhiteSpace(report.AppVersion));
        Assert.Equal(ErrorReportFingerprint.Compute(null, null, null, null), report.Fingerprint);
    }

    [Fact]
    public void CreateExternal_UsesEmptyTextForAMissingTemplateAndMessage()
    {
        var report = _factory.CreateExternal(
            "Error",
            null,
            null,
            new ExternalFailure("TypeError", "x is undefined", null),
            null,
            null,
            Timestamp);

        Assert.Equal(string.Empty, report.MessageTemplate);
        Assert.Equal(string.Empty, report.RenderedMessage);
        Assert.Equal("TypeError", report.ExceptionType);
    }

    private ErrorReport CreateExternal(ExternalFailure failure)
        => _factory.CreateExternal(
            "Error",
            "Uncaught client error",
            "Uncaught client error",
            failure,
            "Browser",
            "corr-1",
            Timestamp);
}
