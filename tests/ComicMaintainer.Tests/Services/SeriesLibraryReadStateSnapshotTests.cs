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
/// Covers how <see cref="SeriesLibraryService"/> caches the library when the file store keeps
/// per-user read state apart from the file list (<see cref="IUserReadStateSource"/>): one
/// snapshot per file filter is shared by every user, read state is applied per request, one
/// user's read changes invalidate nobody else's view, and different views never wait on a
/// single global build lock.
/// </summary>
public class SeriesLibraryReadStateSnapshotTests
{
    private readonly Mock<IFileStoreService> _fileStore = new();
    private readonly Mock<ISeriesMetadataCacheService> _metadataCache = new();
    private readonly Mock<IOptionsMonitor<AppSettings>> _settings = new();
    private readonly TestUserContextAccessor _user = new("alice");
    private readonly Dictionary<string, long> _readVersions = new() { ["alice"] = 0, ["bob"] = 0 };
    private readonly Dictionary<string, HashSet<string>> _readPaths = new()
    {
        ["alice"] = new HashSet<string>(StringComparer.Ordinal),
        ["bob"] = new HashSet<string>(StringComparer.Ordinal),
    };
    private readonly List<ComicFile> _files =
    [
        File("/library/Batman/Batman 001.cbz"),
        File("/library/Batman/Batman 002.cbz"),
        File("/library/Saga/Saga 001.cbz"),
    ];
    private long _fileVersion = 1;
    private Func<string?, Task>? _beforeFileLoad;

    public SeriesLibraryReadStateSnapshotTests()
    {
        _fileStore.As<IChangeVersionSource>().SetupGet(v => v.ChangeVersion).Returns(() => _fileVersion);
        var readState = _fileStore.As<IUserReadStateSource>();
        readState.Setup(r => r.GetReadStateVersion(It.IsAny<string>()))
            .Returns((string userId) => _readVersions.GetValueOrDefault(userId));
        readState.Setup(r => r.GetReadFilePathsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string userId, CancellationToken _) =>
                (IReadOnlySet<string>)new HashSet<string>(_readPaths[userId], StringComparer.Ordinal));

        // Like the real store: the copies it returns carry the *current* user's read flag,
        // and the read/unread filters select by it.
        _fileStore.Setup(s => s.GetFilteredFilesAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(async (string? filter, CancellationToken _) =>
            {
                if (_beforeFileLoad is not null)
                {
                    await _beforeFileLoad(filter);
                }

                var read = _readPaths[_user.UserId!];
                var copies = _files.Select(f =>
                {
                    var copy = f.ShallowCopy();
                    copy.IsRead = read.Contains(f.FilePath);
                    return copy;
                });
                return (filter switch
                {
                    "read" => copies.Where(f => f.IsRead),
                    "unread" => copies.Where(f => !f.IsRead),
                    _ => copies,
                }).ToList();
            });

        _metadataCache.As<IChangeVersionSource>().SetupGet(v => v.ChangeVersion).Returns(1);
        _metadataCache.Setup(m => m.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SeriesMetadataCacheRecord>());
        _settings.Setup(s => s.CurrentValue).Returns(new AppSettings());
    }

    private static ComicFile File(string path) => new()
    {
        FilePath = path,
        FileName = Path.GetFileName(path),
        Directory = Path.GetDirectoryName(path)!.Replace('\\', '/'),
        FileSize = 10,
        LastModified = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    private SeriesLibraryService CreateService() => new(
        _fileStore.Object,
        new Mock<IComicProcessorService>().Object,
        _metadataCache.Object,
        _settings.Object,
        new Mock<ILogger<SeriesLibraryService>>().Object,
        seriesNameResolver: null,
        userContext: _user);

    private int Builds(string? filter) => _fileStore.Invocations.Count(i =>
        i.Method.Name == nameof(IFileStoreService.GetFilteredFilesAsync) && Equals(i.Arguments[0], filter));

    private void MarkRead(string userId, string path)
    {
        _readPaths[userId].Add(path);
        _readVersions[userId]++;
    }

    private async Task<SeriesIssuesResult> BatmanAsync(SeriesLibraryService service, string? filter = null)
    {
        var summaries = await service.GetSeriesSummariesAsync(search: "Batman");
        return (await service.GetSeriesIssuesAsync(Assert.Single(summaries.Series).Id, filter))!;
    }

    [Fact]
    public async Task EveryUser_SharesOneSnapshotPerFileFilter()
    {
        var service = CreateService();

        await BatmanAsync(service);
        _user.UserId = "bob";
        await BatmanAsync(service);
        await service.GetSeriesOverviewEntriesAsync();

        // Regression: snapshots were keyed by user, so every user rebuilt the whole library.
        Assert.Equal(1, Builds(null));
    }

    [Fact]
    public async Task OneUsersReadChange_LeavesTheSharedSnapshotAndOtherUsersAlone()
    {
        var service = CreateService();
        await BatmanAsync(service);
        _user.UserId = "bob";
        await BatmanAsync(service);

        MarkRead("alice", "/library/Batman/Batman 001.cbz");

        var bobs = await BatmanAsync(service);
        _user.UserId = "alice";
        var alices = await BatmanAsync(service);

        Assert.Equal(1, Builds(null));
        Assert.All(bobs.Issues, issue => Assert.False(issue.Read));
        Assert.True(alices.Issues.Single(i => i.FileName == "Batman 001.cbz").Read);
        Assert.False(alices.Issues.Single(i => i.FileName == "Batman 002.cbz").Read);
    }

    [Fact]
    public async Task SharedSnapshot_NeverCarriesTheReadStateOfTheUserWhoTriggeredTheBuild()
    {
        MarkRead("alice", "/library/Saga/Saga 001.cbz");
        var service = CreateService();

        // Alice's request builds the snapshot; the store stamps her read flag on its copies.
        var alicesOverview = await service.GetSeriesOverviewEntriesAsync();
        _user.UserId = "bob";
        var bobsOverview = await service.GetSeriesOverviewEntriesAsync();
        Assert.Equal(1, Builds(null));
        var bobsSeries = await service.GetSeriesAsync(search: "Saga");

        Assert.Contains(alicesOverview, e => e.ReadFilePaths.Contains("/library/Saga/Saga 001.cbz"));
        Assert.All(bobsOverview, e => Assert.Empty(e.ReadFilePaths));
        Assert.False(Assert.Single(Assert.Single(bobsSeries.Series).Issues).Read);
    }

    [Fact]
    public async Task ReadFilters_AreCachedPerUserAndInvalidatedOnlyByThatUsersReadChanges()
    {
        var service = CreateService();
        await BatmanAsync(service, "unread");
        _user.UserId = "bob";
        await BatmanAsync(service, "unread");
        Assert.Equal(2, Builds("unread"));

        // Alice reads an issue: only her "unread" view is rebuilt.
        MarkRead("alice", "/library/Batman/Batman 002.cbz");
        var bobs = await BatmanAsync(service, "unread");
        Assert.Equal(2, Builds("unread"));
        _user.UserId = "alice";
        var alices = await BatmanAsync(service, "unread");
        Assert.Equal(3, Builds("unread"));

        Assert.Equal(2, bobs.Issues.Count);
        Assert.Equal("Batman 001.cbz", Assert.Single(alices.Issues).FileName);

        // Bob's cached view survived Alice's rebuild.
        _user.UserId = "bob";
        await BatmanAsync(service, "unread");
        Assert.Equal(3, Builds("unread"));
    }

    [Fact]
    public async Task DifferentViews_DoNotQueueBehindOneGlobalBuildLock()
    {
        var service = CreateService();
        var processedBuildStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProcessedBuild = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _beforeFileLoad = async filter =>
        {
            if (filter == "processed")
            {
                processedBuildStarted.TrySetResult();
                await releaseProcessedBuild.Task;
            }
        };

        var slowBuild = service.GetSeriesSummariesAsync(filter: "processed");
        await processedBuildStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Regression: one SemaphoreSlim serialized every snapshot build, so this request
        // waited for the unrelated "processed" build to finish.
        var unfiltered = await service.GetSeriesSummariesAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, unfiltered.TotalSeries);
        Assert.False(slowBuild.IsCompleted);
        releaseProcessedBuild.SetResult();
        await slowBuild;
    }
}
