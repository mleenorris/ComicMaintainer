namespace ComicMaintainer.Tests.Wwwroot;

public class ReaderCompletionTests
{
    [Fact]
    public void ReaderHtml_MarksWebcomicCompleteWhenFinalPageIsVisible()
    {
        var contents = File.ReadAllText(Path.Combine(WwwRoot, "reader.html"));
        var observerStart = contents.IndexOf("function setupPageObserver()", StringComparison.Ordinal);
        var observerEnd = contents.IndexOf("function setActiveComic(", observerStart, StringComparison.Ordinal);

        Assert.True(observerStart >= 0 && observerEnd > observerStart, "Could not locate the webcomic page observer.");
        var observer = contents[observerStart..observerEnd];
        Assert.Contains("pageNum === totalPages && !hasMarkedAsRead", observer);
        Assert.Contains("markFileAsRead();", observer);
    }

    private static string WwwRoot
    {
        get
        {
            var current = AppContext.BaseDirectory;
            for (var i = 0; i < 10 && current is not null; i++)
            {
                var candidate = Path.Combine(current, "src", "ComicMaintainer.WebApi", "wwwroot");
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }

                current = Path.GetDirectoryName(current);
            }

            throw new DirectoryNotFoundException("Could not locate wwwroot relative to test binaries.");
        }
    }
}
