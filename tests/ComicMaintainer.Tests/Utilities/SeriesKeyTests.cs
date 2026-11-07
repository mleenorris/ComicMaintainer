using ComicMaintainer.Core.Utilities;

namespace ComicMaintainer.Tests.Utilities;

public class SeriesKeyTests
{
    [Theory]
    [InlineData("One Piece", "one-piece")]
    [InlineData("One_Piece", "one-piece")]
    [InlineData("  ONE PIECE!! ", "one-piece")]
    [InlineData("Spider-Man: Blue", "spider-man-blue")]
    [InlineData("進撃の巨人", "進撃の巨人")]
    [InlineData("Pokémon Adventures", "pokémon-adventures")]
    [InlineData("20th Century Boys", "20th-century-boys")]
    public void Normalize_FoldsCaseAndPunctuationButKeepsUnicodeLetters(string title, string expected)
    {
        Assert.Equal(expected, SeriesKey.Normalize(title));
        Assert.Equal(expected, SeriesKey.TryNormalize(title));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!! ???")]
    [InlineData("---")]
    public void TitleWithoutLettersOrDigits_HasNoKey(string? title)
    {
        Assert.Null(SeriesKey.TryNormalize(title));
        Assert.Equal(SeriesKey.Unknown, SeriesKey.Normalize(title));
    }

    [Fact]
    public void AreEquivalent_ComparesKeys()
    {
        Assert.True(SeriesKey.AreEquivalent("Wan_Pisu", "wan pisu"));
        Assert.False(SeriesKey.AreEquivalent("Monster", "Monsters"));
        Assert.False(SeriesKey.AreEquivalent("???", "!!!"));
        Assert.False(SeriesKey.AreEquivalent(null, null));
    }
}
