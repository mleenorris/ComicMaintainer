using System.Text.RegularExpressions;

namespace ComicMaintainer.Tests.Wwwroot;

public class SuwayomiUiTests
{
    private static string ReadMainJs()
    {
        // main.js is now split into several ordered scripts; scan all of them.
        return WwwrootFiles.ReadAppScripts();
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
    public void DownloadMissingIssues_SendsTheSeriesOtherTitlesForSuwayomiToFallBackOn()
    {
        var contents = ReadMainJs();
        var downloadAction = Regex.Match(
            contents,
            @"async function downloadMissingIssuesFromSuwayomi\(seriesId, seriesTitle\) \{.*?(?=\n        // Render a small banner)",
            RegexOptions.Singleline);

        Assert.True(downloadAction.Success, "Expected to find the Suwayomi download action.");
        // Suwayomi may list the series under its canonical title or an alias
        // rather than the title shown here.
        Assert.Contains("data.canonical_title", downloadAction.Value);
        Assert.Contains("data.aliases", downloadAction.Value);
        Assert.Matches(@"alternateTitles:\s*titles\.slice\(1\)", downloadAction.Value);
    }

    [Fact]
    public void SavingSuwayomiSettings_InvalidatesAndRefreshesTheStatusProbe()
    {
        var contents = ReadMainJs();

        Assert.Matches(
            new Regex(@"const suwayomiResponse = await fetch\([\s\S]*?suwayomiStatusPromise = null;\s*await refreshSuwayomiAvailability\(\);"),
            contents);
    }

    [Fact]
    public void SuwayomiSettings_LoadAndSaveTheDownloadImportFolders()
    {
        var contents = ReadMainJs();

        Assert.Contains("suwayomiDownloadsDirectory.value = settingsData.suwayomi_downloads_directory || ''", contents);
        Assert.Contains("suwayomiImportDirectory.value = settingsData.suwayomi_import_directory || ''", contents);

        var body = Regex.Match(contents, @"const suwayomiBody = \{.*?\};", RegexOptions.Singleline);
        Assert.True(body.Success, "Expected to find the Suwayomi settings request body.");
        // Sent trimmed but not nulled: an emptied field must clear the stored folder.
        Assert.Matches(@"downloadsDirectory: suwayomiDownloadsDirectoryEl \? suwayomiDownloadsDirectoryEl\.value\.trim\(\) : null", body.Value);
        Assert.Matches(@"importDirectory: suwayomiImportDirectoryEl \? suwayomiImportDirectoryEl\.value\.trim\(\) : null", body.Value);
    }

    [Fact]
    public void SettingsPage_OffersTheDownloadImportFoldersInsteadOfMountingInsideTheLibrary()
    {
        var html = WwwrootFiles.Read("index.html");

        Assert.Contains("id=\"suwayomiDownloadsDirectory\"", html);
        Assert.Contains("id=\"suwayomiImportDirectory\"", html);
        Assert.DoesNotContain("mount that directory inside the watched directory", html);
    }

    [Fact]
    public void ScheduledJobsPage_HasAnIconForTheDownloadImportJob()
    {
        Assert.Contains("case 'suwayomi-download-import': return uiIcon('download');", ReadMainJs());
    }
}
