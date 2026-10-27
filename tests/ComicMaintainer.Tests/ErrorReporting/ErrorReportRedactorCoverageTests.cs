using ComicMaintainer.Core.ErrorReporting;

namespace ComicMaintainer.Tests.ErrorReporting;

public class ErrorReportRedactorCoverageTests
{
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz";

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Truncate_TreatsANonPositiveLimitAsUnlimited(int maxLength)
    {
        Assert.Equal(Alphabet, ErrorReportRedactor.Truncate(Alphabet, maxLength));
    }

    [Theory]
    [InlineData(5, "abcde")]
    [InlineData(13, "abcdefghijklm")]
    public void Truncate_CutsWithoutAMarkerWhenTheMarkerWouldNotFit(int maxLength, string expected)
    {
        Assert.Equal(expected, ErrorReportRedactor.Truncate(Alphabet, maxLength));
    }

    [Fact]
    public void Truncate_AppendsTheMarkerOnceThereIsRoomForIt()
    {
        var truncated = ErrorReportRedactor.Truncate(Alphabet, 14);

        Assert.Equal("a… [truncated]", truncated);
        Assert.Equal(14, truncated.Length);
    }

    [Fact]
    public void RedactStackTrace_DropsFramesThatAreBlank()
    {
        var stack = "at A.B()\n   \n\r\nat C.D()";

        Assert.Equal("at A.B()\nat C.D()", ErrorReportRedactor.RedactStackTrace(stack, Array.Empty<string>()));
    }
}
