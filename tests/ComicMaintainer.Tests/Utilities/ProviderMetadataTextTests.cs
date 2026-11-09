using ComicMaintainer.Core.Utilities;

namespace ComicMaintainer.Tests.Utilities;

public class ProviderMetadataTextTests
{
    [Theory]
    [InlineData("https://anilist.co/manga/1", "https://anilist.co/manga/1")]
    [InlineData(" http://example.org/a b ", "http://example.org/a%20b")]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("ftp://example.org/x", null)]
    [InlineData("/manga/1", null)]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    public void HttpUrl_KeepsOnlyAbsoluteWebAddresses(string? value, string? expected)
    {
        Assert.Equal(expected, ProviderMetadataText.HttpUrl(value));
    }

    [Fact]
    public void HttpUrl_RefusesAnOverLongAddress()
    {
        var url = "https://example.org/" + new string('a', ProviderMetadataText.MaxSiteUrlLength);

        Assert.Null(ProviderMetadataText.HttpUrl(url));
    }

    [Fact]
    public void Genres_CleansMarkupRepeatsBlanksAndCommas()
    {
        var genres = ProviderMetadataText.Genres(new[]
        {
            "Action", "action", " <b>Slice&amp;of</b> Life ", null, "   ", "Boys, Love", "Sci&#44;Fi",
            "bad\u0001char", new string('x', ProviderMetadataText.MaxGenreLength + 1)
        });

        Assert.Equal(new[] { "Action", "Slice&of Life", "Boys Love", "Sci Fi", "badchar" }, genres);
    }

    [Fact]
    public void Genres_StopsAtTheLimit()
    {
        var genres = ProviderMetadataText.Genres(Enumerable.Range(0, ProviderMetadataText.MaxGenres + 5).Select(i => $"Genre {i}"));

        Assert.Equal(ProviderMetadataText.MaxGenres, genres.Count);
    }

    [Fact]
    public void Genres_SkipsAValueTooLongToBeAGenreWithoutCleaningIt()
    {
        var genres = ProviderMetadataText.Genres(new[] { new string('<', 2_000_000), "Action" });

        Assert.Equal(new[] { "Action" }, genres);
    }

    [Fact]
    public void Genres_CleansAValueWhoseMarkupMakesItLong()
    {
        var genres = ProviderMetadataText.Genres(new[] { $"<span class=\"{new string('x', 400)}\">Action</span>" });

        Assert.Equal(new[] { "Action" }, genres);
    }

    [Fact]
    public void Genres_LooksAtABoundedNumberOfValues()
    {
        var values = Enumerable.Repeat("   ", 1000).Append("Action");

        Assert.Empty(ProviderMetadataText.Genres(values));
    }

    [Fact]
    public void Genres_OfNothingIsEmpty()
    {
        Assert.Empty(ProviderMetadataText.Genres(null));
    }

    [Theory]
    [InlineData("ja", "ja")]
    [InlineData(" ZH-hk ", "zh")]
    [InlineData("j", null)]
    [InlineData("toolonglang", null)]
    [InlineData("1a", null)]
    [InlineData(null, null)]
    public void PrimaryLanguage_ReadsTheFirstSubtag(string? value, string? expected)
    {
        Assert.Equal(expected, ProviderMetadataText.PrimaryLanguage(value));
    }

    [Fact]
    public void RemoveInvalidXmlCharacters_KeepsTextAndSurrogatePairs()
    {
        Assert.Null(ProviderMetadataText.RemoveInvalidXmlCharacters(null));
        Assert.Equal("a\tb😀c", ProviderMetadataText.RemoveInvalidXmlCharacters("a\u0001\tb😀￿c"));
    }

    [Fact]
    public void RemoveInvalidXmlCharacters_DropsLoneSurrogates()
    {
        Assert.Equal("ab", ProviderMetadataText.RemoveInvalidXmlCharacters("a\uD83Db\uDC00"));
        Assert.Equal("a", ProviderMetadataText.RemoveInvalidXmlCharacters("a\uD83D"));
    }
}
