using ComicMaintainer.Core.ErrorReporting;

namespace ComicMaintainer.Tests.ErrorReporting;

public class ClientErrorNormalizerCoverageTests
{
    [Theory]
    [InlineData("main.js", "main.js")]
    [InlineData("js/app.js?v=3", "js/app.js")]
    [InlineData("<anonymous>", "<anonymous>")]
    // A drive letter is a path, not a one-letter URI scheme.
    [InlineData(@"C:\app\main.js", @"C:\app\main.js")]
    public void NormalizeLocation_PassesRelativeAndSchemelessValuesThrough(string input, string expected)
    {
        Assert.Equal(expected, ClientErrorNormalizer.NormalizeLocation(input));
    }

    [Fact]
    public void NormalizeLocation_PassesThroughAUrlThatCannotBeParsed()
    {
        // Out-of-range port: it looks like a URL but Uri rejects it, so it is
        // returned (minus the query) rather than guessed at.
        Assert.Equal(
            "https://comics.example.com:99999/js/main.js",
            ClientErrorNormalizer.NormalizeLocation("https://comics.example.com:99999/js/main.js?v=2"));
    }

    [Theory]
    [InlineData("?v=2.0.316")]
    [InlineData("#top")]
    public void NormalizeLocation_ReportsALocationThatIsOnlyAQueryOrFragmentAsUnknown(string input)
    {
        Assert.Equal(ClientErrorNormalizer.UnknownLocation, ClientErrorNormalizer.NormalizeLocation(input));
    }

    [Fact]
    public void NormalizeLocation_KeepsTheFilePathOfAFileUrl()
    {
        Assert.Equal("/home/user/index.html", ClientErrorNormalizer.NormalizeLocation("file:///home/user/index.html"));
    }

    [Fact]
    public void NormalizeMessage_DropsControlCharactersThatAreNotWhitespace()
    {
        Assert.Equal("bad value here", ClientErrorNormalizer.NormalizeMessage("bad\u0001 val\u007fue\u0000 here"));
    }

    [Fact]
    public void NormalizeStack_SkipsBlankLinesBetweenFrames()
    {
        var stack = "at first (https://comics.example.com/js/a.js:1:2)\n\n   \r\nat second (https://comics.example.com/js/b.js:3:4)";

        Assert.Equal("at first (/js/a.js)\nat second (/js/b.js)", ClientErrorNormalizer.NormalizeStack(stack));
    }

    [Fact]
    public void NormalizeStack_SkipsAFrameWithNeitherFunctionNorLocation()
    {
        var stack = "@\nrender@https://comics.example.com/js/main.js:10:5";

        Assert.Equal("at render (/js/main.js)", ClientErrorNormalizer.NormalizeStack(stack));
    }

    [Fact]
    public void NormalizeStack_KeepsAFrameThatHasOnlyAFunctionName()
    {
        Assert.Equal("at onClick", ClientErrorNormalizer.NormalizeStack("onClick@"));
    }

    [Fact]
    public void NormalizeStack_KeepsTheRawLocationWhenItReducesToNothing()
    {
        // The location is all query, which would otherwise normalize to "(unknown)".
        Assert.Equal("at handler (?bundle=1)", ClientErrorNormalizer.NormalizeStack("handler@?bundle=1"));
    }

    [Fact]
    public void NormalizeStack_KeepsTabsButStripsOtherControlCharactersFromFrames()
    {
        var stack = "\tat\tload (https://comics.example.com/js/a.js:1:2)\r\nat re\u0007nder (https://comics.example.com/js/b.js:3:4)";

        Assert.Equal("at load (/js/a.js)\nat render (/js/b.js)", ClientErrorNormalizer.NormalizeStack(stack));
    }

    [Fact]
    public void NormalizeStack_KeepsARelativeFrameLocationAsWritten()
    {
        Assert.Equal("at init (vendor/lib.js)", ClientErrorNormalizer.NormalizeStack("at init (vendor/lib.js:12:9)"));
    }
}
