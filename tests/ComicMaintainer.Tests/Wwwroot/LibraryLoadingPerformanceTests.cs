using System.Text.RegularExpressions;

namespace ComicMaintainer.Tests.Wwwroot;

/// <summary>
/// Guards the large-library loading behaviour of the series/folder lists:
/// superseded list requests are aborted rather than rendered, and infinite
/// scroll appends only the newly fetched series cards.
/// </summary>
public class LibraryLoadingPerformanceTests
{
    private static string ExtractFunction(string contents, string name)
    {
        var match = Regex.Match(
            contents,
            @"async function " + Regex.Escape(name) + @"\(.*?\n        \}\n",
            RegexOptions.Singleline);
        Assert.True(match.Success, $"Expected to find {name}.");
        return match.Value;
    }

    [Theory]
    [InlineData("loadSeriesLibrary", "seriesLoadController", "seriesReloadPending")]
    [InlineData("loadFolders", "folderLoadController", "folderReloadPending")]
    public void ListLoaders_AbortSupersededRequestsAndSuppressAbortErrors(
        string function, string controller, string pendingFlag)
    {
        var body = ExtractFunction(WwwrootFiles.Read("js/app/library.js"), function);

        Assert.Contains($"{pendingFlag} = true;", body);
        Assert.Contains($"{controller}.abort();", body);
        Assert.Contains("signal: controller ? controller.signal : undefined", body);
        Assert.Contains("if (!isAbortError(error))", body);
        Assert.Contains($"if ({controller} === controller)", body);
    }

    [Fact]
    public void SeriesLibrary_AppendsInfiniteScrollPagesInsteadOfReRenderingEverything()
    {
        var contents = WwwrootFiles.Read("js/app/library.js");
        var loader = ExtractFunction(contents, "loadSeriesLibrary");

        Assert.Contains("else if (!(append && appendSeriesLibraryItems(newSeries)))", loader);

        var append = Regex.Match(
            contents,
            @"function appendSeriesLibraryItems\(newSeries\) \{.*?\n        \}\n",
            RegexOptions.Singleline);
        Assert.True(append.Success, "Expected to find appendSeriesLibraryItems.");
        Assert.Contains("appendHtml(", append.Value);
        Assert.Contains("hydrateProtectedImages(node)", append.Value);
        Assert.Contains("syncSeriesSelectionControls();", append.Value);
        Assert.DoesNotContain("renderSeriesLibrary()", append.Value);

        foreach (var layout in new[] { "Compact", "Grid", "List" })
        {
            Assert.Contains($"function renderSeriesLibrary{layout}(items = seriesLibrary)", contents);
        }
    }
}
