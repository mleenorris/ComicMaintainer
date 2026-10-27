using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Services;

public class SeriesLibraryServiceCoverageTests
{
    private static readonly DateTime BaseTime = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly Mock<IFileStoreService> _fileStore = new();
    private readonly Mock<IComicProcessorService> _processor = new();
    private readonly Mock<ISeriesMetadataCacheService> _metadataCache = new();
    private readonly AppSettings _appSettings = new();
    private readonly Mock<IOptionsMonitor<AppSettings>> _settings = new();
    private readonly Dictionary<string, List<ComicFile>> _filteredFiles = new(StringComparer.Ordinal);
    private List<ComicFile> _files = new();
    private List<SeriesMetadataCacheRecord> _records = new();

    public SeriesLibraryServiceCoverageTests()
    {
        // Version sources make snapshot caching possible; it is only enabled
        // for services created with a user context.
        _fileStore.As<IChangeVersionSource>().SetupGet(v => v.ChangeVersion).Returns(1);
        _metadataCache.As<IChangeVersionSource>().SetupGet(v => v.ChangeVersion).Returns(1);
        _settings.Setup(s => s.CurrentValue).Returns(() => _appSettings);
        _fileStore.Setup(s => s.GetFilteredFilesAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string? filter, CancellationToken _) => SelectFiles(filter));
        _metadataCache.Setup(m => m.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _records);
    }

    private IEnumerable<ComicFile> SelectFiles(string? filter)
    {
        var source = filter is not null && _filteredFiles.TryGetValue(filter, out var filtered) ? filtered : _files;
        return source.Select(file => file.ShallowCopy()).ToList();
    }

    private SeriesLibraryService CreateService(
        ISeriesNameResolver? resolver = null,
        IUserContextAccessor? user = null,
        IOptionsMonitor<AppSettings>? settings = null) => new(
        _fileStore.Object,
        _processor.Object,
        _metadataCache.Object,
        settings ?? _settings.Object,
        NullLogger<SeriesLibraryService>.Instance,
        resolver,
        user);

    private void VerifyFileLoads(int times) =>
        _fileStore.Verify(s => s.GetFilteredFilesAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Exactly(times));

    private static ComicFile File(
        string path,
        string? series = null,
        string? issue = null,
        string? title = null,
        long size = 10,
        DateTime? modified = null,
        DateTime? created = null,
        string? volume = null,
        string? publisher = null,
        int? year = null)
    {
        var hasMetadata = series is not null || issue is not null || title is not null
            || volume is not null || publisher is not null || year is not null;
        return new ComicFile
        {
            FilePath = path,
            FileName = Path.GetFileName(path),
            Directory = Path.GetDirectoryName(path) ?? string.Empty,
            FileSize = size,
            LastModified = modified ?? BaseTime,
            CreatedAt = created ?? default,
            Metadata = hasMetadata
                ? new ComicMetadata { Series = series, Issue = issue, Title = title, Volume = volume, Publisher = publisher, Year = year }
                : null
        };
    }

    // A library whose files exercise the folder bucketing edge cases: several
    // files per directory, the same series spread over two directories, a file
    // without a Directory value, and files whose path has no parent directory.
    private void SeedFolderLibrary()
    {
        _files = new List<ComicFile>
        {
            File("/lib/Saga/Saga 001.cbz", series: "Saga", issue: "1", size: 10),
            File("/lib/Saga/Saga 002.cbz", series: "Saga", issue: "2", size: 20),
            File("/archive/Saga/Saga 003.cbz", series: "Saga", issue: "3", size: 5),
            File("/lib/Batman/Batman 001.cbz", series: "Batman", issue: "1", size: 7),
            new() { FilePath = "/lib/Batman/Batman 002.cbz", FileName = "Batman 002.cbz", Directory = null!, FileSize = 3, LastModified = BaseTime },
            new() { FilePath = "/", FileName = string.Empty, Directory = null!, FileSize = 1, LastModified = BaseTime },
            new() { FilePath = string.Empty, FileName = string.Empty, Directory = string.Empty, FileSize = 2, LastModified = BaseTime },
        };
    }

    [Fact]
    public async Task GetTitlesForSeriesIdAsync_BlankId_ReturnsEmptyWithoutLoading()
    {
        var titles = await CreateService().GetTitlesForSeriesIdAsync("   ");

        Assert.Empty(titles);
        VerifyFileLoads(0);
    }

    [Fact]
    public async Task GetFoldersForSeriesIdAsync_BlankId_ReturnsNull()
    {
        Assert.Null(await CreateService().GetFoldersForSeriesIdAsync(string.Empty));
        VerifyFileLoads(0);
    }

    [Fact]
    public async Task GetFoldersForSeriesIdAsync_SkipsIssuesWithoutUsableDirectory()
    {
        SeedFolderLibrary();
        var service = CreateService();

        var unknown = await service.GetFoldersForSeriesIdAsync("unknown-series");
        var saga = await service.GetFoldersForSeriesIdAsync("saga");

        Assert.NotNull(unknown);
        Assert.Equal("Unknown Series", unknown.Title);
        Assert.Empty(unknown.Folders);

        Assert.NotNull(saga);
        Assert.Collection(saga.Folders,
            folder =>
            {
                Assert.Equal("/lib/Saga", folder.Directory);
                Assert.Equal(2, folder.FileCount);
                Assert.Equal(30, folder.TotalSize);
            },
            folder =>
            {
                Assert.Equal("/archive/Saga", folder.Directory);
                Assert.Equal(1, folder.FileCount);
                Assert.Equal(5, folder.TotalSize);
            });
    }

    [Fact]
    public async Task GetAllSeriesFolderGroupsAsync_BucketsEverySeriesByDirectory()
    {
        SeedFolderLibrary();

        var groups = await CreateService().GetAllSeriesFolderGroupsAsync();

        Assert.Equal(3, groups.Count);

        var saga = groups.Single(g => g.Id == "saga");
        Assert.Equal("Saga", saga.Title);
        Assert.Equal(new[] { "/lib/Saga", "/archive/Saga" }, saga.Folders.Select(f => f.Directory));
        Assert.Equal(new[] { 2, 1 }, saga.Folders.Select(f => f.FileCount));
        Assert.Equal(new long[] { 30, 5 }, saga.Folders.Select(f => f.TotalSize));

        var batman = groups.Single(g => g.Id == "batman");
        var batmanFolder = Assert.Single(batman.Folders);
        Assert.Equal("/lib/Batman", batmanFolder.Directory);
        Assert.Equal(2, batmanFolder.FileCount);
        Assert.Equal(10, batmanFolder.TotalSize);

        var unknown = groups.Single(g => g.Id == "unknown-series");
        Assert.Equal("Unknown Series", unknown.Title);
        Assert.Empty(unknown.Folders);
    }

    [Fact]
    public async Task GetAllSeriesFolderGroupsAsync_EmptyLibrary_ReturnsEmpty()
    {
        Assert.Empty(await CreateService().GetAllSeriesFolderGroupsAsync(filter: "processed"));
    }

    [Fact]
    public async Task NormalizedKeyLookups_BlankKey_ShortCircuit()
    {
        var service = CreateService();

        Assert.Empty(await service.GetFoldersForNormalizedKeyAsync(" "));
        Assert.Null(await service.GetFirstIssueFilePathForNormalizedKeyAsync(string.Empty));
        VerifyFileLoads(0);
    }

    [Fact]
    public async Task NormalizedKeyLookups_MatchImageKeyFirstThenSeriesId()
    {
        SeedFolderLibrary();
        // The pinned name moves the public id away from the image cache key, so
        // the two match paths can be told apart.
        _records = new List<SeriesMetadataCacheRecord>
        {
            new()
            {
                NormalizedKey = "saga-record",
                CanonicalTitle = "Saga",
                SeriesName = "Saga Deluxe",
                LocalImageFile = "saga.jpg",
                ImageStatus = "downloaded"
            }
        };
        var service = CreateService();

        var byImageKey = await service.GetFoldersForNormalizedKeyAsync("SAGA-RECORD");
        var byPinnedId = await service.GetFoldersForNormalizedKeyAsync("saga-deluxe");
        var byPlainId = await service.GetFoldersForNormalizedKeyAsync("batman");
        var withoutDirectories = await service.GetFoldersForNormalizedKeyAsync("unknown-series");
        var unmatched = await service.GetFoldersForNormalizedKeyAsync("not-a-series");

        Assert.Equal(new[] { "/lib/Saga", "/archive/Saga" }, byImageKey.Select(f => f.Directory));
        Assert.Equal(new[] { 2, 1 }, byImageKey.Select(f => f.FileCount));
        Assert.Equal(byImageKey.Select(f => f.Directory), byPinnedId.Select(f => f.Directory));
        var batman = Assert.Single(byPlainId);
        Assert.Equal("/lib/Batman", batman.Directory);
        Assert.Equal(2, batman.FileCount);
        Assert.Empty(withoutDirectories);
        Assert.Empty(unmatched);

        Assert.Equal("/lib/Saga/Saga 001.cbz", await service.GetFirstIssueFilePathForNormalizedKeyAsync("saga-record"));
        Assert.Equal("/lib/Saga/Saga 001.cbz", await service.GetFirstIssueFilePathForNormalizedKeyAsync("SAGA-DELUXE"));
        Assert.Equal("/lib/Batman/Batman 001.cbz", await service.GetFirstIssueFilePathForNormalizedKeyAsync("batman"));
        Assert.Equal("/", await service.GetFirstIssueFilePathForNormalizedKeyAsync("unknown-series"));
        Assert.Null(await service.GetFirstIssueFilePathForNormalizedKeyAsync("not-a-series"));
    }

    [Fact]
    public async Task Grouping_DegenerateTitles_KeepStableIdsAndAreNeverMerged()
    {
        _files = new List<ComicFile>
        {
            File("/lib/!!!/Bang 001.cbz", series: "!!!", issue: "1"),
            File("/lib/1985/1985 001.cbz", series: "1985", issue: "1"),
            File("/lib/X/X 001.cbz", series: "X", issue: "1"),
            File("/lib/1 Alpha/1 Alpha 001.cbz", series: "1 Alpha", issue: "1"),
            File("/lib/Saga/Saga 001.cbz", series: "Saga", issue: "1"),
        };
        // A pinned name that slugs to a digit-only key is rejected as the public
        // id, so the union-find representative is used instead.
        _records = new List<SeriesMetadataCacheRecord>
        {
            new() { NormalizedKey = "saga", CanonicalTitle = "Saga", SeriesName = "42" }
        };

        var result = await CreateService().GetSeriesSummariesAsync(perPage: -1);

        var idsByTitle = result.Series.ToDictionary(s => s.Title, s => s.Id);
        Assert.Equal(5, idsByTitle.Count);
        Assert.Equal("unknown-series", idsByTitle["!!!"]);
        Assert.Equal("1985", idsByTitle["1985"]);
        Assert.Equal("x", idsByTitle["X"]);
        Assert.Equal("1-alpha", idsByTitle["1 Alpha"]);
        Assert.Equal("saga", idsByTitle["42"]);
        Assert.Equal(1, result.TotalPages);
    }

    [Fact]
    public async Task Grouping_RanksRecordsAndImagesWithinOneSeries()
    {
        _records = new List<SeriesMetadataCacheRecord>
        {
            new()
            {
                NormalizedKey = "alpha", CanonicalTitle = "Alpha", LookupStatus = "error",
                LocalImageFile = "alpha.jpg", ImageStatus = "downloaded", ImageDownloadedUtc = BaseTime.AddDays(1),
                Aliases = new List<string> { "Shared Name", " " }
            },
            new()
            {
                NormalizedKey = "beta", CanonicalTitle = "Beta", LookupStatus = "manual",
                LocalImageFile = "beta.png", ImageStatus = "user", ImageDownloadedUtc = BaseTime,
                UserAliases = new List<string> { "Shared Name", string.Empty }
            },
            new()
            {
                NormalizedKey = "gamma", CanonicalTitle = "Gamma", LookupStatus = "cleared",
                LocalImageFile = "gamma.jpg", ImageStatus = "downloaded", ImageDownloadedUtc = BaseTime.AddDays(5),
                Aliases = new List<string> { "Shared Name", "Alpha" }
            },
            new()
            {
                NormalizedKey = "delta", CanonicalTitle = "Delta", LookupStatus = "mystery", SeriesName = "Delta Prime",
                LocalImageFile = "delta.png", ImageStatus = "user", ImageDownloadedUtc = BaseTime.AddDays(2),
                LocalizedTitles = new List<LocalizedTitle> { null!, new("Delta JP", "ja") }
            },
            new()
            {
                NormalizedKey = "eps", CanonicalTitle = "Eps", LookupStatus = null,
                LocalImageFile = "eps.png", ImageStatus = "user", ImageDownloadedUtc = null,
                UserAliases = null!, Aliases = null!, LocalizedTitles = null!
            },
            new() { NormalizedKey = "zeta", CanonicalTitle = "Zeta", LookupStatus = "MANUAL", LastLookupUtc = BaseTime.AddDays(10), Source = "AniList" },
            new() { NormalizedKey = "eta", CanonicalTitle = "Eta", LookupStatus = "manual", LastLookupUtc = null },
            new() { NormalizedKey = "theta", CanonicalTitle = "Theta", LookupStatus = "manual", LastLookupUtc = BaseTime.AddDays(3) },
        };
        // Every file shares one folder but names a different record, so all
        // records land in a single union-find component.
        var seriesNames = new[] { "Alpha", "Beta", "Gamma", "Delta", "Eps", "Zeta", "Eta", "Theta" };
        _files = seriesNames
            .Select((name, i) => File($"/lib/Omni/Omni {i + 1:000}.cbz", series: name, issue: (i + 1).ToString()))
            .ToList();

        var result = await CreateService().GetSeriesSummariesAsync();

        var series = Assert.Single(result.Series);
        // Highest lookup-status rank wins; the most recent lookup breaks ties.
        Assert.Equal("Zeta", series.Title);
        Assert.Equal("MANUAL", series.LookupStatus);
        Assert.Equal("AniList", series.MetadataSource);
        Assert.Equal(BaseTime.AddDays(10), series.LastLookupUtc);
        // User images beat downloaded ones; the newest user image wins.
        Assert.True(series.HasExternalImage);
        Assert.Equal("/api/series-images/delta", series.ExternalImageUrl);
        Assert.Equal(8, series.IssueCount);
        Assert.Contains("Omni", series.Aliases);
        Assert.Contains("Theta", series.Aliases);
    }

    [Fact]
    public async Task Grouping_ManualMatchOutranksNewerManualRecord()
    {
        _records = new List<SeriesMetadataCacheRecord>
        {
            new() { NormalizedKey = "kappa", CanonicalTitle = "Kappa", LookupStatus = "manual", LastLookupUtc = BaseTime.AddDays(9), Source = "AniList" },
            new() { NormalizedKey = "lambda", CanonicalTitle = "Lambda", LookupStatus = "Manual_Match", LastLookupUtc = BaseTime, Source = "ComicVine" },
        };
        _files = new List<ComicFile>
        {
            File("/lib/Pair/Pair 001.cbz", series: "Kappa", issue: "1"),
            File("/lib/Pair/Pair 002.cbz", series: "Lambda", issue: "2"),
        };

        var result = await CreateService().GetSeriesSummariesAsync();

        var series = Assert.Single(result.Series);
        Assert.Equal("Lambda", series.Title);
        Assert.Equal("Manual_Match", series.LookupStatus);
        Assert.Equal("ComicVine", series.MetadataSource);
        Assert.Equal(BaseTime, series.LastLookupUtc);
    }

    [Fact]
    public async Task Grouping_CollapsedDuplicate_PromotesImageSourceAndLatestModified()
    {
        _records = new List<SeriesMetadataCacheRecord>
        {
            new() { NormalizedKey = "saga-one", CanonicalTitle = "Saga", LookupStatus = "not_found" },
            new() { NormalizedKey = "saga-two", CanonicalTitle = "Saga", LookupStatus = "success", Source = "ComicVine", LastLookupUtc = BaseTime },
            new()
            {
                NormalizedKey = "sg-cover", CanonicalTitle = "SG Cover",
                LocalImageFile = "sg.jpg", ImageStatus = "downloaded", ImageDownloadedUtc = BaseTime
            },
        };
        _files = new List<ComicFile>
        {
            File("/lib/Saga/Saga 001.cbz", series: "Saga", issue: "1", size: 10),
            File("/lib/Saga/Saga 002.cbz", series: "Saga", issue: "2", size: 10),
            File("/lib/Saga Two/Saga Two 003.cbz", series: "SG Cover", issue: "3", size: 5, modified: BaseTime.AddMonths(5)),
        };

        var result = await CreateService().GetSeriesSummariesAsync();

        var series = Assert.Single(result.Series);
        Assert.Equal("saga-one", series.Id);
        Assert.Equal("Saga", series.Title);
        Assert.Equal(3, series.IssueCount);
        Assert.Equal(25, series.TotalSize);
        Assert.Equal(new DateTimeOffset(BaseTime.AddMonths(5)).ToUnixTimeSeconds(), series.LatestModified);
        Assert.True(series.HasExternalImage);
        Assert.Equal("/api/series-images/sg-cover", series.ExternalImageUrl);
        Assert.Equal("ComicVine", series.MetadataSource);
        Assert.Contains("Saga Two", series.Aliases);
        Assert.Contains("SG Cover", series.Aliases);
    }

    [Fact]
    public async Task Grouping_ResolverReturningNoTitle_FallsBackToCanonicalTitle()
    {
        _records = new List<SeriesMetadataCacheRecord>
        {
            new() { NormalizedKey = "saga", CanonicalTitle = "Saga Canonical" }
        };
        _files = new List<ComicFile> { File("/lib/Saga/Saga 001.cbz", series: "Saga", issue: "1") };
        var resolver = new Mock<ISeriesNameResolver>();
        resolver.Setup(r => r.ResolveForRecord(It.IsAny<SeriesMetadataCacheRecord>())).Returns((string)null!);

        var result = await CreateService(resolver.Object).GetSeriesSummariesAsync();

        var series = Assert.Single(result.Series);
        Assert.Equal("Saga Canonical", series.Title);
        Assert.Equal("Saga Canonical", series.CanonicalTitle);
        resolver.Verify(r => r.ResolveForRecord(It.Is<SeriesMetadataCacheRecord>(rec => rec.NormalizedKey == "saga")), Times.Once);
    }

    public static TheoryData<string?, string?, string> SortCases => new()
    {
        { "date", "desc", "Bravo,Charlie,Alpha" },
        { "DATE", "ASC", "Alpha,Charlie,Bravo" },
        { "size", "desc", "Alpha,Charlie,Bravo" },
        { "size", "asc", "Bravo,Charlie,Alpha" },
        { "name", "desc", "Charlie,Bravo,Alpha" },
        { null, null, "Alpha,Bravo,Charlie" },
    };

    [Theory]
    [MemberData(nameof(SortCases))]
    public async Task SeriesLists_SortByDateSizeAndName(string? sort, string? direction, string expected)
    {
        _files = new List<ComicFile>
        {
            File("/lib/Alpha/Alpha 001.cbz", series: "Alpha", issue: "1", size: 300, modified: BaseTime.AddDays(1)),
            File("/lib/Bravo/Bravo 001.cbz", series: "Bravo", issue: "1", size: 100, modified: BaseTime.AddDays(3)),
            File("/lib/Charlie/Charlie 001.cbz", series: "Charlie", issue: "1", size: 200, modified: BaseTime.AddDays(2)),
        };
        var service = CreateService();

        var full = await service.GetSeriesAsync(sort: sort, direction: direction, perPage: -1);
        var summaries = await service.GetSeriesSummariesAsync(sort: sort, direction: direction, perPage: -1);

        Assert.Equal(expected, string.Join(",", full.Series.Select(s => s.Title)));
        Assert.Equal(expected, string.Join(",", summaries.Series.Select(s => s.Title)));
        Assert.Equal(1, full.TotalPages);
        Assert.Equal(1, full.Page);
        Assert.Equal(3, full.TotalSeries);
        Assert.Equal(1, summaries.TotalPages);
    }

    [Theory]
    [InlineData("Fabulas", "Fabulas Edition")]
    [InlineData("Fables", "Fabulas Edition")]
    [InlineData("Y: The", "Y The Last Man")]
    [InlineData("Beginning", "Saga")]
    [InlineData("777", "Y The Last Man")]
    [InlineData("YTLM", "Y The Last Man")]
    public async Task GetSeriesAsync_SearchMatchesTitlesAliasesAndIssues(string search, string expectedTitle)
    {
        SeedSearchLibrary();
        var service = CreateService(SearchResolver());

        var result = await service.GetSeriesAsync(search: search);

        var series = Assert.Single(result.Series);
        Assert.Equal(expectedTitle, series.Title);
        Assert.Equal(1, result.TotalSeries);
    }

    [Fact]
    public async Task GetSeriesAsync_SearchWithoutMatches_ReturnsEmptyFirstPage()
    {
        SeedSearchLibrary();

        var result = await CreateService(SearchResolver()).GetSeriesAsync(search: "zzz-no-such-text", page: 4);

        Assert.Empty(result.Series);
        Assert.Equal(0, result.TotalSeries);
        Assert.Equal(0, result.TotalPages);
        Assert.Equal(1, result.Page);
    }

    private void SeedSearchLibrary()
    {
        _records = new List<SeriesMetadataCacheRecord>
        {
            new() { NormalizedKey = "fables", CanonicalTitle = "Fables" }
        };
        _files = new List<ComicFile>
        {
            File("/lib/Saga/Saga 001.cbz", series: "Saga", issue: "1", title: "The Beginning"),
            File("/lib/Saga/Saga 002.cbz", series: "Saga", issue: "2"),
            File("/lib/Fables/Fables 010.cbz", series: "Fables", issue: "10"),
            File("/lib/Y The Last Man/YTLM 777.cbz", series: "Y: The Last Man", issue: "777"),
        };
    }

    private static ISeriesNameResolver SearchResolver()
    {
        var resolver = new Mock<ISeriesNameResolver>();
        resolver.Setup(r => r.ResolveForRecord(It.IsAny<SeriesMetadataCacheRecord>())).Returns("Fabulas Edition");
        return resolver.Object;
    }

    [Fact]
    public async Task GetSeriesSummariesAsync_ManyDistinctViews_StaysCorrectAfterViewCacheReset()
    {
        _files = new List<ComicFile>
        {
            File("/lib/Saga/Saga 001.cbz", series: "Saga", issue: "1", title: "Opening"),
            File("/lib/Batman/Batman 001.cbz", series: "Batman", issue: "1"),
        };
        var service = CreateService(user: new TestUserContextAccessor("alice"));

        // More distinct search views than the per-snapshot view cache holds.
        for (var i = 0; i < 34; i++)
        {
            var miss = await service.GetSeriesSummariesAsync(search: $"needle-{i}");
            Assert.Empty(miss.Series);
        }
        var hit = await service.GetSeriesSummariesAsync(search: "opening", sort: null, direction: null);
        var all = await service.GetSeriesSummariesAsync();

        Assert.Equal("Saga", Assert.Single(hit.Series).Title);
        Assert.Equal(2, all.TotalSeries);
        VerifyFileLoads(1);
    }

    [Fact]
    public async Task GetSeriesSummariesAsync_MissingFilter_ReusesCachedGapDetection()
    {
        _files = new List<ComicFile>
        {
            File("/lib/Gapped/Gapped 001.cbz", series: "Gapped", issue: "1"),
            File("/lib/Gapped/Gapped 003.cbz", series: "Gapped", issue: "3"),
            File("/lib/Complete/Complete 001.cbz", series: "Complete", issue: "1"),
            File("/lib/Complete/Complete 002.cbz", series: "Complete", issue: "2"),
            File("/lib/Specials/Annual.cbz", series: "Specials", issue: "Annual"),
            File("/lib/Specials/Holiday Special.cbz", series: "Specials", issue: "Special"),
        };
        var service = CreateService(user: new TestUserContextAccessor("alice"));

        var first = await service.GetSeriesSummariesAsync(filter: "missing");
        var second = await service.GetSeriesSummariesAsync(filter: "MISSING", sort: "size", direction: "desc");

        Assert.Equal("Gapped", Assert.Single(first.Series).Title);
        Assert.Equal("Gapped", Assert.Single(second.Series).Title);
        VerifyFileLoads(1);
    }

    [Fact]
    public async Task GetSeriesSummariesAsync_ConcurrentCacheMiss_WaiterReusesSnapshotBuiltMeanwhile()
    {
        _files = new List<ComicFile> { File("/lib/Saga/Saga 001.cbz", series: "Saga", issue: "1") };
        var gate = new TaskCompletionSource<IEnumerable<ComicFile>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var loads = 0;
        _fileStore.Setup(s => s.GetFilteredFilesAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(() => Interlocked.Increment(ref loads) == 1 ? gate.Task : Task.FromResult(SelectFiles(null)));
        var service = CreateService(user: new TestUserContextAccessor("alice"));

        // The first request holds the build lock while its file load is pending;
        // the second misses the cache and queues on the lock.
        var first = service.GetSeriesSummariesAsync();
        var second = service.GetSeriesSummariesAsync();
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);

        gate.SetResult(SelectFiles(null));
        var results = await Task.WhenAll(first, second);

        Assert.All(results, r => Assert.Equal("Saga", Assert.Single(r.Series).Title));
        Assert.Equal(1, loads);
    }

    [Fact]
    public async Task NullSettingsValue_UsesDefaultsForSnapshotAndUpgradeBudget()
    {
        var settings = new Mock<IOptionsMonitor<AppSettings>>();
        settings.Setup(s => s.CurrentValue).Returns((AppSettings)null!);
        _files = new List<ComicFile> { File("/lib/Saga/Saga 001.cbz", series: "Saga") };
        _processor.Setup(p => p.GetSeriesMetadataAsync("/lib/Saga/Saga 001.cbz", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesMetadata { Title = "From Disk", Issue = "1" });
        var service = CreateService(user: new TestUserContextAccessor("alice"), settings: settings.Object);

        var result = await service.GetSeriesIssuesAsync("saga");

        Assert.NotNull(result);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("From Disk", issue.Title);
        Assert.Equal("1", issue.Issue);
    }

    [Fact]
    public async Task GetSeriesOverviewEntriesAsync_TracksCreationRangeAndReadFiles()
    {
        var feb = new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        var mar = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        _files = new List<ComicFile>
        {
            File("/lib/Saga/Saga 001.cbz", series: "Saga", issue: "1", created: feb),
            File("/lib/Saga/Saga 002.cbz", series: "Saga", issue: "2", created: mar),
            File("/lib/Forever/Forever 001.cbz", series: "Forever", issue: "1", created: DateTime.MaxValue),
        };
        _files[0].IsRead = true;

        var entries = await CreateService().GetSeriesOverviewEntriesAsync();

        var saga = entries.Single(e => e.Summary.Title == "Saga");
        Assert.Equal(feb, saga.EarliestCreatedAt);
        Assert.Equal(mar, saga.LatestCreatedAt);
        Assert.Equal(new[] { "/lib/Saga/Saga 001.cbz", "/lib/Saga/Saga 002.cbz" }, saga.FilePaths);
        Assert.Equal(new[] { "/lib/Saga/Saga 001.cbz" }, saga.ReadFilePaths);

        var forever = entries.Single(e => e.Summary.Title == "Forever");
        Assert.Equal(DateTime.MinValue, forever.EarliestCreatedAt);
        Assert.Equal(DateTime.MaxValue, forever.LatestCreatedAt);
        Assert.Empty(forever.ReadFilePaths);
    }

    [Fact]
    public async Task GetAdjacentIssueAsync_BlankPath_ReturnsNotFound()
    {
        var result = await CreateService().GetAdjacentIssueAsync("  ", "previous");

        Assert.False(result.Found);
        Assert.False(result.HasAdjacent);
        VerifyFileLoads(0);
    }

    [Fact]
    public async Task GetSeriesIssuesAsync_BlankId_ReturnsNull()
    {
        Assert.Null(await CreateService().GetSeriesIssuesAsync(" "));
        VerifyFileLoads(0);
    }

    [Fact]
    public async Task GetSeriesIssuesAsync_UnknownIdWithoutFileFilter_ReturnsNull()
    {
        SeedDriftLibrary();
        var service = CreateService();

        Assert.Null(await service.GetSeriesIssuesAsync("no-such-series"));
        Assert.Null(await service.GetSeriesIssuesAsync("no-such-series", filter: "unmatched"));
    }

    [Fact]
    public async Task GetSeriesIssuesAsync_UnknownIdUnderFileFilter_ReturnsNull()
    {
        SeedDriftLibrary();

        Assert.Null(await CreateService().GetSeriesIssuesAsync("no-such-series", filter: "no-match"));
    }

    [Theory]
    [InlineData("by-canonical", "alpha", "Beta", "/lib/Beta/Beta 002.cbz")]
    [InlineData("by-display", "gamma-rec", "Alpha", "/lib/Gamma/Gamma 003.cbz")]
    [InlineData("by-alias", "alpha", "Delta", "/lib/Delta/Delta 005.cbz")]
    public async Task GetSeriesIssuesAsync_DriftedIdUnderFileFilter_MapsToEquivalentFilteredSeries(
        string filter, string expectedId, string expectedTitle, string expectedPath)
    {
        SeedDriftLibrary();
        var resolver = new Mock<ISeriesNameResolver>();
        resolver.Setup(r => r.ResolveForRecord(It.IsAny<SeriesMetadataCacheRecord>()))
            .Returns<SeriesMetadataCacheRecord>(record => record.NormalizedKey == "gamma-rec" ? "Alpha" : null!);

        var result = await CreateService(resolver.Object).GetSeriesIssuesAsync("beta", filter: filter);

        Assert.NotNull(result);
        Assert.Equal(expectedId, result.Id);
        Assert.Equal(expectedTitle, result.Title);
        Assert.Contains(result.Issues, issue => issue.FilePath == expectedPath);
    }

    [Theory]
    [InlineData("no-match")]
    [InlineData("empty")]
    public async Task GetSeriesIssuesAsync_FilterExcludesSeries_ReturnsEmptySeriesShell(string filter)
    {
        SeedDriftLibrary();

        var result = await CreateService().GetSeriesIssuesAsync("beta", filter: filter, page: 3);

        Assert.NotNull(result);
        Assert.Equal("beta", result.Id);
        Assert.Equal("Alpha", result.Title);
        Assert.Equal("Alpha", result.CanonicalTitle);
        Assert.Equal(new[] { "Beta" }, result.Aliases);
        Assert.Empty(result.Issues);
        Assert.Equal(0, result.IssueCount);
        Assert.Equal(string.Empty, result.CoverFilePath);
        Assert.Equal(1, result.Page);
        Assert.Equal(0, result.TotalPages);
    }

    // Unfiltered, "/lib/Alpha" (series "Beta") groups under id "beta" with
    // canonical title "Alpha". Each filter returns a different subset whose
    // union-find representative (and so the series id) drifts away from "beta".
    private void SeedDriftLibrary()
    {
        _files = new List<ComicFile>
        {
            File("/lib/Alpha/Alpha 001.cbz", series: "Beta", issue: "1"),
            File("/lib/Omega/Omega 001.cbz", series: "Omega", issue: "1"),
        };
        _records = new List<SeriesMetadataCacheRecord>
        {
            new() { NormalizedKey = "gamma-rec", CanonicalTitle = "Gamma Canon" }
        };
        _filteredFiles["by-canonical"] = new List<ComicFile>
        {
            File("/lib/Beta/Beta 002.cbz", series: "Alpha", issue: "2"),
        };
        _filteredFiles["by-display"] = new List<ComicFile>
        {
            File("/lib/Gamma/Gamma 003.cbz", series: "Gamma Canon", issue: "3"),
        };
        _filteredFiles["by-alias"] = new List<ComicFile>
        {
            File("/lib/Kappa/Kappa 001.cbz", series: "Kappa", issue: "1"),
            File("/lib/Delta/Delta 004.cbz", series: "Zulu", issue: "4"),
            File("/lib/Delta/Delta 005.cbz", series: "Alpha", issue: "5"),
        };
        _filteredFiles["no-match"] = new List<ComicFile>
        {
            File("/lib/Omega/Omega 002.cbz", series: "Omicron", issue: "2"),
        };
        _filteredFiles["empty"] = new List<ComicFile>();
    }

    [Fact]
    public async Task GetSeriesIssuesAsync_UpgradeKeepsCachedFieldsAndSurvivesProcessorFailures()
    {
        _files = new List<ComicFile>
        {
            File("/lib/Upgrade/Upgrade 001.cbz", series: "Upgrade", title: "Kept Title", volume: "V1", publisher: "Pub", year: 2001),
            File("/lib/Upgrade/Upgrade 002.cbz", series: "Upgrade"),
            File("/lib/Upgrade/Upgrade 003.cbz", series: "Upgrade"),
            new()
            {
                FilePath = "/lib/Upgrade/noname",
                FileName = string.Empty,
                Directory = "/lib/Upgrade",
                LastModified = BaseTime,
                Metadata = new ComicMetadata { Series = "Upgrade" }
            },
        };
        _processor.Setup(p => p.GetSeriesMetadataAsync("/lib/Upgrade/Upgrade 001.cbz", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesMetadata { Title = "Disk Title", Issue = "1", Volume = "Disk Volume", Publisher = "Disk Pub", Year = 1999 });
        _processor.Setup(p => p.GetSeriesMetadataAsync("/lib/Upgrade/Upgrade 002.cbz", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("corrupt archive"));
        _processor.Setup(p => p.GetSeriesMetadataAsync("/lib/Upgrade/Upgrade 003.cbz", It.IsAny<CancellationToken>()))
            .ReturnsAsync((SeriesMetadata?)null);

        var result = await CreateService().GetSeriesIssuesAsync("upgrade", perPage: 0);

        Assert.NotNull(result);
        Assert.Equal(4, result.IssueCount);
        Assert.Equal(1, result.PerPage);
        Assert.Equal(4, result.TotalPages);
        var upgraded = Assert.Single(result.Issues);
        Assert.Equal("/lib/Upgrade/Upgrade 001.cbz", upgraded.FilePath);
        Assert.Equal("Kept Title", upgraded.Title);
        Assert.Equal("1", upgraded.Issue);
        Assert.Equal("V1", upgraded.Volume);
        Assert.Equal("Pub", upgraded.Publisher);
        Assert.Equal(2001, upgraded.Year);

        var all = await CreateService().GetSeriesIssuesAsync("upgrade", perPage: -1);

        Assert.NotNull(all);
        Assert.Equal(new[] { "1", "2", "3", null }, all.Issues.Select(i => i.Issue));
        var failed = all.Issues.Single(i => i.FilePath == "/lib/Upgrade/Upgrade 002.cbz");
        Assert.Null(failed.Title);
        Assert.Equal("/lib/Upgrade/noname", all.Issues[^1].FilePath);
        _processor.Verify(p => p.GetSeriesMetadataAsync("/lib/Upgrade/Upgrade 002.cbz", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetSeriesIssuesAsync_UpgradeFillsMissingTitleButKeepsCachedIssueNumber()
    {
        _files = new List<ComicFile> { File("/lib/Keep/Keep 001.cbz", series: "Keep", issue: "7") };
        _processor.Setup(p => p.GetSeriesMetadataAsync("/lib/Keep/Keep 001.cbz", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesMetadata { Title = "Disk Title", Issue = "99" });

        var result = await CreateService().GetSeriesIssuesAsync("keep");

        Assert.NotNull(result);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("Disk Title", issue.Title);
        Assert.Equal("7", issue.Issue);
    }

    [Fact]
    public async Task GetSeriesIssuesForIdsAsync_ResolvesDistinctKnownIds()
    {
        _files = new List<ComicFile>
        {
            File("/lib/Saga/Saga 002.cbz", series: "Saga", issue: "2", title: "Two"),
            File("/lib/Saga/Saga 001.cbz", series: "Saga", issue: "1", title: "One"),
            File("/lib/Batman/Batman 001.cbz", series: "Batman", issue: "1", title: "Origin"),
        };

        var results = await CreateService().GetSeriesIssuesForIdsAsync(
            new[] { "saga", string.Empty, "  ", "saga", "missing", "BATMAN" });

        Assert.Equal(2, results.Count);
        var saga = results["saga"];
        Assert.Equal(2, saga.IssueCount);
        Assert.Equal(2, saga.PerPage);
        Assert.Equal(1, saga.TotalPages);
        Assert.Equal(new[] { "/lib/Saga/Saga 001.cbz", "/lib/Saga/Saga 002.cbz" }, saga.Issues.Select(i => i.FilePath));
        Assert.Equal("Batman", results["BATMAN"].Title);
        Assert.False(results.ContainsKey("missing"));
    }

    [Fact]
    public async Task GetSeriesIssuesForIdsAsync_NullIds_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => CreateService().GetSeriesIssuesForIdsAsync(null!));
    }

    [Fact]
    public async Task GetSeriesIssuesForIdsAsync_CanceledToken_Throws()
    {
        _files = new List<ComicFile> { File("/lib/Saga/Saga 001.cbz", series: "Saga", issue: "1") };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateService().GetSeriesIssuesForIdsAsync(new[] { "saga" }, cts.Token));
    }
}
