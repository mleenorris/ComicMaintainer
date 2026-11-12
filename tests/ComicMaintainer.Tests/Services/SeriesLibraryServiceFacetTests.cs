using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// The genre and status filters of the series library, how they combine with the existing
/// filter, search, sorting and paging, and the state of a series with no such data yet.
/// </summary>
public class SeriesLibraryServiceFacetTests
{
    private readonly Mock<IFileStoreService> _fileStore = new();
    private readonly Mock<ISeriesMetadataCacheService> _metadataCache = new();
    private readonly Mock<IOptionsMonitor<AppSettings>> _settings = new();
    private readonly List<ComicFile> _files = new();
    private readonly List<SeriesMetadataCacheRecord> _records = new();

    public SeriesLibraryServiceFacetTests()
    {
        _settings.Setup(s => s.CurrentValue).Returns(new AppSettings());
        _fileStore.Setup(s => s.GetFilteredFilesAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _files.Select(file => file.ShallowCopy()).ToList());
        _metadataCache.Setup(m => m.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => _records);

        AddSeries("Alpha", new[] { "Action", "Drama" }, SeriesPublicationStatus.Ongoing);
        AddSeries("Beta", new[] { "action" }, SeriesPublicationStatus.Completed);
        AddSeries("Gamma", new[] { "Comedy" }, SeriesPublicationStatus.Completed);
        AddSeries("Delta", Array.Empty<string>(), null);
    }

    private void AddSeries(string title, string[] genres, string? status, string? canonical = null, int issues = 1)
    {
        var key = title.ToLowerInvariant().Replace(' ', '-');
        _records.Add(new SeriesMetadataCacheRecord
        {
            NormalizedKey = key,
            CanonicalTitle = canonical ?? title,
            LookupStatus = "success",
            Source = "AniListManga",
            Genres = genres.ToList(),
            PublicationStatus = status,
            SiteUrl = "https://example.org/" + key
        });
        for (var issue = 1; issue <= issues; issue++)
        {
            _files.Add(new ComicFile
            {
                FilePath = $"/lib/{key}/{title} {issue:000}.cbz",
                FileName = $"{title} {issue:000}.cbz",
                Directory = $"/lib/{key}",
                FileSize = 10,
                LastModified = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                CreatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                Metadata = new ComicMetadata { Series = title, Issue = issue.ToString() }
            });
        }
    }

    private SeriesLibraryService CreateService() => TestServices.CreateSeriesLibrary(
        _fileStore.Object,
        _metadataCache.Object,
        _settings.Object,
        NullLogger<SeriesLibraryService>.Instance);

    private async Task<IEnumerable<string>> SummariesAsync(SeriesFacetFilter? facets, string? search = null, string? direction = "asc")
    {
        var result = await CreateService().GetSeriesSummariesAsync(null, search, 1, -1, "name", direction, null, null, facets);
        return result.Series.Select(series => series.Title);
    }

    [Fact]
    public async Task GenreFilter_MatchesAnyCaseAndKeepsTheOrder()
    {
        Assert.Equal(new[] { "Alpha", "Beta" }, await SummariesAsync(new SeriesFacetFilter(Genre: "ACTION")));
    }

    [Fact]
    public async Task StatusFilter_MatchesTheStoredStatus()
    {
        Assert.Equal(new[] { "Beta", "Gamma" }, await SummariesAsync(new SeriesFacetFilter(Status: "completed")));
    }

    [Fact]
    public async Task GenreAndStatusCombine()
    {
        Assert.Equal(new[] { "Beta" }, await SummariesAsync(new SeriesFacetFilter("Action", "completed")));
    }

    [Fact]
    public async Task NoDataSelectsSeriesWithoutThatValue()
    {
        Assert.Equal(new[] { "Delta" }, await SummariesAsync(new SeriesFacetFilter(Genre: SeriesFacetFilter.NoData)));
        Assert.Equal(new[] { "Delta" }, await SummariesAsync(new SeriesFacetFilter(Status: SeriesFacetFilter.NoData)));
    }

    [Fact]
    public async Task AnUnknownValueMatchesNothing()
    {
        Assert.Empty(await SummariesAsync(new SeriesFacetFilter(Genre: "Nonexistent")));
        Assert.Empty(await SummariesAsync(new SeriesFacetFilter(Status: "paused")));
    }

    [Fact]
    public async Task NoFilterReturnsEverySeries()
    {
        Assert.Equal(4, (await SummariesAsync(null)).Count());
    }

    [Fact]
    public async Task CombinesWithSearchAndSort()
    {
        Assert.Equal(new[] { "Beta" }, await SummariesAsync(new SeriesFacetFilter(Genre: "action"), search: "bet"));
        Assert.Equal(new[] { "Gamma", "Beta" }, await SummariesAsync(new SeriesFacetFilter(Status: "completed"), direction: "desc"));
    }

    [Fact]
    public async Task TotalsAndPagingApplyToTheFilteredList()
    {
        var service = CreateService();
        var facets = new SeriesFacetFilter(Genre: "action");

        var first = await service.GetSeriesSummariesAsync(null, null, 1, 1, "name", "asc", 0, 1, facets);
        var second = await service.GetSeriesSummariesAsync(null, null, 1, 1, "name", "asc", 1, 1, facets);

        Assert.Equal(2, first.TotalSeries);
        Assert.Equal("Alpha", Assert.Single(first.Series).Title);
        Assert.Equal("Beta", Assert.Single(second.Series).Title);
        Assert.Equal(2, second.TotalPages);
    }

    [Fact]
    public async Task ACachedViewIsNotReusedForOtherFilters()
    {
        _fileStore.As<IChangeVersionSource>().SetupGet(v => v.ChangeVersion).Returns(1);
        _metadataCache.As<IChangeVersionSource>().SetupGet(v => v.ChangeVersion).Returns(1);
        var service = CreateService();

        var action = await service.GetSeriesSummariesAsync(null, null, 1, -1, "name", "asc", null, null, new SeriesFacetFilter(Genre: "action"));
        var comedy = await service.GetSeriesSummariesAsync(null, null, 1, -1, "name", "asc", null, null, new SeriesFacetFilter(Genre: "comedy"));
        var completed = await service.GetSeriesSummariesAsync(null, null, 1, -1, "name", "asc", null, null, new SeriesFacetFilter(Status: "completed"));
        var everything = await service.GetSeriesSummariesAsync(null, null, 1, -1, "name", "asc", null, null, null);

        Assert.Equal(2, action.TotalSeries);
        Assert.Equal(1, comedy.TotalSeries);
        Assert.Equal(2, completed.TotalSeries);
        Assert.Equal(4, everything.TotalSeries);
    }

    [Fact]
    public async Task FullListingAppliesTheFiltersToo()
    {
        var result = await CreateService().GetSeriesAsync(null, null, 1, -1, "name", "asc", new SeriesFacetFilter(Status: "ongoing"));

        var series = Assert.Single(result.Series);
        Assert.Equal("Alpha", series.Title);
        Assert.Equal(new[] { "Action", "Drama" }, series.Genres);
        Assert.Equal(SeriesPublicationStatus.Ongoing, series.PublicationStatus);
        Assert.Equal("https://example.org/alpha", series.SiteUrl);
    }

    [Fact]
    public async Task ASummaryCarriesTheDetails()
    {
        var result = await CreateService().GetSeriesSummariesAsync(search: "Gamma");

        var series = Assert.Single(result.Series);
        Assert.Equal(new[] { "Comedy" }, series.Genres);
        Assert.Equal(SeriesPublicationStatus.Completed, series.PublicationStatus);
        Assert.Equal("https://example.org/gamma", series.SiteUrl);
    }

    [Fact]
    public async Task ASeriesWithNoDataHasAnEmptyState()
    {
        var result = await CreateService().GetSeriesSummariesAsync(search: "Delta");

        var series = Assert.Single(result.Series);
        Assert.Empty(series.Genres);
        Assert.Null(series.PublicationStatus);
    }

    [Fact]
    public async Task ASeriesWithoutARecordHasAnEmptyState()
    {
        _files.Add(new ComicFile
        {
            FilePath = "/lib/plain/Plain 001.cbz",
            FileName = "Plain 001.cbz",
            Directory = "/lib/plain",
            Metadata = new ComicMetadata { Series = "Plain", Issue = "1" }
        });

        var result = await CreateService().GetSeriesSummariesAsync(search: "Plain");

        Assert.Empty(Assert.Single(result.Series).Genres);
    }

    [Fact]
    public async Task OverviewEntriesCarryTheDetails()
    {
        var entries = await CreateService().GetSeriesOverviewEntriesAsync();

        Assert.Equal(new[] { "Comedy" }, entries.Single(e => e.Summary.Title == "Gamma").Summary.Genres);
    }

    [Fact]
    public async Task Facets_CountEachValueAndTheSeriesWithoutData()
    {
        var facets = await CreateService().GetSeriesFacetsAsync();

        Assert.Equal(new[] { "Action", "Comedy", "Drama" }, facets.Genres.Select(genre => genre.Value));
        Assert.Equal(new[] { 2, 1, 1 }, facets.Genres.Select(genre => genre.Count));
        Assert.Equal(new[] { "ongoing", "completed" }, facets.Statuses.Select(status => status.Value));
        Assert.Equal(new[] { 1, 2 }, facets.Statuses.Select(status => status.Count));
        Assert.Equal(1, facets.SeriesWithoutGenres);
        Assert.Equal(1, facets.SeriesWithoutStatus);
    }

    [Fact]
    public async Task Facets_OfALibraryWithNoDataYetAreEmpty()
    {
        foreach (var record in _records)
        {
            record.Genres = new List<string>();
            record.PublicationStatus = null;
        }

        var facets = await CreateService().GetSeriesFacetsAsync();

        Assert.Empty(facets.Genres);
        Assert.Empty(facets.Statuses);
        Assert.Equal(4, facets.SeriesWithoutGenres);
        Assert.Equal(4, facets.SeriesWithoutStatus);
    }

    [Fact]
    public async Task ACollapsedCardKeepsTheDetailsOfTheCardMergedIntoIt()
    {
        _records.Clear();
        _files.Clear();
        AddSeries("Saga", Array.Empty<string>(), null, issues: 2);
        AddSeries("Saga Deluxe", new[] { "Drama" }, SeriesPublicationStatus.Completed, canonical: "Saga");
        _records[0].SiteUrl = null;

        var result = await CreateService().GetSeriesSummariesAsync();

        var series = Assert.Single(result.Series);
        Assert.Equal(3, series.IssueCount);
        Assert.Equal(new[] { "Drama" }, series.Genres);
        Assert.Equal(SeriesPublicationStatus.Completed, series.PublicationStatus);
        Assert.Equal("https://example.org/saga-deluxe", series.SiteUrl);
    }

    [Fact]
    public async Task ACollapsedCardKeepsItsOwnDetailsWhenItHasThem()
    {
        _records.Clear();
        _files.Clear();
        AddSeries("Saga", new[] { "Horror" }, SeriesPublicationStatus.Ongoing, issues: 2);
        AddSeries("Saga Deluxe", new[] { "Drama" }, SeriesPublicationStatus.Completed, canonical: "Saga");

        var result = await CreateService().GetSeriesSummariesAsync();

        var series = Assert.Single(result.Series);
        Assert.Equal(3, series.IssueCount);
        Assert.Equal(new[] { "Horror" }, series.Genres);
        Assert.Equal(SeriesPublicationStatus.Ongoing, series.PublicationStatus);
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData("  ", "\t", true)]
    [InlineData("Action", null, false)]
    [InlineData(null, "completed", false)]
    public void Create_IsNullOnlyWhenNothingIsSet(string? genre, string? status, bool isNull)
    {
        Assert.Equal(isNull, SeriesFacetFilter.Create(genre, status) is null);
    }

    [Fact]
    public void Create_TrimsTheValues()
    {
        Assert.Equal(new SeriesFacetFilter("Action", "completed"), SeriesFacetFilter.Create(" Action ", " completed "));
        Assert.Equal(new SeriesFacetFilter("Action", null), SeriesFacetFilter.Create("Action", "  "));
        Assert.Equal(new SeriesFacetFilter(null, "completed"), SeriesFacetFilter.Create("  ", "completed"));
    }

    [Theory]
    [InlineData("COMPLETED", "completed")]
    [InlineData(" hiatus ", "hiatus")]
    public void Status_NormalizesKnownValues(string input, string expected)
    {
        Assert.Equal(expected, SeriesPublicationStatus.Normalize(input));
    }

    [Theory]
    [InlineData("paused")]
    [InlineData(null)]
    public void Status_RefusesUnknownValues(string? input)
    {
        Assert.Null(SeriesPublicationStatus.Normalize(input));
    }
}
