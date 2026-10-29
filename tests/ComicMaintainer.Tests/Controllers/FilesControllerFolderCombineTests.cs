using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.WebApi.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Controllers;

/// <summary>
/// Covers combinable-folder discovery (FilesController.CombinableFolders.cs), the
/// combine-folders plan and endpoints (FilesController.CombineFolders.cs) and the
/// alias bookkeeping behind them (FilesController.FolderCombineAliases.cs).
/// </summary>
public sealed class FilesControllerFolderCombineTests : IDisposable
{
    private const string NoGroupError = "No combinable folder group matches the request. The library may have changed; please reload.";
    private const string CombinableGroupsCacheKey = "FilesController.CombinableFolderGroups";

    private static readonly DateTime T1 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T2 = new(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T3 = new(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "fc-n2a-tests", Guid.NewGuid().ToString("N"));
    private readonly Mock<IFileStoreService> _fileStore = new();
    private readonly Mock<IComicProcessorService> _processor = new();
    private readonly Mock<IProcessingHistoryService> _history = new();
    private readonly Mock<ISeriesLibraryService> _seriesLibrary = new();
    private readonly Mock<ISeriesMetadataCacheService> _metadataCache = new();
    private readonly Mock<IOptionsMonitor<AppSettings>> _settings = new();
    private readonly List<(string Title, List<string> Aliases)> _aliasWrites = new();
    private readonly List<string> _normalizedPaths = new();
    private long _fileStoreVersion = 1;

    public FilesControllerFolderCombineTests()
    {
        Directory.CreateDirectory(_root);
        _settings.Setup(s => s.CurrentValue).Returns(new AppSettings { WatchedDirectory = _root });

        // Both change-version sources must be present for the combinable-groups cache to engage.
        _fileStore.As<IChangeVersionSource>().SetupGet(v => v.ChangeVersion).Returns(() => _fileStoreVersion);
        _metadataCache.As<IChangeVersionSource>().SetupGet(v => v.ChangeVersion).Returns(1);

        _metadataCache
            .Setup(c => c.NormalizeKey(It.IsAny<string?>()))
            .Returns((string? title) => title?.Trim().ToLowerInvariant() ?? string.Empty);
        _metadataCache
            .Setup(c => c.SetUserAliasesAsync(It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, IEnumerable<string>, CancellationToken>((title, aliases, _) => _aliasWrites.Add((title, aliases.ToList())))
            .ReturnsAsync(new SeriesMetadataCacheRecord());
        _processor
            .Setup(p => p.NormalizeAndRenameFilesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<string>, CancellationToken>((paths, _) => _normalizedPaths.AddRange(paths))
            .ReturnsAsync(Guid.NewGuid());

        // Run the per-item operation and post-loop hook inline so the effects of a
        // combine job are observable as soon as the action returns.
        _processor
            .Setup(p => p.RunCustomBatchJobAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<Func<string, CancellationToken, Task<bool>>>(),
                It.IsAny<string>(),
                It.IsAny<Func<CancellationToken, Task>?>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (string _, IEnumerable<string> items, Func<string, CancellationToken, Task<bool>> operation, string _, Func<CancellationToken, Task>? postLoop, CancellationToken token) =>
            {
                foreach (var item in items)
                {
                    await operation(item, token);
                }
                if (postLoop is not null)
                {
                    await postLoop(token);
                }
                return Guid.NewGuid();
            });
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private FilesController CreateController(
        IMemoryCache? memoryCache = null,
        IDbContextFactory<ComicMaintainerDbContext>? dbFactory = null) =>
        new(
            _fileStore.Object,
            _processor.Object,
            _history.Object,
            _seriesLibrary.Object,
            NullLogger<FilesController>.Instance,
            _settings.Object,
            dbContextFactory: dbFactory,
            metadataCache: _metadataCache.Object,
            memoryCache: memoryCache);

    private string Dir(string name) => Path.Combine(_root, name);

    private static ComicFile MakeFile(string directory, string fileName, string? series, DateTime modified, string? volume = null) => new()
    {
        FilePath = Path.Combine(directory, fileName),
        FileName = fileName,
        Directory = directory,
        Metadata = series is null && volume is null ? null : new ComicMetadata { Series = series, Volume = volume },
        LastModified = modified,
        FileSize = 100
    };

    private void SetupFiles(params ComicFile[] files) =>
        _fileStore.Setup(f => f.GetAllFilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(files);

    private void SetupFooGroup() => SetupFiles(
        MakeFile(Dir("Foo A"), "Foo 001.cbz", "Foo", T1),
        MakeFile(Dir("Foo B"), "Foo 002.cbz", "Foo", T2));

    private void SetupSeriesGroups(params SeriesFoldersResult[] series) =>
        _seriesLibrary
            .Setup(s => s.GetAllSeriesFolderGroupsAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(series);

    private static List<SeriesFolderDto> Folders(params string[] directories) =>
        directories.Select(d => new SeriesFolderDto { Directory = d, FileCount = 1 }).ToList();

    private static object? Prop(object? value, string name) => value?.GetType().GetProperty(name)?.GetValue(value);

    private static List<object> Items(object? value) => Assert.IsAssignableFrom<IEnumerable<object>>(value).ToList();

    private static async Task<List<FilesController.CombinableFolderGroupDto>> GetGroupsAsync(FilesController controller)
    {
        var result = await controller.GetCombinableFolders();
        return GroupsOf(result);
    }

    private static List<FilesController.CombinableFolderGroupDto> GroupsOf(ActionResult<object> result)
    {
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        return Assert.IsType<List<FilesController.CombinableFolderGroupDto>>(Prop(ok.Value, "groups"));
    }

    private static void AssertBadRequest(ActionResult? result, string expectedError)
    {
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(expectedError, Prop(badRequest.Value, "error"));
    }

    private static void AssertStatus(ActionResult? result, int expectedStatus, object? expectedValue)
    {
        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(expectedStatus, objectResult.StatusCode);
        Assert.Equal(expectedValue, objectResult.Value);
    }

    private sealed class InMemoryDbFactory(DbContextOptions<ComicMaintainerDbContext> options)
        : IDbContextFactory<ComicMaintainerDbContext>
    {
        public ComicMaintainerDbContext CreateDbContext() => new(options);

        public Task<ComicMaintainerDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    // ------------------------------------------------------------ groups cache

    [Fact]
    public async Task GetCombinableFolders_VersionsUnchanged_ServesRepeatRequestsFromCache()
    {
        SetupFooGroup();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var controller = CreateController(memoryCache: cache);

        var first = await GetGroupsAsync(controller);
        var second = await GetGroupsAsync(controller);

        Assert.Equal("foo", Assert.Single(first).GroupKey);
        Assert.Equal("foo", Assert.Single(second).GroupKey);
        _fileStore.Verify(f => f.GetAllFilesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetCombinableFolders_FileStoreVersionChanges_RebuildsGroups()
    {
        SetupFooGroup();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var controller = CreateController(memoryCache: cache);
        Assert.Single(await GetGroupsAsync(controller));

        SetupFiles(
            MakeFile(Dir("Foo A"), "Foo 001.cbz", "Foo", T1),
            MakeFile(Dir("Bar A"), "Bar 001.cbz", "Bar", T2));
        _fileStoreVersion = 2;
        var rebuilt = await GetGroupsAsync(controller);

        Assert.Empty(rebuilt);
        _fileStore.Verify(f => f.GetAllFilesAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task GetCombinableFolders_CachedEntryIsNull_RebuildsAndCachesGroups()
    {
        SetupFooGroup();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        cache.Set<object?>(CombinableGroupsCacheKey, null);
        var controller = CreateController(memoryCache: cache);

        var groups = await GetGroupsAsync(controller);

        Assert.Equal("foo", Assert.Single(groups).GroupKey);
        Assert.NotNull(cache.Get(CombinableGroupsCacheKey));
    }

    [Fact]
    public async Task GetCombinableFolders_ConcurrentRequests_ShareASingleBuild()
    {
        IEnumerable<ComicFile> files = new[]
        {
            MakeFile(Dir("Foo A"), "Foo 001.cbz", "Foo", T1),
            MakeFile(Dir("Foo B"), "Foo 002.cbz", "Foo", T2)
        };
        var buildStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBuild = new TaskCompletionSource<IEnumerable<ComicFile>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _fileStore
            .Setup(f => f.GetAllFilesAsync(It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                buildStarted.TrySetResult();
                return releaseBuild.Task;
            });
        using var cache = new MemoryCache(new MemoryCacheOptions());

        Task<ActionResult<object>> firstRequest;
        Task<ActionResult<object>> secondRequest;
        try
        {
            firstRequest = CreateController(memoryCache: cache).GetCombinableFolders();
            await buildStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));

            // The first request holds the build lock until its file scan completes.
            secondRequest = CreateController(memoryCache: cache).GetCombinableFolders();
            Assert.False(secondRequest.IsCompleted);
        }
        finally
        {
            releaseBuild.TrySetResult(files);
        }

        var results = await Task.WhenAll(firstRequest, secondRequest);

        Assert.All(results, result => Assert.Equal("foo", Assert.Single(GroupsOf(result)).GroupKey));
        _fileStore.Verify(f => f.GetAllFilesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // ------------------------------------------------------- alias-index pass

    [Fact]
    public async Task GetCombinableFolders_FileStoreReturnsNull_ReturnsNoGroups()
    {
        _fileStore.Setup(f => f.GetAllFilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync((IEnumerable<ComicFile>)null!);

        Assert.Empty(await GetGroupsAsync(CreateController()));
    }

    [Fact]
    public async Task GetCombinableFolders_FilesWithoutDirectory_AreBucketedByTheirPathsParent()
    {
        var older = MakeFile(Dir("Foo A"), "Foo 001.cbz", "Foo", T1);
        var newer = MakeFile(Dir("Foo B"), "Foo 002.cbz", "Foo", T2);
        older.Directory = string.Empty;
        newer.Directory = " ";
        SetupFiles(older, newer);

        var group = Assert.Single(await GetGroupsAsync(CreateController()));

        Assert.Equal("foo", group.GroupKey);
        Assert.Equal(new[] { Dir("Foo B"), Dir("Foo A") }, group.Folders.Select(f => f.Directory));
        Assert.Equal(Dir("Foo B"), group.SuggestedDestinationDirectory);
        Assert.True(group.Folders[0].IsSuggested);
        Assert.False(group.Folders[1].IsSuggested);
    }

    [Fact]
    public async Task GetCombinableFolders_VolumeMetadata_IsPartOfTheGroupKey()
    {
        SetupFiles(
            MakeFile(Dir("Saga A"), "Saga 001.cbz", "Saga", T1, volume: " V2 "),
            MakeFile(Dir("Saga B"), "Saga 002.cbz", "Saga", T2, volume: "v2"));

        var group = Assert.Single(await GetGroupsAsync(CreateController()));

        Assert.Equal("saga|v2", group.GroupKey);
        Assert.Equal("Saga", group.SeriesName);
        Assert.Equal("V2", group.Volume);
        Assert.Equal(2, group.TotalFileCount);
    }

    [Fact]
    public async Task GetCombinableFolders_FilesWithoutUsableSeriesNames_AreNotGrouped()
    {
        SetupFiles(
            // Nothing (metadata, file name, folder) yields a series name.
            new ComicFile { FilePath = "/001.cbz", FileName = "001.cbz", Directory = "/", LastModified = T1 },
            // The series name normalizes to an empty key.
            MakeFile(Dir("Bang A"), "!!! 001.cbz", "!!!", T1),
            MakeFile(Dir("Bang B"), "!!! 002.cbz", "!!!", T2),
            // Digit-only series key and a file name without a stem.
            MakeFile(Dir("Twelve A"), ".cbz", "12", T1),
            // A file path without a parent directory.
            new ComicFile { FilePath = "/", FileName = string.Empty, Directory = Dir("Weird"), Metadata = new ComicMetadata { Series = "Weird" }, LastModified = T1 });

        Assert.Empty(await GetGroupsAsync(CreateController()));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetCombinableFolders_MetadataCacheUnavailable_FallsBackToFileSeriesNames(bool ioFailure)
    {
        Exception failure = ioFailure ? new IOException("disk") : new InvalidOperationException("db closed");
        _metadataCache.Setup(c => c.GetAllAsync(It.IsAny<CancellationToken>())).ThrowsAsync(failure);
        SetupFooGroup();

        var group = Assert.Single(await GetGroupsAsync(CreateController()));

        Assert.Equal("foo", group.GroupKey);
        Assert.Equal("Foo", group.SeriesName);
    }

    [Fact]
    public async Task GetCombinableFolders_AliasRecordWithoutCanonicalTitle_StillBridgesFolders()
    {
        _metadataCache.Setup(c => c.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<SeriesMetadataCacheRecord>
        {
            new() { CanonicalTitle = string.Empty, Aliases = new() { "The Dark Knight Returns", "  ", "DKR" }, UserAliases = null! },
            // Titles that normalize to nothing must not create a bridge.
            new() { CanonicalTitle = "!!!", Aliases = null!, UserAliases = new() { "???" }, NormalizedKey = string.Empty },
            new(),
            new() { CanonicalTitle = "12" }
        });
        SetupFiles(
            MakeFile(Dir("DKR Main"), "TDKR 001.cbz", "The Dark Knight Returns", T1),
            MakeFile(Dir("DKR Extra"), "DKR 001.cbz", "DKR", T2));

        var group = Assert.Single(await GetGroupsAsync(CreateController()));

        Assert.Equal("dkr", group.GroupKey);
        Assert.Equal("The Dark Knight Returns", group.SeriesName);
        Assert.Equal(new[] { Dir("DKR Extra"), Dir("DKR Main") }, group.Folders.Select(f => f.Directory));
    }

    // ------------------------------------------------------ series-library pass

    [Fact]
    public async Task GetCombinableFolders_SeriesLibraryThrows_KeepsAliasGroups()
    {
        SetupFooGroup();
        _seriesLibrary
            .Setup(s => s.GetAllSeriesFolderGroupsAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("snapshot missing"));

        var group = Assert.Single(await GetGroupsAsync(CreateController()));

        Assert.Equal("foo", group.GroupKey);
    }

    [Fact]
    public async Task GetCombinableFolders_SeriesSpansAliasGroupAndAnotherFolder_MergesThemIntoOneSeriesGroup()
    {
        SetupFiles(
            MakeFile(Dir("Foo A"), "Foo 001.cbz", "Foo", T1),
            MakeFile(Dir("Foo B"), "Foo 002.cbz", "Foo", T2),
            MakeFile(Dir("Foo C"), "Bar 001.cbz", "Bar", T3));
        SetupSeriesGroups(new SeriesFoldersResult { Id = "s1", Title = "Foo Series", Folders = Folders(Dir("Foo A"), Dir("Foo C")) });

        var group = Assert.Single(await GetGroupsAsync(CreateController()));

        Assert.Equal("series:s1", group.GroupKey);
        Assert.Equal("Foo Series", group.SeriesName);
        Assert.Null(group.Volume);
        Assert.Equal(3, group.TotalFileCount);
        Assert.Equal(new[] { Dir("Foo C"), Dir("Foo B"), Dir("Foo A") }, group.Folders.Select(f => f.Directory));
        Assert.Equal(Dir("Foo C"), group.SuggestedDestinationDirectory);
        Assert.Equal($"Contains the most recently added file ({T3:yyyy-MM-dd}).", group.SuggestionReason);
    }

    [Fact]
    public async Task GetCombinableFolders_UntitledSeriesSpanningTwoAliasGroups_UsesTheFirstGroupsName()
    {
        SetupFiles(
            MakeFile(Dir("Foo A"), "Foo 001.cbz", "Foo", T1),
            MakeFile(Dir("Foo B"), "Foo 002.cbz", "Foo", T2),
            MakeFile(Dir("Bar A"), "Bar 001.cbz", "Bar", T1),
            MakeFile(Dir("Bar B"), "Bar 002.cbz", "Bar", T2));
        SetupSeriesGroups(new SeriesFoldersResult { Id = "s2", Title = string.Empty, Folders = Folders(Dir("Foo A"), Dir("Bar A")) });

        var group = Assert.Single(await GetGroupsAsync(CreateController()));

        Assert.Equal("series:s2", group.GroupKey);
        Assert.Equal("Foo", group.SeriesName);
        Assert.Equal(4, group.Folders.Count);
    }

    [Fact]
    public async Task GetCombinableFolders_UntitledSeriesWithoutAliasGroup_IsNamedAfterItsId()
    {
        SetupFiles(
            MakeFile(Dir("X A"), "Alpha 001.cbz", "Alpha", T1),
            MakeFile(Dir("X B"), "Beta 001.cbz", "Beta", T2));
        SetupSeriesGroups(
            new SeriesFoldersResult { Id = "s3", Title = "  ", Folders = Folders(Dir("X A"), Dir("X B")) },
            new SeriesFoldersResult { Id = "single", Title = "Single", Folders = Folders(Dir("X A")) },
            new SeriesFoldersResult { Id = "untracked", Title = "Untracked", Folders = Folders(" ", Dir("Missing")) });

        var group = Assert.Single(await GetGroupsAsync(CreateController()));

        Assert.Equal("series:s3", group.GroupKey);
        Assert.Equal("s3", group.SeriesName);
        Assert.Equal("These folders are all attributed to the same series.", group.SuggestionReason);
        Assert.Equal(Dir("X B"), group.SuggestedDestinationDirectory);
    }

    [Fact]
    public async Task GetCombinableFolders_SeriesMatchingAnAliasGroupExactly_KeepsTheAliasGroup()
    {
        SetupFooGroup();
        SetupSeriesGroups(new SeriesFoldersResult { Id = "s4", Title = "Foo Series", Folders = Folders(Dir("Foo A"), Dir("Foo B")) });

        var group = Assert.Single(await GetGroupsAsync(CreateController()));

        Assert.Equal("foo", group.GroupKey);
        Assert.Equal("Foo", group.SeriesName);
    }

    // ------------------------------------------------------ parenthetical suffix pass

    [Fact]
    public async Task GetCombinableFolders_EmptyOrBareParentheticalSuffixes_DoNotTriggerSuffixMerge()
    {
        SetupFiles(
            MakeFile(Dir("Gamma (   )"), "Gamma 001.cbz", "Gamma One", T1),
            MakeFile(Dir("Gamma"), "Gamma 001.cbz", "Gamma Two", T2),
            MakeFile(Dir("(Official)"), "Official 001.cbz", "Official One", T1),
            MakeFile(Dir("Official"), "Official 001.cbz", "Official Two", T2));

        Assert.Empty(await GetGroupsAsync(CreateController()));
    }

    [Fact]
    public async Task GetCombinableFolders_SuffixVariantOfAnAliasGroup_AbsorbsTheWholeGroup()
    {
        SetupFiles(
            MakeFile(Dir("Tomb Raider King"), "Tomb Raider King 001.cbz", "Tomb Raider King", T1),
            MakeFile(Dir("TRK Extras"), "Extra 001.cbz", "Tomb Raider King", T2),
            MakeFile(Dir("Tomb Raider King (Official)"), "Chapter 001.cbz", null, T3));

        var group = Assert.Single(await GetGroupsAsync(CreateController()));

        Assert.Equal("suffix:tomb-raider-king", group.GroupKey);
        Assert.Equal("Tomb Raider King", group.SeriesName);
        Assert.Equal("Folder name differs only by a non-distinguishing suffix: (Official).", group.SuggestionReason);
        Assert.Equal(
            new[] { Dir("Tomb Raider King (Official)"), Dir("TRK Extras"), Dir("Tomb Raider King") },
            group.Folders.Select(f => f.Directory));
        Assert.Equal(Dir("Tomb Raider King (Official)"), group.SuggestedDestinationDirectory);
    }

    // ------------------------------------------------------------- plan validation

    [Fact]
    public async Task CombineEndpoints_NullRequest_ReturnBadRequest()
    {
        var controller = CreateController();

        AssertBadRequest((await controller.PreviewCombineFolders(null!)).Result, "Request body is required.");
        AssertBadRequest((await controller.CombineFolders(null!)).Result, "Request body is required.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task PreviewCombineFolders_BlankDestination_ReturnsBadRequest(string destination)
    {
        var result = await CreateController().PreviewCombineFolders(
            new FilesController.CombineFoldersRequest { DestinationDirectory = destination });

        AssertBadRequest(result.Result, "Destination directory is required.");
    }

    [Fact]
    public async Task CombineEndpoints_GroupDiscoveryFails_Return500()
    {
        _fileStore.Setup(f => f.GetAllFilesAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("disk"));
        var controller = CreateController();
        var request = new FilesController.CombineFoldersRequest { GroupKey = "foo", DestinationDirectory = Dir("Foo A") };

        AssertStatus((await controller.PreviewCombineFolders(request)).Result, 500, "Error building combine preview");
        AssertStatus((await controller.CombineFolders(request)).Result, 500, "Error combining folders");
    }

    [Fact]
    public async Task PreviewCombineFolders_SourceOutsideLibrary_ReturnsBadRequest()
    {
        SetupFooGroup();

        var result = await CreateController().PreviewCombineFolders(new FilesController.CombineFoldersRequest
        {
            GroupKey = "foo",
            DestinationDirectory = Dir("Foo A"),
            SourceDirectories = new List<string> { "/etc" }
        });

        AssertBadRequest(result.Result, "One or more source directories are outside the allowed directory.");
    }

    [Fact]
    public async Task PreviewCombineFolders_SourceTraversingOutOfLibrary_ReturnsBadRequest()
    {
        SetupFooGroup();

        var result = await CreateController().PreviewCombineFolders(new FilesController.CombineFoldersRequest
        {
            GroupKey = "foo",
            DestinationDirectory = Dir("Foo A"),
            SourceDirectories = new List<string> { Path.Combine(_root, "..", "..", "escaped") }
        });

        AssertBadRequest(result.Result, "One or more source directories are outside the allowed directory.");
    }

    [Fact]
    public async Task PreviewCombineFolders_SourceNotInGroup_ReturnsBadRequest()
    {
        SetupFooGroup();
        var other = Dir("Other");

        var result = await CreateController().PreviewCombineFolders(new FilesController.CombineFoldersRequest
        {
            GroupKey = "foo",
            DestinationDirectory = Dir("Foo A"),
            SourceDirectories = new List<string> { other }
        });

        AssertBadRequest(result.Result, $"Source directory '{other}' is not part of this group.");
    }

    [Fact]
    public async Task PreviewCombineFolders_OnlySourceIsTheDestination_ReturnsBadRequest()
    {
        SetupFooGroup();

        var result = await CreateController().PreviewCombineFolders(new FilesController.CombineFoldersRequest
        {
            GroupKey = "foo",
            DestinationDirectory = Dir("Foo A"),
            SourceDirectories = new List<string> { Dir("Foo A") }
        });

        AssertBadRequest(result.Result, "No source folders were selected for the combine operation.");
    }

    [Fact]
    public async Task PreviewCombineFolders_DestinationOutsideGroup_ReturnsBadRequest()
    {
        SetupFooGroup();

        var result = await CreateController().PreviewCombineFolders(new FilesController.CombineFoldersRequest
        {
            GroupKey = "foo",
            DestinationDirectory = Dir("Elsewhere"),
            SourceDirectories = new List<string> { Dir("Foo B") }
        });

        AssertBadRequest(result.Result, "Destination directory is not part of this group.");
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("FOO", true)]
    public async Task PreviewCombineFolders_NoSourcesSelected_UsesEveryOtherFolderInTheGroup(string? groupKey, bool emptySourceList)
    {
        SetupFooGroup();

        var result = await CreateController().PreviewCombineFolders(new FilesController.CombineFoldersRequest
        {
            GroupKey = groupKey,
            DestinationDirectory = Dir("Foo A"),
            SourceDirectories = emptySourceList ? new List<string>() : null
        });

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(Dir("Foo A"), Prop(ok.Value, "destination"));
        Assert.Equal(new[] { Dir("Foo B") }, Assert.IsType<List<string>>(Prop(ok.Value, "sources")));
        var move = Assert.Single(Items(Prop(ok.Value, "moves")));
        Assert.Equal(Path.Combine(Dir("Foo B"), "Foo 002.cbz"), Prop(move, "sourcePath"));
        Assert.Equal(Path.Combine(Dir("Foo A"), "Foo 002.cbz"), Prop(move, "destinationPath"));
        Assert.False((bool)Prop(move, "conflict")!);
        Assert.False((bool)Prop(move, "skipped")!);
        Assert.Null(Prop(move, "reason"));
    }

    [Fact]
    public async Task PreviewCombineFolders_NoGroupKey_InfersTheGroupFromNormalizedDestinationAndSources()
    {
        SetupFooGroup();

        var result = await CreateController().PreviewCombineFolders(new FilesController.CombineFoldersRequest
        {
            DestinationDirectory = Dir("Foo B"),
            SourceDirectories = new List<string> { Path.Combine(_root, "Foo B", "..", "Foo A") }
        });

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(Dir("Foo B"), Prop(ok.Value, "destination"));
        Assert.Equal(new[] { Dir("Foo A") }, Assert.IsType<List<string>>(Prop(ok.Value, "sources")));
        var move = Assert.Single(Items(Prop(ok.Value, "moves")));
        Assert.Equal(Path.Combine(Dir("Foo A"), "Foo 001.cbz"), Prop(move, "sourcePath"));
        Assert.Equal(Path.Combine(Dir("Foo B"), "Foo 001.cbz"), Prop(move, "destinationPath"));
    }

    [Fact]
    public async Task PreviewCombineFolders_NoGroupKeyAndFoldersFromDifferentGroups_ReportsNoMatchingGroup()
    {
        SetupFiles(
            MakeFile(Dir("Foo A"), "Foo 001.cbz", "Foo", T1),
            MakeFile(Dir("Foo B"), "Foo 002.cbz", "Foo", T2),
            MakeFile(Dir("Bar A"), "Bar 001.cbz", "Bar", T1),
            MakeFile(Dir("Bar B"), "Bar 002.cbz", "Bar", T2));

        var result = await CreateController().PreviewCombineFolders(new FilesController.CombineFoldersRequest
        {
            DestinationDirectory = Dir("Foo A"),
            SourceDirectories = new List<string> { Dir("Bar B") }
        });

        AssertBadRequest(result.Result, NoGroupError);
    }

    [Fact]
    public async Task CombineFolders_EveryMoveTargetsItsOwnPath_ReturnsSkippedResultsWithoutStartingAJob()
    {
        // A stale index entry: bucketed under "Foo A" but the file already lives in "Foo B".
        var alreadyInPlace = new ComicFile
        {
            FilePath = Path.Combine(Dir("Foo B"), "x.cbz"),
            FileName = "x.cbz",
            Directory = Dir("Foo A"),
            Metadata = new ComicMetadata { Series = "Foo" },
            LastModified = T1
        };
        SetupFiles(alreadyInPlace, MakeFile(Dir("Foo B"), "Foo 002.cbz", "Foo", T2));

        var result = await CreateController().CombineFolders(new FilesController.CombineFoldersRequest
        {
            GroupKey = "foo",
            DestinationDirectory = Dir("Foo B"),
            SourceDirectories = new List<string> { Dir("Foo A") }
        });

        var accepted = Assert.IsType<AcceptedResult>(result.Result);
        Assert.Equal(Dir("Foo B"), Prop(accepted.Value, "destination"));
        Assert.Equal(new[] { Dir("Foo A") }, Assert.IsType<List<string>>(Prop(accepted.Value, "sources")));
        Assert.Equal(0, Prop(accepted.Value, "moved"));
        Assert.Equal(1, Prop(accepted.Value, "skipped"));
        Assert.Equal(0, Prop(accepted.Value, "failed"));
        Assert.Equal(0, Prop(accepted.Value, "totalItems"));
        var skipped = Assert.Single(Items(Prop(accepted.Value, "results")));
        Assert.Equal(alreadyInPlace.FilePath, Prop(skipped, "sourcePath"));
        Assert.Equal(alreadyInPlace.FilePath, Prop(skipped, "destinationPath"));
        Assert.Equal("skipped", Prop(skipped, "status"));
        Assert.Equal("Source and destination are the same file.", Prop(skipped, "reason"));
        _processor.Verify(p => p.RunCustomBatchJobAsync(
            It.IsAny<string>(),
            It.IsAny<IEnumerable<string>>(),
            It.IsAny<Func<string, CancellationToken, Task<bool>>>(),
            It.IsAny<string>(),
            It.IsAny<Func<CancellationToken, Task>?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    // ------------------------------------------------- series-scoped group keys

    private FilesController.CombineFoldersRequest SeriesKeyRequest(string groupKey) => new()
    {
        GroupKey = groupKey,
        DestinationDirectory = Dir("S A")
    };

    [Fact]
    public async Task PreviewCombineFolders_SeriesKeyWithoutId_ReportsNoMatchingGroup()
    {
        var result = await CreateController().PreviewCombineFolders(SeriesKeyRequest("series:"));

        AssertBadRequest(result.Result, NoGroupError);
        _seriesLibrary.Verify(
            s => s.GetFoldersForSeriesIdAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task PreviewCombineFolders_SeriesFolderLookupThrows_ReportsNoMatchingGroup()
    {
        _seriesLibrary
            .Setup(s => s.GetFoldersForSeriesIdAsync("s7", null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("snapshot missing"));

        var result = await CreateController().PreviewCombineFolders(SeriesKeyRequest("series:s7"));

        AssertBadRequest(result.Result, NoGroupError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreviewCombineFolders_SeriesWithFewerThanTwoFolders_ReportsNoMatchingGroup(bool seriesExists)
    {
        _seriesLibrary
            .Setup(s => s.GetFoldersForSeriesIdAsync("s7", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(seriesExists ? new SeriesFoldersResult { Id = "s7", Folders = Folders(Dir("S A")) } : null);

        var result = await CreateController().PreviewCombineFolders(SeriesKeyRequest("series:s7"));

        AssertBadRequest(result.Result, NoGroupError);
        _fileStore.Verify(f => f.GetAllFilesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PreviewCombineFolders_SeriesKeyWhenFileStoreReturnsNull_ReportsNoMatchingGroup()
    {
        _fileStore.Setup(f => f.GetAllFilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync((IEnumerable<ComicFile>)null!);
        _seriesLibrary
            .Setup(s => s.GetFoldersForSeriesIdAsync("s7", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesFoldersResult { Id = "s7", Folders = Folders(Dir("S A"), Dir("S B")) });

        var result = await CreateController().PreviewCombineFolders(SeriesKeyRequest("series:s7"));

        AssertBadRequest(result.Result, NoGroupError);
        _fileStore.Verify(f => f.GetAllFilesAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task PreviewCombineFolders_SeriesKeyWithFilesInOnlyOneFolder_ReportsNoMatchingGroup()
    {
        SetupFiles(
            MakeFile(Dir("S A"), "Alpha 001.cbz", "Alpha", T1),
            MakeFile(Dir("S A"), "Alpha 002.cbz", "Alpha", T2));
        _seriesLibrary
            .Setup(s => s.GetFoldersForSeriesIdAsync("s7", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesFoldersResult { Id = "s7", Folders = Folders(Dir("S A"), Dir("S B")) });

        var result = await CreateController().PreviewCombineFolders(SeriesKeyRequest("series:s7"));

        AssertBadRequest(result.Result, NoGroupError);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Alpha Beta")]
    public async Task PreviewCombineFolders_SeriesKeyMissingFromGroups_ResolvesFoldersFromSeriesLibrary(string seriesTitle)
    {
        var dirA = Dir("S A");
        var dirB = Dir("S B");
        var unbucketed = MakeFile(dirA, "Alpha 001.cbz", "Alpha", T1);
        unbucketed.Directory = string.Empty;
        SetupFiles(
            unbucketed,
            MakeFile(dirB, "Beta 001.cbz", "Beta", T2),
            MakeFile(Dir("Elsewhere"), "Gamma 001.cbz", "Gamma", T3));
        var options = new DbContextOptionsBuilder<ComicMaintainerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using (var db = new ComicMaintainerDbContext(options))
        {
            db.ComicFiles.Add(new ComicFileEntity
            {
                FilePath = unbucketed.FilePath,
                FileName = unbucketed.FileName,
                Directory = dirA,
                CreatedAt = T3
            });
            await db.SaveChangesAsync();
        }
        _seriesLibrary
            .Setup(s => s.GetFoldersForSeriesIdAsync("s7", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesFoldersResult { Id = "s7", Title = seriesTitle, Folders = Folders(dirA, dirB) });

        var result = await CreateController(dbFactory: new InMemoryDbFactory(options)).PreviewCombineFolders(
            new FilesController.CombineFoldersRequest { GroupKey = "series:s7", DestinationDirectory = dirB });

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(dirB, Prop(ok.Value, "destination"));
        Assert.Equal(new[] { dirA }, Assert.IsType<List<string>>(Prop(ok.Value, "sources")));
        var move = Assert.Single(Items(Prop(ok.Value, "moves")));
        Assert.Equal(unbucketed.FilePath, Prop(move, "sourcePath"));
        Assert.Equal(Path.Combine(dirB, "Alpha 001.cbz"), Prop(move, "destinationPath"));
    }

    // ------------------------------------------------ combine + alias persistence

    private ComicFile[] ArrangeSeriesCombineOnDisk(bool sourceHasSeries = true)
    {
        var destinationDir = Dir("Foo");
        var sourceDir = Dir("Foo (Old)");
        Directory.CreateDirectory(destinationDir);
        Directory.CreateDirectory(sourceDir);
        var existing = MakeFile(destinationDir, "Foo 001.cbz", "Foo", T1);
        var incoming = MakeFile(sourceDir, "Foo Legacy 002.cbz", sourceHasSeries ? "Foo Legacy" : null, T2);
        File.WriteAllBytes(existing.FilePath, new byte[] { 1 });
        File.WriteAllBytes(incoming.FilePath, new byte[] { 2 });
        SetupSeriesGroups(new SeriesFoldersResult { Id = "s1", Title = "Foo", Folders = Folders(destinationDir, sourceDir) });
        return new[] { existing, incoming };
    }

    private async Task<AcceptedResult> CombineSeriesFoldersAsync()
    {
        var result = await CreateController().CombineFolders(new FilesController.CombineFoldersRequest
        {
            GroupKey = "series:s1",
            DestinationDirectory = Dir("Foo"),
            SourceDirectories = new List<string> { Dir("Foo (Old)") }
        });
        return Assert.IsType<AcceptedResult>(result.Result);
    }

    private (string Title, string[] Aliases) SingleAliasWrite()
    {
        var write = Assert.Single(_aliasWrites);
        return (write.Title, write.Aliases.OrderBy(a => a, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task CombineFolders_SeriesGroup_MovesFilesAndRecordsSourceNamesAsAliases()
    {
        var files = ArrangeSeriesCombineOnDisk();
        SetupFiles(files);
        var movedPath = Path.Combine(Dir("Foo"), "Foo Legacy 002.cbz");

        var accepted = await CombineSeriesFoldersAsync();

        Assert.Equal(1, Prop(accepted.Value, "totalItems"));
        Assert.Equal(Dir("Foo"), Prop(accepted.Value, "destination"));
        Assert.True(File.Exists(movedPath));
        Assert.False(Directory.Exists(Dir("Foo (Old)")));
        _fileStore.Verify(f => f.UpdateFilePathAsync(files[1].FilePath, movedPath, It.IsAny<CancellationToken>(), false), Times.Once);
        var (title, aliases) = SingleAliasWrite();
        Assert.Equal("Foo", title);
        Assert.Equal(new[] { "Foo (Old)", "Foo Legacy" }, aliases);
        Assert.Equal(
            new[] { files[0].FilePath, movedPath }.OrderBy(p => p, StringComparer.Ordinal),
            _normalizedPaths.OrderBy(p => p, StringComparer.Ordinal));
    }

    [Fact]
    public async Task CombineFolders_PreservesSourceDirectoriesWithPendingSuwayomiImports()
    {
        var files = ArrangeSeriesCombineOnDisk();
        SetupFiles(files);
        var staging = Path.Combine(Dir("Foo (Old)"), ".tmp_0123456789abcdef.suwayomi-import");
        File.WriteAllText(staging, "pending import");

        await CombineSeriesFoldersAsync();

        Assert.True(Directory.Exists(Dir("Foo (Old)")));
        Assert.True(File.Exists(staging));
        Assert.True(File.Exists(Path.Combine(Dir("Foo"), "Foo Legacy 002.cbz")));
    }

    [Theory]
    [InlineData("Foo Canon", null, "success", "Foo Canon")]
    [InlineData("Foo Canon", null, "MANUAL_MATCH", "Foo Canon")]
    [InlineData("Foo Canon ", "Picked Name", null, "Foo Canon")]
    [InlineData("Foo Canon", null, "pending", "Foo")]
    [InlineData("  ", "Picked Name", "success", "Foo")]
    public async Task CombineFolders_DestinationCacheRecord_DecidesTheCanonicalTitle(
        string canonicalTitle, string? seriesName, string? lookupStatus, string expectedCanonical)
    {
        SetupFiles(ArrangeSeriesCombineOnDisk());
        _metadataCache
            .Setup(c => c.GetAsync("foo", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesMetadataCacheRecord { CanonicalTitle = canonicalTitle, SeriesName = seriesName, LookupStatus = lookupStatus });

        await CombineSeriesFoldersAsync();

        var (title, aliases) = SingleAliasWrite();
        Assert.Equal(expectedCanonical, title);
        // When a matched canonical title wins, the destination folder name is kept as an alias too.
        var expectedAliases = expectedCanonical == "Foo"
            ? new[] { "Foo (Old)", "Foo Legacy" }
            : new[] { "Foo", "Foo (Old)", "Foo Legacy" };
        Assert.Equal(expectedAliases, aliases);
    }

    [Fact]
    public async Task CombineFolders_CacheLookupsThrow_StillPersistsFolderDerivedAliases()
    {
        SetupFiles(ArrangeSeriesCombineOnDisk());
        _metadataCache
            .Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("cache offline"));

        await CombineSeriesFoldersAsync();

        var (title, aliases) = SingleAliasWrite();
        Assert.Equal("Foo", title);
        Assert.Equal(new[] { "Foo (Old)", "Foo Legacy" }, aliases);
        _metadataCache.Verify(c => c.GetAsync("foo", It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task CombineFolders_AliasPersistenceFails_StillQueuesNormalization()
    {
        var files = ArrangeSeriesCombineOnDisk();
        SetupFiles(files);
        _metadataCache
            .Setup(c => c.SetUserAliasesAsync(It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db locked"));

        var accepted = await CombineSeriesFoldersAsync();

        Assert.Equal(1, Prop(accepted.Value, "totalItems"));
        Assert.Contains(Path.Combine(Dir("Foo"), "Foo Legacy 002.cbz"), _normalizedPaths);
        Assert.Contains(files[0].FilePath, _normalizedPaths);
    }

    [Fact]
    public async Task CombineFolders_DestinationAlreadyHasUserAliases_MergesThem()
    {
        SetupFiles(ArrangeSeriesCombineOnDisk());
        _metadataCache
            .Setup(c => c.GetAsync("foo", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesMetadataCacheRecord { UserAliases = new() { "Old Alias", "  ", "foo", "Foo Legacy " } });

        await CombineSeriesFoldersAsync();

        var (title, aliases) = SingleAliasWrite();
        Assert.Equal("Foo", title);
        Assert.Equal(new[] { "Foo (Old)", "Foo Legacy", "Old Alias" }, aliases);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CombineFolders_DestinationRecordWithoutUserAliases_PersistsOnlyNewAliases(bool nullAliases)
    {
        SetupFiles(ArrangeSeriesCombineOnDisk());
        _metadataCache
            .Setup(c => c.GetAsync("foo", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesMetadataCacheRecord { UserAliases = nullAliases ? null! : new List<string>() });

        await CombineSeriesFoldersAsync();

        Assert.Equal(new[] { "Foo (Old)", "Foo Legacy" }, SingleAliasWrite().Aliases);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("throws")]
    [InlineData("no-metadata")]
    public async Task CombineFolders_PreMoveSeriesUnavailable_PersistsOnlyTheFolderAlias(string scenario)
    {
        var files = ArrangeSeriesCombineOnDisk(sourceHasSeries: scenario != "no-metadata");
        // Calls: group discovery, pre-move series capture, destination listing.
        var sequence = _fileStore.SetupSequence(f => f.GetAllFilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(files);
        sequence = scenario switch
        {
            "null" => sequence.ReturnsAsync((IEnumerable<ComicFile>)null!),
            "throws" => sequence.ThrowsAsync(new IOException("store busy")),
            _ => sequence.ReturnsAsync(files)
        };
        sequence.ReturnsAsync(files);

        var accepted = await CombineSeriesFoldersAsync();

        Assert.Equal(1, Prop(accepted.Value, "totalItems"));
        var (title, aliases) = SingleAliasWrite();
        Assert.Equal("Foo", title);
        Assert.Equal(new[] { "Foo (Old)" }, aliases);
    }
}
