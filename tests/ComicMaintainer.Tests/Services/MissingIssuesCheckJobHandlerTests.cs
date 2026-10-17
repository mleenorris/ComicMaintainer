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
    private readonly Dictionary<string, SeriesIssuesResult> _issuesById = new(StringComparer.Ordinal);

    private MissingIssuesCheckJobHandler BuildHandler() => new(
        _library.Object,
        _suwayomi.Object,
        new Mock<ILogger<MissingIssuesCheckJobHandler>>().Object);

    private void SetupSeries(string id, string title, params string?[] issueNumbers)
    {
        _issuesById[id] = new SeriesIssuesResult
        {
            Id = id,
            Title = title,
            CanonicalTitle = title,
            Aliases = new List<string> { title + " Alias" },
            Issues = issueNumbers.Select(n => new SeriesIssueDto { Issue = n }).ToList()
        };
    }

    private void SetupCandidates(params (string Id, string Title)[] series)
    {
        _library.Setup(l => l.GetSeriesIssuesForIdsAsync(
                It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<string> ids, CancellationToken _) => ids
                .Where(_issuesById.ContainsKey)
                .ToDictionary(id => id, id => _issuesById[id], StringComparer.Ordinal));
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
    public void ComputeMissingIssueNumbers_RejectsExcessiveRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MissingIssuesCheckJobHandler.ComputeMissingIssueNumbers(new[] { "1", "2147483647" }));
    }

    [Fact]
    public void ComputeMissingIssueNumbers_HonorsCancellationDuringExpansion()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            MissingIssuesCheckJobHandler.ComputeMissingIssueNumbers(new[] { "100000" }, cancellation.Token));
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

        _library.Verify(l => l.GetSeriesIssuesForIdsAsync(
            It.Is<IEnumerable<string>>(ids => ids.SequenceEqual(new[] { "a", "b" })),
            It.IsAny<CancellationToken>()), Times.Once);
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
        _suwayomi.Setup(s => s.GetLibrarySeriesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SuwayomiLibrarySnapshot());
        _suwayomi.Setup(s => s.DownloadMissingIssuesFromSnapshotAsync(
                "Alpha", It.IsAny<IEnumerable<int>>(), It.IsAny<SuwayomiLibrarySnapshot>(),
                It.IsAny<IEnumerable<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SuwayomiDownloadResult
            {
                QueuedChapters = new List<SuwayomiChapter> { new(), new() }
            });

        var summary = await BuildHandler().ExecuteAsync("""{"autoDownload":true}""", CancellationToken.None);

        _suwayomi.Verify(s => s.GetLibrarySeriesAsync(It.IsAny<CancellationToken>()), Times.Once);
        _suwayomi.Verify(s => s.DownloadMissingIssuesFromSnapshotAsync(
            "Alpha",
            It.Is<IEnumerable<int>>(n => n.SequenceEqual(new[] { 2, 3 })),
            It.IsAny<SuwayomiLibrarySnapshot>(),
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
        _suwayomi.Setup(s => s.GetLibrarySeriesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SuwayomiLibrarySnapshot());
        _suwayomi.Setup(s => s.DownloadMissingIssuesFromSnapshotAsync(
                "Alpha", It.IsAny<IEnumerable<int>>(), It.IsAny<SuwayomiLibrarySnapshot>(),
                It.IsAny<IEnumerable<string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new SuwayomiException("not found"));
        _suwayomi.Setup(s => s.DownloadMissingIssuesFromSnapshotAsync(
                "Beta", It.IsAny<IEnumerable<int>>(), It.IsAny<SuwayomiLibrarySnapshot>(),
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
        _suwayomi.Verify(s => s.DownloadMissingIssuesFromSnapshotAsync(
            It.IsAny<string?>(), It.IsAny<IEnumerable<int>>(), It.IsAny<SuwayomiLibrarySnapshot>(),
            It.IsAny<IEnumerable<string>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_OversizedIssueRange_IsReportedAsSkipped()
    {
        SetupCandidates(("a", "Alpha"));
        SetupSeries("a", "Alpha", "1", "2147483647");

        var summary = await BuildHandler().ExecuteAsync(null, CancellationToken.None);

        Assert.Contains("Skipped 1 series with issue ranges over 100000", summary);
        Assert.Contains("Checked 1 series; no other series are missing issues.", summary);
        Assert.DoesNotContain("none are missing issues", summary);
    }

    [Fact]
    public async Task ExecuteAsync_DownloadOutcomePrecedesLongSeriesList()
    {
        var series = Enumerable.Range(0, 10)
            .Select(i => ($"id-{i}", new string((char)('A' + i), 120)))
            .ToArray();
        SetupCandidates(series.Select(s => (s.Item1, s.Item2)).ToArray());
        foreach (var (id, title) in series)
        {
            SetupSeries(id, title, "2");
        }

        _suwayomi.SetupGet(s => s.IsConfigured).Returns(true);
        _suwayomi.Setup(s => s.GetLibrarySeriesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SuwayomiLibrarySnapshot());
        _suwayomi.Setup(s => s.DownloadMissingIssuesFromSnapshotAsync(
                It.IsAny<string?>(), It.IsAny<IEnumerable<int>>(), It.IsAny<SuwayomiLibrarySnapshot>(),
                It.IsAny<IEnumerable<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SuwayomiDownloadResult());

        var summary = await BuildHandler().ExecuteAsync("""{"autoDownload":true}""", CancellationToken.None);

        Assert.StartsWith("Queued 0 chapter(s) on Suwayomi.", summary);
    }
}
