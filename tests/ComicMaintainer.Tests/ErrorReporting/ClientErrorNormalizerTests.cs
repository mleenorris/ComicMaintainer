using ComicMaintainer.Core.ErrorReporting;

namespace ComicMaintainer.Tests.ErrorReporting;

/// <summary>
/// The normalizer sits between a hostile, inconsistent input (whatever a
/// browser chose to put in <c>Error.stack</c>) and a public issue tracker, so
/// the properties worth asserting are that one defect produces one fingerprint
/// whatever reported it, and that nothing volatile or unbounded survives.
/// </summary>
public class ClientErrorNormalizerTests
{
    [Theory]
    [InlineData("TypeError", "TypeError")]
    [InlineData("  SyntaxError  ", "SyntaxError")]
    [InlineData("", "Error")]
    [InlineData(null, "Error")]
    // Not an identifier: it did not come from Error.prototype.name and has no
    // business being rendered into an issue title.
    [InlineData("Type Error!", "Error")]
    [InlineData("<img src=x onerror=alert(1)>", "Error")]
    public void NormalizeName_KeepsOnlyIdentifierShapedNames(string? input, string expected)
    {
        Assert.Equal(expected, ClientErrorNormalizer.NormalizeName(input));
    }

    [Fact]
    public void NormalizeName_IsBounded()
    {
        var normalized = ClientErrorNormalizer.NormalizeName(new string('A', 500));

        Assert.True(normalized.Length <= ClientErrorNormalizer.MaxNameLength);
    }

    [Fact]
    public void NormalizeMessage_CollapsesNewlinesSoItCannotBreakOutOfAMarkdownCell()
    {
        var normalized = ClientErrorNormalizer.NormalizeMessage("first\nsecond\r\n\tthird");

        Assert.Equal("first second third", normalized);
    }

    [Fact]
    public void NormalizeMessage_IsBounded()
    {
        var normalized = ClientErrorNormalizer.NormalizeMessage(new string('x', 5000));

        Assert.True(normalized.Length <= ClientErrorNormalizer.MaxMessageLength);
    }

    [Fact]
    public void NormalizeMessage_DescribesAnEmptyMessage()
    {
        Assert.Equal("(no message)", ClientErrorNormalizer.NormalizeMessage("   "));
    }

    [Fact]
    public void NormalizeMessageTemplate_RemovesTheVolatilePartsOfAMessage()
    {
        // The template is a fingerprint input. Two occurrences of one defect
        // that mention different ids must not file two issues.
        var first = ClientErrorNormalizer.NormalizeMessageTemplate(
            "Build 3f2504e0-4f89-11d3-9a0c-0305e82c3301 failed with status 503");
        var second = ClientErrorNormalizer.NormalizeMessageTemplate(
            "Build 0a1b2c3d-4e5f-6071-8293-a4b5c6d7e8f9 failed with status 500");

        Assert.Equal(first, second);
        Assert.Equal("Build {id} failed with status {n}", first);
    }

    [Theory]
    [InlineData("https://comics.example.com/js/main.js?v=2.0.316", "/js/main.js")]
    [InlineData("https://comics.example.com/js/main.js", "/js/main.js")]
    [InlineData("https://comics.example.com/", "/")]
    [InlineData("/js/main.js?v=2.0.316", "/js/main.js")]
    [InlineData("/index.html#top", "/index.html")]
    [InlineData("", "(unknown)")]
    [InlineData(null, "(unknown)")]
    public void NormalizeLocation_KeepsOnlyThePath(string? input, string expected)
    {
        Assert.Equal(expected, ClientErrorNormalizer.NormalizeLocation(input));
    }

    [Theory]
    // A data: URL is the script source itself; a blob: URL is an id minted per
    // page load. Neither may reach an issue.
    [InlineData("data:text/javascript,alert(1)", "data:")]
    [InlineData("blob:https://comics.example.com/5a8c0e1e-1f21-4a3f-9a1e-2b0a6a6b1f30", "blob:")]
    public void NormalizeLocation_RefusesToEchoInlineAndBlobScripts(string input, string expected)
    {
        Assert.Equal(expected, ClientErrorNormalizer.NormalizeLocation(input));
    }

    [Fact]
    public void NormalizeStack_RewritesChromiumFramesIntoTheCanonicalShape()
    {
        var stack = string.Join('\n', new[]
        {
            "TypeError: Cannot read properties of null",
            "    at renderEmailCondenseBuilds (https://comics.example.com/js/main.js?v=2.0.316:9180:62)",
            "    at HTMLButtonElement.onclick (https://comics.example.com/:1:30)"
        });

        var normalized = ClientErrorNormalizer.NormalizeStack(stack);

        Assert.Equal(
            "at renderEmailCondenseBuilds (/js/main.js)\nat HTMLButtonElement.onclick (/)",
            normalized);
    }

    [Fact]
    public void NormalizeStack_RewritesFirefoxFramesIntoTheSameShape()
    {
        // Same defect, different engine. If the two stacks normalized
        // differently the tracker would carry one issue per browser.
        var chromium = ClientErrorNormalizer.NormalizeStack(
            "    at renderEmailCondenseBuilds (https://comics.example.com/js/main.js?v=2.0.316:9180:62)");
        var firefox = ClientErrorNormalizer.NormalizeStack(
            "renderEmailCondenseBuilds@https://comics.example.com/js/main.js?v=2.0.316:9180:62");

        Assert.Equal("at renderEmailCondenseBuilds (/js/main.js)", chromium);
        Assert.Equal(chromium, firefox);
    }

    [Fact]
    public void NormalizeStack_ProducesFramesTheFingerprinterRecognises()
    {
        // ErrorReportFingerprint only folds in lines that start with "at ".
        // A stack left in the engine's own shape would contribute nothing and
        // every client error would share one fingerprint.
        var normalized = ClientErrorNormalizer.NormalizeStack(
            "renderEmailCondenseBuilds@https://comics.example.com/js/main.js:9180:62\n" +
            "onclick@https://comics.example.com/:1:30");

        var frames = ErrorReportFingerprint.NormalizeFrames(normalized);

        Assert.Equal(2, frames.Count);
    }

    [Fact]
    public void NormalizeStack_SurvivesTheCacheBustingQueryChangingOnEveryRelease()
    {
        var before = ClientErrorNormalizer.NormalizeStack(
            "    at downloadBuiltCondensedBook (https://comics.example.com/js/main.js?v=2.0.316:9068:30)");
        var after = ClientErrorNormalizer.NormalizeStack(
            "    at downloadBuiltCondensedBook (https://comics.example.com/js/main.js?v=2.0.401:9068:30)");

        Assert.Equal(before, after);
    }

    [Fact]
    public void NormalizeStack_DropsTheHeaderLineWhichIsNotAFrame()
    {
        var normalized = ClientErrorNormalizer.NormalizeStack(
            "SyntaxError: Invalid or unexpected token\n    at onclick (https://comics.example.com/:1:1)");

        Assert.Equal("at onclick (/)", normalized);
    }

    [Fact]
    public void NormalizeStack_KeepsOnlyTheLeadingFrames()
    {
        var stack = string.Join(
            '\n',
            Enumerable.Range(0, 100).Select(i => $"    at fn{i} (https://comics.example.com/js/main.js:{i}:1)"));

        var frames = ClientErrorNormalizer.NormalizeStack(stack).Split('\n');

        Assert.Equal(ClientErrorNormalizer.MaxStackFrames, frames.Length);
        Assert.Equal("at fn0 (/js/main.js)", frames[0]);
    }

    [Fact]
    public void NormalizeStack_HandlesAFrameWithNoFunctionName()
    {
        var normalized = ClientErrorNormalizer.NormalizeStack(
            "    at https://comics.example.com/js/main.js:120:7");

        Assert.Equal("at /js/main.js", normalized);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("no frames here at all")]
    public void NormalizeStack_ReturnsEmptyWhenThereIsNothingUsable(string? stack)
    {
        Assert.Equal(string.Empty, ClientErrorNormalizer.NormalizeStack(stack));
    }
}
