using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Additional coverage for <see cref="MissingIssuesCheckJobHandler"/>: Suwayomi library failures,
/// title fallbacks, long summaries, option parsing and cancellation.
/// </summary>
public class MissingIssuesCheckJobHandlerCoverageTests
{
    private const string AutoDownloadOptions = """{"autoDownload":true}""";

    private readonly Mock<ISeriesLibraryService> _library = new();
    private readonly Mock<ISuwayomiService> _suwayomi = new();
    private readonly List<SeriesSummaryDto> _candidates = new();
    private readonly Dictionary<string, SeriesIssuesResult> _issuesById = new(StringComparer.Ordinal);

    public MissingIssuesCheckJobHandlerCoverageTests()
    {
        _library.Setup(l => l.GetSeriesSummariesAsync(
                "missing", It.IsAny<string?>(), 1, -1, It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new SeriesSummaryResult { Series = _candidates.ToList() });
        _library.Setup(l => l.GetSeriesIssuesForIdsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<string> ids, CancellationToken _) => ids
                .Where(_issuesById.ContainsKey)
                .ToDictionary(id => id, id => _issuesById[id], StringComparer.Ordinal));
        _suwayomi.SetupGet(s => s.IsConfigured).Returns(true);
        _suwayomi.Setup(s => s.GetLibrarySeriesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SuwayomiLibrarySnapshot());
        _suwayomi.Setup(s => s.DownloadMissingIssuesFromSnapshotAsync(
                It.IsAny<string?>(), It.IsAny<IEnumerable<int>>(), It.IsAny<SuwayomiLibrarySnapshot>(),
                It.IsAny<IEnumerable<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SuwayomiDownloadResult { QueuedChapters = new List<SuwayomiChapter> { new() } });
    }

    private MissingIssuesCheckJobHandler CreateHandler() => new(
        _library.Object,
        _suwayomi.Object,
        NullLogger<MissingIssuesCheckJobHandler>.Instance);

    private void AddSeries(string id, string title, params string?[] issueNumbers)
    {
        _candidates.Add(new SeriesSummaryDto { Id = id, Title = title, CanonicalTitle = title });
        _issuesById[id] = new SeriesIssuesResult
        {
            Id = id,
            Title = title,
            CanonicalTitle = title,
            Issues = issueNumbers.Select(n => new SeriesIssueDto { Issue = n }).ToList(),
        };
    }

    private void VerifyNoDownloads() =>
        _suwayomi.Verify(s => s.DownloadMissingIssuesFromSnapshotAsync(
            It.IsAny<string?>(), It.IsAny<IEnumerable<int>>(), It.IsAny<SuwayomiLibrarySnapshot>(),
            It.IsAny<IEnumerable<string>?>(), It.IsAny<CancellationToken>()), Times.Never);

    [Fact]
    public async Task ExecuteAsync_SuwayomiLibraryCannotBeLoaded_CountsEverySeriesAsNotDownloaded()
    {
        AddSeries("a", "Alpha", "1", "3");
        AddSeries("b", "Beta", "2");
        _suwayomi.Setup(s => s.GetLibrarySeriesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new SuwayomiException("Suwayomi request timed out"));

        var summary = await CreateHandler().ExecuteAsync(AutoDownloadOptions, CancellationToken.None);

        Assert.Equal(
            "Queued 0 chapter(s) on Suwayomi; 2 series could not be downloaded. 2 series missing 2 issue(s): Alpha (1); Beta (1).",
            summary);
        VerifyNoDownloads();
    }

    [Fact]
    public async Task ExecuteAsync_DownloadOutcomeAndOversizedSkip_AreBothReported()
    {
        AddSeries("a", "Alpha", "2");
        AddSeries("huge", "Huge", "100001");
        _suwayomi.Setup(s => s.GetLibrarySeriesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new SuwayomiException("unreachable"));

        var summary = await CreateHandler().ExecuteAsync(AutoDownloadOptions, CancellationToken.None);

        Assert.Equal(
            "Queued 0 chapter(s) on Suwayomi; 1 series could not be downloaded; Skipped 1 series with issue ranges over 100000. 1 series missing 1 issue(s): Alpha (1).",
            summary);
    }

    [Fact]
    public async Task ExecuteAsync_MoreThanTenSeriesWithGaps_NamesTheFirstTenAndCountsTheRest()
    {
        for (var i = 1; i <= 12; i++)
        {
            AddSeries($"s{i:D2}", $"Series {i:D2}", "2");
        }

        var summary = await CreateHandler().ExecuteAsync(null, CancellationToken.None);

        var named = string.Join("; ", Enumerable.Range(1, 10).Select(i => $"Series {i:D2} (1)"));
        Assert.Equal($"12 series missing 12 issue(s): {named}; +2 more.", summary);
        _suwayomi.Verify(s => s.GetLibrarySeriesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_ExactlyTenSeriesWithGaps_NamesThemAllWithoutAMoreSuffix()
    {
        for (var i = 1; i <= 10; i++)
        {
            AddSeries($"s{i:D2}", $"Series {i:D2}", "2");
        }

        var summary = await CreateHandler().ExecuteAsync(null, CancellationToken.None);

        Assert.DoesNotContain("more", summary);
        Assert.EndsWith("Series 10 (1).", summary);
    }

    [Fact]
    public async Task ExecuteAsync_EveryTitleBlank_FallsBackToTheSeriesId()
    {
        _candidates.Add(new SeriesSummaryDto { Id = "series-42", Title = "", CanonicalTitle = "   " });
        _issuesById["series-42"] = new SeriesIssuesResult
        {
            Id = "series-42",
            Title = " ",
            CanonicalTitle = "",
            Issues = new List<SeriesIssueDto> { new() { Issue = "3" } },
        };

        var summary = await CreateHandler().ExecuteAsync(AutoDownloadOptions, CancellationToken.None);

        Assert.Equal("Queued 1 chapter(s) on Suwayomi. 1 series missing 2 issue(s): series-42 (2).", summary);
        _suwayomi.Verify(s => s.DownloadMissingIssuesFromSnapshotAsync(
            "series-42",
            It.Is<IEnumerable<int>>(n => n.SequenceEqual(new[] { 1, 2 })),
            It.IsAny<SuwayomiLibrarySnapshot>(),
            It.Is<IEnumerable<string>?>(t => t != null && !t.Any()),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_BlankSeriesTitle_UsesTheIssuesTitleAndPassesDistinctAlternates()
    {
        _candidates.Add(new SeriesSummaryDto
        {
            Id = "a",
            Title = "  ",
            CanonicalTitle = "Canon",
            Aliases = new List<string> { "canon", "Series Alias", " " },
        });
        _issuesById["a"] = new SeriesIssuesResult
        {
            Id = "a",
            Title = "Issue Title",
            CanonicalTitle = "ISSUE TITLE",
            Aliases = new List<string> { "Issue Alias", "series alias", "" },
            Issues = new List<SeriesIssueDto> { new() { Issue = "1" }, new() { Issue = "3" } },
        };
        IEnumerable<string>? alternates = null;
        _suwayomi.Setup(s => s.DownloadMissingIssuesFromSnapshotAsync(
                "Issue Title", It.IsAny<IEnumerable<int>>(), It.IsAny<SuwayomiLibrarySnapshot>(),
                It.IsAny<IEnumerable<string>?>(), It.IsAny<CancellationToken>()))
            .Callback<string?, IEnumerable<int>, SuwayomiLibrarySnapshot, IEnumerable<string>?, CancellationToken>(
                (_, _, _, titles, _) => alternates = titles)
            .ReturnsAsync(new SuwayomiDownloadResult());

        var summary = await CreateHandler().ExecuteAsync(AutoDownloadOptions, CancellationToken.None);

        Assert.Equal("Queued 0 chapter(s) on Suwayomi. 1 series missing 1 issue(s): Issue Title (1).", summary);
        Assert.Equal(new[] { "Canon", "Issue Alias", "series alias" }, alternates);
    }

    [Fact]
    public async Task ExecuteAsync_BlankSeriesAndIssuesTitles_UseTheCanonicalTitle()
    {
        _candidates.Add(new SeriesSummaryDto { Id = "a", Title = "", CanonicalTitle = "Canonical Alpha" });
        _issuesById["a"] = new SeriesIssuesResult
        {
            Id = "a",
            Issues = new List<SeriesIssueDto> { new() { Issue = "2" } },
        };

        var summary = await CreateHandler().ExecuteAsync(null, CancellationToken.None);

        Assert.Equal("1 series missing 1 issue(s): Canonical Alpha (1).", summary);
    }

    [Fact]
    public async Task ExecuteAsync_CandidateWithoutIssueData_IsCheckedButNotReported()
    {
        _candidates.Add(new SeriesSummaryDto { Id = "ghost", Title = "Ghost" });
        AddSeries("a", "Alpha", "1", "2");

        var summary = await CreateHandler().ExecuteAsync(AutoDownloadOptions, CancellationToken.None);

        Assert.Equal("Checked 2 series: none are missing issues.", summary);
        _suwayomi.Verify(s => s.GetLibrarySeriesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("not json", false)]
    [InlineData("null", false)]
    [InlineData("   ", false)]
    [InlineData("{}", false)]
    [InlineData("""{"autoDownload":false}""", false)]
    [InlineData("""{"AUTODOWNLOAD":true}""", true)]
    public async Task ExecuteAsync_ParsesTheAutoDownloadOption(string optionsJson, bool expectDownload)
    {
        AddSeries("a", "Alpha", "2");

        var summary = await CreateHandler().ExecuteAsync(optionsJson, CancellationToken.None);

        if (expectDownload)
        {
            Assert.Equal("Queued 1 chapter(s) on Suwayomi. 1 series missing 1 issue(s): Alpha (1).", summary);
            _suwayomi.Verify(s => s.GetLibrarySeriesAsync(It.IsAny<CancellationToken>()), Times.Once);
        }
        else
        {
            Assert.Equal("1 series missing 1 issue(s): Alpha (1).", summary);
            _suwayomi.Verify(s => s.GetLibrarySeriesAsync(It.IsAny<CancellationToken>()), Times.Never);
            VerifyNoDownloads();
        }
    }

    [Fact]
    public async Task ExecuteAsync_CancelledWhileLoadingIssues_ThrowsBeforeCheckingAnySeries()
    {
        AddSeries("a", "Alpha", "2");
        using var cts = new CancellationTokenSource();
        _library.Setup(l => l.GetSeriesIssuesForIdsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Callback(() => cts.Cancel())
            .ReturnsAsync(new Dictionary<string, SeriesIssuesResult>(_issuesById, StringComparer.Ordinal));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateHandler().ExecuteAsync(AutoDownloadOptions, cts.Token));

        _suwayomi.Verify(s => s.GetLibrarySeriesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_CancelledMidDownload_StopsBeforeTheNextSeries()
    {
        AddSeries("a", "Alpha", "2");
        AddSeries("b", "Beta", "2");
        using var cts = new CancellationTokenSource();
        _suwayomi.Setup(s => s.DownloadMissingIssuesFromSnapshotAsync(
                "Alpha", It.IsAny<IEnumerable<int>>(), It.IsAny<SuwayomiLibrarySnapshot>(),
                It.IsAny<IEnumerable<string>?>(), It.IsAny<CancellationToken>()))
            .Callback(() => cts.Cancel())
            .ReturnsAsync(new SuwayomiDownloadResult());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateHandler().ExecuteAsync(AutoDownloadOptions, cts.Token));

        _suwayomi.Verify(s => s.DownloadMissingIssuesFromSnapshotAsync(
            "Beta", It.IsAny<IEnumerable<int>>(), It.IsAny<SuwayomiLibrarySnapshot>(),
            It.IsAny<IEnumerable<string>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void Metadata_DescribesTheJob()
    {
        var handler = CreateHandler();

        Assert.Equal("missing-issues-check", handler.JobKey);
        Assert.Equal("Missing Issues Check", handler.DisplayName);
        Assert.Contains("autoDownload", handler.Description);
    }
}
