using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;

namespace ComicMaintainer.Tests.Utilities;

public class VolumeOrdinalTests
{
    private static ExternalSeriesMetadata Volume(string name, int? year, string? publisher = "DC Comics", int? issues = 50)
        => new() { CanonicalTitle = name, StartYear = year, Publisher = publisher, TotalChapters = issues, Source = "ComicVine" };

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
    [InlineData("Batman", 2003)]
    [InlineData("Superman", 2016)]
    [InlineData("!!", 2016)]
    [InlineData("", 2016)]
    public void Suggest_NoVolumeOfThatNameAndYear_IsNull(string title, int year)
    {
        Assert.Null(VolumeOrdinal.Suggest(Batman, title, year));
    }
}
