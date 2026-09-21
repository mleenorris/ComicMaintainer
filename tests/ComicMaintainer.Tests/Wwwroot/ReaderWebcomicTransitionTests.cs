using System.Text.RegularExpressions;

namespace ComicMaintainer.Tests.Wwwroot;

/// <summary>
/// Guards the webcomic-mode comic-to-comic transition in <c>reader.html</c>.
/// Pages appended during a transition have no layout height until their blob
/// decodes, so a one-shot <c>scrollTop</c> assignment left the reader parked
/// somewhere in the middle of the newly stitched issue. The transition must
/// keep re-asserting the start marker position until layout settles.
/// </summary>
public class ReaderWebcomicTransitionTests
{
    private static string ReaderHtml
    {
        get
        {
            var current = AppContext.BaseDirectory;
            for (var i = 0; i < 10 && current != null; i++)
            {
                var candidate = Path.Combine(current, "src", "ComicMaintainer.WebApi", "wwwroot", "reader.html");
                if (File.Exists(candidate))
                {
                    return File.ReadAllText(candidate);
                }
                current = Path.GetDirectoryName(current);
            }
            throw new FileNotFoundException("Could not locate reader.html relative to test binaries.");
        }
    }

    [Fact]
    public void LoadNextComic_PinsScrollToNewComicStartMarker()
    {
        var html = ReaderHtml;

        Assert.Contains("function pinScrollToComicStart(", html);
        Assert.Contains("await pinScrollToComicStart(content, startMarker)", html);
    }

    [Fact]
    public void LoadNextComic_DoesNotUseOneShotScrollAssignment()
    {
        var html = ReaderHtml;

        // A single `content.scrollTop = <captured offset>` cannot survive the
        // images that keep loading (and re-flowing the stream) right after the
        // transition — re-measure inside the pin loop instead.
        Assert.DoesNotMatch(new Regex(@"content\.scrollTop\s*=\s*markerPosition"), html);
    }

    [Fact]
    public void PinScrollToComicStart_ReMeasuresMarkerAndSuspendsScrollAnchoring()
    {
        var html = ReaderHtml;
        var pin = ExtractFunction(html, "pinScrollToComicStart");

        // The marker offset must be read inside the settle loop, not captured
        // once up front.
        Assert.Contains("marker.offsetTop", pin);
        Assert.Contains("requestAnimationFrame(tick)", pin);

        // Browser scroll anchoring otherwise locks the viewport onto whichever
        // page happened to be sized already, skipping the first pages.
        Assert.Contains("overflowAnchor = 'none'", pin);
        Assert.Contains("overflowAnchor = previousAnchor", pin);
    }

    [Fact]
    public void PinScrollToComicStart_AlwaysCompletes()
    {
        var pin = ExtractFunction(ReaderHtml, "pinScrollToComicStart");

        // requestAnimationFrame does not fire in a hidden tab; without the
        // timer backstop the isLoadingNext latch would never be released and
        // no further issue could be stitched in.
        Assert.Contains("safetyTimer = setTimeout(finish", pin);
        Assert.Contains("clearTimeout(safetyTimer)", pin);
    }

    /// <summary>
    /// Returns the source text of a top-level function declaration by brace
    /// matching from its opening brace.
    /// </summary>
    private static string ExtractFunction(string source, string name)
    {
        var start = source.IndexOf($"function {name}(", StringComparison.Ordinal);
        Assert.True(start >= 0, $"Expected reader.html to declare {name}()");

        var braceStart = source.IndexOf('{', start);
        Assert.True(braceStart > 0, $"Expected a body for {name}()");

        var depth = 0;
        for (var i = braceStart; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source[start..(i + 1)];
                }
            }
        }

        throw new InvalidOperationException($"Unbalanced braces while extracting {name}()");
    }
}
