using ComicMaintainer.Core.Utilities;

namespace ComicMaintainer.Tests.Utilities;

public class SynopsisTextNormalizerTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<p></p>")]
    [InlineData("[i][/i]")]
    [InlineData("<p> <br/> &nbsp; </p>")]
    public void Normalize_ReturnsNullWhenNoTextRemains(string? raw)
    {
        Assert.Null(SynopsisTextNormalizer.Normalize(raw));
    }

    [Fact]
    public void Normalize_StripsMarkupDecodesEntitiesAndCollapsesWhitespace()
    {
        var raw = "<p>Ninjas<br/>and   pirates &amp; more</p>\n[i]Source: Viz[/i] [spoiler]twist[/spoiler]";

        Assert.Equal("Ninjas and pirates & more Source: Viz twist", SynopsisTextNormalizer.Normalize(raw));
    }
}

public class SynopsisComicInfoSummaryTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    [InlineData("<p></p>")]
    [InlineData("--- ___")]
    [InlineData("(Source: MU)")]
    public void ToComicInfoSummary_NullWhenNoTextRemains(string? raw)
    {
        Assert.Null(SynopsisTextNormalizer.ToComicInfoSummary(raw));
    }

    [Theory]
    [InlineData("<p>A <i>dark</i> tale</p>", "A dark tale")]
    [InlineData("__Bold__ and **bolder** and ~~gone~~", "Bold and bolder and gone")]
    [InlineData("Plot ~!secret!~ ends", "Plot secret ends")]
    [InlineData("*italic* and _also_ here", "italic and also here")]
    [InlineData("See [the wiki](https://example.org/wiki) for more", "See the wiki for more")]
    [InlineData("Story\n\n---\n\nExtras", "Story Extras")]
    [InlineData("A tale. (Source: MangaUpdates)", "A tale.")]
    [InlineData("snake_case and 2 * 3 stay", "snake_case and 2 * 3 stay")]
    public void ToComicInfoSummary_StripsProviderMarkup(string raw, string expected)
    {
        Assert.Equal(expected, SynopsisTextNormalizer.ToComicInfoSummary(raw));
    }

    [Fact]
    public void ToComicInfoSummary_RemovesMarkupThatWasEscapedTwice()
    {
        Assert.Equal("bold text", SynopsisTextNormalizer.ToComicInfoSummary("&lt;b&gt;bold&lt;/b&gt; text"));
    }

    [Fact]
    public void ToComicInfoSummary_DropsCharactersXmlCannotHold()
    {
        // &#1; only becomes a control character once the entity is decoded.
        Assert.Equal("ab cd", SynopsisTextNormalizer.ToComicInfoSummary("a&#1;b\u0002 cd"));
    }

    [Fact]
    public void ToComicInfoSummary_KeepsTextAtTheLimit()
    {
        var text = new string('a', SynopsisTextNormalizer.MaxComicInfoSummaryLength);

        Assert.Equal(text, SynopsisTextNormalizer.ToComicInfoSummary(text));
    }

    [Fact]
    public void ToComicInfoSummary_CutsLongTextAtAWord()
    {
        var raw = string.Join(' ', Enumerable.Repeat("word", 1000));

        var summary = SynopsisTextNormalizer.ToComicInfoSummary(raw)!;

        Assert.True(summary.Length <= SynopsisTextNormalizer.MaxComicInfoSummaryLength);
        Assert.EndsWith("word...", summary);
    }

    [Fact]
    public void ToComicInfoSummary_CutsTextWithoutSpacesAtTheLimit()
    {
        var summary = SynopsisTextNormalizer.ToComicInfoSummary(new string('a', 5000))!;

        Assert.Equal(SynopsisTextNormalizer.MaxComicInfoSummaryLength, summary.Length);
        Assert.EndsWith("a...", summary);
    }

    [Fact]
    public void ToComicInfoSummary_DoesNotSplitASurrogatePair()
    {
        // The pair straddles the cut position.
        var cut = SynopsisTextNormalizer.MaxComicInfoSummaryLength - 3;
        var raw = new string('a', cut - 1) + "😀" + new string('b', 200);

        var summary = SynopsisTextNormalizer.ToComicInfoSummary(raw)!;

        Assert.Equal(new string('a', cut - 1) + "...", summary);
    }
}

public class SynopsisMarkdownLinkTests
{
    [Fact]
    public void Normalize_KeepsTheLabelOfAMarkdownLink()
    {
        // The BBCode pattern used to take "[Wikipedia]" for a tag and leave only the address.
        Assert.Equal("See Wikipedia here", SynopsisTextNormalizer.Normalize("See [Wikipedia](https://en.wikipedia.org/wiki/X) here"));
    }

    [Fact]
    public void ToComicInfoSummary_DropsTheAddressLeftInAnAlreadyCachedSynopsis()
    {
        Assert.Equal("See here", SynopsisTextNormalizer.ToComicInfoSummary("See (https://en.wikipedia.org/wiki/X) here"));
    }
}
