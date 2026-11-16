using ComicMaintainer.Core.Utilities;

namespace ComicMaintainer.Tests.Utilities;

public class SeriesTitleYearTests
{
    [Theory]
    [InlineData("Green Lantern (2023)", "Green Lantern", 2023)]
    [InlineData("  Green Lantern  (2023)  ", "Green Lantern", 2023)]
    [InlineData("Green Lantern [2023]", "Green Lantern", 2023)]
    [InlineData("Green Lantern (2023-2024)", "Green Lantern", 2023)]
    [InlineData("Green Lantern (2023 - )", "Green Lantern", 2023)]
    [InlineData("Batman: Year One (1987)", "Batman: Year One", 1987)]
    [InlineData("Batman(1940)", "Batman", 1940)]
    public void Parse_SplitsTheTrailingVolumeYear(string name, string title, int year)
    {
        Assert.Equal((title, (int?)year), SeriesTitleYear.Parse(name));
    }

    [Theory]
    [InlineData("Green Lantern")]
    [InlineData("1984")]
    [InlineData("(2023)")]
    [InlineData("Green Lantern 2023")]
    [InlineData("Green Lantern (2023) Annual")]
    [InlineData("Green Lantern (Vol. 2)")]
    [InlineData("Lantern (0042)")]
    [InlineData("Lantern (12345)")]
    public void Parse_LeavesNamesWithoutAVolumeYearAlone(string name)
    {
        Assert.Equal((name.Trim(), (int?)null), SeriesTitleYear.Parse(name));
    }

    [Fact]
    public void Parse_BlankName_HasNoYear()
    {
        Assert.Equal((string.Empty, (int?)null), SeriesTitleYear.Parse(null));
        Assert.Equal((string.Empty, (int?)null), SeriesTitleYear.Parse("   "));
    }

    [Fact]
    public void WithYear_AddsTheYearUnlessTheNameHasOne()
    {
        Assert.Equal("Green Lantern (2023)", SeriesTitleYear.WithYear(" Green Lantern ", 2023));
        Assert.Equal("Green Lantern (2021)", SeriesTitleYear.WithYear("Green Lantern (2021)", 2023));
    }
}
