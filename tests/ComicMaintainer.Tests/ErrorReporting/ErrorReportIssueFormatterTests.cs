using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.ErrorReporting;

namespace ComicMaintainer.Tests.ErrorReporting;

public class ErrorReportIssueFormatterTests
{
    [Fact]
    public void BuildTitle_NamesTheExceptionAndTheSource()
    {
        var title = ErrorReportIssueFormatter.BuildTitle(CreateReport());

        Assert.Equal("[field error] IOException in ComicProcessorService", title);
    }

    [Fact]
    public void BuildTitle_FallsBackWhenThereIsNoExceptionOrSource()
    {
        var report = CreateReport() with { ExceptionType = null, SourceContext = null };

        Assert.Equal("[field error] Error in unknown source", ErrorReportIssueFormatter.BuildTitle(report));
    }

    [Fact]
    public void BuildLabels_MarksSeverityAndAutomation()
    {
        var labels = ErrorReportIssueFormatter.BuildLabels(CreateReport() with { Level = "Fatal" });

        Assert.Contains(ErrorReportIssueFormatter.FieldErrorLabel, labels);
        Assert.Contains(ErrorReportIssueFormatter.AutomatedLabel, labels);
        Assert.Contains("severity:fatal", labels);
    }

    [Fact]
    public void BuildBody_IncludesTheMachineReadableFingerprintBlock()
    {
        var body = ErrorReportIssueFormatter.BuildBody(CreateReport());

        Assert.Contains(ErrorReportIssueFormatter.FingerprintBlockLanguage, body);
        Assert.Contains("fingerprint: fp-1", body);
    }

    [Fact]
    public void BuildBody_ReportsTheOccurrenceCountFromTheLedger()
    {
        var body = ErrorReportIssueFormatter.BuildBody(
            CreateReport(),
            new ErrorReportEntity { Fingerprint = "fp-1", OccurrenceCount = 42 });

        Assert.Contains("| Occurrences | 42 |", body);
    }

    [Fact]
    public void BuildBody_NeutralisesFenceBreakoutsInReportText()
    {
        // Report text is derived from field data; it must not be able to close
        // the code fence and inject arbitrary Markdown into the issue.
        var report = CreateReport() with { RenderedMessage = "boom ``` <!-- hidden" };

        var body = ErrorReportIssueFormatter.BuildBody(report);

        Assert.DoesNotContain("boom ``` ", body, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildBody_EscapesPipesSoTableCellsCannotBeBroken()
    {
        var report = CreateReport() with { SourceContext = "Ns.Ty|pe" };

        var body = ErrorReportIssueFormatter.BuildBody(report);

        Assert.Contains(@"Ns.Ty\|pe", body);
    }

    [Fact]
    public void BuildRecurrenceComment_StatesTheUpdatedCount()
    {
        var comment = ErrorReportIssueFormatter.BuildRecurrenceComment(
            CreateReport(),
            new ErrorReportEntity { Fingerprint = "fp-1", OccurrenceCount = 7 });

        Assert.Contains("7 occurrence(s)", comment);
        Assert.Contains("fingerprint: fp-1", comment);
    }

    private static ErrorReport CreateReport() => new()
    {
        Fingerprint = "fp-1",
        Level = "Error",
        MessageTemplate = "Failed to process {File}",
        RenderedMessage = "Failed to process a file",
        ExceptionType = "System.IO.IOException",
        ExceptionMessage = "disk on fire",
        StackTrace = "at Ns.Type.Method()",
        SourceContext = "ComicMaintainer.Core.Services.ComicProcessorService",
        AppVersion = "2.0.0",
        TimestampUtc = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc)
    };
}
