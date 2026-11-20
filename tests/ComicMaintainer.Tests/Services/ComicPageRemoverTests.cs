using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Notifications;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Removing pages (scan credits, ads) from a comic's CBZ: real archives are rewritten, and
/// ComicInfo.xml's page list follows the pages it describes.
/// </summary>
public sealed class ComicPageRemoverTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "cm-pages-" + Guid.NewGuid().ToString("N"));
    private readonly Mock<IFileWatcherService> _watcher = new();
    private readonly Mock<IFileCoverCacheService> _coverCache = new();
    private readonly Mock<IFileStoreService> _fileStore = new();
    private readonly Mock<INotificationPublisher> _notifications = new();
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _provider;
    private readonly IDbContextFactory<ComicMaintainerDbContext> _db;

    public ComicPageRemoverTests()
    {
        Directory.CreateDirectory(_root);
        _fileStore.Setup(f => f.GetFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string path, CancellationToken _) => new ComicFile { FilePath = path });
        _connection.Open();
        var services = new ServiceCollection();
        services.AddDbContextFactory<ComicMaintainerDbContext>(o => o.UseSqlite(_connection));
        _provider = services.BuildServiceProvider();
        _db = _provider.GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>();
        using var db = _db.CreateDbContext();
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
        TestIO.TryDeleteDirectory(_root);
    }

    private ComicPageRemover CreateRemover() => new(
        new FileOperationLock(),
        _watcher.Object,
        _coverCache.Object,
        _fileStore.Object,
        _notifications.Object,
        new TestOptionsMonitor<AppSettings>(new AppSettings { WatchedDirectory = _root, DuplicateDirectory = Path.Join(_root, "duplicates") }),
        _db,
        NullLogger<ComicPageRemover>.Instance);

    private const string ComicInfo =
        "<?xml version=\"1.0\"?><ComicInfo><Series>Saga</Series><PageCount>4</PageCount><Pages>"
        + "<Page Image=\"0\" Type=\"FrontCover\" /><Page Image=\"1\" /><Page Image=\"2\" Type=\"Advertisement\" /><Page Image=\"3\" Type=\"Other\" />"
        + "</Pages></ComicInfo>";

    private string WriteCbz(string name, string? comicInfo = ComicInfo, params string[] pages)
    {
        var path = Path.Join(_root, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var page in pages.Length > 0 ? pages : ["p01.jpg", "p02.jpg", "p03.jpg", "p04.jpg"])
        {
            using var writer = new StreamWriter(zip.CreateEntry(page).Open());
            writer.Write("image of " + page);
        }

        if (comicInfo is not null)
        {
            using var writer = new StreamWriter(zip.CreateEntry("ComicInfo.xml").Open());
            writer.Write(comicInfo);
        }

        return path;
    }

    private static (string[] Entries, XElement? Info) Read(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var info = zip.GetEntry("ComicInfo.xml") is { } entry ? XDocument.Load(entry.Open()).Root : null;
        return (zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal).ToArray(), info);
    }

    [Fact]
    public async Task RemovePages_TakesThemOut_AndTheComicInfoPageListFollows()
    {
        var path = WriteCbz("Saga 001.cbz");

        var result = await CreateRemover().RemovePagesAsync(path, ["p03.jpg", "p01.jpg"]);

        Assert.Equal(new PageRemovalResult(true, 2, 2, null), result);
        var (entries, info) = Read(path);
        Assert.Equal(new[] { "ComicInfo.xml", "p02.jpg", "p04.jpg" }, entries);
        Assert.Equal("2", info!.Element("PageCount")?.Value);
        var pages = info.Element("Pages")!.Elements("Page").Select(p => ((string?)p.Attribute("Image"), (string?)p.Attribute("Type"))).ToArray();
        Assert.Equal(new[] { ((string?)"0", (string?)null), ("1", "Other") }, pages);
        _watcher.Verify(w => w.SuppressProcessing(path), Times.AtLeastOnce);
        _coverCache.Verify(c => c.InvalidateAsync(path, It.IsAny<CancellationToken>()), Times.Once);
        _fileStore.Verify(f => f.RefreshFileStatsAsync(It.Is<IEnumerable<string>>(p => p.Single() == path), It.IsAny<CancellationToken>()), Times.Once);
        _notifications.Verify(n => n.LibraryChanged(), Times.Once);
    }

    [Fact]
    public async Task RemovePages_MovesEveryReadersSavedPageDownPastTheRemovedOnes()
    {
        var path = WriteCbz("Saga 001.cbz");
        await using (var seed = await _db.CreateDbContextAsync())
        {
            seed.ReadingProgresses.Add(new ReadingProgressEntity { UserId = "a", ContentId = path, CurrentPage = 4, TotalPages = 4, PercentComplete = 100 });
            seed.ReadingProgresses.Add(new ReadingProgressEntity { UserId = "b", ContentId = path, CurrentPage = 1, TotalPages = 4, PercentComplete = 25 });
            seed.UserFileReadStatuses.Add(new UserFileReadStatusEntity { UserId = "a", FilePath = path, CurrentPage = 3 });
            await seed.SaveChangesAsync();
        }

        // Pages 1 and 3 go: page 4 becomes page 2, and page 3 (gone) gives way to the next kept, page 2.
        await CreateRemover().RemovePagesAsync(path, ["p01.jpg", "p03.jpg"]);

        await using var db = await _db.CreateDbContextAsync();
        var progress = db.ReadingProgresses.OrderBy(p => p.UserId).Select(p => new { p.CurrentPage, p.TotalPages, p.PercentComplete }).ToList();
        Assert.Equal((2, 2, 100.0), (progress[0].CurrentPage, progress[0].TotalPages, progress[0].PercentComplete));
        Assert.Equal((1, 2, 50.0), (progress[1].CurrentPage, progress[1].TotalPages, progress[1].PercentComplete));
        Assert.Equal(2, db.UserFileReadStatuses.Single().CurrentPage);
    }

    [Theory]
    [InlineData(5, new[] { 0, 2 }, 3, 3)]
    [InlineData(3, new[] { 0, 2 }, 3, 2)]
    [InlineData(1, new[] { 0 }, 3, 1)]
    [InlineData(4, new[] { 3 }, 3, 3)]
    [InlineData(0, new int[0], 3, 1)]
    public void PageAfterRemoval_MovesDownWithinThePagesLeft(int page, int[] removed, int left, int expected)
        => Assert.Equal(expected, ComicPageRemover.PageAfterRemoval(page, removed, left));

    [Fact]
    public async Task RemovePages_NamedByTwoEntries_IsRefused()
    {
        var path = WriteCbz("Saga 001.cbz", comicInfo: null, "p01.jpg", "a/p02.jpg", "b/p02.jpg");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            using var writer = new StreamWriter(zip.CreateEntry("p01.jpg").Open());
            writer.Write("a second p01");
        }

        var result = await CreateRemover().RemovePagesAsync(path, ["p01.jpg"]);

        Assert.Equal(PageRemovalResult.Refused("2 pages are named p01.jpg; they cannot be told apart to remove one"), result);
    }

    [Fact]
    public async Task RemovePages_OfAPathOutsideTheLibrary_IsRefusedEvenWhenTracked()
    {
        var outside = Path.Join(Path.GetTempPath(), "cm-pages-outside-" + Guid.NewGuid().ToString("N") + ".cbz");
        File.WriteAllBytes(outside, File.ReadAllBytes(WriteCbz("Saga 001.cbz")));
        try
        {
            var result = await CreateRemover().RemovePagesAsync(outside, ["p01.jpg"]);

            Assert.Equal("The file is not in the library", result.Error);
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task RemovePages_WithoutComicInfo_OrOneWithoutAPageList_KeepsItAsItIs()
    {
        var bare = WriteCbz("bare.cbz", comicInfo: null);
        const string plain = "<?xml version=\"1.0\"?><ComicInfo><Series>Saga</Series></ComicInfo>";
        var tagged = WriteCbz("tagged.cbz", comicInfo: plain);

        Assert.True((await CreateRemover().RemovePagesAsync(bare, ["p04.jpg"])).Succeeded);
        Assert.True((await CreateRemover().RemovePagesAsync(tagged, ["p04.jpg"])).Succeeded);

        Assert.Equal(new[] { "p01.jpg", "p02.jpg", "p03.jpg" }, Read(bare).Entries);
        using var zip = ZipFile.OpenRead(tagged);
        using var reader = new StreamReader(zip.GetEntry("ComicInfo.xml")!.Open(), Encoding.UTF8);
        Assert.Equal(plain, reader.ReadToEnd());
    }

    [Theory]
    [InlineData(new[] { "p05.jpg" }, "Those pages are not in the archive any more; reload its pages")]
    [InlineData(new[] { "p01.jpg", "p05.jpg" }, "1 of those pages are not in the archive any more; reload its pages")]
    [InlineData(new[] { "ComicInfo.xml" }, "Those pages are not in the archive any more; reload its pages")]
    [InlineData(new[] { "p01.jpg", "p02.jpg", "p03.jpg", "p04.jpg" }, "A comic must keep at least one page")]
    [InlineData(new[] { "", null }, "Choose the pages to remove")]
    public async Task RemovePages_ThatCannotBeDone_WritesNothing(string?[] pages, string expected)
    {
        var path = WriteCbz("Saga 001.cbz");
        var before = File.ReadAllBytes(path);

        var result = await CreateRemover().RemovePagesAsync(path, pages.Select(p => p!).ToList());

        Assert.Equal(PageRemovalResult.Refused(expected), result);
        Assert.Equal(before, File.ReadAllBytes(path));
        _notifications.Verify(n => n.LibraryChanged(), Times.Never);
    }

    [Fact]
    public async Task RemovePages_OfAFileOutsideTheLibrary_OrNotACbz_OrGone_IsRefused()
    {
        var cbr = Path.Join(_root, "Saga 001.cbr");
        File.WriteAllText(cbr, "rar");
        _fileStore.Setup(f => f.GetFileAsync(Path.Join(_root, "untracked.cbz"), It.IsAny<CancellationToken>())).ReturnsAsync((ComicFile?)null);

        Assert.Equal("The file is not in the library", (await CreateRemover().RemovePagesAsync(Path.Join(_root, "untracked.cbz"), ["p01.jpg"])).Error);
        Assert.Equal("Only a CBZ's pages can be removed; convert this file to CBZ first", (await CreateRemover().RemovePagesAsync(cbr, ["p01.jpg"])).Error);
        Assert.Equal("The file is gone", (await CreateRemover().RemovePagesAsync(Path.Join(_root, "gone.cbz"), ["p01.jpg"])).Error);
    }

    [Fact]
    public async Task RemovePages_OfADamagedArchive_IsRefusedWithTheReason()
    {
        var path = Path.Join(_root, "broken.cbz");
        File.WriteAllText(path, "not a zip");

        var result = await CreateRemover().RemovePagesAsync(path, ["p01.jpg"]);

        Assert.False(result.Succeeded);
        Assert.StartsWith("The archive could not be rewritten: ", result.Error);
    }

    [Fact]
    public void WithoutPages_RenumbersThePageList_AndLeavesWhatItCannotRead()
    {
        var updated = ComicPageRemover.WithoutPages(
            "<ComicInfo xmlns=\"urn:x\"><PageCount>3</PageCount><Pages><Page Image=\"0\" /><Page Image=\"x\" /><Page Image=\"2\" Type=\"Other\" /></Pages></ComicInfo>",
            [0],
            2)!;
        XNamespace ns = "urn:x";
        Assert.Equal("2", updated.Root!.Element(ns + "PageCount")?.Value);
        Assert.Equal(new[] { "x", "1" }, updated.Root.Element(ns + "Pages")!.Elements(ns + "Page").Select(p => (string?)p.Attribute("Image")));

        Assert.Null(ComicPageRemover.WithoutPages("not xml <", [0], 1));
        Assert.Null(ComicPageRemover.WithoutPages("<ComicInfo><Series>Saga</Series></ComicInfo>", [0], 1));
    }
}
