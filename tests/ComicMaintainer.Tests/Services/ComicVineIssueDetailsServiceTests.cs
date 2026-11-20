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
    private readonly Mock<IMetronClient> _metron = new();
    private readonly Mock<ICoverFingerprintService> _fingerprints = new();
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
        _client.Setup(c => c.ListVolumeIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComicVineResult<IReadOnlyList<ComicVineIssueSummary>>([], 1));
        _search.Setup(s => s.ListVolumesNamedAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComicVineVolumeList([], 0, Complete: true));
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
            .Callback((string path, ComicMetadata metadata, ComicMetadataFieldFlags fields, ProviderComicInfoFields fill, CancellationToken _) =>
            {
                _writes.Add((path, metadata, fields, fill));

                // The archive now holds the publisher filled into an empty element, and the store reads it back.
                if (fill.Publisher is not null && _files.FirstOrDefault(f => f.FilePath == path)?.Metadata is { } stored && string.IsNullOrWhiteSpace(stored.Publisher))
                {
                    stored.Publisher = fill.Publisher;
                }
            })
            .ReturnsAsync((string path, ComicMetadata _, ComicMetadataFieldFlags _, ProviderComicInfoFields _, CancellationToken _) => new MetadataWriteResult(MetadataWriteOutcome.Written, path));
    }

    public Task DisposeAsync() => _connection.DisposeAsync().AsTask();

    private ComicVineIssueDetailsService CreateService(TimeSpan? linkRecheck = null) => new(
        new Factory(_options),
        _fileStore.Object,
        _search.Object,
        _client.Object,
        _seriesCache.Object,
        _processor.Object,
        _jobs.Object,
        _metron.Object,
        _fingerprints.Object,
        NullLogger<ComicVineIssueDetailsService>.Instance)
    {
        LinkRecheck = linkRecheck ?? TimeSpan.FromSeconds(30),
    };

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
        // The publisher ComicVine lists for the volume, stored with its link.
        Assert.Equal("DC Comics", first.Fill.Publisher);
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
    public async Task RunAsync_AFileFilledBeforeItsPublisherWasKnown_GetsItOnce()
    {
        var file = AddFile("/c/Batman/Batman (2016) - Chapter 0001.cbz", "1");
        await CreateService().RunAsync(null, 20);
        file.Metadata!.Publisher = null;
        _writes.Clear();

        var again = await CreateService().RunAsync(null, 20);

        Assert.Equal("DC Comics", Assert.Single(_writes).Fill.Publisher);
        Assert.Equal(1, again.Filled);

        // A publisher of the user's, or a locked empty one, is left as it is and asks for no write.
        _writes.Clear();
        file.Metadata.Publisher = null;
        file.Metadata.UserLockedFieldsMask = (long)ComicMetadataFieldFlags.Publisher;
        await CreateService().RunAsync(null, 20);
        Assert.Empty(_writes);
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
        // An issue says nothing of its volume's publisher, and no link of the volume knows it yet.
        Assert.Null(write.Fill.Publisher);
        _search.Verify(s => s.SearchVolumesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _client.Verify(c => c.GetIssueDetailsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);

        // The series' Vol. 3 is linked to the issue's volume, so its other issues are found there.
        await using var db = new ComicMaintainerDbContext(_options);
        var link = Assert.Single(db.ComicVineVolumeLinks);
        Assert.Equal(("batman", 0, 3, 4242, "Batman"), (link.SeriesKey, link.StartYear, link.VolumeNumber, link.VolumeId, link.VolumeName));
        Assert.Equal(555, db.ComicVineFileStates.Single().IssueId);
        Assert.False(write.Fill.ComicVineIssueMatchedByName);
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
    // The tagged issue has another number than the file: tags copied from another file, replaced.
    [InlineData(true)]
    // ComicVine has no such issue: nothing shows the tag is wrong, so it is kept.
    [InlineData(false)]
    public async Task RunAsync_ATagThatCannotBeTrusted_IsMatchedInstead_AndReplacedOnlyWhenWrong(bool otherNumber)
    {
        var file = AddFile("/c/Batman/Batman (2016) - Chapter 0001.cbz", "1");
        file.Metadata!.ComicVineIssueId = 557;
        _client.Setup(c => c.GetIssueAsync(557, It.IsAny<CancellationToken>())).ReturnsAsync(otherNumber ? TaggedIssue(557, "7") : null);

        var summary = await CreateService().RunAsync(null, 20);

        Assert.Equal(1, summary.Filled);
        var write = Assert.Single(_writes);
        Assert.Equal("I Am Gotham, Part One", write.Metadata.Title);
        Assert.Equal(otherNumber, write.Fill.ReplacesComicVineIssueId);
        if (otherNumber)
        {
            // The wrong id gives way to this match, marked as the app's own.
            Assert.Equal((101, true), (write.Fill.ComicVineIssueId!.Value, write.Fill.ComicVineIssueMatchedByName));
            return;
        }

        // The tag is kept, and nothing is written beside it that would name another issue.
        Assert.Null(write.Fill.ComicVineIssueId);
        Assert.Null(write.Fill.Web);
        await using (var db = new ComicMaintainerDbContext(_options))
        {
            Assert.Equal(557, db.ComicVineFileStates.Single().RejectedTagId);
        }

        // The next run does not ask for it again.
        _writes.Clear();
        await CreateService().RunAsync(null, 20);
        _client.Verify(c => c.GetIssueAsync(557, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    // No requests left, or ComicVine not configured: the tag cannot be read now.
    [InlineData(0, true)]
    [InlineData(20, false)]
    public async Task RunAsync_ATagThatCannotBeReadNow_WaitsForTheNextRun_AndIsNeverMatchedByName(int maxRequests, bool configured)
    {
        // A stored link would let name matching fill it, from the wrong volume.
        var file = AddFile("/c/Batman/Batman (2016) - Chapter 0001.cbz", "1");
        file.Metadata!.ComicVineIssueId = 563;
        _client.SetupGet(c => c.IsConfigured).Returns(configured);

        var summary = await CreateService().RunAsync(null, maxRequests);

        Assert.Equal((1, maxRequests == 0), (summary.NoVolume, summary.StoppedEarly));
        Assert.Empty(_writes);
        _client.Verify(c => c.GetIssueAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_ATagThisAppWrote_IsMatchedAgain_SoAVolumeTheUserSetTakesEffect()
    {
        // Filled from Batman (2016) #1 by name before, which wrote "[CVDB101] (matched by name …)";
        // the file has since been renamed (no stored state), and the user has said it is from 2011.
        var file = AddFile("/c/Batman/Batman (2011) - Chapter 0001.cbz", "1", volumeYear: 2011);
        file.Metadata!.ComicVineIssueId = 101;
        file.Metadata.ComicVineIssueIdSource = ComicVineIdSource.NameMatch;

        _search.Setup(s => s.SearchVolumesAsync("Batman", It.IsAny<CancellationToken>())).ReturnsAsync(
            [new ExternalSeriesMetadata { CanonicalTitle = "Batman", StartYear = 2011, Publisher = "DC Comics", SourceId = "42721", TotalChapters = 52 }]);
        _client.Setup(c => c.ListVolumeIssuesAsync(42721, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComicVineResult<IReadOnlyList<ComicVineIssueSummary>>([new ComicVineIssueSummary(4201, "1", "Knightfall", "2011-11-01", null, null)], 1));
        _client.Setup(c => c.GetIssueDetailsAsync(4201, It.IsAny<CancellationToken>())).ReturnsAsync(new ComicVineIssueDetails(4201, [], []));

        var summary = await CreateService().RunAsync(null, 20);

        Assert.Equal(1, summary.Filled);
        var write = Assert.Single(_writes);
        Assert.Equal((4201, true, true), (write.Fill.ComicVineIssueId!.Value, write.Fill.ReplacesComicVineIssueId, write.Fill.ComicVineIssueMatchedByName));
        _client.Verify(c => c.GetIssueAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_AWebLinkAnEarlierVersionWrote_IsMatchedAgain()
    {
        // Earlier versions wrote the matched issue's page into an empty <Web>, with no marker: an id
        // there naming the very issue the file was filled from is that version's own guess.
        var file = AddFile("/c/Batman/Batman (2011) - Chapter 0001.cbz", "1", volumeYear: 2011);
        file.Metadata!.ComicVineIssueId = 101;
        file.Metadata.ComicVineIssueIdSource = ComicVineIdSource.WebOnly;
        await using (var db = new ComicMaintainerDbContext(_options))
        {
            db.ComicVineFileStates.Add(new ComicVineFileStateEntity { FilePath = file.FilePath, IssueId = 101, AppliedUtc = DateTime.UtcNow.AddDays(-1) });
            await db.SaveChangesAsync();
        }

        _search.Setup(s => s.SearchVolumesAsync("Batman", It.IsAny<CancellationToken>())).ReturnsAsync(
            [new ExternalSeriesMetadata { CanonicalTitle = "Batman", StartYear = 2011, Publisher = "DC Comics", SourceId = "42721", TotalChapters = 52 }]);
        _client.Setup(c => c.ListVolumeIssuesAsync(42721, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComicVineResult<IReadOnlyList<ComicVineIssueSummary>>([new ComicVineIssueSummary(4201, "1", "Knightfall", "2011-11-01", null, "https://comicvine.gamespot.com/batman-1/4000-4201/")], 1));
        _client.Setup(c => c.GetIssueDetailsAsync(4201, It.IsAny<CancellationToken>())).ReturnsAsync(new ComicVineIssueDetails(4201, [], []));

        await CreateService().RunAsync(null, 20);

        var write = Assert.Single(_writes);
        Assert.Equal((4201, true, true), (write.Fill.ComicVineIssueId!.Value, write.Fill.ReplacesComicVineIssueId, write.Fill.ComicVineIssueMatchedByName));
        _client.Verify(c => c.GetIssueAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);

        // A web link alone, on a file not filled from it, is a tagger's.
        _writes.Clear();
        var other = AddFile("/c/Batman/Batman (2011) - Chapter 0002.cbz", "2", volumeYear: 2011);
        other.Metadata!.ComicVineIssueId = 4202;
        other.Metadata.ComicVineIssueIdSource = ComicVineIdSource.WebOnly;
        _client.Setup(c => c.GetIssueAsync(4202, It.IsAny<CancellationToken>())).ReturnsAsync(TaggedIssue(4202, "2", volumeId: 42721));
        await CreateService().RunAsync([other.FilePath], 20);
        Assert.Equal(4202, Assert.Single(_writes).Fill.ComicVineIssueId);
        _client.Verify(c => c.GetIssueAsync(4202, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_ATagComicVineDoesNotGive_IsRemembered_EvenWhenTheFileIsNotWrittenAgain()
    {
        // Filled from #1 by name already; its tags now name an issue ComicVine does not have.
        var file = AddFile("/c/Batman/Batman (2016) - Chapter 0001.cbz", "1");
        await CreateService().RunAsync(null, 20);
        file.Metadata!.ComicVineIssueId = 566;
        _writes.Clear();

        var summary = await CreateService().RunAsync(null, 20);

        Assert.Equal(1, summary.Unchanged);
        Assert.Empty(_writes);
        await using (var db = new ComicMaintainerDbContext(_options))
        {
            Assert.Equal(566, db.ComicVineFileStates.Single().RejectedTagId);
        }

        await CreateService().RunAsync(null, 20);
        _client.Verify(c => c.GetIssueAsync(566, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_ATagNamingAnotherVolumeThanTheLinkedOne_FillsTheFile_ButKeepsTheLink()
    {
        AddFile("/c/Batman/Batman (2016) - Chapter 0002.cbz", "2");
        var tagged = AddFile("/c/Batman/Batman (2016) - Chapter 0001.cbz", "1");
        tagged.Metadata!.ComicVineIssueId = 564;
        _client.Setup(c => c.GetIssueAsync(564, It.IsAny<CancellationToken>())).ReturnsAsync(TaggedIssue(564, "1", volumeId: 4242));
        await using (var seed = new ComicMaintainerDbContext(_options))
        {
            seed.ComicVineVolumeLinks.Add(new ComicVineVolumeLinkEntity { SeriesKey = "batman", StartYear = 2016, VolumeId = BatmanVolume, VolumeName = "Batman", LinkedUtc = DateTime.UtcNow });
            await seed.SaveChangesAsync();
        }

        await CreateService().RunAsync(null, 20);

        Assert.Equal(564, _writes.Single(w => w.Path == tagged.FilePath).Fill.ComicVineIssueId);
        await using var db = new ComicMaintainerDbContext(_options);
        Assert.Equal(BatmanVolume, Assert.Single(db.ComicVineVolumeLinks).VolumeId);
    }

    [Fact]
    public async Task RunAsync_AVolumeLinkedThroughATag_IsUsedForItsUntaggedIssues_NotSearchedAgain()
    {
        // Vol. 3: #3 is tagged, #30 is not. Only the tagged issue of the volume is stored at first,
        // which must not make the volume look too short for #30.
        var tagged = AddFile("/c/Batman/Batman Vol. 3 - Chapter 0003.cbz", "3", volumeYear: null, volume: "3");
        tagged.Metadata!.ComicVineIssueId = 565;
        AddFile("/c/Batman/Batman Vol. 3 - Chapter 0030.cbz", "30", volumeYear: null, volume: "3");
        _client.Setup(c => c.GetIssueAsync(565, It.IsAny<CancellationToken>())).ReturnsAsync(TaggedIssue(565, "3", volumeId: 4242));
        _client.Setup(c => c.ListVolumeIssuesAsync(4242, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComicVineResult<IReadOnlyList<ComicVineIssueSummary>>(
                [new ComicVineIssueSummary(565, "3", "Tagged 3", "2016-09-01", null, null), new ComicVineIssueSummary(590, "30", "Thirty", "2017-10-01", null, null)], 1));
        _client.Setup(c => c.GetIssueDetailsAsync(590, It.IsAny<CancellationToken>())).ReturnsAsync(new ComicVineIssueDetails(590, [], []));

        var summary = await CreateService().RunAsync(null, 20);

        Assert.Equal(2, summary.Filled);
        Assert.Equal("Thirty", _writes.Single(w => w.Path.EndsWith("0030.cbz")).Metadata.Title);
        _search.Verify(s => s.SearchVolumesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_NoRequestsLeftToReadATaggedAnnual_StopsEarly()
    {
        var file = AddFile("/c/Batman/Batman Annual - Chapter 0001.cbz", "1", format: "Annual");
        file.Metadata!.ComicVineIssueId = 558;

        var summary = await CreateService().RunAsync(null, 0);

        Assert.Equal((1, true), (summary.NoVolume, summary.StoppedEarly));
        _client.Verify(c => c.GetIssueAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_TheRankedSearchMissesTheVolume_ItIsFoundInTheListByName()
    {
        AddFile("/c/Batman/Batman (2011) - Chapter 0001.cbz", "1", volumeYear: 2011);
        _search.Setup(s => s.ListVolumesNamedAsync("Batman", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComicVineVolumeList(
                [new ExternalSeriesMetadata { CanonicalTitle = "Batman", StartYear = 2011, Publisher = "DC Comics", SourceId = "42721", TotalChapters = 52 }], 2, Complete: false));
        _client.Setup(c => c.ListVolumeIssuesAsync(42721, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComicVineResult<IReadOnlyList<ComicVineIssueSummary>>([new ComicVineIssueSummary(4201, "1", "Knightfall", "2011-11-01", null, null)], 1));
        _client.Setup(c => c.GetIssueDetailsAsync(4201, It.IsAny<CancellationToken>())).ReturnsAsync(new ComicVineIssueDetails(4201, [], []));

        var summary = await CreateService().RunAsync(null, 20);

        Assert.Equal(1, summary.Filled);
        Assert.Equal("Knightfall", Assert.Single(_writes).Metadata.Title);
        Assert.True(Assert.Single(_writes).Fill.ComicVineIssueMatchedByName);
        // One search page, two list pages, one issue list, one issue's details.
        Assert.Equal(1 + 2 + 1 + 1, summary.Requests);
        _search.Verify(s => s.ListVolumesNamedAsync("Batman", 5, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public async Task RunAsync_AVolumeNumber_IsCountedFromTheListByName_OnlyWhenItIsComplete(bool complete, int filled)
    {
        // The search has 1940 and 2016; the list by name adds 2011, so Vol. 3 is 2016 counted in full.
        // Cut off, the list could leave later volumes out and count the wrong one.
        AddFile("/c/Batman/Batman Vol. 4 - Chapter 0001.cbz", "1", volumeYear: null, volume: "4");
        _search.Setup(s => s.ListVolumesNamedAsync("Batman", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComicVineVolumeList(
            [
                new ExternalSeriesMetadata { CanonicalTitle = "Batman", StartYear = 2011, Publisher = "DC Comics", SourceId = "42721", TotalChapters = 52 },
                new ExternalSeriesMetadata { CanonicalTitle = "Batman", StartYear = 2020, Publisher = "DC Comics", SourceId = "7777", TotalChapters = 30 },
            ], 3, complete));
        _client.Setup(c => c.ListVolumeIssuesAsync(7777, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComicVineResult<IReadOnlyList<ComicVineIssueSummary>>([new ComicVineIssueSummary(7701, "1", "Vol Four", "2020-01-01", null, null)], 1));
        _client.Setup(c => c.GetIssueDetailsAsync(7701, It.IsAny<CancellationToken>())).ReturnsAsync(new ComicVineIssueDetails(7701, [], []));

        var summary = await CreateService().RunAsync(null, 20);

        Assert.Equal(filled, summary.Filled);
    }

    [Fact]
    public async Task GetCoverCandidatesAsync_TheListByName_LeavesARequestToListTheIssues()
    {
        var file = AddFile("/c/Batman/Batman (2011) - Chapter 0001.cbz", "1", volumeYear: 2011);
        _search.Setup(s => s.ListVolumesNamedAsync("Batman", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, int max, CancellationToken _) => new ComicVineVolumeList(
                [new ExternalSeriesMetadata { CanonicalTitle = "Batman", StartYear = 2011, Publisher = "DC Comics", SourceId = "42721", TotalChapters = 52 }], max, Complete: false));
        _client.Setup(c => c.ListVolumeIssuesAsync(42721, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComicVineResult<IReadOnlyList<ComicVineIssueSummary>>([new ComicVineIssueSummary(4201, "1", "Knightfall", "2011-11-01", null, null, "https://comicvine.example/c/4201.jpg")], 1));

        var candidates = await CreateService().GetCoverCandidatesAsync(file.FilePath);

        Assert.Equal(4201, Assert.Single(candidates!.Covers).IssueId);
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
        Assert.Equal((9999, "Batman Annual", 559, null), (candidates!.VolumeId!.Value, candidates.VolumeName, candidates.MatchedIssueId!.Value, candidates.Reason));
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

    private void TwoBatmans2016()
        => _search.Setup(s => s.SearchVolumesAsync("Batman", It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new ExternalSeriesMetadata { CanonicalTitle = "Batman", StartYear = 2016, Publisher = "DC Comics", SourceId = BatmanVolume.ToString(), TotalChapters = 158 },
            new ExternalSeriesMetadata { CanonicalTitle = "Batman", StartYear = 2016, Publisher = "DC Comics", SourceId = "5555", TotalChapters = 158 },
        ]);

    [Fact]
    public async Task RunAsync_TwoVolumesFitAboutEquallyWell_AreLeftForTheUser_AndNothingIsFilled()
    {
        AddFile("/c/Batman/Batman (2016) - Chapter 0001.cbz", "1");
        TwoBatmans2016();

        var summary = await CreateService().RunAsync(null, 20);

        Assert.Equal((0, 1), (summary.Filled, summary.NoVolume));
        Assert.Empty(_writes);
        await using var db = new ComicMaintainerDbContext(_options);
        var link = Assert.Single(db.ComicVineVolumeLinks);
        Assert.Equal((null, ComicVineVolumeDecision.Review), (link.VolumeId, link.Decision));
        Assert.Equal("2 ComicVine volumes named Batman fit about equally well", link.ReviewReason);

        var volume = Assert.Single(await CreateService().GetVolumeLinksAsync(_files.Select(f => f.FilePath).ToList()));
        Assert.Equal(ComicVineVolumeDecision.Review, volume.Decision);
        Assert.Equal([BatmanVolume, 5555], volume.Candidates.Select(candidate => candidate.VolumeId!.Value));
        Assert.Contains("started in 2016", volume.Candidates[0].Reasons);
        Assert.Equal($"https://comicvine.gamespot.com/volume/4050-{BatmanVolume}/", volume.Candidates[0].SiteUrl);
    }

    [Fact]
    public async Task RunAsync_TheIssuesCoverYears_TellTwoVolumesApart()
    {
        AddFile("/c/Batman/Batman (2016) - Chapter 0001.cbz", "1").Metadata!.Year = 2016;
        AddFile("/c/Batman/Batman (2016) - Chapter 0002.cbz", "2").Metadata!.Year = 2016;
        TwoBatmans2016();
        _client.Setup(c => c.ListVolumeIssuesAsync(5555, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComicVineResult<IReadOnlyList<ComicVineIssueSummary>>(
                [new ComicVineIssueSummary(9001, "1", "Reprint One", "1990-01-01", null, null), new ComicVineIssueSummary(9002, "2", "Reprint Two", "1990-02-01", null, null)], 1));

        var summary = await CreateService().RunAsync(null, 20);

        Assert.Equal(2, summary.Filled);
        await using var db = new ComicMaintainerDbContext(_options);
        var link = Assert.Single(db.ComicVineVolumeLinks);
        Assert.Equal((BatmanVolume, ComicVineVolumeDecision.Weighed), (link.VolumeId!.Value, link.Decision));
    }

    [Fact]
    public async Task ChooseVolumeAsync_TheUsersChoice_IsUsed_AndNeverSearchedForAgain()
    {
        var file = AddFile("/c/Batman/Batman (2016) - Chapter 0001.cbz", "1");
        TwoBatmans2016();
        await CreateService().RunAsync(null, 20);
        var paths = _files.Select(f => f.FilePath).ToList();

        Assert.Equal(ChooseVolumeOutcome.NoSuchSeriesVolume, await CreateService().ChooseVolumeAsync(paths, null, 1999, 0, BatmanVolume));
        Assert.Equal(ChooseVolumeOutcome.NoSuchSeriesVolume, await CreateService().ChooseVolumeAsync(paths, "other", 2016, 0, BatmanVolume));
        Assert.Equal(ChooseVolumeOutcome.NoSuchSeriesVolume, await CreateService().ChooseVolumeAsync(paths, null, 2016, 0, 0));
        Assert.Equal(ChooseVolumeOutcome.Chosen, await CreateService().ChooseVolumeAsync(paths, "batman", 2016, 0, BatmanVolume));
        _search.Invocations.Clear();

        var summary = await CreateService().RunAsync(null, 20);

        Assert.Equal(1, summary.Filled);
        Assert.Equal("I Am Gotham, Part One", Assert.Single(_writes).Metadata.Title);
        _search.Verify(s => s.SearchVolumesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        var volume = Assert.Single(await CreateService().GetVolumeLinksAsync(paths));
        Assert.Equal((ComicVineVolumeDecision.Manual, "Batman", null), (volume.Decision, volume.Name, volume.ReviewReason));

        // None of them: never searched for, and the file says why it is not filled.
        Assert.Equal(ChooseVolumeOutcome.Chosen, await CreateService().ChooseVolumeAsync(paths, null, 2016, 0, null));
        file.Metadata!.Title = "Chapter 1";
        await using (var db = new ComicMaintainerDbContext(_options))
        {
            db.ComicVineFileStates.RemoveRange(db.ComicVineFileStates);
            await db.SaveChangesAsync();
        }

        var none = await CreateService().RunAsync(null, 20);
        Assert.Equal(1, none.NoVolume);
        _search.Verify(s => s.SearchVolumesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ChooseVolumeAsync_AVolumeNotWeighed_IsCheckedAndNamedOnComicVine()
    {
        AddFile("/c/Batman/Batman (2016) - Chapter 0001.cbz", "1");
        var paths = _files.Select(f => f.FilePath).ToList();
        _search.Setup(s => s.GetVolumeAsync(42721, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalSeriesMetadata { CanonicalTitle = "Batman", Publisher = "DC Comics", SourceId = "42721", StartYear = 2011 });

        Assert.Equal(ChooseVolumeOutcome.NoSuchComicVineVolume, await CreateService().ChooseVolumeAsync(paths, null, 2016, 0, 999999));
        Assert.Equal(ChooseVolumeOutcome.Chosen, await CreateService().ChooseVolumeAsync(paths, null, 2016, 0, 42721));
        var volume = Assert.Single(await CreateService().GetVolumeLinksAsync(paths));
        Assert.Equal((42721, "Batman", "DC Comics", "batman", true), (volume.ComicVineVolumeId!.Value, volume.Name, volume.Publisher, volume.SeriesKey, volume.Searched));

        // ComicVine cannot be asked: kept unnamed.
        _search.Setup(s => s.GetVolumeAsync(7, It.IsAny<CancellationToken>())).ThrowsAsync(new ExternalMetadataUnavailableException("ComicVine", ExternalMetadataFailureKind.RateLimited, "slow down"));
        Assert.Equal(ChooseVolumeOutcome.Chosen, await CreateService().ChooseVolumeAsync(paths, null, 2016, 0, 7));
        Assert.Null(Assert.Single(await CreateService().GetVolumeLinksAsync(paths)).Name);
    }

    [Fact]
    public async Task ChooseVolumeAsync_TwoSeriesOfOnePage_AreToldApartByTheirKey()
    {
        AddFile("/c/Batman/Batman (2016) - Chapter 0001.cbz", "1");
        AddFile("/c/Batman/The Batman (2016) - Chapter 0001.cbz", "1", series: "The Batman");
        var paths = _files.Select(f => f.FilePath).ToList();

        Assert.Equal(ChooseVolumeOutcome.NoSuchSeriesVolume, await CreateService().ChooseVolumeAsync(paths, null, 2016, 0, null));
        Assert.Equal(ChooseVolumeOutcome.Chosen, await CreateService().ChooseVolumeAsync(paths, "thebatman", 2016, 0, null));
        Assert.Equal(["batman", "thebatman"], (await CreateService().GetVolumeLinksAsync(paths)).Select(volume => volume.SeriesKey).Order());
    }

    [Fact]
    public async Task RunAsync_AVolumeLeftForReview_IsNotWeighedAgainOnEveryRun()
    {
        AddFile("/c/Batman/Batman (2016) - Chapter 0001.cbz", "1");
        TwoBatmans2016();
        await CreateService().RunAsync(null, 20);
        _search.Invocations.Clear();

        // A user's job, and the cover picker, ask nothing more.
        BatchJobDefinition? definition = null;
        _jobs.Setup(j => j.StartJob(It.IsAny<BatchJobDefinition>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Callback((BatchJobDefinition d, IEnumerable<string> _, CancellationToken _) => definition = d)
            .Returns(Guid.NewGuid());
        CreateService().StartJob([_files[0].FilePath]);
        var item = await definition!.ItemOperation(_files[0].FilePath, CancellationToken.None);
        var covers = await CreateService().GetCoverCandidatesAsync(_files[0].FilePath);

        Assert.False(item.Success);
        Assert.Contains("choose it under Volumes on the series page", item.Error);

        _search.Verify(s => s.SearchVolumesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Contains("choose it under Volumes on the series page", covers!.Reason);
    }

    [Fact]
    public async Task RunAsync_WeighingVolumes_KeepsARequestForTheChosenOne_AndReusesItsListing()
    {
        // Two volumes fit equally until their issues are read: the cover years pick 2016. Its listing,
        // read while weighing, fills the files without being asked for again.
        AddFile("/c/Batman/Batman (2016) - Chapter 0001.cbz", "1").Metadata!.Year = 2016;
        AddFile("/c/Batman/Batman (2016) - Chapter 0002.cbz", "2").Metadata!.Year = 2016;
        TwoBatmans2016();
        _client.Setup(c => c.ListVolumeIssuesAsync(5555, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComicVineResult<IReadOnlyList<ComicVineIssueSummary>>(
                [new ComicVineIssueSummary(9001, "1", "Reprint One", "1990-01-01", null, null), new ComicVineIssueSummary(9002, "2", "Reprint Two", "1990-02-01", null, null)], 1));

        var summary = await CreateService().RunAsync(null, 20);

        Assert.Equal(2, summary.Filled);
        _client.Verify(c => c.ListVolumeIssuesAsync(BatmanVolume, It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);

        // With only enough requests to search and list one volume, nothing is listed for weighing.
        _client.Invocations.Clear();
        await using (var db = new ComicMaintainerDbContext(_options))
        {
            db.ComicVineVolumeLinks.RemoveRange(db.ComicVineVolumeLinks);
            db.ComicVineIssues.RemoveRange(db.ComicVineIssues);
            db.ComicVineFileStates.RemoveRange(db.ComicVineFileStates);
            await db.SaveChangesAsync();
        }

        await CreateService().RunAsync(null, 2);
        _client.Verify(c => c.ListVolumeIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_AChoiceMadeWhileAJobRuns_IsNotOverwrittenByIt()
    {
        AddFile("/c/Batman/Batman (2016) - Chapter 0001.cbz", "1");
        var paths = _files.Select(f => f.FilePath).ToList();

        // The user chooses volume 42721 after the run has read the links but before it stores its own.
        _search.Setup(s => s.SearchVolumesAsync("Batman", It.IsAny<CancellationToken>()))
            .Callback(() => CreateService().ChooseVolumeAsync(paths, null, 2016, 0, 42721).GetAwaiter().GetResult())
            .ReturnsAsync([new ExternalSeriesMetadata { CanonicalTitle = "Batman", StartYear = 2016, Publisher = "DC Comics", SourceId = BatmanVolume.ToString(), TotalChapters = 158 }]);
        _search.Setup(s => s.GetVolumeAsync(42721, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalSeriesMetadata { CanonicalTitle = "Batman", SourceId = "42721" });

        await CreateService().RunAsync(null, 20);

        await using var db = new ComicMaintainerDbContext(_options);
        var link = Assert.Single(db.ComicVineVolumeLinks);
        Assert.Equal((42721, ComicVineVolumeDecision.Manual), (link.VolumeId!.Value, link.Decision));
    }

    [Fact]
    public async Task RunAsync_AChoiceMadeWhileAJobRunsOnTheOldVolume_IsUsedForTheRestOfIt()
    {
        AddFile("/c/Batman/Batman (2016) - Chapter 0001.cbz", "1");
        AddFile("/c/Batman/Batman (2016) - Chapter 0002.cbz", "2");
        var paths = _files.Select(f => f.FilePath).ToList();
        await using (var db = new ComicMaintainerDbContext(_options))
        {
            db.ComicVineVolumeLinks.Add(new ComicVineVolumeLinkEntity { SeriesKey = "batman", StartYear = 2016, VolumeId = BatmanVolume, VolumeName = "Batman", LinkedUtc = DateTime.UtcNow, Decision = ComicVineVolumeDecision.Weighed });
            await db.SaveChangesAsync();
        }

        // While #1 is filled from the old volume, the user chooses 42721.
        _search.Setup(s => s.GetVolumeAsync(42721, It.IsAny<CancellationToken>())).ReturnsAsync(new ExternalSeriesMetadata { CanonicalTitle = "Batman", SourceId = "42721" });
        _seriesCache.Setup(c => c.GetByTitleAsync("Batman", It.IsAny<CancellationToken>()))
            .Callback(() => CreateService().ChooseVolumeAsync(paths, null, 2016, 0, 42721).GetAwaiter().GetResult())
            .ReturnsAsync((SeriesMetadataCacheRecord?)null);
        _client.Setup(c => c.ListVolumeIssuesAsync(42721, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComicVineResult<IReadOnlyList<ComicVineIssueSummary>>([new ComicVineIssueSummary(4202, "2", "The Chosen Two", "2016-09-01", null, null)], 1));
        _client.Setup(c => c.GetIssueDetailsAsync(4202, It.IsAny<CancellationToken>())).ReturnsAsync(new ComicVineIssueDetails(4202, [], []));

        await CreateService(linkRecheck: TimeSpan.Zero).RunAsync(null, 20);

        Assert.Equal("The Chosen Two", _writes.Single(w => w.Path.EndsWith("0002.cbz")).Metadata.Title);
        await using var check = new ComicMaintainerDbContext(_options);
        Assert.Equal((42721, ComicVineVolumeDecision.Manual), (check.ComicVineVolumeLinks.Single().VolumeId!.Value, check.ComicVineVolumeLinks.Single().Decision));
    }

    private void MetronKnows(int volumeId, int? volume, int? year)
    {
        _metron.SetupGet(m => m.IsConfigured).Returns(true);
        _metron.Setup(m => m.SeriesOfComicVineVolumeAsync(volumeId, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MetronAnswer(true, new MetronSeries(volumeId + 1, "Batman", volume, year)));
    }

    [Fact]
    public async Task RunAsync_MetronsRecord_SettlesTwoVolumesThatFitEqually()
    {
        AddFile("/c/Batman/Batman (2016) - Chapter 0001.cbz", "1");
        TwoBatmans2016();
        MetronKnows(BatmanVolume, 3, 2016);
        MetronKnows(5555, 1, 2019);

        var summary = await CreateService().RunAsync(null, 20);

        Assert.Equal(1, summary.Filled);
        await using var db = new ComicMaintainerDbContext(_options);
        var link = Assert.Single(db.ComicVineVolumeLinks);
        Assert.Equal((BatmanVolume, ComicVineVolumeDecision.Weighed), (link.VolumeId!.Value, link.Decision));
        Assert.Contains("Metron lists it as Vol. 3 (2016), as your issues say", link.CandidatesJson);
    }

    [Fact]
    public async Task RunAsync_AVolumeMetronCountsAsAnother_IsLeftForReview()
    {
        // Clear on ComicVine's evidence, but Metron says the volume started in 2010.
        AddFile("/c/Batman/Batman (2016) - Chapter 0001.cbz", "1");
        MetronKnows(BatmanVolume, 2, 2010);

        var summary = await CreateService().RunAsync(null, 20);

        Assert.Equal(1, summary.NoVolume);
        await using var db = new ComicMaintainerDbContext(_options);
        var link = Assert.Single(db.ComicVineVolumeLinks);
        Assert.Equal((null, ComicVineVolumeDecision.Review), (link.VolumeId, link.Decision));
        Assert.Equal("ComicVine's Batman (2016) fits best, but Metron lists it as Vol. 2 (2010), not started in 2016", link.ReviewReason);
    }

    [Fact]
    public async Task RunAsync_MetronThatDoesNotAnswer_ChangesNothing()
    {
        AddFile("/c/Batman/Batman (2016) - Chapter 0001.cbz", "1");
        _metron.SetupGet(m => m.IsConfigured).Returns(true);
        _metron.Setup(m => m.SeriesOfComicVineVolumeAsync(It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(MetronAnswer.Unavailable);

        Assert.Equal(1, (await CreateService().RunAsync(null, 20)).Filled);
    }

    [Fact]
    public async Task RunAsync_TheCovers_SettleTwoVolumesThatFitEqually()
    {
        var file = AddFile("/c/Batman/Batman (2016) - Chapter 0001.cbz", "1");
        TwoBatmans2016();
        _client.Setup(c => c.ListVolumeIssuesAsync(BatmanVolume, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComicVineResult<IReadOnlyList<ComicVineIssueSummary>>(
                [new ComicVineIssueSummary(101, "1", "I Am Gotham, Part One", "2016-08-01", null, null, "https://comicvine.example/101.jpg", "https://comicvine.example/101-thumb.jpg")], 1));
        _client.Setup(c => c.ListVolumeIssuesAsync(5555, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComicVineResult<IReadOnlyList<ComicVineIssueSummary>>(
                [new ComicVineIssueSummary(9001, "1", "Reprint One", "2016-09-01", null, null, "https://comicvine.example/9001.jpg")], 1));
        _fingerprints.Setup(f => f.OfFileAsync(file.FilePath, It.IsAny<CancellationToken>())).ReturnsAsync(0x00FF00FF00FF00FFUL);
        _fingerprints.Setup(f => f.OfImageAsync("https://comicvine.example/101-thumb.jpg", It.IsAny<CancellationToken>())).ReturnsAsync(0x00FF00FF00FF00FEUL);
        _fingerprints.Setup(f => f.OfImageAsync("https://comicvine.example/9001.jpg", It.IsAny<CancellationToken>())).ReturnsAsync(0xFF00FF00FF00FF00UL);

        var summary = await CreateService().RunAsync(null, 20);

        Assert.Equal(1, summary.Filled);
        await using var db = new ComicMaintainerDbContext(_options);
        var link = Assert.Single(db.ComicVineVolumeLinks);
        Assert.Equal(BatmanVolume, link.VolumeId);
        Assert.Contains("a cover of yours matches its cover", link.CandidatesJson);
        Assert.Contains("your covers do not match its covers", link.CandidatesJson);
    }

    [Fact]
    public async Task RunAsync_CoversOfOnlyOneVolume_CanOnlyCountForIt()
    {
        // The right volume's cover differs (a variant); the other volume lists no cover to compare.
        // Covers that differ must not hand the pick to the volume that could not be compared.
        var file = AddFile("/c/Batman/Batman (2016) - Chapter 0001.cbz", "1");
        TwoBatmans2016();
        _client.Setup(c => c.ListVolumeIssuesAsync(BatmanVolume, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComicVineResult<IReadOnlyList<ComicVineIssueSummary>>(
                [new ComicVineIssueSummary(101, "1", "I Am Gotham, Part One", "2016-08-01", null, null, "https://comicvine.example/101.jpg")], 1));
        _fingerprints.Setup(f => f.OfFileAsync(file.FilePath, It.IsAny<CancellationToken>())).ReturnsAsync(0x00FF00FF00FF00FFUL);
        _fingerprints.Setup(f => f.OfImageAsync("https://comicvine.example/101.jpg", It.IsAny<CancellationToken>())).ReturnsAsync(0xFF00FF00FF00FF00UL);

        var summary = await CreateService().RunAsync(null, 20);

        Assert.Equal(1, summary.NoVolume);
        await using var db = new ComicMaintainerDbContext(_options);
        Assert.Equal(ComicVineVolumeDecision.Review, db.ComicVineVolumeLinks.Single().Decision);
    }

    [Fact]
    public async Task RunAsync_NoCoverToCompare_LeavesTheTie()
    {
        AddFile("/c/Batman/Batman (2016) - Chapter 0001.cbz", "1");
        TwoBatmans2016();
        _fingerprints.Setup(f => f.OfFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.FromResult<ulong?>(null));

        Assert.Equal(1, (await CreateService().RunAsync(null, 20)).NoVolume);
    }

    [Fact]
    public async Task RunAsync_AFileAlreadyFilledFromTheIssue_IsNotRefusedForItsOwnYear()
    {
        var file = AddFile("/c/Batman/Batman (2016) - Chapter 0001.cbz", "1");
        await CreateService().RunAsync(null, 20);
        file.Metadata!.Year = 2010;

        var summary = await CreateService().RunAsync(null, 20);

        Assert.Equal((0, 1), (summary.NoIssue, summary.Unchanged));
    }

    [Fact]
    public async Task RunAsync_AnIssueDatedYearsFromComicVinesIssueOfThatNumber_IsNotFilledFromIt()
    {
        var file = AddFile("/c/Batman/Batman (2016) - Chapter 0001.cbz", "1");
        file.Metadata!.Year = 2010;

        var summary = await CreateService().RunAsync(null, 20);

        Assert.Equal(1, summary.NoIssue);
        Assert.Empty(_writes);
    }

    [Fact]
    public async Task RunAsync_ACoverYearAnEarlierFillWrote_DoesNotStopTheRightIssue()
    {
        // Filled from Batman (1940) #1 before, which wrote 1940; the file is now linked to 2016.
        var file = AddFile("/c/Batman/Batman (2016) - Chapter 0001.cbz", "1");
        file.Metadata!.Year = 1940;
        await using (var db = new ComicMaintainerDbContext(_options))
        {
            db.ComicVineIssues.Add(new ComicVineIssueEntity { Id = 7961, VolumeId = 796, IssueNumber = "1", CoverDate = "1940-04-01", ListedUtc = DateTime.UtcNow });
            db.ComicVineFileStates.Add(new ComicVineFileStateEntity { FilePath = file.FilePath, IssueId = 7961, AppliedUtc = DateTime.UtcNow.AddDays(-1) });
            await db.SaveChangesAsync();
        }

        var summary = await CreateService().RunAsync(null, 20);

        Assert.Equal(1, summary.Filled);
        Assert.Equal(2016, Assert.Single(_writes).Metadata.Year);
    }

    [Fact]
    public async Task GetVolumeLinksAsync_ShowsIssuesDatedYearsFromComicVine_AndVolumesFiledAsOne()
    {
        var conflicting = AddFile("/c/Batman/Batman (2016) - Chapter 0001.cbz", "1");
        conflicting.Metadata!.Year = 2010;
        AddFile("/c/Batman/Batman (2016) - Chapter 0002.cbz", "2").Metadata!.Year = 2016;
        AddFile("/c/Batman/Batman - Chapter 0001.cbz", "1", volumeYear: null).Metadata!.Year = 2011;
        AddFile("/c/Batman/Batman - Chapter 0001 (2).cbz", "001", volumeYear: null).Metadata!.Year = 2016;
        AddFile("/c/Batman/Batman - Chapter 0002.cbz", "2", volumeYear: null).Metadata!.Year = 2012;
        await using (var db = new ComicMaintainerDbContext(_options))
        {
            db.ComicVineVolumeLinks.Add(new ComicVineVolumeLinkEntity { SeriesKey = "batman", StartYear = 2016, VolumeId = BatmanVolume, VolumeName = "Batman", LinkedUtc = DateTime.UtcNow, Decision = ComicVineVolumeDecision.Weighed });
            db.ComicVineIssues.AddRange(
                new ComicVineIssueEntity { Id = 101, VolumeId = BatmanVolume, IssueNumber = "1", CoverDate = "2016-08-01", ListedUtc = DateTime.UtcNow },
                new ComicVineIssueEntity { Id = 102, VolumeId = BatmanVolume, IssueNumber = "2", CoverDate = "2016-08-15", ListedUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        var volumes = await CreateService().GetVolumeLinksAsync(_files.Select(f => f.FilePath).ToList());

        var linked = volumes.Single(volume => volume.StartYear == 2016);
        Assert.Equal(ComicVineVolumeDecision.Weighed, linked.Decision);
        Assert.Equal(new YearConflict(conflicting.FilePath, "1", 2010, 2016), Assert.Single(linked.Conflicts));
        Assert.Empty(linked.MixedYears);
        var unnamed = volumes.Single(volume => volume.StartYear == 0);
        Assert.Equal([2011, 2016], unnamed.MixedYears);
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
        ], volumes.Select(volume => new SeriesVolumeLink(volume.VolumeNumber, volume.StartYear, volume.Issues, volume.ComicVineVolumeId, volume.Name, volume.Publisher, volume.SiteUrl)));
        Assert.All(volumes, volume => Assert.Empty(volume.Conflicts));
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
