using ComicMaintainer.Core.Utilities;

namespace ComicMaintainer.Tests.Utilities;

public class IssueNumberParserTests
{
    [Theory]
    [InlineData("Chapter 12", "12")]
    [InlineData("ch 5", "5")]
    [InlineData("ch.10", "10")]
    [InlineData("Ch-3.5", "3.5")]
    [InlineData("Series ch_3.5", "3.5")]
    [InlineData("Batman - Ch 0004.5", "4.5")]
    [InlineData("Series - Chapter 0042", "42")]
    [InlineData("Series - Chapter 0000", "0")]
    [InlineData("Series Chap. 7", "7")]
    [InlineData("Berserk v01 c005", "5")]
    [InlineData("Batman #005", "5")]
    [InlineData("Batman Issue 3", "3")]
    [InlineData("Batman Issue#3", "3")]
    public void Parse_MarkedNumber_ReturnsIt(string name, string expected)
    {
        Assert.Equal(expected, IssueNumberParser.Parse(name));
    }

    [Theory]
    // The cases the old first-number parser got wrong: a year, a volume or a number in the
    // series name came before the issue number.
    [InlineData("Batman (2016) 005", "5")]
    [InlineData("Vol.1 Ch.12", "12")]
    [InlineData("Vol.03 Chapter 012.5", "12.5")]
    [InlineData("Spider-Man 2099 012", "12")]
    [InlineData("Series Vol.1 005", "5")]
    [InlineData("Berserk v01 005", "5")]
    [InlineData("Mob Psycho 100 - 005", "5")]
    [InlineData("20th Century Boys 005", "5")]
    [InlineData("Batman 005 (2016) (Digital) (Zone-Empire)", "5")]
    [InlineData("Batman [2016] {HQ} 005", "5")]
    public void Parse_YearVolumeOrNumberInTitle_ReturnsTheIssueNumber(string name, string expected)
    {
        Assert.Equal(expected, IssueNumberParser.Parse(name));
    }

    [Theory]
    [InlineData("Series Name 042", "42")]
    [InlineData("Test 12.5 Extra", "12.5")]
    [InlineData("Series Name 0004.5", "4.5")]
    [InlineData("Series_005", "5")]
    [InlineData("Series.005", "5")]
    public void Parse_UnmarkedNumber_ReturnsIt(string name, string expected)
    {
        Assert.Equal(expected, IssueNumberParser.Parse(name));
    }

    [Theory]
    [InlineData("Series (2023)")]
    [InlineData("Series Name [2023]")]
    [InlineData("Test [v1]")]
    [InlineData("Series Vol 2")]
    [InlineData("Series Volume 3")]
    [InlineData("No Numbers Here")]
    [InlineData("The 1st Day")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Parse_NameWithoutIssueNumber_ReturnsNull(string? name)
    {
        Assert.Null(IssueNumberParser.Parse(name));
    }

    [Fact]
    public void Parse_MarkerInsideAWord_IsNotAMarker()
    {
        // "Punch" ends in "ch" and "Doc" in "c"; neither starts a chapter marker, so the
        // unmarked fallback reads the trailing issue number instead of the 2 after "Punch".
        Assert.Equal("7", IssueNumberParser.Parse("Punch 2 Doc5 007"));
    }

    [Theory]
    [InlineData("Series - Chapter 5", "5")]
    [InlineData("Series Ch.10", "10")]
    [InlineData("Series ch_3.5", "3.5")]
    [InlineData("Batman - Ch 0004.5", "4.5")]
    [InlineData("Series Ch 0042", "42")]
    [InlineData("Batman #007", "7")]
    [InlineData("Series Vol 2", null)]
    [InlineData("Series 042", null)]
    [InlineData("Series Name (2023)", null)]
    [InlineData(null, null)]
    public void ParseMarked_ReturnsOnlyAnExplicitlyMarkedNumber(string? name, string? expected)
    {
        Assert.Equal(expected, IssueNumberParser.ParseMarked(name));
    }

    [Theory]
    [InlineData("Batman - Chapter 0005", "5", true)]
    [InlineData("0005 - Mob Psycho 100", "5", true)]
    [InlineData("Mob Psycho 100 0005", "5", true)]
    [InlineData("Mob Psycho 100", "5", false)]
    [InlineData("Batman (0005)", "5", false)]
    [InlineData("Batman v5", "5", false)]
    [InlineData("Batman v2 #0005", "5", true)]
    [InlineData("Batman 12.5", "12.5", true)]
    [InlineData("Batman 12", "12.5", false)]
    [InlineData("", "5", false)]
    [InlineData("Batman 5", "", false)]
    public void ContainsIssueNumber_FindsTheIssueWhereverTheTemplatePutIt(string name, string issue, bool expected)
    {
        Assert.Equal(expected, IssueNumberParser.ContainsIssueNumber(name, issue));
    }

    [Theory]
    [InlineData("1", "1", true)]
    [InlineData("1", "0001", true)]
    [InlineData("0001", "0001.0", true)]
    [InlineData("12.5", "0012.5", true)]
    [InlineData("1", "2", false)]
    [InlineData("1.0", "1.5", false)]
    [InlineData("", "", true)]
    [InlineData(null, null, true)]
    [InlineData("", null, true)]
    [InlineData("", "1", false)]
    [InlineData("1", null, false)]
    [InlineData("abc", "abc", true)]
    [InlineData("abc", "def", false)]
    public void AreEquivalent_ComparesNumericValues(string? a, string? b, bool expected)
    {
        Assert.Equal(expected, IssueNumberParser.AreEquivalent(a, b));
    }

    [Theory]
    [InlineData("0012", "12")]
    [InlineData("0004.5", "4.5")]
    [InlineData("0000", "0")]
    [InlineData("12", "12")]
    [InlineData("Extra", "Extra")]
    [InlineData("", "")]
    public void StripLeadingZeros_RemovesIntegerPaddingOnly(string input, string expected)
    {
        Assert.Equal(expected, IssueNumberParser.StripLeadingZeros(input));
    }
}
