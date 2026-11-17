using ComicMaintainer.Core.Utilities;

namespace ComicMaintainer.Tests.Utilities;

public class FileNameTitleTests
{
    [Theory]
    // A file name left in the title: the series (punctuation aside) and the issue number.
    [InlineData("All New X-Men v2 008 (2016)", "All-New X-Men", "8", true)]
    [InlineData("All-New X-Men 008", "All-New X-Men", "008", true)]
    [InlineData("Green Lantern (1990) 012.5", "Green Lantern (1990)", "12.5", true)]
    [InlineData("Green Lantern Annual 003 (1994)", "Green Lantern Annual", "3", true)]
    [InlineData("Green Lantern 003", "Green Lantern", "Annual 3", true)]
    // A story title, another issue's number, or nothing to compare with.
    [InlineData("The Dark Phoenix Saga", "X-Men", "8", false)]
    [InlineData("All New X-Men v2 009 (2016)", "All-New X-Men", "8", false)]
    [InlineData("Batman v2 008", "All-New X-Men", "8", false)]
    [InlineData("All New X-Men", "All-New X-Men", "8", false)]
    [InlineData(null, "All-New X-Men", "8", false)]
    [InlineData("All New X-Men 008", null, "8", false)]
    [InlineData("All New X-Men 008", "All-New X-Men", null, false)]
    [InlineData("All New X-Men 008", "(2016)", "8", false)]
    [InlineData("Saga Special 001", "Saga", "Special", false)]
    public void IsLeftoverFileName_TellsAFileNameFromAStoryTitle(string? title, string? series, string? issue, bool expected)
    {
        Assert.Equal(expected, FileNameTitle.IsLeftoverFileName(title, series, issue));
    }

    [Theory]
    [InlineData("Saga 001 (2012)", 2012)]
    [InlineData("All New X-Men v2 008 (2016)", 2016)]
    [InlineData("All New X-Men v2 008 (2016) (Digital) (Zone-Empire)", 2016)]
    [InlineData("Green Lantern (1990) 001 [1990-1991]", 1990)]
    // A year before the issue number is the volume's start, not the cover date.
    [InlineData("Green Lantern (1990) 001", null)]
    [InlineData("Series (2023)", null)]
    [InlineData("Saga 001", null)]
    [InlineData("Saga 001 (1850)", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void CoverYear_IsTheBracketedYearAfterTheIssueNumber(string? stem, int? expected)
    {
        Assert.Equal(expected, FileNameTitle.CoverYear(stem));
    }

    [Theory]
    // The series (punctuation and case aside), then any volume token, then punctuation go.
    [InlineData("All New X-Men .Special – One Down", "All-New X-Men", "Special – One Down")]
    [InlineData("All New X-Men v2 .Special", "All-New X-Men (2015)", "Special")]
    [InlineData("All-New X-Men Vol. 2 - One Down", "All-New X-Men", "One Down")]
    [InlineData("Green Lantern (1990) 001", "Green Lantern Annual", "001")]
    [InlineData("All New X-Men", "All-New X-Men", "")]
    // A title that does not start with the series only loses its leading punctuation.
    [InlineData(".Special – One Down", "All-New X-Men", "Special – One Down")]
    [InlineData("X-Mens Special", "X-Men", "X-Mens Special")]
    [InlineData("All New", "All-New X-Men", "All New")]
    [InlineData("#Giant 2025 2", null, "Giant 2025 2")]
    [InlineData("- One Down", "(2016)", "One Down")]
    public void AfterSeries_TakesTheSeriesNameOffTheTitle(string title, string? series, string expected)
    {
        Assert.Equal(expected, FileNameTitle.AfterSeries(title, series));
    }
}
