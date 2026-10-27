using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using Moq;

namespace ComicMaintainer.Tests.Services;

public class SuwayomiDownloadImportJobHandlerTests
{
    private static SuwayomiImportResult WithImports(params string[] destinations)
    {
        var result = new SuwayomiImportResult();
        foreach (var destination in destinations)
        {
            result.Imported.Add(new SuwayomiImportedChapter("/downloads/source/series/" + Path.GetFileName(destination), destination));
        }

        return result;
    }

    [Fact]
    public void Handler_IsEnabledByDefaultAndRunsEveryFifteenMinutes()
    {
        var handler = new SuwayomiDownloadImportJobHandler(Mock.Of<ISuwayomiDownloadImportService>());

        Assert.Equal("suwayomi-download-import", handler.JobKey);
        Assert.True(handler.Defaults.Enabled);
        Assert.Equal(15, handler.Defaults.IntervalMinutes);
        Assert.DoesNotContain("<", handler.Description);
    }

    [Fact]
    public async Task ExecuteAsync_SummarizesTheImport()
    {
        var result = WithImports(
            "/library/Solo Leveling/Chapter 1.cbz",
            "/library/Solo Leveling/Chapter 2.cbz",
            "/library/Manga/Omniscient Reader/Chapter 7.cbz");
        result.StillDownloading = 1;
        result.RemovedFolders = 2;
        var importer = new Mock<ISuwayomiDownloadImportService>();
        importer.Setup(i => i.ImportAsync(It.IsAny<CancellationToken>())).ReturnsAsync(result);

        var summary = await new SuwayomiDownloadImportJobHandler(importer.Object).ExecuteAsync(null, CancellationToken.None);

        Assert.Equal(
            "Imported 3 chapter(s) into 2 series folder(s) (Solo Leveling, Omniscient Reader); 1 still downloading; removed 2 empty folder(s).",
            summary);
    }

    [Fact]
    public void FormatSummary_PointsAtTheSettingWhenNoDownloadsFolderIsConfigured()
    {
        var summary = SuwayomiDownloadImportJobHandler.FormatSummary(
            SuwayomiImportResult.Skipped("No Suwayomi downloads folder is configured.", notConfigured: true));

        Assert.StartsWith("Skipped:", summary);
        Assert.Contains("Settings → Suwayomi", summary);
    }

    [Fact]
    public void FormatSummary_ReportsWhyARunWasSkipped()
    {
        var summary = SuwayomiDownloadImportJobHandler.FormatSummary(
            SuwayomiImportResult.Skipped("The Suwayomi downloads folder '/suwayomi' does not exist or is not accessible."));

        Assert.Equal("Skipped: The Suwayomi downloads folder '/suwayomi' does not exist or is not accessible.", summary);
    }

    [Fact]
    public void FormatSummary_SaysSoWhenThereIsNothingToImport()
    {
        Assert.Equal("No new Suwayomi downloads.", SuwayomiDownloadImportJobHandler.FormatSummary(new SuwayomiImportResult()));
    }

    [Fact]
    public void FormatSummary_ReportsAPermissionProblemApartFromOtherFailures()
    {
        var summary = SuwayomiDownloadImportJobHandler.FormatSummary(
            new SuwayomiImportResult { Failed = 2, PermissionDenied = true, Unreadable = 1, WithoutSeriesFolder = 1 });

        Assert.StartsWith("Stopped: permission denied", summary);
        Assert.Contains("1 unreadable archive(s) left in place", summary);
        Assert.Contains("1 could not be moved", summary);
        Assert.Contains("1 left in place for lack of a series folder", summary);
    }

    [Fact]
    public void FormatSummary_PointsAtTheCbzSettingWhenChaptersAreSavedAsImages()
    {
        var summary = SuwayomiDownloadImportJobHandler.FormatSummary(new SuwayomiImportResult { ImageFolders = 3 });

        Assert.StartsWith("No new Suwayomi downloads.", summary);
        Assert.Contains("3 chapter folder(s) hold loose images", summary);
        Assert.Contains("Save as CBZ archive", summary);
    }

    [Fact]
    public void FormatSummary_NamesAtMostTenSeries()
    {
        var result = WithImports(Enumerable.Range(1, 12).Select(i => $"/library/Series {i:00}/Chapter 1.cbz").ToArray());

        var summary = SuwayomiDownloadImportJobHandler.FormatSummary(result);

        Assert.Contains("into 12 series folder(s)", summary);
        Assert.Contains("Series 10, +2 more)", summary);
        Assert.DoesNotContain("Series 11", summary);
    }
}
