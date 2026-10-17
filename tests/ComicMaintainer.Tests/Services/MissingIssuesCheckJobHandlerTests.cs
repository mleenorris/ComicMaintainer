using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using Microsoft.Extensions.Logging;
using Moq;

namespace ComicMaintainer.Tests.Services;

public class MissingIssuesCheckJobHandlerTests
{
    private readonly Mock<ISeriesLibraryService> _library = new();
    private readonly Mock<ISuwayomiService> _suwayomi = new();

    private MissingIssuesCheckJobHandler BuildHandler() => new(
        _library.Object,
        _suwayomi.Object,
        new Mock<ILogger<MissingIssuesCheckJobHandler>>().Object);

    private void SetupSeries(string id, string title, params string?[] issueNumbers)
    {
        _library.Setup(l => l.GetSeriesIssuesAsync(id, It.IsAny<string?>(), 1, -1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesIssuesResult
            {
                Id = id,
                Title = title,
                CanonicalTitle = title,
                Aliases = new List<string> { title + " Alias" },
                Issues = issueNumbers.Select(n => new SeriesIssueDto { Issue = n }).ToList()
            });
    }

    private void SetupCandidates(params (string Id, string Title)[] series)
    {
        _library.Setup(l => l.GetSeriesSummariesAsync(
                "missing", It.IsAny<string?>(), 1, -1, It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesSummaryResult
            {
                Series = series.Select(s => new SeriesSummaryDto { Id = s.Id, Title = s.Title, CanonicalTitle = s.Title }).ToList()
            });
    }

    [Fact]
    public void ComputeMissingIssueNumbers_ReportsGapsFromOneToMax()
    {
        var missing = MissingIssuesCheckJobHandler.ComputeMissingIssueNumbers(
            new[] { "002", "5", "3.5", "Annual", null, "" });

        Assert.Equal(new[] { 1, 3, 4 }, missing);
    }

    [Fact]
    public void ComputeMissingIssueNumbers_NoNumericIssues_ReturnsEmpty()
    {
        Assert.Empty(MissingIssuesCheckJobHandler.ComputeMissingIssueNumbers(new[] { "Annual", "1.5" }));
    }

    [Fact]
    public void Defaults_DisabledDailyReportOnly()
    {
        var defaults = BuildHandler().Defaults;

        Assert.False(defaults.Enabled);
        Assert.Equal(60 * 24, defaults.IntervalMinutes);
        Assert.Contains("\"autoDownload\":false", defaults.OptionsJson);
    }

    [Fact]
    public async Task ExecuteAsync_ReportOnly_SummarizesMissingWithoutDownloading()
    {
        SetupCandidates(("a", "Alpha"), ("b", "Beta"));
        SetupSeries("a", "Alpha", "1", "2", "4");
        SetupSeries("b", "Beta", "1", "2", "3");
        _suwayomi.SetupGet(s => s.IsConfigured).Returns(true);

        var summary = await BuildHandler().ExecuteAsync(null, CancellationToken.None);

        Assert.Contains("1 series missing 1 issue(s)", summary);
        Assert.Contains("Alpha (1)", summary);
        Assert.DoesNotContain("Beta", summary);
        _suwayomi.Verify(s => s.DownloadMissingIssuesAsync(
            It.IsAny<string?>(), It.IsAny<IEnumerable<int>>(), It.IsAny<int?>(),
            It.IsAny<IEnumerable<string>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_NoCandidates_ReportsNothingMissing()
    {
        SetupCandidates();

        var summary = await BuildHandler().ExecuteAsync(null, CancellationToken.None);

        Assert.Equal("No series are missing issues.", summary);
    }

    [Fact]
    public async Task ExecuteAsync_AutoDownload_QueuesMissingIssuesOnSuwayomi()
    {
        SetupCandidates(("a", "Alpha"));
        SetupSeries("a", "Alpha", "1", "4");
        _suwayomi.SetupGet(s => s.IsConfigured).Returns(true);
        _suwayomi.Setup(s => s.DownloadMissingIssuesAsync(
                "Alpha", It.IsAny<IEnumerable<int>>(), null,
                It.IsAny<IEnumerable<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SuwayomiDownloadResult
            {
                QueuedChapters = new List<SuwayomiChapter> { new(), new() }
            });

        var summary = await BuildHandler().ExecuteAsync("""{"autoDownload":true}""", CancellationToken.None);

        _suwayomi.Verify(s => s.DownloadMissingIssuesAsync(
            "Alpha",
            It.Is<IEnumerable<int>>(n => n.SequenceEqual(new[] { 2, 3 })),
            null,
            It.Is<IEnumerable<string>?>(t => t != null && t.Contains("Alpha Alias") && !t.Contains("Alpha")),
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains("Queued 2 chapter(s) on Suwayomi.", summary);
    }

    [Fact]
    public async Task ExecuteAsync_AutoDownload_FailureForOneSeriesDoesNotAbortOthers()
    {
        SetupCandidates(("a", "Alpha"), ("b", "Beta"));
        SetupSeries("a", "Alpha", "2");
        SetupSeries("b", "Beta", "3");
        _suwayomi.SetupGet(s => s.IsConfigured).Returns(true);
        _suwayomi.Setup(s => s.DownloadMissingIssuesAsync(
                "Alpha", It.IsAny<IEnumerable<int>>(), null,
                It.IsAny<IEnumerable<string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new SuwayomiException("not found"));
        _suwayomi.Setup(s => s.DownloadMissingIssuesAsync(
                "Beta", It.IsAny<IEnumerable<int>>(), null,
                It.IsAny<IEnumerable<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SuwayomiDownloadResult { QueuedChapters = new List<SuwayomiChapter> { new() } });

        var summary = await BuildHandler().ExecuteAsync("""{"autoDownload":true}""", CancellationToken.None);

        Assert.Contains("Queued 1 chapter(s) on Suwayomi; 1 series could not be downloaded.", summary);
    }

    [Fact]
    public async Task ExecuteAsync_AutoDownloadWithoutSuwayomi_SkipsDownloadAndSaysSo()
    {
        SetupCandidates(("a", "Alpha"));
        SetupSeries("a", "Alpha", "2");
        _suwayomi.SetupGet(s => s.IsConfigured).Returns(false);

        var summary = await BuildHandler().ExecuteAsync("""{"autoDownload":true}""", CancellationToken.None);

        Assert.Contains("not configured", summary);
        _suwayomi.Verify(s => s.DownloadMissingIssuesAsync(
            It.IsAny<string?>(), It.IsAny<IEnumerable<int>>(), It.IsAny<int?>(),
            It.IsAny<IEnumerable<string>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
