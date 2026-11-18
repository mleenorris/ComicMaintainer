using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;

namespace ComicMaintainer.Tests.Utilities;

public class VolumeOrdinalTests
{
    private static ExternalSeriesMetadata Volume(string name, int? year, string? publisher = "DC Comics", int? issues = 50)
        => new() { CanonicalTitle = name, StartYear = year, Publisher = publisher, TotalChapters = issues, Source = "ComicVine", SourceId = $"{publisher}-{year}" };

    // What ComicVine's search answers for "Batman": DC's volumes, other names, a foreign
    // edition, a one-shot and a volume without a year.
    private static readonly ExternalSeriesMetadata[] Batman =
    [
        Volume("Batman", 2016, issues: 158),
        Volume("Batman: The Long Halloween", 1996, issues: 13),
        Volume("Batman", 1940, issues: 713),
        Volume("Batman", 2011, issues: 57),
        Volume("Batman", 2016, publisher: "Panini Comics", issues: 20),
        Volume("Batman", 1989, issues: 1),
        Volume("Batman", 2025, issues: 5),
        Volume("Batman", null),
    ];

    [Fact]
    public void Suggest_CountsTheSameNamedVolumesOfThePublisherThatStartedEarlier()
    {
        var suggestion = VolumeOrdinal.Suggest(Batman, "Batman", 2016);

        Assert.NotNull(suggestion);
        Assert.Equal(3, suggestion.Number);
        Assert.Equal("DC Comics", suggestion.Publisher);
        Assert.Equal(158, suggestion.Match.TotalChapters);
        // The one-shot of 1989 is listed, for the user to count, but not counted.
        Assert.Equal(new int?[] { 1940, 1989, 2011 }, suggestion.Earlier.Select(v => v.StartYear));
        Assert.Equal(new[] { true, false, true }, suggestion.Earlier.Select(VolumeOrdinal.CountsByDefault));
        Assert.DoesNotContain(suggestion.Match, suggestion.Earlier);
    }

    [Theory]
    [InlineData("Batman", 1940, 1)]
    [InlineData("Batman", 2025, 4)]
    // Punctuation and case do not matter.
    [InlineData("BATMAN!", 2011, 2)]
    public void Suggest_NumbersEachVolume(string title, int year, int expected)
    {
        Assert.Equal(expected, VolumeOrdinal.Suggest(Batman, title, year)?.Number);
    }

    [Fact]
    public void Suggest_ForeignEditionOfTheYear_CountsItsOwnPublisher()
    {
        var suggestion = VolumeOrdinal.Suggest([Volume("Batman", 2016, publisher: "Panini Comics", issues: 20)], "Batman", 2016);

        Assert.Equal(1, suggestion?.Number);
        Assert.Equal("Panini Comics", suggestion?.Publisher);
    }

    [Fact]
    public void Suggest_UnknownIssueCount_IsCounted()
    {
        var suggestion = VolumeOrdinal.Suggest([Volume("Saga", 2012), Volume("Saga", 2005, issues: null)], "Saga", 2012);

        Assert.Equal(2, suggestion?.Number);
    }

    [Theory]
    // DC's "Batman" volumes by start year, the one-shot of 1989 left out.
    [InlineData(1, 1940)]
    [InlineData(2, 2011)]
    [InlineData(3, 2016)]
    [InlineData(4, 2025)]
    [InlineData(5, null)]
    [InlineData(0, null)]
    public void Numbered_FindsTheNthVolumeOfTheMostFrequentPublisher(int number, int? expectedYear)
    {
        var volume = VolumeOrdinal.Numbered(Batman, "Batman", number);

        Assert.Equal(expectedYear, volume?.StartYear);
        if (volume is not null)
        {
            Assert.Equal("DC Comics", volume.Publisher);
        }
    }

    // Marvel's "All-New X-Men" on ComicVine: the 2013 volume, a 4-issue listing of 2014 (collected
    // editions) and the 2016 volume.
    private static readonly ExternalSeriesMetadata[] AllNewXMen =
    [
        Volume("All-New X-Men", 2013, "Marvel", 41),
        Volume("All-New X-Men", 2014, "Marvel", 4),
        Volume("All-New X-Men", 2016, "Marvel", 19),
    ];

    [Theory]
    // Without knowing the issues held, the 2014 listing counts as Vol. 2.
    [InlineData(2, 0, 2014)]
    // Holding issue #19 of Vol. 2 rules out a volume of 4 issues.
    [InlineData(2, 19, 2016)]
    [InlineData(2, 4, 2014)]
    [InlineData(1, 41, 2013)]
    // No volume is long enough.
    [InlineData(2, 50, null)]
    public void Numbered_SkipsAVolumeTooShortForTheIssuesHeld(int number, int highestIssueHeld, int? expectedYear)
    {
        Assert.Equal(expectedYear, VolumeOrdinal.Numbered(AllNewXMen, "All-New X-Men", number, highestIssueHeld: highestIssueHeld)?.StartYear);
    }

    [Fact]
    public void Numbered_CountsThePublisherOfTheSeriesOwnMatch()
    {
        Assert.Equal("Panini Comics", VolumeOrdinal.Numbered(Batman, "Batman", 1, knownVolumeId: "Panini Comics-2016")?.Publisher);
        Assert.Equal("DC Comics", VolumeOrdinal.Numbered(Batman, "Batman", 1, knownVolumeId: "not-among-them")?.Publisher);
        Assert.Null(VolumeOrdinal.Numbered(Batman, "!!", 1));
        Assert.Null(VolumeOrdinal.Numbered([], "Batman", 1));
    }

    [Theory]
    [InlineData("Batman", 2003)]
    [InlineData("Superman", 2016)]
    [InlineData("!!", 2016)]
    [InlineData("", 2016)]
    public void Suggest_NoVolumeOfThatNameAndYear_IsNull(string title, int year)
    {
        Assert.Null(VolumeOrdinal.Suggest(Batman, title, year));
    }
}
