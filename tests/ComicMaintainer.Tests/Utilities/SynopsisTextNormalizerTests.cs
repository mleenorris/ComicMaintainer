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
