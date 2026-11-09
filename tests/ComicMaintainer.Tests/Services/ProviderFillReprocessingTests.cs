using System.IO.Compression;
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
/// A provider fill against the real file store and SQLite: afterwards nothing about the row may
/// make the file look user-edited or out of date, or the watcher, the library scan and the
/// backfill job would pick it up again.
/// </summary>
public sealed class ProviderFillReprocessingTests : IAsyncLifetime
{
    private const int CurrentRecordVersion = 5;

    private readonly string _root = Path.Join(Path.GetTempPath(), $"fill_loop_{Guid.NewGuid():N}");
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private ServiceProvider _provider = null!;
    private IDbContextFactory<ComicMaintainerDbContext> _factory = null!;
    private FileStoreService _store = null!;
    private ComicProcessorService _processor = null!;
    private string _path = null!;

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

        Directory.CreateDirectory(Path.Join(_root, "Berserk"));
        var settings = new TestOptionsMonitor<AppSettings>(new AppSettings
        {
            WatchedDirectory = _root,
            DuplicateDirectory = Path.Join(_root, "duplicates"),
            TempFileDirectory = Path.Join(_root, "temp"),
            FilenameFormat = AppSettings.DefaultFilenameFormat,
            IssueNumberPadding = 4
        });
        _store = TestServices.CreateFileStore(settings, NullLogger<FileStoreService>.Instance, _factory);

        var record = new SeriesMetadataCacheRecord
        {
            NormalizedKey = "berserk",
            CanonicalTitle = "Berserk",
            ResolvedSeriesName = "Berserk",
            LookupStatus = "success",
            Synopsis = "Guts fights.",
            Genres = new List<string> { "Action" },
            SiteUrl = "https://anilist.co/manga/30002",
            PublicationStatus = SeriesPublicationStatus.Completed,
            TotalChapters = 364,
            Origin = "ja",
            MetadataVersion = CurrentRecordVersion
        };
        var cache = new Mock<ISeriesMetadataCacheService>();
        cache.Setup(c => c.GetByTitleAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(record);
        cache.Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(record);
        cache.Setup(c => c.NormalizeKey(It.IsAny<string?>())).Returns("berserk");
        var resolver = new Mock<ISeriesNameResolver>();
        resolver.Setup(r => r.ResolveAsync(It.IsAny<string>(), It.IsAny<ComicMetadata?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesNameResolution { ResolvedSeries = "Berserk", WinningStep = SeriesNameResolutionStep.MatchedCache });

        _processor = TestServices.CreateComicProcessor(
            settings,
            NullLogger<ComicProcessorService>.Instance,
            _store,
            Mock.Of<IProcessingHistoryService>(),
            seriesMetadataCache: cache.Object,
            seriesNameResolver: resolver.Object);

        _path = Path.Join(_root, "Berserk", "Berserk - Chapter 0001.cbz");
        using (var zip = ZipFile.Open(_path, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("ComicInfo.xml").Open()))
            {
                writer.Write("<ComicInfo><Title>The Black Swordsman</Title><Series>Berserk</Series><Number>1</Number></ComicInfo>");
            }

            using var page = zip.CreateEntry("page001.jpg").Open();
            page.Write(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 });
        }

        await _store.AddFileAsync(_path);
    }

    public async Task DisposeAsync()
    {
        _processor.Dispose();
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
        TestIO.TryDeleteDirectory(_root);
    }

    private async Task<ComicFileEntity> RowAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.ComicFiles.AsNoTracking().SingleAsync(e => e.FilePath == _path);
    }

    [Fact]
    public async Task AFilledFileIsNotUserEditedNotOutOfDateAndNotPickedUpAgain()
    {
        Assert.True(await _processor.ProcessFileAsync(_path));

        var row = await RowAsync();

        // The cache holds what the archive now says, without claiming a user wrote it.
        Assert.Equal("Guts fights.", row.Metadata!.Summary);
        Assert.False(row.Metadata.IsUserEdited);
        Assert.Equal(0L, row.Metadata.UserLockedFieldsMask);
        Assert.NotEqual(FileMetadataSource.UserEdit.ToString(), row.MetadataSource);

        // The backfill job only looks for edits the archive has not received yet.
        Assert.True(row.MetadataVersion <= row.WrittenMetadataVersion);

        // The stored stats follow the write, so a scan sees no changed file.
        var disk = new FileInfo(_path);
        Assert.Equal(disk.Length, row.FileSize);
        Assert.Empty(await _store.GetFilesNeedingMetadataScanAsync(0, 10));

        // The retag pass compares this stamp with the series record's version.
        Assert.Equal(CurrentRecordVersion, row.SeriesMetadataVersion);
        Assert.Empty(await _store.GetFilesWithStaleSeriesMetadataAsync(new[] { _path }, CurrentRecordVersion));

        Assert.True(row.IsNormalized);
        Assert.True(await _store.IsFileProcessedAsync(_path));
    }

    [Fact]
    public async Task TheWatcherSeeingOurOwnWriteDoesNotRewriteTheFile()
    {
        Assert.True(await _processor.ProcessFileAsync(_path));
        var filled = await File.ReadAllBytesAsync(_path);
        var modified = File.GetLastWriteTimeUtc(_path);

        // What the watcher does after the debounce: process again, which is skipped.
        Assert.True(await _processor.ProcessFileAsync(_path));

        Assert.Equal(filled, await File.ReadAllBytesAsync(_path));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(_path));
    }
}
