using ComicMaintainer.Core.ErrorReporting;

namespace ComicMaintainer.Tests.ErrorReporting;

/// <summary>
/// The fingerprint decides whether a failure becomes a new GitHub issue or just
/// bumps a counter, so these tests pin the two properties that matter: the same
/// defect must always hash the same, and different defects must not collide.
/// </summary>
public class ErrorReportFingerprintTests
{
    private const string StackTrace = """
        at ComicMaintainer.Core.Services.ComicProcessorService.ProcessAsync(String path) in /src/ComicMaintainer.Core/Services/ComicProcessorService.cs:line 120
        at ComicMaintainer.Core.Services.ComicProcessorService.RunAsync() in /src/ComicMaintainer.Core/Services/ComicProcessorService.cs:line 44
        """;

    [Fact]
    public void Compute_IsStableForIdenticalInput()
    {
        var first = ErrorReportFingerprint.Compute("System.IO.IOException", StackTrace, "Failed to process {File}", "Svc");
        var second = ErrorReportFingerprint.Compute("System.IO.IOException", StackTrace, "Failed to process {File}", "Svc");

        Assert.Equal(first, second);
    }

    [Fact]
    public void Compute_IgnoresLineNumbersAndSourcePaths()
    {
        // The same defect built from a different checkout, or after an
        // unrelated edit moved the frame down a line, must not file a new issue.
        var shifted = StackTrace
            .Replace("line 120", "line 173")
            .Replace("/src/", "/home/builder/work/");

        Assert.Equal(
            ErrorReportFingerprint.Compute("System.IO.IOException", StackTrace, "Failed to process {File}", "Svc"),
            ErrorReportFingerprint.Compute("System.IO.IOException", shifted, "Failed to process {File}", "Svc"));
    }

    [Fact]
    public void Compute_IgnoresRenderedArgumentsBecauseItUsesTheTemplate()
    {
        // Two occurrences of one bug differ only in the file that tripped it.
        var a = ErrorReportFingerprint.Compute("System.IO.IOException", StackTrace, "Failed to process {File}", "Svc");
        var b = ErrorReportFingerprint.Compute("System.IO.IOException", StackTrace, "Failed to process {File}", "Svc");

        Assert.Equal(a, b);
    }

    [Fact]
    public void Compute_DiffersByExceptionType()
    {
        Assert.NotEqual(
            ErrorReportFingerprint.Compute("System.IO.IOException", StackTrace, "Failed", "Svc"),
            ErrorReportFingerprint.Compute("System.InvalidOperationException", StackTrace, "Failed", "Svc"));
    }

    [Fact]
    public void Compute_DiffersByMessageTemplate()
    {
        Assert.NotEqual(
            ErrorReportFingerprint.Compute("System.IO.IOException", StackTrace, "Failed to rename {File}", "Svc"),
            ErrorReportFingerprint.Compute("System.IO.IOException", StackTrace, "Failed to delete {File}", "Svc"));
    }

    [Fact]
    public void Compute_DiffersByTopFrame()
    {
        var other = StackTrace.Replace("ProcessAsync", "NormalizeAsync");

        Assert.NotEqual(
            ErrorReportFingerprint.Compute("System.IO.IOException", StackTrace, "Failed", "Svc"),
            ErrorReportFingerprint.Compute("System.IO.IOException", other, "Failed", "Svc"));
    }

    [Fact]
    public void Compute_DiffersBySourceContextWhenThereIsNoException()
    {
        Assert.NotEqual(
            ErrorReportFingerprint.Compute(null, null, "Something failed", "ComicMaintainer.A"),
            ErrorReportFingerprint.Compute(null, null, "Something failed", "ComicMaintainer.B"));
    }

    [Fact]
    public void Compute_ProducesShortHexIdentifier()
    {
        var fingerprint = ErrorReportFingerprint.Compute("System.Exception", StackTrace, "Boom", "Svc");

        Assert.Equal(32, fingerprint.Length);
        Assert.Matches("^[0-9a-f]{32}$", fingerprint);
    }

    [Fact]
    public void NormalizeFrames_KeepsOnlyLeadingFramesAndDropsNonFrameLines()
    {
        var trace = """
            System.IO.IOException: disk on fire
               at A.B.C()
               --- End of stack trace from previous location ---
               at D.E.F()
            """;

        var frames = ErrorReportFingerprint.NormalizeFrames(trace, maxFrames: 5);

        Assert.Equal(new[] { "at A.B.C()", "at D.E.F()" }, frames);
    }

    [Fact]
    public void NormalizeFrames_StripsAsyncStateMachineDecorationButKeepsTheMethodName()
    {
        var withDecoration = "   at ComicMaintainer.Core.Services.Svc+<ProcessAsync>d__12.MoveNext()";
        var frames = ErrorReportFingerprint.NormalizeFrames(withDecoration);

        Assert.Equal("at ComicMaintainer.Core.Services.Svc.ProcessAsync()", frames.Single());
    }

    [Fact]
    public void NormalizeFrames_KeepsDistinctAsyncMethodsOnTheSameTypeApart()
    {
        // Dropping the captured name would collapse every async method on a
        // type to "Svc.MoveNext" and merge unrelated defects into one issue.
        var process = ErrorReportFingerprint.NormalizeFrames("at Ns.Svc+<ProcessAsync>d__12.MoveNext()").Single();
        var normalize = ErrorReportFingerprint.NormalizeFrames("at Ns.Svc+<NormalizeAsync>d__13.MoveNext()").Single();

        Assert.NotEqual(process, normalize);
    }

    [Fact]
    public void NormalizeFrames_IgnoresTheStateMachineOrdinal()
    {
        // Adding an unrelated async member shifts d__12 to d__13 without
        // changing the defect, so the fingerprint must not move with it.
        Assert.Equal(
            ErrorReportFingerprint.NormalizeFrames("at Ns.Svc+<ProcessAsync>d__12.MoveNext()").Single(),
            ErrorReportFingerprint.NormalizeFrames("at Ns.Svc+<ProcessAsync>d__31.MoveNext()").Single());
    }

    [Fact]
    public void NormalizeFrames_KeepsLambdaMethodNames()
    {
        var frame = ErrorReportFingerprint.NormalizeFrames("at Ns.Svc+<>c.<Handle>b__3_0()").Single();

        Assert.Equal("at Ns.Svc.Handle()", frame);
    }

    [Fact]
    public void Compute_DiffersByAsyncMethodOnTheSameType()
    {
        Assert.NotEqual(
            ErrorReportFingerprint.Compute("System.IO.IOException", "at Ns.Svc+<ProcessAsync>d__12.MoveNext()", "Failed", "Svc"),
            ErrorReportFingerprint.Compute("System.IO.IOException", "at Ns.Svc+<NormalizeAsync>d__13.MoveNext()", "Failed", "Svc"));
    }

    [Fact]
    public void Compute_HandlesMissingStackTrace()
    {
        var fingerprint = ErrorReportFingerprint.Compute(null, null, null, null);

        Assert.Equal(32, fingerprint.Length);
    }
}
