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
        _search.Setup(s => s.ListVolumesNamedAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComicVineResult<IReadOnlyList<ExternalSeriesMetadata>>([], 0));
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

    private ComicFile AddFile(string path, string issue, int? volumeYear = 2016, string series = "Batman", string? format = null, bool duplicate = false, string? volume = null)
    {
        var file = new ComicFile
        {
            FilePath = path,
            FileName = Path.GetFileName(path),
            IsDuplicate = duplicate,
            Metadata = new ComicMetadata { Series = series, Issue = issue, Title = $"Chapter {issue}", VolumeYear = volumeYear, Format = format, Volume = volume }
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
    public async Task RunAsync_AVolumeNumberWithoutAStartYear_FindsThePublishersNthVolume()
    {
        // Batman Vol. 2 with no start year: DC's second "Batman", started 2016 here (the mocked
        // search lists 1940 and 2016).
        AddFile("/c/Batman/Batman Vol. 2 - Chapter 0001.cbz", "1", volumeYear: null, volume: "2");

        var summary = await CreateService().RunAsync(null, 20);

        Assert.Equal(1, summary.Filled);
        Assert.Equal("I Am Gotham, Part One", _writes.Single().Metadata.Title);
        await using var db = new ComicMaintainerDbContext(_options);
        var link = await db.ComicVineVolumeLinks.SingleAsync();
        Assert.Equal((0, 2, (int?)BatmanVolume), (link.StartYear, link.VolumeNumber, link.VolumeId));
    }

    [Fact]
    public async Task RunAsync_AYearWrittenInVolume_CountsAsTheStartYear()
    {
        AddFile("/c/Batman/Batman (2016) - Chapter 0001.cbz", "1", volumeYear: null, volume: "2016");

        Assert.Equal(1, (await CreateService().RunAsync(null, 20)).Filled);
    }

    [Fact]
    public async Task RunAsync_MatchingTheSeriesToComicVine_TakesEffectOnTheNextRun()
    {
        // No start year or volume number: the series' own match is used, and read afresh each run.
        AddFile("/c/Saga/Saga - Chapter 0001.cbz", "1", volumeYear: null, series: "Saga");
        _client.Setup(c => c.ListVolumeIssuesAsync(48934, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComicVineResult<IReadOnlyList<ComicVineIssueSummary>>([new ComicVineIssueSummary(201, "1", "Chapter One", "2012-03-14", null, null)], 1));

        Assert.Equal(1, (await CreateService().RunAsync(null, 20)).NoVolume);

        _seriesCache.Setup(c => c.GetByTitleAsync("Saga", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesMetadataCacheRecord { CanonicalTitle = "Saga", Source = "ComicVine", SourceId = "48934", LookupStatus = "manual_match" });

        Assert.Equal(1, (await CreateService().RunAsync(null, 20)).Filled);
    }

    [Fact]
    public async Task StartJob_SearchesAgainForAVolumeNotFoundBefore_AndSaysWhyAFileWasNotFilled()
    {
        AddFile("/c/Batman/Batman (2003) - Chapter 0001.cbz", "1", volumeYear: 2003);
        AddFile("/c/Batman/Batman Vol. 7 - Chapter 0001.cbz", "1", volumeYear: null, volume: "7");
        AddFile("/c/Batman/Batman (2016) - Chapter 0099.cbz", "99");
        AddFile("/c/Saga/Saga - Chapter 0001.cbz", "1", volumeYear: null, series: "Saga");
        AddFile("/c/Batman/Batman Annual - Chapter 0001.cbz", "1", format: "Annual");
        await CreateService().RunAsync(null, 20);
        _search.Invocations.Clear();
        BatchJobDefinition? definition = null;
        _jobs.Setup(j => j.StartJob(It.IsAny<BatchJobDefinition>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Callback((BatchJobDefinition d, IEnumerable<string> _, CancellationToken _) => definition = d)
            .Returns(Guid.NewGuid());
        CreateService().StartJob(["/c/Batman/Batman (2003) - Chapter 0001.cbz"]);

        Assert.Equal(
            new BatchItemResult(false, "ComicVine has no Batman volume that started in 2003"),
            await definition!.ItemOperation("/c/Batman/Batman (2003) - Chapter 0001.cbz", CancellationToken.None));
        _search.Verify(s => s.SearchVolumesAsync("Batman", It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(
            new BatchItemResult(false, "ComicVine has no Batman Vol. 7"),
            await definition.ItemOperation("/c/Batman/Batman Vol. 7 - Chapter 0001.cbz", CancellationToken.None));
        Assert.Equal(
            new BatchItemResult(false, $"ComicVine's Batman (volume {BatmanVolume}) has no issue #99"),
            await definition.ItemOperation("/c/Batman/Batman (2016) - Chapter 0099.cbz", CancellationToken.None));
        Assert.Equal(
            new BatchItemResult(false, "The issue's volume is not known and Saga has no ComicVine match: set its volume number or start year, or match the series to ComicVine"),
            await definition.ItemOperation("/c/Saga/Saga - Chapter 0001.cbz", CancellationToken.None));
        Assert.True((await definition.ItemOperation("/c/Batman/Batman Annual - Chapter 0001.cbz", CancellationToken.None)).Success);
    }

    [Fact]
    public async Task StartJob_OutOfRequestsToSearch_SaysTheNextRunContinues()
    {
        AddFile("/c/Batman/Batman (2016) - Chapter 0001.cbz", "1");
        BatchJobDefinition? definition = null;
        _jobs.Setup(j => j.StartJob(It.IsAny<BatchJobDefinition>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Callback((BatchJobDefinition d, IEnumerable<string> _, CancellationToken _) => definition = d)
            .Returns(Guid.NewGuid());
        _search.Setup(s => s.SearchVolumesAsync("Batman", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Range(0, 200).Select(i => new ExternalSeriesMetadata { CanonicalTitle = "Other", StartYear = 1900 + i }).ToList());
        CreateService().StartJob(["/c/Batman/Batman (2016) - Chapter 0001.cbz"]);

        // Each search of 200 results counts three requests; the job allows 250.
        for (var i = 0; i < 83; i++)
        {
            AddFile($"/c/Series{i}/Series{i} (2016) - Chapter 0001.cbz", "1", series: $"Batman{i}");
            _search.Setup(s => s.SearchVolumesAsync($"Batman{i}", It.IsAny<CancellationToken>()))
                .ReturnsAsync(Enumerable.Range(0, 200).Select(j => new ExternalSeriesMetadata { CanonicalTitle = "Other", StartYear = 1900 + j }).ToList());
            await definition!.ItemOperation($"/c/Series{i}/Series{i} (2016) - Chapter 0001.cbz", CancellationToken.None);
        }

        Assert.Equal(
            new BatchItemResult(false, "No ComicVine requests were left to look up the issue's volume; the next run continues"),
            await definition!.ItemOperation("/c/Batman/Batman (2016) - Chapter 0001.cbz", CancellationToken.None));
    }

    [Fact]
    public async Task GetCoverCandidatesAsync_ListsTheVolumesCovers_WithTheFilesIssueMatched()
    {
        AddFile("/c/Batman/Batman (2016) - Chapter 0002.cbz", "2");
        _client.Setup(c => c.ListVolumeIssuesAsync(BatmanVolume, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComicVineResult<IReadOnlyList<ComicVineIssueSummary>>(
            [
                new ComicVineIssueSummary(110, "10", "Ten", "2016-12-01", null, null, "https://cv.example/110.jpg", "https://cv.example/110-m.jpg"),
                new ComicVineIssueSummary(102, "2", "Two", "2016-08-15", null, "https://cv.example/102", "https://cv.example/102.jpg", null),
                new ComicVineIssueSummary(101, "1", "One", "2016-08-01", null, null, null, null),
                new ComicVineIssueSummary(199, "1/2", "Half", null, null, null, "https://cv.example/199.jpg", null),
            ], 1));

        var candidates = await CreateService().GetCoverCandidatesAsync("/c/Batman/Batman (2016) - Chapter 0002.cbz");

        Assert.Equal((BatmanVolume, 102, null), (candidates!.VolumeId, candidates.MatchedIssueId, candidates.Reason));
        Assert.Equal(["2", "10", "1/2"], candidates.Covers.Select(c => c.IssueNumber));
        Assert.Equal(new CoverCandidate(102, "2", "Two", "2016-08-15", "https://cv.example/102.jpg", null, "https://cv.example/102"), candidates.Covers[0]);
        Assert.Null(await CreateService().GetCoverCandidatesAsync("/c/missing.cbz"));
    }

    [Fact]
    public async Task GetCoverCandidatesAsync_ListsAgainAVolumeListedBeforeCoversWereKept()
    {
        AddFile("/c/Batman/Batman (2016) - Chapter 0001.cbz", "1");
        await CreateService().RunAsync(null, 20);
        _client.Invocations.Clear();
        _client.Setup(c => c.ListVolumeIssuesAsync(BatmanVolume, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComicVineResult<IReadOnlyList<ComicVineIssueSummary>>(
                [new ComicVineIssueSummary(101, "1", "I Am Gotham, Part One", "2016-08-01", "Batman meets Gotham.", "https://comicvine.example/101", "https://cv.example/101.jpg", null)], 1));

        var candidates = await CreateService().GetCoverCandidatesAsync("/c/Batman/Batman (2016) - Chapter 0001.cbz");

        Assert.Equal("https://cv.example/101.jpg", candidates!.Covers.Single().CoverUrl);
        _client.Verify(c => c.ListVolumeIssuesAsync(BatmanVolume, It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetCoverCandidatesAsync_SaysWhyThereAreNone()
    {
        AddFile("/c/a.cbz", "1", format: "Annual");
        AddFile("/c/b.cbz", "1", volumeYear: 2003);
        AddFile("/c/c.cbz", "99");
        AddFile("/c/d.cbz", "2");
        _files.Add(new ComicFile { FilePath = "/c/e.cbz", FileName = "e.cbz" });
        var service = CreateService();

        Assert.Equal("Annuals, Specials, Giant-Size issues and One-Shots are not matched to ComicVine yet", (await service.GetCoverCandidatesAsync("/c/a.cbz"))!.Reason);
        Assert.Equal("ComicVine has no Batman volume that started in 2003", (await service.GetCoverCandidatesAsync("/c/b.cbz"))!.Reason);
        Assert.Equal("ComicVine's Batman has no issue #99; pick its cover from the others", (await service.GetCoverCandidatesAsync("/c/c.cbz"))!.Reason);
        Assert.Equal("ComicVine has no cover for this issue", (await service.GetCoverCandidatesAsync("/c/d.cbz"))!.Reason);
        Assert.Equal("The issue has no series name to look up on ComicVine", (await service.GetCoverCandidatesAsync("/c/e.cbz"))!.Reason);

        _client.Setup(c => c.ListVolumeIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ExternalMetadataUnavailableException("ComicVine", ExternalMetadataFailureKind.RateLimited, "budget used up"));
        AddFile("/c/f.cbz", "1", volumeYear: null, volume: "1");
        _search.Setup(s => s.SearchVolumesAsync("Batman", It.IsAny<CancellationToken>())).ReturnsAsync(
            [new ExternalSeriesMetadata { CanonicalTitle = "Batman", StartYear = 1940, Publisher = "DC Comics", SourceId = "796", TotalChapters = 713 }]);
        Assert.Contains("ComicVine", (await CreateService().GetCoverCandidatesAsync("/c/f.cbz"))!.Reason);
    }

    private void SetUpAllNewXMen()
    {
        // Marvel's "All-New X-Men" on ComicVine: the 2013 volume (41 issues), a 4-issue listing of
        // collected editions (2014) and the 2016 volume (19 issues).
        _search.Setup(s => s.SearchVolumesAsync("All-New X-Men", It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new ExternalSeriesMetadata { CanonicalTitle = "All-New X-Men", StartYear = 2013, Publisher = "Marvel", SourceId = "53919", TotalChapters = 41 },
            new ExternalSeriesMetadata { CanonicalTitle = "All-New X-Men", StartYear = 2014, Publisher = "Marvel", SourceId = "77305", TotalChapters = 4 },
            new ExternalSeriesMetadata { CanonicalTitle = "All-New X-Men", StartYear = 2016, Publisher = "Marvel", SourceId = "88888", TotalChapters = 19 },
        ]);
        _client.Setup(c => c.ListVolumeIssuesAsync(77305, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComicVineResult<IReadOnlyList<ComicVineIssueSummary>>(
                Enumerable.Range(1, 4).Select(n => new ComicVineIssueSummary(7700 + n, n.ToString(), $"Collected Vol. {n}", "2014-06-01", null, null)).ToList(), 1));
        _client.Setup(c => c.ListVolumeIssuesAsync(88888, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComicVineResult<IReadOnlyList<ComicVineIssueSummary>>(
                Enumerable.Range(1, 19).Select(n => new ComicVineIssueSummary(8800 + n, n.ToString(), $"Issue {n}", "2016-02-01", null, null)).ToList(), 1));
        _client.Setup(c => c.GetIssueDetailsAsync(It.IsInRange(7700, 8900, Moq.Range.Inclusive), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int id, CancellationToken _) => new ComicVineIssueDetails(id, [], []));
        for (var n = 1; n <= 19; n++)
        {
            AddFile($"/c/ANXM/All-New X-Men - Vol. 2 Chapter {n:0000}.cbz", n.ToString(), volumeYear: null, series: "All-New X-Men", volume: "2");
        }
    }

    [Fact]
    public async Task RunAsync_Vol2_SkipsAComicVineVolumeTooShortForTheIssuesHeld()
    {
        SetUpAllNewXMen();

        var summary = await CreateService().RunAsync(null, 100);

        Assert.Equal(19, summary.Filled);
        Assert.Equal("Issue 5", _writes.Single(w => w.Path.EndsWith("0005.cbz")).Metadata.Title);
        await using var db = new ComicMaintainerDbContext(_options);
        Assert.Equal(88888, (await db.ComicVineVolumeLinks.SingleAsync()).VolumeId);
    }

    [Fact]
    public async Task RunAsync_AVolumeStoredBeforeTooShortForTheIssuesHeld_IsFoundAgain_AndItsWritesReplaced()
    {
        SetUpAllNewXMen();
        // Matched before this check: Vol. 2 linked to the 4-issue listing, and #1 filled from it.
        await using (var db = new ComicMaintainerDbContext(_options))
        {
            db.ComicVineVolumeLinks.Add(new ComicVineVolumeLinkEntity { SeriesKey = "allnewxmen", VolumeNumber = 2, VolumeId = 77305, LinkedUtc = DateTime.UtcNow, IssuesListedUtc = DateTime.UtcNow });
            db.ComicVineIssues.AddRange(Enumerable.Range(1, 4).Select(n => new ComicVineIssueEntity { Id = 7700 + n, VolumeId = 77305, IssueNumber = n.ToString(), Name = $"Collected Vol. {n}", ListedUtc = DateTime.UtcNow, DetailsUtc = DateTime.UtcNow }));
            db.ComicVineFileStates.Add(new ComicVineFileStateEntity { FilePath = "/c/ANXM/All-New X-Men - Vol. 2 Chapter 0001.cbz", IssueId = 7701, AppliedUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        _files.Single(f => f.FilePath.EndsWith("0001.cbz")).Metadata!.Title = "Collected Vol. 1";

        await CreateService().RunAsync(null, 100);

        Assert.Equal("Issue 1", _writes.Single(w => w.Path.EndsWith("0001.cbz")).Metadata.Title);
        await using var check = new ComicMaintainerDbContext(_options);
        Assert.Equal(88888, (await check.ComicVineVolumeLinks.SingleAsync()).VolumeId);
        Assert.Equal(8801, (await check.ComicVineFileStates.SingleAsync(s => s.FilePath.EndsWith("0001.cbz"))).IssueId);
    }

    [Fact]
    public async Task GetCoverCandidatesAsync_Vol2_ListsTheCoversOfTheRightVolume()
    {
        SetUpAllNewXMen();

        var candidates = await CreateService().GetCoverCandidatesAsync("/c/ANXM/All-New X-Men - Vol. 2 Chapter 0005.cbz");

        Assert.Equal(88888, candidates!.VolumeId);
    }

    private static ComicVineIssue TaggedIssue(int id, string number, int volumeId = 4242, string volumeName = "Batman")
        => new(
            new ComicVineIssueSummary(id, number, $"Tagged {number}", "2016-09-01", "Tagged summary.", $"https://comicvine.gamespot.com/batman-{number}/4000-{id}/",
                $"https://comicvine.example/covers/{id}.jpg", $"https://comicvine.example/covers/{id}-thumb.jpg"),
            volumeId,
            volumeName,
            new ComicVineIssueDetails(id, ["Tom King"], [new ComicVineArcReference(9, "Tagged Arc")]));

    [Fact]
    public async Task RunAsync_AFileWhoseTagsNameItsIssue_IsFilledFromIt_WithoutMatching()
    {
        // No year and no number, and the series has no ComicVine match: matching could not find it.
        var file = AddFile("/c/Batman/Batman - Chapter 0003.cbz", "3", volumeYear: null, volume: "3");
        file.Metadata!.ComicVineIssueId = 555;
        _client.Setup(c => c.GetIssueAsync(555, It.IsAny<CancellationToken>())).ReturnsAsync(TaggedIssue(555, "3"));

        var summary = await CreateService().RunAsync(null, 20);

        Assert.Equal(1, summary.Filled);
        Assert.Equal(1, summary.Requests);
        var write = Assert.Single(_writes);
        Assert.Equal("Tagged 3", write.Metadata.Title);
        Assert.Equal(555, write.Fill.ComicVineIssueId);
        Assert.False(write.Fill.ReplacesComicVineIssueId);
        Assert.Equal("Tagged Arc", write.Fill.StoryArc);
        _search.Verify(s => s.SearchVolumesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _client.Verify(c => c.GetIssueDetailsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);

        // The series' Vol. 3 is linked to the issue's volume, so its other issues are found there.
        await using var db = new ComicMaintainerDbContext(_options);
        var link = Assert.Single(db.ComicVineVolumeLinks);
        Assert.Equal(("batman", 0, 3, 4242, "Batman"), (link.SeriesKey, link.StartYear, link.VolumeNumber, link.VolumeId, link.VolumeName));
        Assert.Equal(555, Assert.Single(db.ComicVineFileStates).IssueId);
        Assert.NotNull(db.ComicVineIssues.Single(issue => issue.Id == 555).DetailsUtc);

        // The next run reads the stored issue and asks ComicVine nothing.
        var again = await CreateService().RunAsync(null, 20);
        Assert.Equal((0, 1, 0), (again.Filled, again.Unchanged, again.Requests));
    }

    [Fact]
    public async Task RunAsync_ATaggedAnnual_IsFilled_ButLinksNoVolume()
    {
        var file = AddFile("/c/Batman/Batman Annual - Chapter 0001.cbz", "1", format: "Annual");
        file.Metadata!.ComicVineIssueId = 556;
        _client.Setup(c => c.GetIssueAsync(556, It.IsAny<CancellationToken>())).ReturnsAsync(TaggedIssue(556, "1", volumeId: 9999, volumeName: "Batman Annual"));

        var summary = await CreateService().RunAsync(null, 20);

        Assert.Equal(1, summary.Filled);
        Assert.Equal(556, Assert.Single(_writes).Fill.ComicVineIssueId);
        await using var db = new ComicMaintainerDbContext(_options);
        Assert.Empty(db.ComicVineVolumeLinks);
    }

    [Theory]
    // The tagged issue has another number than the file: tags copied from another file.
    [InlineData(true)]
    // ComicVine has no such issue.
    [InlineData(false)]
    public async Task RunAsync_ATagThatCannotBeTrusted_IsMatchedInstead_AndTheTagReplaced(bool otherNumber)
    {
        var file = AddFile("/c/Batman/Batman (2016) - Chapter 0001.cbz", "1");
        file.Metadata!.ComicVineIssueId = 557;
        _client.Setup(c => c.GetIssueAsync(557, It.IsAny<CancellationToken>())).ReturnsAsync(otherNumber ? TaggedIssue(557, "7") : null);

        var summary = await CreateService().RunAsync(null, 20);

        Assert.Equal(1, summary.Filled);
        var write = Assert.Single(_writes);
        Assert.Equal("I Am Gotham, Part One", write.Metadata.Title);
        Assert.Equal(101, write.Fill.ComicVineIssueId);
        Assert.True(write.Fill.ReplacesComicVineIssueId);
    }

    [Fact]
    public async Task RunAsync_NoRequestsLeftToReadATaggedIssue_StopsEarly()
    {
        var file = AddFile("/c/Batman/Batman Annual - Chapter 0001.cbz", "1", format: "Annual");
        file.Metadata!.ComicVineIssueId = 558;

        var summary = await CreateService().RunAsync(null, 0);

        Assert.Equal((1, true), (summary.Skipped, summary.StoppedEarly));
        _client.Verify(c => c.GetIssueAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_TheRankedSearchMissesTheVolume_ItIsFoundInTheListByName()
    {
        AddFile("/c/Batman/Batman (2011) - Chapter 0001.cbz", "1", volumeYear: 2011);
        _search.Setup(s => s.ListVolumesNamedAsync("Batman", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComicVineResult<IReadOnlyList<ExternalSeriesMetadata>>(
                [new ExternalSeriesMetadata { CanonicalTitle = "Batman", StartYear = 2011, Publisher = "DC Comics", SourceId = "42721", TotalChapters = 52 }], 2));
        _client.Setup(c => c.ListVolumeIssuesAsync(42721, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComicVineResult<IReadOnlyList<ComicVineIssueSummary>>([new ComicVineIssueSummary(4201, "1", "Knightfall", "2011-11-01", null, null)], 1));
        _client.Setup(c => c.GetIssueDetailsAsync(4201, It.IsAny<CancellationToken>())).ReturnsAsync(new ComicVineIssueDetails(4201, [], []));

        var summary = await CreateService().RunAsync(null, 20);

        Assert.Equal(1, summary.Filled);
        Assert.Equal("Knightfall", Assert.Single(_writes).Metadata.Title);
        // One search page, two list pages, one issue list, one issue's details.
        Assert.Equal(1 + 2 + 1 + 1, summary.Requests);
        _search.Verify(s => s.ListVolumesNamedAsync("Batman", 5, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_TheRankedSearchFindsTheVolume_TheListByNameIsNotRead()
    {
        AddFile("/c/Batman/Batman (2016) - Chapter 0001.cbz", "1");

        await CreateService().RunAsync(null, 20);

        _search.Verify(s => s.ListVolumesNamedAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetCoverCandidatesAsync_ATaggedIssue_ShowsItsVolumesCovers_WithItChosen()
    {
        var file = AddFile("/c/Batman/Batman Annual - Chapter 0001.cbz", "1", format: "Annual");
        file.Metadata!.ComicVineIssueId = 559;
        _client.Setup(c => c.GetIssueAsync(559, It.IsAny<CancellationToken>())).ReturnsAsync(TaggedIssue(559, "1", volumeId: 9999, volumeName: "Batman Annual"));
        _client.Setup(c => c.ListVolumeIssuesAsync(9999, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComicVineResult<IReadOnlyList<ComicVineIssueSummary>>(
            [
                new ComicVineIssueSummary(559, "1", "Tagged 1", "2016-09-01", null, null, "https://comicvine.example/covers/559.jpg"),
                new ComicVineIssueSummary(560, "2", "Annual Two", "2017-09-01", null, null, "https://comicvine.example/covers/560.jpg"),
            ], 1));

        var candidates = await CreateService().GetCoverCandidatesAsync(file.FilePath);

        Assert.NotNull(candidates);
        Assert.Equal((9999, "Batman Annual", 559, (string?)null), (candidates!.VolumeId!.Value, candidates.VolumeName, candidates.MatchedIssueId!.Value, candidates.Reason));
        Assert.Equal([559, 560], candidates.Covers.Select(cover => cover.IssueId));
    }

    [Fact]
    public async Task GetIssueLinksAsync_ATaggedIssue_LinksToIt_WhenStoredWithTheFilesNumber()
    {
        var tagged = AddFile("/c/Batman/Batman Annual - Chapter 0001.cbz", "1", format: "Annual");
        tagged.Metadata!.ComicVineIssueId = 561;
        var mismatched = AddFile("/c/Batman/Batman Annual - Chapter 0002.cbz", "2", format: "Annual");
        mismatched.Metadata!.ComicVineIssueId = 561;
        var unknown = AddFile("/c/Batman/Batman Annual - Chapter 0003.cbz", "3", format: "Annual");
        unknown.Metadata!.ComicVineIssueId = 562;
        await using (var db = new ComicMaintainerDbContext(_options))
        {
            db.ComicVineIssues.Add(new ComicVineIssueEntity { Id = 561, VolumeId = 9999, IssueNumber = "1", Name = "Annual One", SiteUrl = "https://comicvine.gamespot.com/a-1/4000-561/", ListedUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        var link = Assert.Single(await CreateService().GetIssueLinksAsync(_files.Select(f => f.FilePath).ToList()));

        Assert.Equal(new IssueComicVineLink(tagged.FilePath, 561, "1", "Annual One", "https://comicvine.gamespot.com/a-1/4000-561/", 9999, null, "https://comicvine.gamespot.com/volume/4050-9999/", Filled: false), link);
    }

    [Fact]
    public async Task GetIssueLinksAsync_LinksEachIssueToItsPage_ElseToItsVolume()
    {
        AddFile("/c/Batman/Batman (2016) - Chapter 0001.cbz", "1");
        AddFile("/c/Batman/Batman (2016) - Chapter 0002.cbz", "002");
        AddFile("/c/Batman/Batman (2016) - Chapter 0003.cbz", "3");
        AddFile("/c/Batman/Batman (1999) - Chapter 0001.cbz", "1", volumeYear: 1999);
        AddFile("/c/Batman/Batman - Chapter 0005.cbz", "5", volumeYear: null);
        AddFile("/c/Batman/Batman Annual - Chapter 0001.cbz", "1", format: "Annual");
        AddFile("/c/Batman/Batman (2016) - Chapter 0009.cbz", "9");
        await using (var db = new ComicMaintainerDbContext(_options))
        {
            db.ComicVineVolumeLinks.AddRange(
                new ComicVineVolumeLinkEntity { SeriesKey = "batman", StartYear = 2016, VolumeId = BatmanVolume, VolumeName = "Batman", LinkedUtc = DateTime.UtcNow },
                new ComicVineVolumeLinkEntity { SeriesKey = "batman", StartYear = 1999, VolumeId = null, LinkedUtc = DateTime.UtcNow });
            db.ComicVineIssues.AddRange(
                new ComicVineIssueEntity { Id = 101, VolumeId = BatmanVolume, IssueNumber = "1", Name = "I Am Gotham, Part One", SiteUrl = "https://comicvine.gamespot.com/batman-1/4000-101/", ListedUtc = DateTime.UtcNow },
                new ComicVineIssueEntity { Id = 102, VolumeId = BatmanVolume, IssueNumber = "2", Name = "I Am Gotham, Part Two", SiteUrl = "https://comicvine.gamespot.com/batman-2/4000-102/", ListedUtc = DateTime.UtcNow },
                new ComicVineIssueEntity { Id = 990, VolumeId = 4242, IssueNumber = "9", Name = "Elsewhere", SiteUrl = null, ListedUtc = DateTime.UtcNow });
            // #1 was filled from its issue; #9 from an issue of another volume, with no page.
            db.ComicVineFileStates.AddRange(
                new ComicVineFileStateEntity { FilePath = "/c/Batman/Batman (2016) - Chapter 0001.cbz", IssueId = 101, AppliedUtc = DateTime.UtcNow },
                new ComicVineFileStateEntity { FilePath = "/c/Batman/Batman (2016) - Chapter 0009.cbz", IssueId = 990, AppliedUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        _seriesCache.Setup(c => c.GetByTitleAsync("Batman", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesMetadataCacheRecord { CanonicalTitle = "Batman", Source = "ComicVine", SourceId = "42721", LookupStatus = "success" });

        var links = (await CreateService().GetIssueLinksAsync(_files.Select(f => f.FilePath).ToList())).ToDictionary(link => link.FilePath);

        var volumeUrl = $"https://comicvine.gamespot.com/volume/4050-{BatmanVolume}/";
        Assert.Equal(
            new IssueComicVineLink("/c/Batman/Batman (2016) - Chapter 0001.cbz", 101, "1", "I Am Gotham, Part One", "https://comicvine.gamespot.com/batman-1/4000-101/", BatmanVolume, "Batman", volumeUrl, Filled: true),
            links["/c/Batman/Batman (2016) - Chapter 0001.cbz"]);
        Assert.Equal(
            new IssueComicVineLink("/c/Batman/Batman (2016) - Chapter 0002.cbz", 102, "2", "I Am Gotham, Part Two", "https://comicvine.gamespot.com/batman-2/4000-102/", BatmanVolume, "Batman", volumeUrl, Filled: false),
            links["/c/Batman/Batman (2016) - Chapter 0002.cbz"]);
        Assert.Equal(
            new IssueComicVineLink("/c/Batman/Batman (2016) - Chapter 0003.cbz", null, null, null, null, BatmanVolume, "Batman", volumeUrl, Filled: false),
            links["/c/Batman/Batman (2016) - Chapter 0003.cbz"]);
        Assert.Equal(
            new IssueComicVineLink("/c/Batman/Batman - Chapter 0005.cbz", null, null, null, null, 42721, "Batman", "https://comicvine.gamespot.com/volume/4050-42721/", Filled: false),
            links["/c/Batman/Batman - Chapter 0005.cbz"]);
        Assert.Equal(
            new IssueComicVineLink("/c/Batman/Batman (2016) - Chapter 0009.cbz", 990, "9", "Elsewhere", null, 4242, null, "https://comicvine.gamespot.com/volume/4050-4242/", Filled: true),
            links["/c/Batman/Batman (2016) - Chapter 0009.cbz"]);
        // An unlinked volume and an Annual have no page.
        Assert.Equal(5, links.Count);
        Assert.Empty(await CreateService().GetIssueLinksAsync(["/c/missing.cbz"]));
    }

    [Fact]
    public async Task GetVolumeLinksAsync_ListsEachVolumeWithItsComicVineLink()
    {
        AddFile("/c/Batman/Batman (2016) - Chapter 0001.cbz", "1");
        AddFile("/c/Batman/Batman (2016) - Chapter 0002.cbz", "2");
        AddFile("/c/Batman/Batman Vol. 1 - Chapter 0001.cbz", "1", volumeYear: null, volume: "1");
        AddFile("/c/Batman/Batman (1999) - Chapter 0001.cbz", "1", volumeYear: 1999);
        AddFile("/c/Batman/Batman - Chapter 0001.cbz", "1", volumeYear: null);
        AddFile("/c/Batman/Batman Annual - Chapter 0001.cbz", "1", format: "Annual");
        AddFile("/c/Batman/dup.cbz", "1", duplicate: true);
        await using (var db = new ComicMaintainerDbContext(_options))
        {
            db.ComicVineVolumeLinks.AddRange(
                new ComicVineVolumeLinkEntity { SeriesKey = "batman", StartYear = 2016, VolumeId = BatmanVolume, VolumeName = "Batman", Publisher = "DC Comics", LinkedUtc = DateTime.UtcNow },
                new ComicVineVolumeLinkEntity { SeriesKey = "batman", VolumeNumber = 1, VolumeId = 796, VolumeName = "Batman", Publisher = "DC Comics", LinkedUtc = DateTime.UtcNow },
                new ComicVineVolumeLinkEntity { SeriesKey = "batman", StartYear = 1999, VolumeId = null, LinkedUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        _seriesCache.Setup(c => c.GetByTitleAsync("Batman", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesMetadataCacheRecord { CanonicalTitle = "Batman", Source = "ComicVine", SourceId = "42721", LookupStatus = "manual_match" });

        var volumes = await CreateService().GetVolumeLinksAsync(_files.Select(f => f.FilePath).ToList());

        // Volumes known by start year, oldest first, then by number, then the rest.
        Assert.Equal(
        [
            new SeriesVolumeLink(0, 1999, 1, null, null, null, null),
            new SeriesVolumeLink(0, 2016, 2, BatmanVolume, "Batman", "DC Comics", $"https://comicvine.gamespot.com/volume/4050-{BatmanVolume}/"),
            new SeriesVolumeLink(1, 0, 1, 796, "Batman", "DC Comics", "https://comicvine.gamespot.com/volume/4050-796/"),
            new SeriesVolumeLink(0, 0, 1, 42721, "Batman", null, "https://comicvine.gamespot.com/volume/4050-42721/"),
        ], volumes);
        Assert.Empty(await CreateService().GetVolumeLinksAsync(["/c/missing.cbz"]));
    }

    [Fact]
    public async Task RunAsync_ATieInNumber_MatchesComicVinesTieIn_NotIssue1()
    {
        AddFile("/c/Batman/Batman (2016) - Chapter 0001.MU.cbz", "1.mu");
        _client.Setup(c => c.ListVolumeIssuesAsync(BatmanVolume, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComicVineResult<IReadOnlyList<ComicVineIssueSummary>>(
            [
                new ComicVineIssueSummary(101, "1", "I Am Gotham, Part One", "2016-08-01", null, null),
                new ComicVineIssueSummary(150, "1.MU", "Monsters Unleashed", "2017-03-01", null, null),
            ], 1));
        _client.Setup(c => c.GetIssueDetailsAsync(150, It.IsAny<CancellationToken>())).ReturnsAsync(new ComicVineIssueDetails(150, [], []));

        await CreateService().RunAsync(null, 20);

        Assert.Equal("Monsters Unleashed", _writes.Single().Metadata.Title);
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
