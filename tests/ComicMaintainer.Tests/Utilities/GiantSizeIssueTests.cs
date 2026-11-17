using ComicMaintainer.Core.Utilities;

namespace ComicMaintainer.Tests.Utilities;

public class GiantSizeIssueTests
{
    [Theory]
    [InlineData("Giant-Size X-Men", true)]
    [InlineData("Giant Size X-Men (1975)", true)]
    [InlineData("giant-sized spider-man", true)]
    [InlineData("X-Men Giant-Size", true)]
    [InlineData("X-Men Giant Size Vol. 2", true)]
    [InlineData("Giant-Size", true)]
    [InlineData("X-Men GiantSize", true)]
    // "Giant" alone, or inside a word, is part of the title.
    [InlineData("Giant Days", false)]
    [InlineData("The Iron Giant", false)]
    [InlineData("X-Men Ungiant-Size", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsGiantSizeSeries_NeedsGiantSizeAtEitherEnd(string? series, bool expected)
    {
        Assert.Equal(expected, GiantSizeIssue.IsGiantSizeSeries(series));
    }

    [Theory]
    [InlineData(null, "Giant-Size X-Men 001.cbz", true)]
    [InlineData("X-Men", "X-Men 001.cbz", false)]
    [InlineData(null, null, false)]
    [InlineData(null, " ", false)]
    public void IsGiantSize_ReadsTheSeriesThenTheFileName(string? series, string? fileName, bool expected)
    {
        Assert.Equal(expected, GiantSizeIssue.IsGiantSize(series, fileName));
    }

    [Theory]
    [InlineData("Giant-Size X-Men", "X-Men")]
    [InlineData("X-Men Giant Size", "X-Men")]
    [InlineData("X-Men", "X-Men")]
    [InlineData("Giant-Size", "Giant-Size")]
    public void WithoutGiantSize_LeavesTheTitle(string title, string expected)
    {
        Assert.Equal(expected, GiantSizeIssue.WithoutGiantSize(title));
    }
}
