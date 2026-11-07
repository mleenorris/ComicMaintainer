using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Covers the archive-metadata cache in <c>ComicFiles.Metadata</c> against real SQLite: what
/// the processor writes and what the background scan reads is stored with a size/mtime
/// stamp, a changed file is scanned again, a user edit that has not reached the archive is
/// never overwritten, and the series library shows the cached values without reading archives.
/// </summary>
public sealed class FileStoreServiceArchiveMetadataTests : IAsyncLifetime
{
    private const string Library = "/library";
    private static readonly DateTime T0 = new(2024, 1, 1, 10, 0, 0);

    private readonly string _root = Path.Join(Path.GetTempPath(), $"fss_meta_{Guid.NewGuid():N}");
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private ServiceProvider _provider = null!;
    private IDbContextFactory<ComicMaintainerDbContext> _factory = null!;
    private FileStoreService _store = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddDbContextFactory<ComicMaintainerDbContext>(o => o.UseSqlite(_connection));
        _provider = services.BuildServiceProvider();
        _factory = _provider.GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
        }

        _store = new FileStoreService(
            new TestOptionsMonitor<AppSettings>(new AppSettings { WatchedDirectory = Library, DuplicateDirectory = string.Empty }),
            NullLogger<FileStoreService>.Instance,
            _factory);
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Only some tests create files.
        }
    }

    [Fact]
    public async Task RecordArchiveMetadataAsync_CachesWhatWasWrittenStampedWithTheStoredStats()
    {
        var path = await TrackAsync("Saga/Saga 001.cbz", size: 1234, modified: T0);
        var versionBefore = _store.ChangeVersion;

        await _store.RecordArchiveMetadataAsync(path, Archive("Saga", "1", "Chapter One"));

        var row = await RowAsync(path);
        Assert.Equal("Saga", row.Metadata!.Series);
        Assert.Equal("1", row.Metadata.Issue);
        Assert.Equal("Chapter One", row.Metadata.Title);
        Assert.Equal("Image", row.Metadata.Publisher);
        Assert.Equal(2012, row.Metadata.Year);
        Assert.Equal(new[] { "Vaughan" }, row.Metadata.Authors);
        Assert.Equal(1234, row.MetadataScannedSize);
        Assert.Equal(T0, row.MetadataScannedModified);
        Assert.Empty(await _store.GetFilesNeedingMetadataScanAsync(0, 10));

        // The in-memory copy (which the series library reads) follows, and derived views rebuild.
        Assert.Equal("Chapter One", (await _store.GetFileAsync(path))!.Metadata!.Title);
        Assert.True(_store.ChangeVersion > versionBefore);
    }

    [Fact]
    public async Task RecordArchiveMetadataAsync_KeepsTheUsersEditFlagsAndLocks()
    {
        var path = await TrackAsync("Saga/Saga 001.cbz");
        await _store.ApplyUserMetadataEditAsync(path, new ComicMetadata { Series = "Saga" }, ComicMetadataFieldFlags.Series);
        // The edit has reached the archive.
        await _store.MarkFileBackfilledAsync(path, (await RowAsync(path)).MetadataVersion);

        await _store.RecordArchiveMetadataAsync(path, Archive("Saga", "1", "Chapter One"));

        var row = await RowAsync(path);
        Assert.Equal("Chapter One", row.Metadata!.Title);
        Assert.True(row.Metadata.IsUserEdited);
        Assert.Equal((long)ComicMetadataFieldFlags.Series, row.Metadata.UserLockedFieldsMask);
    }

    [Fact]
    public async Task RecordArchiveMetadataAsync_NeverOverwritesAnEditNotYetWrittenToTheArchive()
    {
        var path = await TrackAsync("Saga/Saga 001.cbz");
        await _store.ApplyUserMetadataEditAsync(path, new ComicMetadata { Title = "My Title" }, ComicMetadataFieldFlags.None);

        await _store.RecordArchiveMetadataAsync(path, Archive("Saga", "1", "Archive Title"));

        var row = await RowAsync(path);
        Assert.Equal("My Title", row.Metadata!.Title);
        Assert.Null(row.MetadataScannedSize);
        // Nor is it offered to the scan while the edit is pending.
        Assert.Empty(await _store.GetFilesNeedingMetadataScanAsync(0, 10));
    }

    [Fact]
    public async Task RecordArchiveMetadataAsync_ForAnUntrackedFile_DoesNothingAndDoesNotThrow()
    {
        await _store.RecordArchiveMetadataAsync($"{Library}/Saga/unknown.cbz", Archive("Saga", "1", "One"));

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Empty(await db.ComicFiles.ToListAsync());
    }

    [Fact]
    public async Task GetFilesNeedingMetadataScanAsync_ReturnsMissingAndStaleRowsInIdOrderPageByPage()
    {
        var never = await TrackAsync("A/A 001.cbz");
        var fresh = await TrackAsync("B/B 001.cbz");
        var resized = await TrackAsync("C/C 001.cbz");
        var touched = await TrackAsync("D/D 001.cbz");
        await _store.RecordArchiveMetadataAsync(fresh, Archive("B", "1", "B"));
        await _store.RecordArchiveMetadataAsync(resized, Archive("C", "1", "C"));
        await _store.RecordArchiveMetadataAsync(touched, Archive("D", "1", "D"));
        await SetStatsAsync(resized, size: 999, modified: T0);
        await SetStatsAsync(touched, size: 100, modified: T0.AddMinutes(5));

        var all = await _store.GetFilesNeedingMetadataScanAsync(0, 10);
        Assert.Equal(new[] { never, resized, touched }, all.Select(c => c.FilePath));
        Assert.Equal(999, all[1].FileSize);

        var firstPage = await _store.GetFilesNeedingMetadataScanAsync(0, 2);
        var secondPage = await _store.GetFilesNeedingMetadataScanAsync(firstPage[^1].Id, 2);
        Assert.Equal(new[] { never, resized }, firstPage.Select(c => c.FilePath));
        Assert.Equal(new[] { touched }, secondPage.Select(c => c.FilePath));
    }

    [Fact]
    public async Task RecordScannedArchiveMetadataAsync_RecordsOnlyWhileTheRowIsAsSelected()
    {
        var path = await TrackAsync("Saga/Saga 001.cbz");
        var candidate = Assert.Single(await _store.GetFilesNeedingMetadataScanAsync(0, 10));

        // The file was replaced while the scan was reading it.
        await SetStatsAsync(path, size: 5000, modified: T0.AddHours(1));
        Assert.False(await _store.RecordScannedArchiveMetadataAsync(candidate, Archive("Saga", "1", "Old Content")));
        Assert.Null((await RowAsync(path)).Metadata?.Title);

        var again = Assert.Single(await _store.GetFilesNeedingMetadataScanAsync(0, 10));
        Assert.True(await _store.RecordScannedArchiveMetadataAsync(again, Archive("Saga", "1", "New Content")));
        var row = await RowAsync(path);
        Assert.Equal("New Content", row.Metadata!.Title);
        Assert.Equal(5000, row.MetadataScannedSize);
    }

    [Fact]
    public async Task ChangedFileStats_MakeTheCachedMetadataStale()
    {
        Directory.CreateDirectory(Path.Join(_root, "Saga"));
        var path = Path.Join(_root, "Saga", "Saga 001.cbz");
        await File.WriteAllTextAsync(path, "v1");
        var store = new FileStoreService(
            new TestOptionsMonitor<AppSettings>(new AppSettings { WatchedDirectory = _root, DuplicateDirectory = string.Empty }),
            NullLogger<FileStoreService>.Instance,
            _factory);
        await store.AddFileAsync(path);
        await store.RecordArchiveMetadataAsync(path, Archive("Saga", "1", "One"));
        Assert.Empty(await store.GetFilesNeedingMetadataScanAsync(0, 10));

        await File.WriteAllTextAsync(path, "version two");
        File.SetLastWriteTime(path, DateTime.Now.AddMinutes(1));
        await store.RefreshFileStatsAsync(new[] { path });

        Assert.Equal(path, Assert.Single(await store.GetFilesNeedingMetadataScanAsync(0, 10)).FilePath);
        // The old values are still shown until the scan has read the new version.
        Assert.Equal("One", (await store.GetFileAsync(path))!.Metadata!.Title);
    }

    [Fact]
    public async Task SeriesLibrary_ShowsTheCachedArchiveMetadataWithoutReadingArchives()
    {
        var one = await TrackAsync("Saga/Saga 001.cbz");
        var two = await TrackAsync("Saga/Saga 002.cbz");
        await _store.InitializeFromDatabaseAsync();
        await _store.RecordArchiveMetadataAsync(one, Archive("Saga", "1", "Chapter One"));
        await _store.RecordArchiveMetadataAsync(two, Archive("Saga", "2", "Chapter Two"));
        var metadataCache = new Mock<ISeriesMetadataCacheService>();
        metadataCache.Setup(m => m.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<SeriesMetadataCacheRecord>());
        var library = new SeriesLibraryService(
            _store,
            metadataCache.Object,
            new TestOptionsMonitor<AppSettings>(new AppSettings()),
            NullLogger<SeriesLibraryService>.Instance);

        var series = Assert.Single((await library.GetSeriesAsync(perPage: -1)).Series);
        var issues = await library.GetSeriesIssuesAsync(series.Id, perPage: -1);

        Assert.Equal(new[] { "Chapter One", "Chapter Two" }, issues!.Issues.Select(i => i.Title));
        Assert.Equal(new[] { "1", "2" }, issues.Issues.Select(i => i.Issue));
        Assert.Equal(new[] { "Chapter One", "Chapter Two" }, series.Issues.Select(i => i.Title));
    }

    private static ComicMetadata Archive(string series, string issue, string title) => new()
    {
        Series = series,
        Issue = issue,
        Title = title,
        Publisher = "Image",
        Year = 2012,
        Authors = new List<string> { "Vaughan" },
    };

    private async Task<string> TrackAsync(string relativePath, long size = 100, DateTime? modified = null)
    {
        var path = $"{Library}/{relativePath}";
        await using var db = await _factory.CreateDbContextAsync();
        db.ComicFiles.Add(new ComicFileEntity
        {
            FilePath = path,
            FileName = Path.GetFileName(path),
            Directory = path[..path.LastIndexOf('/')],
            FileSize = size,
            LastModified = modified ?? T0,
        });
        await db.SaveChangesAsync();
        return path;
    }

    private async Task SetStatsAsync(string path, long size, DateTime modified)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.ComicFiles.Where(e => e.FilePath == path)
            .ExecuteUpdateAsync(set => set
                .SetProperty(e => e.FileSize, size)
                .SetProperty(e => e.LastModified, modified));
    }

    private async Task<ComicFileEntity> RowAsync(string path)
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.ComicFiles.AsNoTracking().SingleAsync(e => e.FilePath == path);
    }
}
