using ComicMaintainer.Core.Data;

namespace ComicMaintainer.Tests.Data;

public class ErrorReportStateTests
{
    [Theory]
    [InlineData(ErrorReportState.New)]
    [InlineData(ErrorReportState.Reported)]
    [InlineData(ErrorReportState.Muted)]
    [InlineData(ErrorReportState.Failed)]
    public void IsKnown_AcceptsEveryDefinedState(string value)
    {
        Assert.True(ErrorReportState.IsKnown(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("NEW")]
    [InlineData(" muted")]
    [InlineData("resolved")]
    public void IsKnown_RejectsAnythingElse(string? value)
    {
        Assert.False(ErrorReportState.IsKnown(value));
    }
}
