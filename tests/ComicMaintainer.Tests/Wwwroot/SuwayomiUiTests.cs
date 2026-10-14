using System.Text.RegularExpressions;

namespace ComicMaintainer.Tests.Wwwroot;

public class SuwayomiUiTests
{
    private static string ReadMainJs()
    {
        var current = AppContext.BaseDirectory;
        for (var i = 0; i < 10 && current != null; i++)
        {
            var candidate = Path.Combine(current, "src", "ComicMaintainer.WebApi", "wwwroot", "js", "main.js");
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            current = Path.GetDirectoryName(current);
        }

        throw new FileNotFoundException("Could not locate main.js relative to test binaries.");
    }

    [Fact]
    public void DownloadMissingIssues_UsesTheUnfilteredFullSeriesIssueList()
    {
        var contents = ReadMainJs();
        var downloadAction = Regex.Match(
            contents,
            @"async function downloadMissingIssuesFromSuwayomi\(seriesId, seriesTitle\) \{.*?(?=\n        // Render a small banner)",
            RegexOptions.Singleline);

        Assert.True(downloadAction.Success, "Expected to find the Suwayomi download action.");
        Assert.Contains("/api/files/series/${encodeURIComponent(seriesId)}/issues", downloadAction.Value);
        Assert.Contains("per_page=${SERIES_ISSUES_LOAD_ALL}", downloadAction.Value);
        Assert.DoesNotContain("seriesIssuesCache", downloadAction.Value);
        Assert.DoesNotContain("filterMode", downloadAction.Value);
    }

    [Fact]
    public void SavingSuwayomiSettings_InvalidatesAndRefreshesTheStatusProbe()
    {
        var contents = ReadMainJs();

        Assert.Matches(
            new Regex(@"const suwayomiResponse = await fetch\([\s\S]*?suwayomiStatusPromise = null;\s*await refreshSuwayomiAvailability\(\);"),
            contents);
    }
}
