using System.Collections.Concurrent;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Covers the read side of <see cref="FileStoreService"/>: status filters, sort orders and
/// caching of the database-backed paged/folder queries, the in-memory filtered list, and the
/// single-file and stale-metadata lookups (including their database-outage fallbacks).
/// </summary>
/// <remarks>
/// Library layout used by every test (user "reader" has read <c>b.cbz</c> only):
/// <code>
/// A/a.cbz  renamed+normalized  300 B  2021   series v1
/// B/b.cbz  renamed             100 B  2023   series v3
/// c.cbz    normalized          200 B  2022   series v2
/// A/d.cbz  duplicate           400 B  2020
/// B/e.cbz  -                    50 B  2024
/// </code>
/// </remarks>
public sealed class FileStoreServiceQueryCoverageTests : IAsyncLifetime
{
    private const string Reader = "reader";

    private static readonly DateTime A2021 = new(2021, 6, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime B2023 = new(2023, 6, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime C2022 = new(2022, 6, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime D2020 = new(2020, 6, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime E2024 = new(2024, 6, 1, 12, 0, 0, DateTimeKind.Local);

    private readonly string _library = Path.Combine(Path.GetTempPath(), $"fss_query_{Guid.NewGuid():N}", "library");
    private readonly ServiceProvider _provider;
    private readonly IDbContextFactory<ComicMaintainerDbContext> _db;
    private readonly FlakyDbContextFactory _factory;
    private readonly ListLogger _logger = new();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    public FileStoreServiceQueryCoverageTests()
    {
        var dbName = $"fss_query_{Guid.NewGuid()}";
        var services = new ServiceCollection();
        services.AddDbContextFactory<ComicMaintainerDbContext>(o => o.UseInMemoryDatabase(dbName));
        _provider = services.BuildServiceProvider();
        _db = _provider.GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>();
        _factory = new FlakyDbContextFactory(_db);
    }

    private string PathA => Path.Combine(_library, "A", "a.cbz");

    private string PathB => Path.Combine(_library, "B", "b.cbz");

    private string PathC => Path.Combine(_library, "c.cbz");

    public async Task InitializeAsync()
    {
        await using var db = await _db.CreateDbContextAsync();
        db.ComicFiles.AddRange(
            Row(PathA, 300, A2021, renamed: true, normalized: true, seriesVersion: 1),
            Row(PathB, 100, B2023, renamed: true, seriesVersion: 3),
            Row(PathC, 200, C2022, normalized: true, seriesVersion: 2),
            Row(Path.Combine(_library, "A", "d.cbz"), 400, D2020, duplicate: true),
            Row(Path.Combine(_library, "B", "e.cbz"), 50, E2024));
        db.UserFileReadStatuses.AddRange(
            new UserFileReadStatusEntity { UserId = Reader, FilePath = PathB, IsRead = true },
            new UserFileReadStatusEntity { UserId = Reader, FilePath = PathC, IsRead = false },
            new UserFileReadStatusEntity { UserId = "someone-else", FilePath = PathA, IsRead = true });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync()
    {
        _cache.Dispose();
        return _provider.DisposeAsync().AsTask();
    }

    // ---- GetFilesPageAsync --------------------------------------------------------------

    [Theory]
    [InlineData(null, "a,b,c,d,e")]
    [InlineData("processed", "a")]
    [InlineData("Processed", "a")]
    [InlineData("unprocessed", "b,c,e")]
    [InlineData("duplicates", "d")]
    [InlineData("renamed", "a,b")]
    [InlineData("normalized", "a,c")]
    [InlineData("read", "b")]
    [InlineData("unread", "a,c,d,e")]
    [InlineData("no-such-filter", "a,b,c,d,e")]
    public async Task GetFilesPageAsync_Filter_SelectsMatchingFiles(string? filter, string expected)
    {
        var service = CreateService();

        var result = await service.GetFilesPageAsync(filter: filter);

        Assert.Equal(expected, Names(result.Files));
        Assert.Equal(expected.Split(',').Length, result.TotalFiles);
    }

    [Theory]
    [InlineData("name", "desc", "e,d,c,b,a")]
    [InlineData("date", "asc", "d,a,c,b,e")]
    [InlineData("date", "desc", "e,b,c,a,d")]
    [InlineData("DATE", "DESC", "e,b,c,a,d")]
    [InlineData("size", "asc", "e,b,c,a,d")]
    [InlineData("size", "desc", "d,a,c,b,e")]
    [InlineData("no-such-sort", "asc", "a,b,c,d,e")]
    [InlineData(null, null, "a,b,c,d,e")]
    public async Task GetFilesPageAsync_Sort_OrdersFiles(string? sort, string? direction, string expected)
    {
        var service = CreateService();

        var result = await service.GetFilesPageAsync(sort: sort, direction: direction);

        Assert.Equal(expected, string.Join(',', result.Files.Select(f => Path.GetFileNameWithoutExtension(f.Name))));
    }

    [Fact]
    public async Task GetFilesPageAsync_AllOnOnePage_ProjectsEveryFlagForTheCurrentUser()
    {
        var service = CreateService();

        var result = await service.GetFilesPageAsync(perPage: -1);

        Assert.Equal(1, result.Page);
        Assert.Equal(1, result.TotalPages);
        Assert.Equal(5, result.TotalFiles);
        var files = result.Files.ToDictionary(f => Path.GetFileNameWithoutExtension(f.Name));
        Assert.Equal(5, files.Count);

        Assert.Equal(PathA, files["a"].RelativePath);
        Assert.Equal(300, files["a"].Size);
        Assert.True(files["a"].Processed && files["a"].Renamed && files["a"].Normalized);
        Assert.False(files["b"].Processed);
        Assert.True(files["b"].Renamed);
        Assert.False(files["b"].Normalized);
        Assert.False(files["c"].Processed || files["c"].Renamed);
        Assert.True(files["c"].Normalized);
        Assert.True(files["d"].Duplicate);
        Assert.Equal(new[] { "b" }, files.Where(f => f.Value.Read).Select(f => f.Key));

        Assert.Equal(new DateTimeOffset(A2021, TimeSpan.Zero).ToUnixTimeSeconds(), files["a"].Modified);
        Assert.Equal(new DateTimeOffset(E2024).ToUnixTimeSeconds(), files["e"].Modified);
    }

    [Fact]
    public async Task GetFilesPageAsync_WithCache_ReusesTotalUntilTheStoreChanges()
    {
        var service = CreateService(_cache);
        Assert.Equal(5, (await service.GetFilesPageAsync()).TotalFiles);

        await AddRowOutOfBandAsync(Path.Combine(_library, "f.cbz"));
        Assert.Equal(5, (await service.GetFilesPageAsync()).TotalFiles);

        var before = service.ChangeVersion;
        await service.InitializeFromDatabaseAsync();
        Assert.True(service.ChangeVersion > before);
        Assert.Equal(6, (await service.GetFilesPageAsync()).TotalFiles);
    }

    // ---- GetFolderFilesAsync / GetFolderSummariesAsync / GetUnmarkedCountAsync ----------

    [Fact]
    public async Task GetFolderFilesAsync_UnknownFolder_ReturnsNothing()
    {
        var service = CreateService();

        Assert.Empty(await service.GetFolderFilesAsync("Z"));
    }

    [Fact]
    public async Task GetFolderFilesAsync_NullKey_ReturnsFilesInTheLibraryRoot()
    {
        var service = CreateService();

        var files = await service.GetFolderFilesAsync(null!);

        Assert.Equal("c", Names(files));
    }

    [Fact]
    public async Task FolderQueries_NoSettingsAvailable_UseRawDirectoriesAsKeys()
    {
        var monitor = new Mock<IOptionsMonitor<AppSettings>>();
        monitor.Setup(m => m.CurrentValue).Returns((AppSettings)null!);
        var service = new FileStoreService(monitor.Object, _logger, _factory, userContext: new TestUserContextAccessor(Reader));

        var files = await service.GetFolderFilesAsync(Path.Combine(_library, "A"));
        var summaries = await service.GetFolderSummariesAsync();

        Assert.Equal("a,d", Names(files));
        Assert.Equal(
            new[] { _library, Path.Combine(_library, "A"), Path.Combine(_library, "B") },
            summaries.Folders.Select(f => f.Path));
    }

    [Fact]
    public async Task GetFolderFilesAsync_WithCache_ReusesDirectoryListUntilTheStoreChanges()
    {
        var service = CreateService(_cache);
        Assert.Empty(await service.GetFolderFilesAsync("C"));

        await AddRowOutOfBandAsync(Path.Combine(_library, "C", "f.cbz"));
        Assert.Empty(await service.GetFolderFilesAsync("C"));

        await service.InitializeFromDatabaseAsync();
        Assert.Equal("f", Names(await service.GetFolderFilesAsync("C")));
    }

    [Theory]
    [InlineData("date", "asc", "A,,B")]
    [InlineData("date", "desc", "B,,A")]
    [InlineData("size", "asc", "B,,A")]
    [InlineData("size", "desc", "A,,B")]
    [InlineData("name", "desc", "B,A,")]
    [InlineData("no-such-sort", "DESC", "B,A,")]
    [InlineData(null, null, ",A,B")]
    public async Task GetFolderSummariesAsync_Sort_OrdersFolders(string? sort, string? direction, string expected)
    {
        var service = CreateService();

        var result = await service.GetFolderSummariesAsync(sort: sort, direction: direction);

        Assert.Equal(expected, string.Join(',', result.Folders.Select(f => f.Path)));
        var a = Assert.Single(result.Folders, f => f.Path == "A");
        Assert.Equal(700, a.TotalSize);
        Assert.Equal(new DateTimeOffset(A2021, TimeSpan.Zero).ToUnixTimeSeconds(), a.LastModified);
    }

    [Fact]
    public async Task GetUnmarkedCountAsync_WithoutCache_CountsUnprocessedNonDuplicates()
    {
        var service = CreateService();

        Assert.Equal(3, await service.GetUnmarkedCountAsync());
    }

    [Fact]
    public async Task GetUnmarkedCountAsync_WithCache_ReusesCountUntilTheStoreChanges()
    {
        var service = CreateService(_cache);
        Assert.Equal(3, await service.GetUnmarkedCountAsync());

        await AddRowOutOfBandAsync(Path.Combine(_library, "f.cbz"));
        Assert.Equal(3, await service.GetUnmarkedCountAsync());

        await service.InitializeFromDatabaseAsync();
        Assert.Equal(4, await service.GetUnmarkedCountAsync());
    }

    // ---- In-memory queries --------------------------------------------------------------

    [Theory]
    [InlineData(null, "a,b,c,d,e")]
    [InlineData("processed", "a")]
    [InlineData("unprocessed", "b,c,e")]
    [InlineData("duplicates", "d")]
    [InlineData("renamed", "a,b")]
    [InlineData("normalized", "a,c")]
    [InlineData("read", "b")]
    [InlineData("UNREAD", "a,c,d,e")]
    [InlineData("no-such-filter", "a,b,c,d,e")]
    public async Task GetFilteredFilesAsync_Filter_SelectsMatchingFiles(string? filter, string expected)
    {
        var service = CreateService();
        await service.InitializeFromDatabaseAsync();

        var files = await service.GetFilteredFilesAsync(filter);

        Assert.Equal(expected, string.Join(',', files.Select(f => Path.GetFileNameWithoutExtension(f.FileName)).Order(StringComparer.Ordinal)));
    }

    [Fact]
    public async Task IsFileRenamedOrNormalized_UntrackedFile_ReturnsFalse()
    {
        var service = CreateService();
        var untracked = Path.Combine(_library, "untracked.cbz");

        Assert.False(await service.IsFileRenamedAsync(untracked));
        Assert.False(await service.IsFileNormalizedAsync(untracked));
    }

    // ---- GetFilesWithStaleSeriesMetadataAsync -------------------------------------------

    [Fact]
    public async Task GetFilesWithStaleSeriesMetadataAsync_NoUsablePaths_ReturnsEmptyWithoutQuerying()
    {
        var service = CreateService();

        Assert.Empty(await service.GetFilesWithStaleSeriesMetadataAsync(null!, 3));
        Assert.Empty(await service.GetFilesWithStaleSeriesMetadataAsync(new[] { "", "   " }, 3));
        Assert.Equal(0, _factory.Created);
    }

    [Fact]
    public async Task GetFilesWithStaleSeriesMetadataAsync_ReturnsOnlyRowsBelowTheCurrentVersion()
    {
        var service = CreateService();

        var stale = await service.GetFilesWithStaleSeriesMetadataAsync(
            new[] { PathA, PathB, PathC, PathA.ToUpperInvariant(), Path.Combine(_library, "missing.cbz") },
            3);

        Assert.Equal(new[] { PathA, PathC }, stale.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task GetFilesWithStaleSeriesMetadataAsync_DatabaseUnavailable_ReturnsEmptyAndWarns()
    {
        var service = CreateService();
        _factory.FailAll();

        Assert.Empty(await service.GetFilesWithStaleSeriesMetadataAsync(new[] { PathA }, 3));
        Assert.True(_logger.Contains(LogLevel.Warning, "Failed to query stale series-metadata-version files"));
    }

    // ---- GetFileAsync / GetFilesNeedingBackfillAsync ------------------------------------

    [Fact]
    public async Task GetFileAsync_BlankOrUnknownPath_ReturnsNull()
    {
        var service = CreateService();

        Assert.Null(await service.GetFileAsync("  "));
        Assert.Null(await service.GetFileAsync(Path.Combine(_library, "missing.cbz")));
    }

    [Fact]
    public async Task GetFileAsync_DatabaseUnavailable_FallsBackToTrackedSnapshot()
    {
        var service = CreateService();
        await service.InitializeFromDatabaseAsync();
        _factory.FailAll();

        var tracked = await service.GetFileAsync(PathA);
        var untracked = await service.GetFileAsync(Path.Combine(_library, "missing.cbz"));

        Assert.NotNull(tracked);
        Assert.Equal(PathA, tracked.FilePath);
        Assert.True(tracked.IsProcessed);
        Assert.Null(untracked);
        Assert.True(_logger.Contains(LogLevel.Warning, "Failed to load file row"));
    }

    [Fact]
    public async Task GetFilesNeedingBackfillAsync_NonPositiveMax_ReturnsEmptyWithoutQuerying()
    {
        var service = CreateService();

        Assert.Empty(await service.GetFilesNeedingBackfillAsync(0));
        Assert.Equal(0, _factory.Created);
    }

    // ---- helpers ------------------------------------------------------------------------

    private FileStoreService CreateService(IMemoryCache? cache = null) =>
        new(
            new TestOptionsMonitor<AppSettings>(new AppSettings
            {
                WatchedDirectory = _library,
                DuplicateDirectory = string.Empty,
            }),
            _logger,
            _factory,
            memoryCache: cache,
            userContext: new TestUserContextAccessor(Reader));

    private static string Names(IEnumerable<FileDto> files) =>
        string.Join(',', files.Select(f => Path.GetFileNameWithoutExtension(f.Name)).Order(StringComparer.Ordinal));

    private static ComicFileEntity Row(
        string path,
        long size,
        DateTime modified,
        bool renamed = false,
        bool normalized = false,
        bool duplicate = false,
        int seriesVersion = 0) => new()
    {
        FilePath = path,
        FileName = Path.GetFileName(path),
        Directory = Path.GetDirectoryName(path)!,
        FileSize = size,
        LastModified = modified,
        IsRenamed = renamed,
        IsNormalized = normalized,
        IsProcessed = renamed && normalized,
        IsDuplicate = duplicate,
        SeriesMetadataVersion = seriesVersion,
    };

    private async Task AddRowOutOfBandAsync(string path)
    {
        await using var db = await _db.CreateDbContextAsync();
        db.ComicFiles.Add(Row(path, 10, DateTime.UtcNow));
        await db.SaveChangesAsync();
    }

    /// <summary>Wraps a real factory and fails every context creation once told to.</summary>
    private sealed class FlakyDbContextFactory : IDbContextFactory<ComicMaintainerDbContext>
    {
        private readonly IDbContextFactory<ComicMaintainerDbContext> _inner;
        private int _created;
        private volatile bool _failing;

        public FlakyDbContextFactory(IDbContextFactory<ComicMaintainerDbContext> inner) => _inner = inner;

        public int Created => Volatile.Read(ref _created);

        public void FailAll() => _failing = true;

        public ComicMaintainerDbContext CreateDbContext()
        {
            Interlocked.Increment(ref _created);
            if (_failing)
            {
                throw new InvalidOperationException("Simulated database outage");
            }

            return _inner.CreateDbContext();
        }

        public Task<ComicMaintainerDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class ListLogger : ILogger<FileStoreService>
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _entries.Enqueue((logLevel, formatter(state, exception)));

        public bool Contains(LogLevel level, string fragment) =>
            _entries.Any(e => e.Level == level && e.Message.Contains(fragment, StringComparison.Ordinal));
    }
}
