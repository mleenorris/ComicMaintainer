using ComicMaintainer.Core.ErrorReporting;

namespace ComicMaintainer.Tests.ErrorReporting;

public class ErrorReportFingerprintCoverageTests
{
    [Fact]
    public void NormalizeFrames_SkipsBlankLinesBetweenFrames()
    {
        var frames = ErrorReportFingerprint.NormalizeFrames("at A.B()\n\n   \r\n\tat C.D()");

        Assert.Equal(new[] { "at A.B()", "at C.D()" }, frames);
    }

    [Fact]
    public void Compute_IsUnaffectedByBlankLinesInTheStack()
    {
        Assert.Equal(
            ErrorReportFingerprint.Compute("System.IO.IOException", "at A.B()\nat C.D()", "Failed {File}"),
            ErrorReportFingerprint.Compute("System.IO.IOException", "at A.B()\n\n  \nat C.D()", "Failed {File}"));
    }
}
