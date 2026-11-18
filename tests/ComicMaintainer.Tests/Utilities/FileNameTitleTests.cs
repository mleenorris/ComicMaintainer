using ComicMaintainer.Core.Utilities;

namespace ComicMaintainer.Tests.Utilities;

public class FileNameTitleTests
{
    [Theory]
    [InlineData("v4 010 (2018)", "Nightwing", "10", true)]
    [InlineData("v1 #1", "House of M", "1", true)]
    [InlineData("001 (2015)", "I Hate Fairyland", "1", true)]
    [InlineData("z-issue 001 (2016)", "I Hate Fairyland", "1", true)]
    [InlineData("v2 .Annual 001 (2014)", "Batwoman", "1", true)]
    [InlineData("0_(2011)", "Batwoman", "0", true)]
    [InlineData("Issue #1", "Batwoman", "1", true)]
    [InlineData("Soulfire Vol 1 03 (of 10) (2011)", "Michael Turner's Soulfire", "3", false)]
    [InlineData("v4 010 (2018)", "Nightwing", "11", false)]
    [InlineData("The Hunt, Part 10", "Nightwing", "10", false)]
    [InlineData("v4 010 (2018)", null, "10", true)]
    public void IsLeftoverFileName_ATitleWithoutTheSeries_IsAFileNameWhenItHoldsNothingElse(string title, string? series, string issue, bool expected)
    {
        Assert.Equal(expected, FileNameTitle.IsLeftoverFileName(title, series, issue));
    }

    [Theory]
    // An old file name, of this issue or another.
    [InlineData("v4 010 (2018)", "Nightwing", "10", true)]
    [InlineData("v4 011 (2018)", "Nightwing", "10", true)]
    [InlineData("Vol. 2 003 (2023)", "I Hate Fairyland", "2", true)]
    [InlineData("All New X-Men v2 009 (2016)", "All-New X-Men", "8", true)]
    // A story title is never one, whatever number it holds.
    [InlineData("Vol. 2 003 (2023) Variant Edition", "I Hate Fairyland", "2", false)]
    [InlineData("The Hunt, Part 11", "Nightwing", "10", false)]
    [InlineData("Nightwing: Year One Part 3", "Nightwing", "10", false)]
    [InlineData("(2018)", "Nightwing", "10", false)]
    // A kind word naming another number is a label of this issue, not a file name.
    [InlineData("#Annual 2021", "Batman", "8", false)]
    [InlineData(null, "Nightwing", "10", false)]
    public void IsFileName_TakesAnOldFileNameOfAnyIssue(string? title, string series, string issue, bool expected)
    {
        Assert.Equal(expected, FileNameTitle.IsFileName(title, series, issue));
        if (expected)
        {
            Assert.Equal(title!.Contains($"{int.Parse(issue):000}", StringComparison.Ordinal), FileNameTitle.IsLeftoverFileName(title, series, issue));
        }
    }

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
