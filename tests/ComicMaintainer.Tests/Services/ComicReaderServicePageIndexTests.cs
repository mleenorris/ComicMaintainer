using System.IO.Compression;
using System.Text;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Core.Utilities;
using ComicMaintainer.Tests.Helpers;
using ComicMaintainer.Tests.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// <see cref="ComicReaderService"/> keeps each archive version's sorted page list
/// in a bounded cache, so counting pages and turning pages do not re-list the
/// archive, and it reads archives under the <see cref="ArchiveEntryReader"/>
/// limits, failing with <see cref="ArchiveLimitExceededException"/>.
/// </summary>
public sealed class ComicReaderServicePageIndexTests : IDisposable
{
    private readonly string _dir = Path.Join(Path.GetTempPath(), "reader-index-" + Guid.NewGuid().ToString("N"));
    private readonly Mock<ILogger<ComicReaderService>> _logger = new();
    private readonly ComicReaderService _service;

    public ComicReaderServicePageIndexTests()
    {
        Directory.CreateDirectory(_dir);
        _service = new ComicReaderService(_logger.Object);
    }

    public void Dispose()
    {
        _service.Dispose();
        TestIO.TryDeleteDirectory(_dir);
    }

    // ---- page-list cache (DL-4) -----------------------------------------------------

    [Fact]
    public async Task CountingAndTurningPages_ListTheArchiveOnce()
    {
        var cbz = WriteCbz("a.cbz", "p10.jpg", "p2.jpg", "p1.jpg");

        Assert.Equal(3, await _service.GetPageCountAsync(cbz));
        for (var page = 1; page <= 3; page++)
        {
            Assert.NotNull(await _service.GetPageAsync(cbz, page));
            Assert.Equal(3, await _service.GetPageCountAsync(cbz));
        }

        Assert.Equal(new[] { "p1.jpg", "p2.jpg", "p10.jpg" }, await _service.GetPageNamesAsync(cbz));
        Assert.Equal(1, _service.ListingCount);
    }

    [Fact]
    public async Task ThePageListRules_AreThoseOfPageListVersion1()
    {
        // Page ETags and cached covers assume that page N of an unchanged file is
        // the same image. If this fails because the page-list rules were changed
        // on purpose, bump ComicReaderService.PageListVersion and update the
        // expectations here.
        Assert.Equal(1, ComicReaderService.PageListVersion);
        var cbz = WriteCbz("rules.cbz",
            "Page10.JPG", "page2.jpeg", "page1.png", "a.gif", "b.bmp", "c.webp",
            "d.avif", "e.jxl", "f.tif", "g.tiff", "h.svg", "ComicInfo.xml", "notes.txt", "sub/", "sub/page3.jpg");

        Assert.Equal(
            new[] { "a.gif", "b.bmp", "c.webp", "page1.png", "page2.jpeg", "Page10.JPG", "sub/page3.jpg" },
            await _service.GetPageNamesAsync(cbz));
    }

    [Fact]
    public async Task CbrPages_AreServedThroughTheCachedListInNaturalOrder()
    {
        var cbr = Path.Join(_dir, "a.cbr");
        ComicReaderServiceCoverageTests.StoredRar4.Write(cbr,
            ("p10.jpg", Encoding.ASCII.GetBytes("ten")),
            ("dir", null),
            ("p2.jpg", Encoding.ASCII.GetBytes("two")),
            ("notes.txt", Encoding.ASCII.GetBytes("not a page")));

        Assert.Equal(Encoding.ASCII.GetBytes("two"), (await _service.GetPageAsync(cbr, 1))!.Value.Data);
        Assert.Equal(Encoding.ASCII.GetBytes("ten"), (await _service.GetPageAsync(cbr, 2))!.Value.Data);
        Assert.Equal(2, await _service.GetPageCountAsync(cbr));
        Assert.Equal(1, _service.ListingCount);
    }

    [Fact]
    public async Task ARewrittenArchive_IsListedAgain()
    {
        var cbz = WriteCbz("a.cbz", "p1.jpg", "p2.jpg");
        Assert.Equal(2, await _service.GetPageCountAsync(cbz));

        WriteCbz("a.cbz", "p1.jpg", "p2.jpg", "p3.jpg");

        Assert.Equal(3, await _service.GetPageCountAsync(cbz));
        Assert.Equal(2, _service.ListingCount);
    }

    [Fact]
    public async Task AnArchiveTouchedWithoutChangingItsLength_IsListedAgain()
    {
        var cbz = WriteCbz("a.cbz", "p1.jpg");
        await _service.GetPageCountAsync(cbz);

        File.SetLastWriteTimeUtc(cbz, File.GetLastWriteTimeUtc(cbz).AddMinutes(1));
        await _service.GetPageCountAsync(cbz);

        Assert.Equal(2, _service.ListingCount);
    }

    [Fact]
    public async Task AnArchiveReplacedWithTheSameLengthAndTimestamp_ServesTheNewPage()
    {
        // Same size, same mtime, different entries: the cached list no longer
        // matches the archive, which is noticed when the entry name differs.
        var cbz = WriteCbz("a.cbz", ("a1.jpg", "one"), ("a2.jpg", "two"));
        var mtime = File.GetLastWriteTimeUtc(cbz);
        Assert.Equal(Encoding.ASCII.GetBytes("one"), (await _service.GetPageAsync(cbz, 1))!.Value.Data);
        var length = new FileInfo(cbz).Length;

        WriteCbz("a.cbz", ("b1.jpg", "uno"), ("b2.jpg", "dos"));
        File.SetLastWriteTimeUtc(cbz, mtime);
        Assert.Equal(length, new FileInfo(cbz).Length);

        Assert.Equal(Encoding.ASCII.GetBytes("uno"), (await _service.GetPageAsync(cbz, 1))!.Value.Data);
        Assert.Equal(new[] { "b1.jpg", "b2.jpg" }, await _service.GetPageNamesAsync(cbz));
    }

    [Fact]
    public async Task ConcurrentColdRequests_ShareOneListing()
    {
        var cbz = WriteCbz("a.cbz", Enumerable.Range(1, 200).Select(i => $"p{i}.jpg").ToArray());

        var counts = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => _service.GetPageCountAsync(cbz))));

        Assert.All(counts, c => Assert.Equal(200, c));
        Assert.Equal(1, _service.ListingCount);
    }

    [Fact]
    public async Task ReturnedPageNames_AreTheCallersToChange()
    {
        var cbz = WriteCbz("a.cbz", "p1.jpg", "p2.jpg");

        var names = await _service.GetPageNamesAsync(cbz);
        names.Clear();

        Assert.Equal(new[] { "p1.jpg", "p2.jpg" }, await _service.GetPageNamesAsync(cbz));
    }

    [Fact]
    public async Task TheCache_StaysWithinItsMemoryBudget()
    {
        using var service = new ComicReaderService(NullLogger<ComicReaderService>.Instance, pageIndexCacheBytes: 4096);
        for (var i = 0; i < 40; i++)
        {
            var cbz = WriteCbz($"c{i}.cbz", Enumerable.Range(1, 10).Select(p => $"page-{p:D3}.jpg").ToArray());
            Assert.Equal(10, await service.GetPageCountAsync(cbz));
        }

        // MemoryCache compacts in the background once over its limit.
        await WaitUntilAsync(() => service.CachedPageIndexBytes <= 4096);
        Assert.InRange(service.CachedPageIndexBytes, 1, 4096);
        Assert.True(service.CachedPageIndexCount < 40);
    }

    [Fact]
    public async Task AMissingOrUnsupportedFile_IsNotCached()
    {
        Assert.Equal(0, await _service.GetPageCountAsync(Path.Join(_dir, "missing.cbz")));
        var pdf = Path.Join(_dir, "a.pdf");
        File.WriteAllBytes(pdf, new byte[] { 1 });
        Assert.Equal(0, await _service.GetPageCountAsync(pdf));

        Assert.Equal(0, _service.CachedPageIndexCount);
        Assert.Equal(0, _service.ListingCount);
    }

    [Fact]
    public async Task ACorruptArchive_IsRetriedOnTheNextRequest()
    {
        var cbz = Path.Join(_dir, "a.cbz");
        File.WriteAllBytes(cbz, Encoding.ASCII.GetBytes("not a zip"));
        Assert.Equal(0, await _service.GetPageCountAsync(cbz));

        WriteCbz("a.cbz", "p1.jpg");

        Assert.Equal(1, await _service.GetPageCountAsync(cbz));
    }

    // ---- archive read limits (SEC-8) --------------------------------------------------

    [Fact]
    public async Task APageThatInflatesLikeABomb_IsRefusedWithAClearError()
    {
        var cbz = Path.Join(_dir, "bomb.cbz");
        File.WriteAllBytes(cbz, ArchiveEntryReaderTests.BuildZip(
            ("p1.jpg", Encoding.ASCII.GetBytes("fine"), CompressionLevel.NoCompression),
            ("p2.png", new byte[32 * 1024 * 1024], CompressionLevel.SmallestSize)));

        // Listing is fine and the honest page still reads...
        Assert.Equal(2, await _service.GetPageCountAsync(cbz));
        Assert.NotNull(await _service.GetPageAsync(cbz, 1));

        // ...but the bomb is refused before it is inflated.
        var ex = await Assert.ThrowsAsync<ArchiveLimitExceededException>(() => _service.GetPageAsync(cbz, 2));
        Assert.Contains("p2.png", ex.Message);
        VerifyLogged(LogLevel.Warning, "Refused to read");
    }

    [Fact]
    public async Task ACbrPageDeclaredOverTheCap_IsRefused()
    {
        var cbr = Path.Join(_dir, "huge.cbr");
        ComicReaderServiceCoverageTests.StoredRar4.WriteDeclaringSizes(cbr,
            ("p1.jpg", Encoding.ASCII.GetBytes("tiny"), (uint)(ArchiveEntryReader.MaxPageBytes * 2)));

        await Assert.ThrowsAsync<ArchiveLimitExceededException>(() => _service.GetPageAsync(cbr, 1));
    }

    [Fact]
    public async Task ACbzWithTooManyEntries_IsRefusedAndTheRefusalIsRemembered()
    {
        var cbz = Path.Join(_dir, "many.cbz");
        File.WriteAllBytes(cbz, ArchiveEntryReaderTests.BuildZip(Enumerable.Range(0, ArchiveEntryReader.MaxEntries + 1)
            .Select(i => ($"p{i:D5}.jpg", Array.Empty<byte>(), CompressionLevel.NoCompression)).ToArray()));

        var first = await Assert.ThrowsAsync<ArchiveLimitExceededException>(() => _service.GetPageCountAsync(cbz));
        var second = await Assert.ThrowsAsync<ArchiveLimitExceededException>(() => _service.GetPageNamesAsync(cbz));
        await Assert.ThrowsAsync<ArchiveLimitExceededException>(() => _service.GetPageAsync(cbz, 1));

        Assert.Equal(first.Message, second.Message);
        Assert.Contains("more than 20,000 entries", first.Message);
        Assert.Equal(1, _service.ListingCount);
    }

    [Fact]
    public async Task ACbrWithTooManyEntries_IsRefused()
    {
        var cbr = Path.Join(_dir, "many.cbr");
        ComicReaderServiceCoverageTests.StoredRar4.Write(cbr, Enumerable.Range(0, ArchiveEntryReader.MaxEntries + 1)
            .Select(i => ($"p{i:D5}.jpg", (byte[]?)Array.Empty<byte>())).ToArray());

        var ex = await Assert.ThrowsAsync<ArchiveLimitExceededException>(() => _service.GetPageCountAsync(cbr));

        Assert.Contains("many.cbr", ex.Message);
    }

    [Fact]
    public async Task ARefusedArchive_IsReadAgainOnceItChanges()
    {
        var cbz = Path.Join(_dir, "many.cbz");
        File.WriteAllBytes(cbz, ArchiveEntryReaderTests.BuildZip(Enumerable.Range(0, ArchiveEntryReader.MaxEntries + 1)
            .Select(i => ($"p{i:D5}.jpg", Array.Empty<byte>(), CompressionLevel.NoCompression)).ToArray()));
        await Assert.ThrowsAsync<ArchiveLimitExceededException>(() => _service.GetPageCountAsync(cbz));

        WriteCbz("many.cbz", "p1.jpg");

        Assert.Equal(1, await _service.GetPageCountAsync(cbz));
    }

    // ---- helpers ----------------------------------------------------------------------

    private string WriteCbz(string name, params string[] pages) =>
        WriteCbz(name, pages.Select(p => (p, "data of " + p)).ToArray());

    private string WriteCbz(string name, params (string Name, string Content)[] pages)
    {
        var path = Path.Join(_dir, name);
        File.WriteAllBytes(path, ArchiveEntryReaderTests.BuildZip(pages
            .Select(p => (p.Name, Encoding.ASCII.GetBytes(p.Content), CompressionLevel.NoCompression))
            .ToArray()));
        return path;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
    }

    private void VerifyLogged(LogLevel level, string fragment) =>
        _logger.Verify(
            l => l.Log(
                level,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) => $"{state}".Contains(fragment)),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
}
