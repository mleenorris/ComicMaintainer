using System.Text.RegularExpressions;

namespace ComicMaintainer.Tests.Wwwroot;

/// <summary>
/// Guards the webcomic-mode comic-to-comic transition in <c>reader.html</c>.
/// The next issue must be stitched into the continuous stream *before* the
/// reader reaches the end of the current one, so crossing the boundary costs
/// no fetch and needs no scroll repositioning.
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
    public void WebcomicScroll_StitchesNextIssueBeforeReachingTheEnd()
    {
        var handler = ExtractFunction(ReaderHtml, "handleWebcomicScroll");

        // The stitch must be triggered from a stitch-ahead distance, not only
        // from the 200px "at the bottom" check, otherwise the reader waits for
        // the next issue at the boundary.
        Assert.Contains("WEBCOMIC_STITCH_AHEAD_VIEWPORTS", handler);
        Assert.Contains("ensureNextComicStitched()", handler);
    }

    [Fact]
    public void StitchNextComic_DoesNotMoveTheViewport()
    {
        var html = ReaderHtml;
        var stitch = ExtractFunction(html, "stitchNextComic");

        // Content is appended below the reader, so any scroll assignment or
        // scrollIntoView would visibly jump the view at the boundary.
        Assert.DoesNotMatch(new Regex(@"scrollTop\s*="), stitch);
        Assert.DoesNotContain("scrollIntoView", stitch);

        // The superseded pin-the-view-to-the-new-comic transition must be gone.
        Assert.DoesNotContain("pinScrollToComicStart", html);
    }

    [Fact]
    public void StitchNextComic_RegistersTheNewTailAndPreloadsItsOpeningPages()
    {
        var stitch = ExtractFunction(ReaderHtml, "stitchNextComic");

        Assert.Contains("stitchedComicPaths.add(nextPath)", stitch);
        Assert.Contains("streamTailPath = nextPath", stitch);
        Assert.Contains("loadWebcomicPageForNextComic(i, container, nextPath)", stitch);

        // The pointer for the issue after the new tail must be resolved from
        // the tail, otherwise the same issue is stitched again.
        Assert.Contains("prefetchNextComic(nextPath)", stitch);
    }

    [Fact]
    public void EnsureNextComicStitched_WaitsForTheTailIssueToBeFullyRequested()
    {
        var ensure = ExtractFunction(ReaderHtml, "ensureNextComicStitched");

        // Appending the next issue while pages of the current one are still
        // missing would put it ahead of content the reader has not seen.
        Assert.Contains("isComicFullyRequested(tailPath)", ensure);

        // An issue already in the stream must never be appended twice.
        Assert.Contains("stitchedComicPaths.has(nextComicInfo.filePath)", ensure);
    }

    [Fact]
    public void SetActiveComic_MarksThePrecedingIssueRead()
    {
        var setActive = ExtractFunction(ReaderHtml, "setActiveComic");

        // With pre-stitching, a finished issue no longer sits at the bottom of
        // the stream, so the bottom-of-stream check alone would never mark it.
        Assert.Contains("isStitchedBefore(comicFilePath, filePath)", setActive);
        Assert.Contains("markFileAsRead(comicFilePath)", setActive);
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
