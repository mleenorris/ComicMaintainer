using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Adding a Suwayomi series: the issues of local series with the same title
/// are left out of the downloads, and a failure after the add says that the
/// add went through.
/// </summary>
public sealed class SuwayomiSeriesAddServiceTests : IDisposable
{
    private readonly Mock<ISuwayomiService> _suwayomi = new(MockBehavior.Strict);
    private readonly Mock<ISeriesLibraryService> _library = new(MockBehavior.Strict);
    private readonly CancellationTokenSource _cancellation = new();
    private readonly CancellationToken _token;

    public SuwayomiSeriesAddServiceTests() => _token = _cancellation.Token;

    public void Dispose() => _cancellation.Dispose();

    private SuwayomiSeriesAddService CreateService() =>
        new(_suwayomi.Object, _library.Object, NullLogger<SuwayomiSeriesAddService>.Instance);

    private void SetupAdd(string title, bool alreadyInLibrary = false) =>
        _suwayomi.Setup(s => s.AddToLibraryAsync(5, _token))
            .ReturnsAsync(new SuwayomiAddToLibraryResult
            {
                Series = new SuwayomiSeries { Id = 5, Title = title, InLibrary = true },
                AlreadyInLibrary = alreadyInLibrary
            });

    private void SetupLibrary(params SeriesSummaryDto[] series) =>
        _library.Setup(l => l.GetSeriesSummariesAsync(
                null, null, 1, -1, It.IsAny<string?>(), It.IsAny<string?>(), null, null, _token))
            .ReturnsAsync(new SeriesSummaryResult { Series = series.ToList() });

    private List<string?> CaptureQueue(SuwayomiSeriesDownloadResult downloads)
    {
        var captured = new List<string?>();
        _suwayomi.Setup(s => s.QueueSeriesDownloadsAsync(5, It.IsAny<IEnumerable<string?>?>(), _token))
            .Callback<int, IEnumerable<string?>?, CancellationToken>((_, owned, _) => captured.AddRange(owned!))
            .ReturnsAsync(downloads);
        return captured;
    }

    private static SeriesSummaryDto Local(string id, string title, string canonicalTitle = "", params string[] aliases) =>
        new() { Id = id, Title = title, CanonicalTitle = canonicalTitle, Aliases = aliases.ToList() };

    private static SeriesIssuesResult Issues(string id, params string?[] issues) =>
        new() { Id = id, Issues = issues.Select(issue => new SeriesIssueDto { Issue = issue }).ToList() };

    [Fact]
    public async Task AddSeriesAsync_LeavesOutTheIssuesOfEveryLocalSeriesWithTheSameTitle()
    {
        SetupAdd("Solo Leveling");
        SetupLibrary(
            Local("a", "Solo Leveling", "Solo Leveling"),
            Local("b", "Na Honjaman Level-eob", "SOLO-LEVELING"),
            Local("c", "Only I Level Up", "", "Ore dake Level Up na Ken", "solo leveling!"),
            Local("d", "Solo Leveling: Ragnarok", "Solo Leveling: Ragnarok"),
            Local("e", "  ", "  "));
        _library.Setup(l => l.GetSeriesIssuesForIdsAsync(
                It.Is<IEnumerable<string>>(ids => ids.SequenceEqual(new[] { "a", "b", "c" })),
                _token))
            .ReturnsAsync(new Dictionary<string, SeriesIssuesResult>
            {
                ["a"] = Issues("a", "1", "2"),
                ["b"] = Issues("b", "3", null),
                ["c"] = Issues("c", "2.5")
            });
        var downloads = new SuwayomiSeriesDownloadResult { ChapterCount = 9, AlreadyInLocalLibraryCount = 4 };
        var owned = CaptureQueue(downloads);

        var result = await CreateService().AddSeriesAsync(5, _token);

        Assert.Equal(5, result.Series.Id);
        Assert.False(result.AlreadyInLibrary);
        Assert.Equal("Solo Leveling", result.LocalSeriesTitle);
        Assert.Same(downloads, result.Downloads);
        Assert.Equal(
            new[] { null, "1", "2", "2.5", "3" },
            owned.OrderBy(issue => issue, StringComparer.Ordinal));
    }

    [Fact]
    public async Task AddSeriesAsync_WithoutALocalSeries_LeavesNothingOut()
    {
        SetupAdd("Solo Leveling", alreadyInLibrary: true);
        SetupLibrary(Local("d", "Solo Leveling: Ragnarok", "Solo Leveling: Ragnarok"));
        var downloads = new SuwayomiSeriesDownloadResult { ChapterCount = 3 };
        var owned = CaptureQueue(downloads);

        var result = await CreateService().AddSeriesAsync(5, _token);

        Assert.True(result.AlreadyInLibrary);
        Assert.Null(result.LocalSeriesTitle);
        Assert.Same(downloads, result.Downloads);
        Assert.Empty(owned);
        _library.Verify(
            l => l.GetSeriesIssuesForIdsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task AddSeriesAsync_TitleWithoutLettersOrDigits_MatchesNoLocalSeries()
    {
        // The strict library mock fails the test if the library is consulted.
        SetupAdd("!!!");
        var owned = CaptureQueue(new SuwayomiSeriesDownloadResult());

        var result = await CreateService().AddSeriesAsync(5, _token);

        Assert.Null(result.LocalSeriesTitle);
        Assert.Empty(owned);
    }

    [Theory]
    [InlineData(false, "Added 'Solo Leveling' to the Suwayomi library, but its chapters could not be queued: Could not reach the Suwayomi server: timed out")]
    [InlineData(true, "'Solo Leveling' is in the Suwayomi library, but its chapters could not be queued: Could not reach the Suwayomi server: timed out")]
    public async Task AddSeriesAsync_QueueFailure_SaysTheSeriesIsInTheLibrary(bool alreadyInLibrary, string expected)
    {
        SetupAdd("Solo Leveling", alreadyInLibrary);
        SetupLibrary();
        var failure = new SuwayomiException("Could not reach the Suwayomi server: timed out");
        _suwayomi.Setup(s => s.QueueSeriesDownloadsAsync(5, It.IsAny<IEnumerable<string?>?>(), _token))
            .ThrowsAsync(failure);

        var exception = await Assert.ThrowsAsync<SuwayomiException>(() => CreateService().AddSeriesAsync(5, _token));

        Assert.Equal(expected, exception.Message);
        Assert.Same(failure, exception.InnerException);
    }

    [Fact]
    public async Task AddSeriesAsync_AddFailure_QueuesNothing()
    {
        var failure = new SuwayomiException("Suwayomi has no series with id 5.");
        _suwayomi.Setup(s => s.AddToLibraryAsync(5, _token)).ThrowsAsync(failure);

        var exception = await Assert.ThrowsAsync<SuwayomiException>(() => CreateService().AddSeriesAsync(5, _token));

        Assert.Same(failure, exception);
        _suwayomi.Verify(
            s => s.QueueSeriesDownloadsAsync(It.IsAny<int>(), It.IsAny<IEnumerable<string?>?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
