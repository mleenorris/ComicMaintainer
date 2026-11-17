using System.IO.Compression;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Notifications;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Replacing an archive's cover (its first page in name order) with a ComicVine cover. Real CBZ
/// files are rewritten; the download is mocked.
/// </summary>
public sealed class IssueCoverWriterTests : IAsyncLifetime
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3];
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 4, 5, 6];

    private readonly string _root = Path.Join(Path.GetTempPath(), "cm-cover-" + Guid.NewGuid().ToString("N"));
    private readonly Mock<IFileWatcherService> _watcher = new();
    private readonly Mock<IFileCoverCacheService> _coverCache = new();
    private readonly Mock<IFileStoreService> _fileStore = new();
    private readonly Mock<INotificationPublisher> _notifications = new();
    private readonly Mock<ISeriesImageStore> _imageStore = new();
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private DbContextOptions<ComicMaintainerDbContext> _options = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        await _connection.OpenAsync();
        _options = new DbContextOptionsBuilder<ComicMaintainerDbContext>().UseSqlite(_connection).Options;
        await using var db = new ComicMaintainerDbContext(_options);
        await db.Database.EnsureCreatedAsync();
        db.ComicVineIssues.Add(new ComicVineIssueEntity { Id = 101, VolumeId = 1, IssueNumber = "1", ListedUtc = DateTime.UtcNow, CoverUrl = "https://comicvine.example/101.png" });
        db.ComicVineIssues.Add(new ComicVineIssueEntity { Id = 102, VolumeId = 1, IssueNumber = "2", ListedUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _connection.DisposeAsync();
        TestIO.TryDeleteDirectory(_root);
    }

    private IssueCoverWriter CreateWriter() => new(
        new FileOperationLock(),
        _watcher.Object,
        _coverCache.Object,
        _fileStore.Object,
        _notifications.Object,
        NullLogger<IssueCoverWriter>.Instance);

    private IssueCoverService CreateService(IIssueCoverWriter? writer = null) => new(
        new Factory(_options),
        _fileStore.Object,
        _imageStore.Object,
        writer ?? CreateWriter(),
        NullLogger<IssueCoverService>.Instance);

    private string WriteCbz(string name, params (string Entry, byte[] Bytes)[] entries)
    {
        var path = Path.Join(_root, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, bytes) in entries)
        {
            using var stream = zip.CreateEntry(entry).Open();
            stream.Write(bytes);
        }

        return path;
    }

    private static Dictionary<string, byte[]> Entries(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        return zip.Entries.ToDictionary(entry => entry.FullName, entry =>
        {
            using var stream = entry.Open();
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray();
        });
    }

    private void Track(string path)
        => _fileStore.Setup(f => f.GetFileAsync(path, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComicFile { FilePath = path, FileName = Path.GetFileName(path) });

    [Fact]
    public async Task ReplaceFirstPageAsync_ReplacesPageOne_KeepsTheRest_AndRefreshesTheCover()
    {
        var path = WriteCbz("a.cbz", ("p002.jpg", [2]), ("p001.jpg", [1]), ("ComicInfo.xml", "<ComicInfo/>"u8.ToArray()));

        var outcome = await CreateWriter().ReplaceFirstPageAsync(path, Png, "image/png");

        Assert.Equal(IssueCoverWriteOutcome.Replaced, outcome);
        var entries = Entries(path);
        Assert.Equal(["ComicInfo.xml", "p001.png", "p002.jpg"], entries.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(Png, entries["p001.png"]);
        Assert.Equal([2], entries["p002.jpg"]);
        _watcher.Verify(w => w.SuppressProcessing(path), Times.AtLeastOnce);
        _coverCache.Verify(c => c.InvalidateAsync(path, It.IsAny<CancellationToken>()), Times.Once);
        _fileStore.Verify(f => f.RefreshFileStatsAsync(It.Is<IEnumerable<string>>(p => p.Single() == path), It.IsAny<CancellationToken>()), Times.Once);
        _notifications.Verify(n => n.LibraryChanged(), Times.Once);
        // No temp file is left behind.
        Assert.Equal([path], Directory.EnumerateFiles(_root));
    }

    [Fact]
    public async Task ReplaceFirstPageAsync_TheSameImage_LeavesTheArchiveAlone()
    {
        var path = WriteCbz("b.cbz", ("p001.jpg", Jpeg), ("p002.jpg", [2]));
        var written = File.GetLastWriteTimeUtc(path);

        Assert.Equal(IssueCoverWriteOutcome.Unchanged, await CreateWriter().ReplaceFirstPageAsync(path, Jpeg, "image/jpeg"));

        Assert.Equal(written, File.GetLastWriteTimeUtc(path));
        _notifications.Verify(n => n.LibraryChanged(), Times.Never);
    }

    [Fact]
    public async Task ReplaceFirstPageAsync_ANameTakenByAnotherEntry_GetsACoverSuffix()
    {
        var path = WriteCbz("c.cbz", ("01.jpg", [1]), ("01.png", [9]), ("02.jpg", [2]));

        await CreateWriter().ReplaceFirstPageAsync(path, Png, "image/png");

        var entries = Entries(path);
        Assert.Equal(Png, entries["01-cover.png"]);
        Assert.Equal([9], entries["01.png"]);
        Assert.False(entries.ContainsKey("01.jpg"));
    }

    [Fact]
    public async Task ReplaceFirstPageAsync_RefusesWhatItCannotReplace()
    {
        var writer = CreateWriter();
        var noPages = WriteCbz("d.cbz", ("ComicInfo.xml", "<ComicInfo/>"u8.ToArray()));
        var cbr = Path.Join(_root, "e.cbr");
        File.WriteAllBytes(cbr, [1]);

        Assert.Equal(IssueCoverWriteOutcome.NoPages, await writer.ReplaceFirstPageAsync(noPages, Jpeg, "image/jpeg"));
        Assert.Equal(IssueCoverWriteOutcome.NotWritable, await writer.ReplaceFirstPageAsync(cbr, Jpeg, "image/jpeg"));
        Assert.Equal(IssueCoverWriteOutcome.NotFound, await writer.ReplaceFirstPageAsync(Path.Join(_root, "gone.cbz"), Jpeg, "image/jpeg"));
        Assert.Equal(IssueCoverWriteOutcome.UnsupportedImage, await writer.ReplaceFirstPageAsync(noPages, Jpeg, "image/gif"));
        Assert.Equal(IssueCoverWriteOutcome.UnsupportedImage, await writer.ReplaceFirstPageAsync(noPages, [], "image/jpeg"));
    }

    [Fact]
    public async Task ReplaceWithComicVineCoverAsync_DownloadsTheStoredCover_AndWritesIt()
    {
        var path = WriteCbz("f.cbz", ("p001.jpg", [1]), ("p002.jpg", [2]));
        Track(path);
        _imageStore.Setup(s => s.FetchImageAsync("https://comicvine.example/101.png", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Png, "image/png"));

        var first = await CreateService().ReplaceWithComicVineCoverAsync(path, 101);
        var again = await CreateService().ReplaceWithComicVineCoverAsync(path, 101);

        Assert.Equal(new IssueCoverReplaceResult(true, true, null), first);
        Assert.Equal(new IssueCoverReplaceResult(true, false, null), again);
        Assert.Equal(Png, Entries(path)["p001.png"]);
    }

    [Fact]
    public async Task ReplaceWithComicVineCoverAsync_SaysWhyNothingWasReplaced()
    {
        var path = WriteCbz("g.cbz", ("p001.jpg", [1]));
        Track(path);
        var service = CreateService();

        Assert.Equal("The file is not in the library", (await service.ReplaceWithComicVineCoverAsync("/nowhere.cbz", 101)).Error);
        Assert.Equal("ComicVine has no cover for that issue", (await service.ReplaceWithComicVineCoverAsync(path, 102)).Error);
        Assert.Equal("ComicVine has no cover for that issue", (await service.ReplaceWithComicVineCoverAsync(path, 999)).Error);

        _imageStore.Setup(s => s.FetchImageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Image download failed: HTTP 404 Not Found"));
        Assert.Equal(
            "The cover could not be downloaded: Image download failed: HTTP 404 Not Found",
            (await service.ReplaceWithComicVineCoverAsync(path, 101)).Error);
    }

    [Theory]
    [InlineData(IssueCoverWriteOutcome.NotWritable, "Only a CBZ's cover can be replaced; convert this file to CBZ first")]
    [InlineData(IssueCoverWriteOutcome.NoPages, "The archive has no pages")]
    [InlineData(IssueCoverWriteOutcome.NotFound, "The file is gone")]
    [InlineData(IssueCoverWriteOutcome.UnsupportedImage, "The downloaded cover is not a JPEG, PNG or WebP image")]
    public async Task ReplaceWithComicVineCoverAsync_ReportsWhatTheWriterSays(IssueCoverWriteOutcome outcome, string error)
    {
        Track("/c/x.cbz");
        _imageStore.Setup(s => s.FetchImageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((Jpeg, "image/jpeg"));
        var writer = new Mock<IIssueCoverWriter>();
        writer.Setup(w => w.ReplaceFirstPageAsync("/c/x.cbz", Jpeg, "image/jpeg", It.IsAny<CancellationToken>())).ReturnsAsync(outcome);

        Assert.Equal(new IssueCoverReplaceResult(false, false, error), await CreateService(writer.Object).ReplaceWithComicVineCoverAsync("/c/x.cbz", 101));
    }

    [Fact]
    public async Task ReplaceWithComicVineCoverAsync_AnArchiveThatCannotBeRewritten_IsReported()
    {
        Track("/c/x.cbz");
        _imageStore.Setup(s => s.FetchImageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((Jpeg, "image/jpeg"));
        var writer = new Mock<IIssueCoverWriter>();
        writer.Setup(w => w.ReplaceFirstPageAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("disk full"));

        Assert.Equal(
            new IssueCoverReplaceResult(false, false, "The archive could not be rewritten: disk full"),
            await CreateService(writer.Object).ReplaceWithComicVineCoverAsync("/c/x.cbz", 101));
    }

    private sealed class Factory(DbContextOptions<ComicMaintainerDbContext> options) : IDbContextFactory<ComicMaintainerDbContext>
    {
        public ComicMaintainerDbContext CreateDbContext() => new(options);
    }
}
