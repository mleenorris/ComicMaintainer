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
    [InlineData("Plot ends ~!unclosed marker", "Plot ends unclosed marker")]
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

public class SynopsisLinksFooterTests
{
    [Theory]
    [InlineData("Guts fights.\n\n---\n**Links:**\n- [MU](https://example.org/mu)\n- [Raw](https://example.org/raw)")]
    [InlineData("Guts fights.\r\n\r\n---\r\n**Links:**\r\n- [MU](https://example.org/mu)\r\n")]
    [InlineData("Guts fights.\n---\n[Official](https://example.org/a) | [Twitter](https://example.org/b)")]
    [InlineData("Guts fights.\n- Official English - [Comikey](https://example.org/c)")]
    [InlineData("Guts fights.<br><br>External links:<br>* [MU](https://example.org/mu)")]
    public void Normalize_DropsATrailingBlockOfLinks(string raw)
    {
        Assert.Equal("Guts fights.", SynopsisTextNormalizer.Normalize(raw));
        Assert.Equal("Guts fights.", SynopsisTextNormalizer.ToComicInfoSummary(raw));
    }

    [Fact]
    public void Normalize_OfNothingButLinksIsNull()
    {
        Assert.Null(SynopsisTextNormalizer.Normalize("---\n- [MU](https://example.org/mu)\n[Raw](https://example.org/raw)"));
    }

    [Theory]
    [InlineData("Read it [here](https://example.org/a) today.", "Read it here today.")]
    [InlineData("Story.\n- [MU](https://example.org/mu)\n- A note", "Story. MU A note")]
    [InlineData("Story.\n[MU](https://example.org/mu) is a site.", "Story. MU is a site.")]
    [InlineData("Story.\n\n---\n\nLinks:", "Story. --- Links:")]
    public void Normalize_KeepsWhatIsNotAFooter(string raw, string expected)
    {
        Assert.Equal(expected, SynopsisTextNormalizer.Normalize(raw));
    }

    [Theory]
    [InlineData("Story:\n- one\n* two\n+ three", "Story: one two three")]
    [InlineData("  - indented", "indented")]
    [InlineData("first<br>- second", "first second")]
    [InlineData("Hero - a tale - of woe", "Hero - a tale - of woe")]
    [InlineData("-5 degrees\n*bold* start", "-5 degrees *bold* start")]
    public void Normalize_DropsListMarkersOnly(string raw, string expected)
    {
        Assert.Equal(expected, SynopsisTextNormalizer.Normalize(raw));
    }

    [Theory]
    [InlineData("Story. --- **Links:** - MU - Raw", "Story.")]
    [InlineData("Story. --- **Links:** - (https://example.org/a) - (https://example.org/b)", "Story.")]
    [InlineData("Story. Links: * MU", "Story.")]
    [InlineData("Story. __External Links__: + MU", "Story.")]
    public void ToComicInfoSummary_DropsAFooterAlreadyCachedOnOneLine(string cached, string expected)
    {
        Assert.Equal(expected, SynopsisTextNormalizer.ToComicInfoSummary(cached));
    }

    [Theory]
    [InlineData("The chain links - forged in fire - held.")]
    [InlineData("Its links: the hero and the villain.")]
    public void ToComicInfoSummary_KeepsASentenceAboutLinks(string text)
    {
        Assert.Equal(text, SynopsisTextNormalizer.ToComicInfoSummary(text));
    }

    [Theory]
    [InlineData("Plot ~!secret!~ ends", "Plot ends")]
    [InlineData("Plot ~!across\nlines!~ ends", "Plot ends")]
    [InlineData("~!one!~ middle ~!two!~", "middle")]
    public void ToComicInfoSummary_LeavesOutMarkedSpoilers(string raw, string expected)
    {
        Assert.Equal(expected, SynopsisTextNormalizer.ToComicInfoSummary(raw));
    }

    [Fact]
    public void ToComicInfoSummary_OfOnlyASpoilerIsNull()
    {
        Assert.Null(SynopsisTextNormalizer.ToComicInfoSummary("~!all of it!~"));
    }
}

public class SynopsisLengthLimitTests
{
    [Fact]
    public void Normalize_KeepsTextAtTheLimit()
    {
        var text = new string('a', SynopsisTextNormalizer.MaxSynopsisLength);

        Assert.Equal(text, SynopsisTextNormalizer.Normalize(text));
    }

    [Fact]
    public void Normalize_CutsLongerTextAtAWord()
    {
        var synopsis = SynopsisTextNormalizer.Normalize(string.Join(' ', Enumerable.Repeat("word", 5000)))!;

        Assert.True(synopsis.Length <= SynopsisTextNormalizer.MaxSynopsisLength);
        Assert.EndsWith("word...", synopsis);
    }

    [Fact]
    public void Normalize_IsStableOnTextItCut()
    {
        var once = SynopsisTextNormalizer.Normalize(string.Join(' ', Enumerable.Repeat("word", 5000)));

        Assert.Equal(once, SynopsisTextNormalizer.Normalize(once));
    }

    [Fact]
    public void Normalize_DoesNotLookPastTheRawLimit()
    {
        // All tags up to well past the limit, so the words after them can only be reached by
        // reading the whole input.
        var raw = string.Concat(Enumerable.Repeat("<b>", 100_000)) + "visible";

        Assert.Null(SynopsisTextNormalizer.Normalize(raw));
    }

    [Fact]
    public void Normalize_DoesNotSplitASurrogatePairAtTheRawLimit()
    {
        // 21,333 tags end one character short of the limit, so the pair straddles it: only half
        // of it would survive a cut that ignored pairs.
        var raw = string.Concat(Enumerable.Repeat("<b>", 21_333)) + "😀 tail";

        Assert.Null(SynopsisTextNormalizer.Normalize(raw));
    }

    [Theory]
    [InlineData("a < b <i>c</i>", "a < b c")]
    [InlineData("[a and [b]c", "[a and c")]
    public void Normalize_AnOpeningBracketEndsTheTagBeforeIt(string raw, string expected)
    {
        Assert.Equal(expected, SynopsisTextNormalizer.Normalize(raw));
    }

    [Theory]
    [InlineData("<")]
    [InlineData("<a ")]
    [InlineData("[")]
    [InlineData("[a")]
    [InlineData("[](")]
    public async Task Normalize_StaysFastOnAFloodOfOpeningMarkup(string unit)
    {
        // Without the limit and the linear patterns this input takes minutes.
        var flood = string.Concat(Enumerable.Repeat(unit, 2_000_000));

        var synopsis = await Task.Run(() => SynopsisTextNormalizer.Normalize(flood))
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(synopsis is null || synopsis.Length <= SynopsisTextNormalizer.MaxSynopsisLength);
    }
}
