using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Covers the cached library snapshot in <see cref="SeriesLibraryService"/>:
/// repeated cache-only reads must reuse one grouping, and any change to the
/// file store, read state, metadata cache, user or display-language setting
/// must produce a fresh one.
/// </summary>
public class SeriesLibrarySnapshotCacheTests
{
    private readonly Mock<IFileStoreService> _fileStore = new();
    private readonly Mock<IChangeVersionSource> _fileVersions;
    private readonly Mock<ISeriesMetadataCacheService> _metadataCache = new();
    private readonly Mock<IChangeVersionSource> _metadataVersions;
    private readonly Mock<IComicProcessorService> _processor = new();
    private readonly AppSettings _appSettings = new();
    private readonly Mock<IOptionsMonitor<AppSettings>> _settings = new();
    private readonly TestUserContextAccessor _user = new("alice");
    private long _fileVersion = 1;
    private long _metadataVersion = 1;
    private List<ComicFile> _files;

    public SeriesLibrarySnapshotCacheTests()
    {
        _fileVersions = _fileStore.As<IChangeVersionSource>();
        _fileVersions.SetupGet(v => v.ChangeVersion).Returns(() => _fileVersion);
        _metadataVersions = _metadataCache.As<IChangeVersionSource>();
        _metadataVersions.SetupGet(v => v.ChangeVersion).Returns(() => _metadataVersion);
        _settings.Setup(s => s.CurrentValue).Returns(() => _appSettings);

        _files = new List<ComicFile>
        {
            File("/library/Batman/Batman 001.cbz", "Batman"),
            File("/library/Batman/Batman 002.cbz", "Batman"),
            File("/library/Saga/Saga 001.cbz", "Saga"),
        };
        _fileStore.Setup(s => s.GetFilteredFilesAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _files.Select(f => f.ShallowCopy()).ToList());
        _metadataCache.Setup(m => m.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SeriesMetadataCacheRecord>());
    }

    private static ComicFile File(string path, string series) => new()
    {
        FilePath = path,
        FileName = Path.GetFileName(path),
        Directory = Path.GetDirectoryName(path) ?? string.Empty,
        FileSize = 10,
        LastModified = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        Metadata = new ComicMetadata { Series = series }
    };

    private SeriesLibraryService CreateService(IUserContextAccessor? user) => new(
        _fileStore.Object,
        _processor.Object,
        _metadataCache.Object,
        _settings.Object,
        new Mock<ILogger<SeriesLibraryService>>().Object,
        seriesNameResolver: null,
        userContext: user);

    private void VerifyBuilds(int times) =>
        _fileStore.Verify(s => s.GetFilteredFilesAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Exactly(times));

    [Fact]
    public async Task RepeatedSummaryRequests_ReuseSnapshot()
    {
        var service = CreateService(_user);

        var first = await service.GetSeriesSummariesAsync(offset: 0, limit: 1);
        var second = await service.GetSeriesSummariesAsync(offset: 1, limit: 1);
        await service.GetSeriesOverviewEntriesAsync();
        await service.GetSeriesIssuesAsync(first.Series[0].Id);

        VerifyBuilds(1);
        Assert.Equal(2, first.TotalSeries);
        Assert.NotEqual(first.Series[0].Id, second.Series[0].Id);
    }

    [Fact]
    public async Task FileStoreChange_RebuildsSnapshot()
    {
        var service = CreateService(_user);
        var before = await service.GetSeriesSummariesAsync();
        Assert.Equal(2, before.TotalSeries);

        _files.Add(File("/library/Hellboy/Hellboy 001.cbz", "Hellboy"));
        // Unchanged version: cached result is still served.
        Assert.Equal(2, (await service.GetSeriesSummariesAsync()).TotalSeries);

        _fileVersion++;
        var after = await service.GetSeriesSummariesAsync();

        Assert.Equal(3, after.TotalSeries);
        VerifyBuilds(2);
    }

    [Fact]
    public async Task MetadataCacheChange_RebuildsSnapshot()
    {
        var service = CreateService(_user);
        await service.GetSeriesSummariesAsync();

        _metadataCache.Setup(m => m.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SeriesMetadataCacheRecord>
            {
                new() { NormalizedKey = "saga", CanonicalTitle = "Saga", Source = "ComicVine", LookupStatus = "success" }
            });
        _metadataVersion++;

        var result = await service.GetSeriesSummariesAsync(search: "Saga");

        Assert.Equal("ComicVine", Assert.Single(result.Series).MetadataSource);
        VerifyBuilds(2);
    }

    [Fact]
    public async Task DefaultLanguageChange_RebuildsSnapshot()
    {
        var service = CreateService(_user);
        await service.GetSeriesSummariesAsync();

        _appSettings.DefaultPreferredLanguage = "ja";
        await service.GetSeriesSummariesAsync();

        VerifyBuilds(2);
    }

    [Fact]
    public async Task DifferentUsers_GetSeparateSnapshots()
    {
        var service = CreateService(_user);
        await service.GetSeriesSummariesAsync();

        _user.UserId = "bob";
        await service.GetSeriesSummariesAsync();
        _user.UserId = "alice";
        await service.GetSeriesSummariesAsync();

        VerifyBuilds(2);
    }

    [Fact]
    public async Task DifferentFileFilters_GetSeparateSnapshots()
    {
        var service = CreateService(_user);
        await service.GetSeriesSummariesAsync(filter: "unmarked");
        await service.GetSeriesSummariesAsync(filter: null);
        await service.GetSeriesSummariesAsync(filter: "unmarked");

        VerifyBuilds(2);
    }

    [Fact]
    public async Task WithoutUserContext_NeverCaches()
    {
        var service = CreateService(user: null);
        await service.GetSeriesSummariesAsync();
        await service.GetSeriesSummariesAsync();

        VerifyBuilds(2);
    }

    [Fact]
    public async Task SourcesWithoutChangeVersions_NeverCache()
    {
        var fileStore = new Mock<IFileStoreService>();
        fileStore.Setup(s => s.GetFilteredFilesAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _files.ToList());
        var service = new SeriesLibraryService(
            fileStore.Object, _processor.Object, _metadataCache.Object, _settings.Object,
            new Mock<ILogger<SeriesLibraryService>>().Object, userContext: _user);

        await service.GetSeriesSummariesAsync();
        await service.GetSeriesSummariesAsync();

        fileStore.Verify(s => s.GetFilteredFilesAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task SummaryViews_AreSlicedConsistentlyAcrossPages()
    {
        for (var i = 0; i < 25; i++)
        {
            _files.Add(File($"/library/S{i:D2}/S{i:D2} 001.cbz", $"Series {i:D2}"));
        }
        _fileVersion++;
        var service = CreateService(_user);

        var pages = new List<string>();
        for (var offset = 0; offset < 30; offset += 10)
        {
            var page = await service.GetSeriesSummariesAsync(sort: "name", direction: "desc", offset: offset, limit: 10);
            pages.AddRange(page.Series.Select(s => s.Title));
            Assert.Equal(27, page.TotalSeries);
        }

        Assert.Equal(27, pages.Count);
        Assert.Equal(pages.OrderByDescending(t => t, StringComparer.OrdinalIgnoreCase), pages);
        VerifyBuilds(1);
    }

    [Fact]
    public async Task GetSeriesAsync_DoesNotMutateSharedSnapshot()
    {
        var service = CreateService(_user);
        var summaries = await service.GetSeriesSummariesAsync();
        var batman = summaries.Series.Single(s => s.Title == "Batman");

        var issues = await service.GetSeriesIssuesAsync(batman.Id, perPage: -1);
        issues!.Issues.Clear();

        var again = await service.GetSeriesIssuesAsync(batman.Id, perPage: -1);
        Assert.Equal(2, again!.Issues.Count);
    }

    [Fact]
    public async Task AdjacentIssue_UsesSnapshotIndex()
    {
        var service = CreateService(_user);

        var next = await service.GetAdjacentIssueAsync("/library/Batman/Batman 001.cbz", "next");
        var previous = await service.GetAdjacentIssueAsync("/library/Batman/Batman 002.cbz", "previous");
        var missing = await service.GetAdjacentIssueAsync("/library/Nope/Nope.cbz", "next");

        Assert.True(next.HasAdjacent);
        Assert.Equal("/library/Batman/Batman 002.cbz", next.FilePath);
        Assert.Equal("/library/Batman/Batman 001.cbz", previous.FilePath);
        Assert.False(missing.Found);
        VerifyBuilds(1);
    }
}
