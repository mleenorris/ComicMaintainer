using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.ErrorReporting;

namespace ComicMaintainer.Tests.ErrorReporting;

public class ErrorReportIssueFormatterCoverageTests
{
    private static readonly string NewLine = Environment.NewLine;

    [Fact]
    public void BuildBody_FillsPlaceholdersForEveryMissingDetail()
    {
        var report = MinimalReport() with { RenderedMessage = null! };

        var body = ErrorReportIssueFormatter.BuildBody(report);

        Assert.Contains("| Exception | (none) |", body);
        Assert.Contains("| Source | (unknown) |", body);
        Assert.Contains("| App version | (unknown) |", body);
        Assert.Contains("| Runtime | (unknown) |", body);
        Assert.Contains("| OS | (unknown) |", body);
        Assert.Contains("| Correlation id | (none) |", body);
        Assert.Contains("| Occurrences | 1 |", body);
        Assert.Contains("| First seen (UTC) | 2026-01-01 12:00:00Z |", body);
        // A missing message still renders as an (empty) fenced block.
        Assert.Contains($"### Message{NewLine}{NewLine}```text{NewLine}{NewLine}```", body);
        Assert.DoesNotContain("### Message template", body);
        Assert.DoesNotContain("### Exception", body);
        Assert.DoesNotContain("### Stack trace", body);
        Assert.EndsWith(ErrorReportIssueFormatter.BuildFingerprintBlock("fingerprint-1") + NewLine, body);
    }

    [Fact]
    public void BuildBody_ShowsEveryDetailThatIsPresent()
    {
        var report = MinimalReport() with
        {
            ExceptionType = "System.IO.IOException",
            SourceContext = "ComicMaintainer.Core.Services.Svc",
            AppVersion = "2.0.316",
            RuntimeVersion = ".NET 10.0.0",
            OperatingSystem = "Linux 6.8 | x64",
            CorrelationId = "corr-123"
        };
        var existing = new ErrorReportEntity
        {
            Fingerprint = "fingerprint-1",
            OccurrenceCount = 4,
            FirstSeenAt = new DateTime(2025, 12, 31, 8, 30, 0, DateTimeKind.Utc)
        };

        var body = ErrorReportIssueFormatter.BuildBody(report, existing);

        Assert.Contains("| Exception | System.IO.IOException |", body);
        Assert.Contains("| Source | ComicMaintainer.Core.Services.Svc |", body);
        Assert.Contains("| App version | 2.0.316 |", body);
        Assert.Contains("| Runtime | .NET 10.0.0 |", body);
        Assert.Contains("| OS | Linux 6.8 \\| x64 |", body);
        Assert.Contains("| Correlation id | corr-123 |", body);
        Assert.Contains("| First seen (UTC) | 2025-12-31 08:30:00Z |", body);
        Assert.Contains("| Occurrences | 4 |", body);
    }

    [Fact]
    public void BuildRecurrenceComment_ShowsTheVersionAndCorrelationWhenPresent()
    {
        var report = MinimalReport() with { AppVersion = "2.0.316", CorrelationId = "corr-123" };

        var comment = ErrorReportIssueFormatter.BuildRecurrenceComment(report, new ErrorReportEntity { OccurrenceCount = 2 });

        Assert.Contains("| App version | 2.0.316 |", comment);
        Assert.Contains("| Correlation id | corr-123 |", comment);
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("Scan finished")]
    public void BuildBody_OmitsTheTemplateWhenItAddsNothing(string template)
    {
        var report = MinimalReport() with { MessageTemplate = template, RenderedMessage = "Scan finished" };

        var body = ErrorReportIssueFormatter.BuildBody(report);

        Assert.DoesNotContain("### Message template", body);
        Assert.Contains($"```text{NewLine}Scan finished{NewLine}```", body);
    }

    [Fact]
    public void BuildBody_OmitsBlankExceptionMessageAndStackTraceSections()
    {
        var report = MinimalReport() with
        {
            ExceptionType = "System.InvalidOperationException",
            ExceptionMessage = "   ",
            StackTrace = " \n "
        };

        var body = ErrorReportIssueFormatter.BuildBody(report);

        Assert.Contains("| Exception | System.InvalidOperationException |", body);
        Assert.DoesNotContain("### Exception", body);
        Assert.DoesNotContain("### Stack trace", body);
    }

    [Fact]
    public void BuildRecurrenceComment_FillsPlaceholdersForMissingVersionAndCorrelation()
    {
        var entity = new ErrorReportEntity { Fingerprint = "fingerprint-1", OccurrenceCount = 7 };

        var comment = ErrorReportIssueFormatter.BuildRecurrenceComment(MinimalReport(), entity);

        Assert.StartsWith("Still occurring: 7 occurrence(s) as of 2026-01-01 12:00:00Z.", comment);
        Assert.Contains("| App version | (unknown) |", comment);
        Assert.Contains("| Correlation id | (none) |", comment);
        Assert.Contains(ErrorReportIssueFormatter.BuildFingerprintBlock("fingerprint-1"), comment);
    }

    [Theory]
    [InlineData("NoNamespaceException", "Worker", "[field error] NoNamespaceException in Worker")]
    [InlineData("Ns.Trailing.", "Ns.Source.", "[field error] Ns.Trailing. in Ns.Source.")]
    [InlineData("System.IO.IOException", "ComicMaintainer.Core.Services.Svc", "[field error] IOException in Svc")]
    public void BuildTitle_ShortensOnlyNamesThatEndInASegment(string exceptionType, string source, string expected)
    {
        var report = MinimalReport() with { ExceptionType = exceptionType, SourceContext = source };

        Assert.Equal(expected, ErrorReportIssueFormatter.BuildTitle(report));
    }

    private static ErrorReport MinimalReport() => new()
    {
        Fingerprint = "fingerprint-1",
        Level = "Error",
        MessageTemplate = string.Empty,
        RenderedMessage = string.Empty,
        TimestampUtc = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc)
    };
}
