using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Matching library files to ComicVine issues, filling their tags within a request budget, and
/// reading the story arcs they are part of. ComicVine and the archive writes are mocked; the
/// catalogue lives in a real SQLite database.
/// </summary>
public sealed class ComicVineIssueDetailsServiceTests : IAsyncLifetime
{
    private const int BatmanVolume = 91273;

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private DbContextOptions<ComicMaintainerDbContext> _options = null!;
    private readonly Mock<IFileStoreService> _fileStore = new();
    private readonly Mock<IComicVineVolumeSearch> _search = new();
    private readonly Mock<IComicVineCatalogClient> _client = new();
    private readonly Mock<ISeriesMetadataCacheService> _seriesCache = new();
    private readonly Mock<IComicProcessorService> _processor = new();
    private readonly Mock<IBatchJobRegistry> _jobs = new();
    private readonly List<(string Path, ComicMetadata Metadata, ComicMetadataFieldFlags Fields, ProviderComicInfoFields Fill)> _writes = [];
    private readonly List<ComicFile> _files = [];

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _options = new DbContextOptionsBuilder<ComicMaintainerDbContext>().UseSqlite(_connection).Options;
        await using var db = new ComicMaintainerDbContext(_options);
        await db.Database.EnsureCreatedAsync();

        _fileStore.Setup(f => f.GetAllFilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => _files);
        _fileStore.Setup(f => f.GetFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string path, CancellationToken _) => _files.FirstOrDefault(f => f.FilePath == path));
        _fileStore.Setup(f => f.FileExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string path, CancellationToken _) => _files.Any(f => f.FilePath == path));
        _client.SetupGet(c => c.IsConfigured).Returns(true);
        _search.Setup(s => s.SearchVolumesAsync("Batman", It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new ExternalSeriesMetadata { CanonicalTitle = "Batman", StartYear = 2016, Publisher = "DC Comics", SourceId = BatmanVolume.ToString(), TotalChapters = 158 },
            new ExternalSeriesMetadata { CanonicalTitle = "Batman", StartYear = 1940, Publisher = "DC Comics", SourceId = "796", TotalChapters = 713 },
        ]);
        _client.Setup(c => c.ListVolumeIssuesAsync(BatmanVolume, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComicVineResult<IReadOnlyList<ComicVineIssueSummary>>(
            [
                new ComicVineIssueSummary(101, "1", "I Am Gotham, Part One", "2016-08-01", "Batman meets Gotham.", "https://comicvine.example/101"),
                new ComicVineIssueSummary(102, "2", "I Am Gotham, Part Two", "2016-08-15", null, null),
            ], 1));
        _client.Setup(c => c.GetIssueDetailsAsync(101, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComicVineIssueDetails(101, ["Tom King"], [new ComicVineArcReference(7, "I Am Gotham")]));
        _client.Setup(c => c.GetIssueDetailsAsync(102, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComicVineIssueDetails(102, [], [new ComicVineArcReference(7, "I Am Gotham"), new ComicVineArcReference(8, "Night of the Monster Men")]));
        _processor.Setup(p => p.WriteProviderDetailsAsync(It.IsAny<string>(), It.IsAny<ComicMetadata>(), It.IsAny<ComicMetadataFieldFlags>(), It.IsAny<ProviderComicInfoFields>(), It.IsAny<CancellationToken>()))
            .Callback((string path, ComicMetadata metadata, ComicMetadataFieldFlags fields, ProviderComicInfoFields fill, CancellationToken _) => _writes.Add((path, metadata, fields, fill)))
            .ReturnsAsync((string path, ComicMetadata _, ComicMetadataFieldFlags _, ProviderComicInfoFields _, CancellationToken _) => new MetadataWriteResult(MetadataWriteOutcome.Written, path));
    }

    public Task DisposeAsync() => _connection.DisposeAsync().AsTask();

    private ComicVineIssueDetailsService CreateService() => new(
        new Factory(_options),
        _fileStore.Object,
        _search.Object,
        _client.Object,
        _seriesCache.Object,
        _processor.Object,
        _jobs.Object,
        NullLogger<ComicVineIssueDetailsService>.Instance);

    private ComicFile AddFile(string path, string issue, int? volumeYear = 2016, string series = "Batman", string? format = null, bool duplicate = false)
    {
        var file = new ComicFile
        {
            FilePath = path,
            FileName = Path.GetFileName(path),
            IsDuplicate = duplicate,
            Metadata = new ComicMetadata { Series = series, Issue = issue, Title = $"Chapter {issue}", VolumeYear = volumeYear, Format = format }
        };
        _files.Add(file);
        return file;
    }

    [Fact]
    public async Task RunAsync_LinksTheVolume_ListsItsIssues_FetchesDetails_AndFillsEachFile()
    {
        AddFile("/c/Batman/Batman (2016) - Chapter 0001.cbz", "1");
        AddFile("/c/Batman/Batman (2016) - Chapter 0002.cbz", "002");

        var summary = await CreateService().RunAsync(null, 20);

        Assert.Equal(new ComicVineDetailsRunSummary(2, 2, 0, 0, 0, 0, 0, 1 + 1 + 2, false), summary);
        Assert.Contains("2 files filled", summary.ToString());
        var first = _writes.Single(w => w.Path.EndsWith("0001.cbz"));
        Assert.Equal("I Am Gotham, Part One", first.Metadata.Title);
        Assert.Equal(2016, first.Metadata.Year);
        Assert.Equal(["Tom King"], first.Metadata.Authors);
        Assert.Equal("I Am Gotham", first.Fill.StoryArc);
        Assert.Equal(8, first.Fill.Month);
        Assert.Equal("I Am Gotham, Night of the Monster Men", _writes.Single(w => w.Path.EndsWith("0002.cbz")).Fill.StoryArc);

        await using var db = new ComicMaintainerDbContext(_options);
        var link = await db.ComicVineVolumeLinks.SingleAsync();
        Assert.Equal(("batman", 2016, (int?)BatmanVolume, "DC Comics"), (link.SeriesKey, link.StartYear, link.VolumeId, link.Publisher));
        Assert.Equal(2, await db.ComicVineFileStates.CountAsync());
    }

    [Fact]
    public async Task RunAsync_ASecondRun_AsksComicVineNothing_AndWritesNothing()
    {
        AddFile("/c/Batman/Batman (2016) - Chapter 0001.cbz", "1");
        await CreateService().RunAsync(null, 20);
        _writes.Clear();
        _search.Invocations.Clear();
        _client.Invocations.Clear();

        var summary = await CreateService().RunAsync(null, 20);

        Assert.Equal((0, 1, 0), (summary.Filled, summary.Unchanged, summary.Requests));
        Assert.Empty(_writes);
        Assert.Empty(_search.Invocations);
        Assert.Empty(_client.Invocations);
    }

    [Fact]
    public async Task RunAsync_ATruncatedIssueList_IsListedAgainByTheNextRun()
    {
        AddFile("/c/Batman/Batman (2016) - Chapter 0001.cbz", "1");
        var page = Enumerable.Range(0, 300)
            .Select(i => new ComicVineIssueSummary(200 + i, (i + 1).ToString(), null, null, null, null)).ToList();
        _client.Setup(c => c.ListVolumeIssuesAsync(BatmanVolume, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComicVineResult<IReadOnlyList<ComicVineIssueSummary>>(page, 3));

        // One search page and three pages of issues: the budget ends on a full page, so more exist.
        var first = await CreateService().RunAsync(null, 4);
        Assert.True(first.StoppedEarly);

        await CreateService().RunAsync(null, 20);
        _client.Verify(c => c.ListVolumeIssuesAsync(BatmanVolume, It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task RunAsync_OutOfRequests_FillsFromTheListAndLeavesDetailsForNextRun()
    {
        AddFile("/c/Batman/Batman (2016) - Chapter 0001.cbz", "1");
        _client.Setup(c => c.ListVolumeIssuesAsync(BatmanVolume, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComicVineResult<IReadOnlyList<ComicVineIssueSummary>>(
                [new ComicVineIssueSummary(101, "1", "I Am Gotham, Part One", "2016-08-01", null, null)], 2));

        // Three requests: one search page and two pages of issues, none left for the issue's details.
        var summary = await CreateService().RunAsync(null, 3);

        Assert.Equal((1, 3), (summary.Filled, summary.Requests));
        _client.Verify(c => c.GetIssueDetailsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Null(_writes.Single().Fill.StoryArc);

        // The next run fetches them and writes the file again.
        _writes.Clear();
        var next = await CreateService().RunAsync(null, 20);
        Assert.Equal((1, 1), (next.Filled, next.Requests));
        Assert.Equal("I Am Gotham", _writes.Single().Fill.StoryArc);
    }

    [Fact]
    public async Task RunAsync_TooFewRequestsToSearch_StopsEarly()
    {
        AddFile("/c/Batman/Batman (2016) - Chapter 0001.cbz", "1");

        var summary = await CreateService().RunAsync(null, 2);

        Assert.Equal((1, true, 0), (summary.NoVolume, summary.StoppedEarly, summary.Requests));
        Assert.Contains("the rest continue next run", summary.ToString());
    }

    [Fact]
    public async Task RunAsync_ComicVineStopsAnswering_StopsAskingForTheRestOfTheRun()
    {
        AddFile("/c/Batman/Batman (2016) - Chapter 0001.cbz", "1");
        AddFile("/c/Batman/Batman (2016) - Chapter 0002.cbz", "2");
        _client.Setup(c => c.ListVolumeIssuesAsync(BatmanVolume, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ExternalMetadataUnavailableException("ComicVine", ExternalMetadataFailureKind.RateLimited, "budget used up"));

        var summary = await CreateService().RunAsync(null, 20);

        Assert.True(summary.StoppedEarly);
        Assert.Equal(1, summary.Failed);
        Assert.Equal(1, summary.NoVolume + summary.NoIssue + summary.Failed - 1);
        _client.Verify(c => c.ListVolumeIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_SkipsDuplicates_AnnualsAndFilesWithoutASeriesOrNumber()
    {
        AddFile("/c/a.cbz", "1", duplicate: true);
        AddFile("/c/b.cbz", "1", format: "Annual");
        AddFile("/c/c.cbz", "");
        _files.Add(new ComicFile { FilePath = "/c/d.cbz", FileName = "d.cbz" });

        var summary = await CreateService().RunAsync(null, 20);

        Assert.Equal((4, 4, 0), (summary.Files, summary.Skipped, summary.Requests));
    }

    [Fact]
    public async Task RunAsync_IssueComicVineDoesNotHave_IsReported()
    {
        AddFile("/c/Batman/Batman (2016) - Chapter 0099.cbz", "99");

        var summary = await CreateService().RunAsync(["/c/Batman/Batman (2016) - Chapter 0099.cbz", "/c/missing.cbz"], 20);

        Assert.Equal((1, 1), (summary.Files, summary.NoIssue));
    }

    [Fact]
    public async Task RunAsync_VolumeComicVineDoesNotHave_IsNotSearchedAgainForAMonth()
    {
        AddFile("/c/Batman/Batman (2003) - Chapter 0001.cbz", "1", volumeYear: 2003);

        Assert.Equal(1, (await CreateService().RunAsync(null, 20)).NoVolume);
        Assert.Equal(1, (await CreateService().RunAsync(null, 20)).NoVolume);

        _search.Verify(s => s.SearchVolumesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_WithoutAStartYear_UsesTheSeriesComicVineMatch()
    {
        AddFile("/c/Saga/Saga - Chapter 0001.cbz", "1", volumeYear: null, series: "Saga");
        AddFile("/c/Monstress/Monstress - Chapter 0001.cbz", "1", volumeYear: null, series: "Monstress");
        _seriesCache.Setup(c => c.GetByTitleAsync("Saga", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesMetadataCacheRecord { CanonicalTitle = "Saga", Source = "ComicVine", SourceId = "48934", LookupStatus = "success", Synopsis = "Saga synopsis." });
        _seriesCache.Setup(c => c.GetByTitleAsync("Monstress", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesMetadataCacheRecord { CanonicalTitle = "Monstress", Source = "AniList", SourceId = "1", LookupStatus = "success" });
        _client.Setup(c => c.ListVolumeIssuesAsync(48934, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComicVineResult<IReadOnlyList<ComicVineIssueSummary>>([new ComicVineIssueSummary(201, "1", "Chapter One", "2012-03-14", null, null)], 1));

        var summary = await CreateService().RunAsync(null, 20);

        Assert.Equal((1, 1), (summary.Filled, summary.NoVolume));
        _search.Verify(s => s.SearchVolumesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_AFailedWrite_IsCountedAndRetriedNextRun()
    {
        AddFile("/c/Batman/Batman (2016) - Chapter 0001.cbz", "1");
        _processor.Setup(p => p.WriteProviderDetailsAsync(It.IsAny<string>(), It.IsAny<ComicMetadata>(), It.IsAny<ComicMetadataFieldFlags>(), It.IsAny<ProviderComicInfoFields>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string path, ComicMetadata _, ComicMetadataFieldFlags _, ProviderComicInfoFields _, CancellationToken _) => MetadataWriteResult.Failed(path));

        Assert.Equal(1, (await CreateService().RunAsync(null, 20)).Failed);
        Assert.Equal(1, (await CreateService().RunAsync(null, 20)).Failed);
    }

    [Fact]
    public async Task StartJob_FillsEachFileAsAJobItem()
    {
        AddFile("/c/Batman/Batman (2016) - Chapter 0001.cbz", "1");
        BatchJobDefinition? definition = null;
        var jobId = Guid.NewGuid();
        _jobs.Setup(j => j.StartJob(It.IsAny<BatchJobDefinition>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Callback((BatchJobDefinition d, IEnumerable<string> _, CancellationToken _) => definition = d)
            .Returns(jobId);

        Assert.Equal(jobId, CreateService().StartJob(["/c/Batman/Batman (2016) - Chapter 0001.cbz", "/c/gone.cbz"]));

        Assert.True((await definition!.ItemOperation("/c/Batman/Batman (2016) - Chapter 0001.cbz", CancellationToken.None)).Success);
        Assert.Equal(new BatchItemResult(false, "File not found"), await definition.ItemOperation("/c/gone.cbz", CancellationToken.None));
        Assert.Single(_writes);

        _processor.Setup(p => p.WriteProviderDetailsAsync(It.IsAny<string>(), It.IsAny<ComicMetadata>(), It.IsAny<ComicMetadataFieldFlags>(), It.IsAny<ProviderComicInfoFields>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string path, ComicMetadata _, ComicMetadataFieldFlags _, ProviderComicInfoFields _, CancellationToken _) => MetadataWriteResult.Failed(path));
        AddFile("/c/Batman/Batman (2016) - Chapter 0002.cbz", "2");
        Assert.Equal(
            new BatchItemResult(false, "The archive could not be written"),
            await definition.ItemOperation("/c/Batman/Batman (2016) - Chapter 0002.cbz", CancellationToken.None));
    }

    [Fact]
    public async Task GetStoryArcsAsync_GroupsTheFilesByArc()
    {
        AddFile("/c/Batman/Batman (2016) - Chapter 0001.cbz", "1");
        AddFile("/c/Batman/Batman (2016) - Chapter 0002.cbz", "2");
        var service = CreateService();
        await service.RunAsync(null, 20);

        var arcs = await service.GetStoryArcsAsync(["/c/Batman/Batman (2016) - Chapter 0001.cbz", "/c/Batman/Batman (2016) - Chapter 0002.cbz", " "]);

        Assert.Equal(
            ["I Am Gotham: 0001.cbz, 0002.cbz", "Night of the Monster Men: 0002.cbz"],
            arcs.Select(arc => $"{arc.Name}: {string.Join(", ", arc.FilePaths.Select(p => p[^8..]))}"));
        Assert.Empty(await service.GetStoryArcsAsync([]));
    }

    [Fact]
    public async Task GetStoryArcAsync_FetchesTheArcOnce_AndOffersTheFilesThatHoldItsIssues()
    {
        AddFile("/c/Batman/Batman (2016) - Chapter 0001.cbz", "1");
        var service = CreateService();
        await service.RunAsync(null, 20);
        _client.Setup(c => c.GetStoryArcAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(new ComicVineStoryArc(
            7, "I Am Gotham", "Gotham and Gotham Girl.", "https://comicvine.example/arc-7",
            [new ComicVineArcIssue(101, "Part One", "1", "Batman"), new ComicVineArcIssue(102, "Part Two", "2", "Batman")]));

        var arc = await service.GetStoryArcAsync(7);
        var again = await service.GetStoryArcAsync(7);

        Assert.Equal("I Am Gotham", arc!.Name);
        Assert.Equal(
            [new StoryArcEntry(101, "Part One", "1", "Batman", "/c/Batman/Batman (2016) - Chapter 0001.cbz"), new StoryArcEntry(102, "Part Two", "2", "Batman", null)],
            arc.Issues);
        Assert.Equal(arc.Issues, again!.Issues);
        _client.Verify(c => c.GetStoryArcAsync(7, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Null(await service.GetStoryArcAsync(99));
    }

    [Fact]
    public void SeriesKey_IgnoresTheVolumeAnnualCaseAndPunctuation()
    {
        Assert.Equal("allnewxmen", ComicVineIssueDetailsService.SeriesKey("All-New X-Men (2016)"));
        Assert.Equal("greenlantern", ComicVineIssueDetailsService.SeriesKey("Green Lantern Annual"));
    }

    private sealed class Factory(DbContextOptions<ComicMaintainerDbContext> options) : IDbContextFactory<ComicMaintainerDbContext>
    {
        public ComicMaintainerDbContext CreateDbContext() => new(options);
    }
}
