using ComicMaintainer.Core.Utilities;

namespace ComicMaintainer.Tests.Utilities;

public class AnnualIssueTests
{
    [Theory]
    [InlineData("Green Lantern Annual")]
    [InlineData("green lantern annual")]
    [InlineData("Green Lantern Annual (1992)")]
    [InlineData("Green Lantern Annual Vol. 2")]
    public void IsAnnualSeries_TrueForASeriesEndingInAnnual(string series)
    {
        Assert.True(AnnualIssue.IsAnnualSeries(series));
    }

    [Theory]
    [InlineData("Green Lantern")]
    [InlineData("Annuals of History")]
    [InlineData("Biannual")]
    [InlineData("The Annual Party")]
    [InlineData(null)]
    public void IsAnnualSeries_FalseOtherwise(string? series)
    {
        Assert.False(AnnualIssue.IsAnnualSeries(series));
    }

    [Theory]
    [InlineData("Green Lantern Annual 001 (1992).cbz", true)]
    [InlineData("Green Lantern (1990) Annual 001.cbz", true)]
    [InlineData("Green Lantern Annual (1990) - Chapter 0001.cbz", true)]
    [InlineData("Green Lantern Annual #1.cbz", true)]
    [InlineData("Green Lantern 001 (1992).cbz", false)]
    [InlineData("Green Lantern - Chapter 0001 - The Annual Party.cbz", false)]
    [InlineData(null, false)]
    public void FromFileName_FindsAnnualInTheSeriesPart(string? fileName, bool expected)
    {
        Assert.Equal(expected, AnnualIssue.FromFileName(fileName));
    }

    [Theory]
    [InlineData("Annual 1", "1")]
    [InlineData("annual #2", "2")]
    [InlineData("Annual 1.5", "1.5")]
    [InlineData("Annual", null)]
    [InlineData("3", "3")]
    [InlineData(null, null)]
    public void IssueNumber_ReadsTheNumberOfAnAnnual(string? issue, string? expected)
    {
        Assert.Equal(expected, AnnualIssue.IssueNumber(issue));
    }

    [Fact]
    public void IsAnnual_AnySourceNamesIt()
    {
        Assert.True(AnnualIssue.IsAnnual("Green Lantern Annual", "1", "x.cbz"));
        Assert.True(AnnualIssue.IsAnnual("Green Lantern", "Annual 1", "x.cbz"));
        Assert.True(AnnualIssue.IsAnnual("Green Lantern", "1", "Green Lantern Annual 001.cbz"));
        Assert.False(AnnualIssue.IsAnnual("Green Lantern", "1", "Green Lantern 001.cbz"));
    }

    [Theory]
    [InlineData("Green Lantern", "Green Lantern Annual")]
    [InlineData("Green Lantern (1990)", "Green Lantern Annual (1990)")]
    [InlineData("Saga Vol. 2", "Saga Annual Vol. 2")]
    [InlineData("Green Lantern Annual", "Green Lantern Annual")]
    public void WithAnnual_PutsAnnualBeforeTheVolume(string series, string expected)
    {
        Assert.Equal(expected, AnnualIssue.WithAnnual(series));
    }

    [Theory]
    [InlineData("Green Lantern Annual", "Green Lantern")]
    [InlineData("Green Lantern Annual (1990)", "Green Lantern (1990)")]
    [InlineData("Green Lantern", "Green Lantern")]
    [InlineData("Annual", "Annual")]
    public void WithoutAnnual_GivesTheSeriesItBelongsTo(string series, string expected)
    {
        Assert.Equal(expected, AnnualIssue.WithoutAnnual(series));
    }
}
