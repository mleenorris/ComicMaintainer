using ComicMaintainer.Core.ErrorReporting;

namespace ComicMaintainer.Tests.ErrorReporting;

public class ErrorReportLogPropertiesTests
{
    [Fact]
    public void Exclude_RecordsTheReasonUnderTheExclusionProperty()
    {
        var scope = ErrorReportLogProperties.Exclude("Malformed archive supplied by a user");

        var entry = Assert.Single(scope);
        Assert.Equal(ErrorReportLogProperties.ExcludeFromErrorReports, entry.Key);
        Assert.Equal("Malformed archive supplied by a user", entry.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Exclude_FallsBackToAnExplicitPlaceholderWhenNoReasonIsGiven(string? reason)
    {
        var scope = ErrorReportLogProperties.Exclude(reason!);

        Assert.Equal("unspecified", scope[ErrorReportLogProperties.ExcludeFromErrorReports]);
    }
}
