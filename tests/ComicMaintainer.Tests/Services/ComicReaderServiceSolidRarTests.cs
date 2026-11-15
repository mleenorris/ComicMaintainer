using System.Text;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Core.Utilities;
using ComicMaintainer.Tests.Helpers;
using ComicMaintainer.Tests.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// <see cref="ComicReaderService"/> reads the pages of a solid CBR, which can only be decompressed after every entry
/// before them, through a pass kept open per archive (<see cref="SolidRarCursorPool"/>): turning pages walks the
/// archive forward once, a burst of prefetches takes turns in page order, and the pool stays within its bounds.
/// The fixtures are real solid RAR 5 archives of stored entries, flagged solid after the first, which the reader can
/// only read in order.
/// </summary>
public sealed class ComicReaderServiceSolidRarTests : IDisposable
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);

    private readonly string _dir = Path.Join(Path.GetTempPath(), "reader-solid-" + Guid.NewGuid().ToString("N"));
    private readonly Mock<ILogger<ComicReaderService>> _logger = new();
    private readonly FakeTimeProvider _time = new();
    private readonly List<ComicReaderService> _services = new();

    public ComicReaderServiceSolidRarTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var service in _services)
        {
            service.Dispose();
        }

        TestIO.TryDeleteDirectory(_dir);
    }

    [Fact]
    public async Task TurningThePagesOfASolidCbr_DecompressesEachEntryOnce()
    {
        // Opened afresh per page, page N would cost N entries: 55 for these ten pages.
        var service = Service();
        var cbr = SolidCbr("book.cbr", 10);

        for (var page = 1; page <= 10; page++)
        {
            Assert.Equal(PageData(page), (await service.GetPageAsync(cbr, page))!.Value.Data);
        }

        Assert.Equal(1, service.SolidPasses.CursorsOpened);
        Assert.Equal(10, service.SolidPasses.EntriesDecompressed);
    }

    [Fact]
    public async Task APrefetchBurst_TakesTurnsInPageOrderAndWalksTheArchiveOnce()
    {
        // The web reader asks for the pages around the one it shows all at once, in no particular order.
        var opening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = Service(Pool(open: path =>
        {
            opening.TrySetResult();
            release.Task.Wait(TimeSpan.FromSeconds(10));
            return ComicArchiveReader.OpenSolidCursor(path);
        }));
        var cbr = SolidCbr("burst.cbr", 8);
        Assert.Equal(8, await service.GetPageCountAsync(cbr));

        var first = service.OpenPageAsync(cbr, 2, default);
        await opening.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var burst = new[] { 8, 5, 3, 7, 6, 4 }.ToDictionary(page => page, page => service.OpenPageAsync(cbr, page, default));
        await WaitUntilAsync(() => service.SolidPasses.WaitingCount(cbr) == burst.Count);
        release.SetResult();

        Assert.Equal(PageData(2), await ReadAsync(first));
        foreach (var (page, request) in burst)
        {
            Assert.Equal(PageData(page), await ReadAsync(request));
        }

        Assert.Equal(1, service.SolidPasses.CursorsOpened);
        Assert.Equal(8, service.SolidPasses.EntriesDecompressed);
    }

    [Fact]
    public async Task AStepBack_StartsANewPassFromTheBeginning()
    {
        var service = Service();
        var cbr = SolidCbr("back.cbr", 6);

        Assert.Equal(PageData(5), (await service.GetPageAsync(cbr, 5))!.Value.Data);
        Assert.Equal(PageData(3), (await service.GetPageAsync(cbr, 3))!.Value.Data);
        Assert.Equal(PageData(4), (await service.GetPageAsync(cbr, 4))!.Value.Data);

        Assert.Equal(2, service.SolidPasses.CursorsOpened);
        Assert.Equal(5 + 4, service.SolidPasses.EntriesDecompressed);
    }

    [Fact]
    public async Task TheFirstPage_IsReadWithoutDisturbingThePass()
    {
        // The library grid asks for page 1, the cover, while the reader is deep in the book. The first entry of a
        // solid archive needs nothing before it, so it is read on its own and the pass keeps its place.
        var service = Service();
        var cbr = SolidCbr("cover.cbr", 6);
        await service.GetPageAsync(cbr, 4);

        Assert.Equal(PageData(1), (await service.GetPageAsync(cbr, 1))!.Value.Data);
        Assert.Equal(PageData(5), (await service.GetPageAsync(cbr, 5))!.Value.Data);

        Assert.Equal(1, service.SolidPasses.CursorsOpened);
    }

    [Fact]
    public async Task AnIdlePass_IsClosedAndLetsGoOfTheFile()
    {
        var service = Service();
        var cbr = SolidCbr("idle.cbr", 3);
        await service.GetPageAsync(cbr, 2);

        _time.Advance(IdleTimeout / 2);
        Assert.Equal(1, service.SolidPasses.OpenCursorCount);

        _time.Advance(IdleTimeout);
        Assert.Equal(0, service.SolidPasses.OpenCursorCount);
        File.Delete(cbr);
        Assert.False(File.Exists(cbr));
    }

    [Fact]
    public async Task AChangedFile_IsReadThroughANewPass()
    {
        var service = Service();
        var cbr = SolidCbr("changed.cbr", 4);
        await service.GetPageAsync(cbr, 2);

        File.WriteAllBytes(cbr, SolidRar5(4, page => "new " + PageText(page)));
        File.SetLastWriteTimeUtc(cbr, DateTime.UtcNow.AddMinutes(1));

        Assert.Equal(Encoding.ASCII.GetBytes("new " + PageText(3)), (await service.GetPageAsync(cbr, 3))!.Value.Data);
        Assert.Equal(2, service.SolidPasses.CursorsOpened);
    }

    [Fact]
    public async Task ThePool_KeepsNoMorePassesThanItsLimit()
    {
        var service = Service(Pool(maxCursors: 1));
        var first = SolidCbr("first.cbr", 4);
        var second = SolidCbr("second.cbr", 4);

        await service.GetPageAsync(first, 2);
        await service.GetPageAsync(second, 2);
        Assert.Equal(1, service.SolidPasses.OpenCursorCount);

        // The most recent one was kept, so it carries on from where it was.
        await service.GetPageAsync(second, 3);
        Assert.Equal(2, service.SolidPasses.CursorsOpened);
    }

    [Fact]
    public async Task APassThatDoesNotFitThePoolsMemory_IsClosedAfterItsRead()
    {
        var service = Service(Pool(maxBytes: 1));
        var cbr = SolidCbr("large.cbr", 4);

        Assert.Equal(PageData(2), (await service.GetPageAsync(cbr, 2))!.Value.Data);
        Assert.Equal(0, service.SolidPasses.OpenCursorCount);
        Assert.Equal(PageData(3), (await service.GetPageAsync(cbr, 3))!.Value.Data);
        Assert.Equal(2, service.SolidPasses.CursorsOpened);
    }

    [Fact]
    public async Task ARequestCancelledWhileWaitingForItsTurn_GivesItUp()
    {
        var opening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = Service(Pool(open: path =>
        {
            opening.TrySetResult();
            release.Task.Wait(TimeSpan.FromSeconds(10));
            return ComicArchiveReader.OpenSolidCursor(path);
        }));
        var cbr = SolidCbr("cancel.cbr", 6);
        await service.GetPageCountAsync(cbr);
        var first = service.OpenPageAsync(cbr, 2, default);
        await opening.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var cancellation = new CancellationTokenSource();
        var dropped = service.OpenPageAsync(cbr, 5, cancellation.Token);
        await WaitUntilAsync(() => service.SolidPasses.WaitingCount(cbr) == 1);

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dropped);
        Assert.Equal(0, service.SolidPasses.WaitingCount(cbr));
        release.SetResult();
        Assert.Equal(PageData(2), await ReadAsync(first));
        Assert.Equal(2, service.SolidPasses.EntriesDecompressed);
        VerifyNoErrorLogged();
    }

    [Fact]
    public async Task ACancelledRead_ThrowsWithoutAnErrorAndLeavesNoPassOpen()
    {
        var service = Service();
        var cbr = SolidCbr("cancelled.cbr", 4);
        await service.GetPageCountAsync(cbr);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.OpenPageAsync(cbr, 4, new CancellationToken(canceled: true)));

        Assert.Equal(0, service.SolidPasses.OpenCursorCount);
        Assert.Equal(0, service.SolidPasses.EntriesDecompressed);
        VerifyNoErrorLogged();
    }

    [Fact]
    public async Task ADamagedEntry_FailsThePageAndClosesThePass()
    {
        // One damaged entry garbles every entry decompressed after it, so the pages after it are not served, and
        // the pass that read it is not kept.
        var service = Service();
        var cbr = Path.Join(_dir, "damaged.cbr");
        File.WriteAllBytes(cbr, SolidRar5(4, PageText, badCrc: "p03.jpg"));

        Assert.Null(await service.GetPageAsync(cbr, 4));
        Assert.Equal(0, service.SolidPasses.OpenCursorCount);
        Assert.Equal(PageData(2), (await service.GetPageAsync(cbr, 2))!.Value.Data);
    }

    [Fact]
    public async Task ASolidPageIsServedFromAPooledBuffer()
    {
        var service = Service();
        var cbr = SolidCbr("served.cbr", 3);

        var page = await service.OpenPageAsync(cbr, 3, default);

        using var content = Assert.IsType<RentedBufferStream>(page!.Value.Content);
        Assert.Equal("image/jpeg", page.Value.ContentType);
        Assert.Equal(PageData(3), content.ToExactArray());
    }

    // ---- helpers ----------------------------------------------------------------------

    private ComicReaderService Service(SolidRarCursorPool? pool = null)
    {
        var service = new ComicReaderService(_logger.Object, ComicReaderService.DefaultPageIndexCacheBytes, pool ?? Pool());
        _services.Add(service);
        return service;
    }

    private SolidRarCursorPool Pool(
        int maxCursors = SolidRarCursorPool.DefaultMaxCursors,
        long maxBytes = SolidRarCursorPool.DefaultMaxBytes,
        Func<string, ComicArchiveReader.SolidRarCursor>? open = null) =>
        new(maxCursors, maxBytes, IdleTimeout, _time, open);

    private static string PageText(int page) => $"page {page}";

    private static byte[] PageData(int page) => Encoding.ASCII.GetBytes(PageText(page));

    private static byte[] SolidRar5(int pages, Func<int, string> text, string? badCrc = null) =>
        ComicArchiveReaderTests.StoredSolidRar5Bytes(
            badCrc,
            ComicArchiveReaderTests.Rar5Checksum.Crc32,
            Enumerable.Range(1, pages).Select(page => ($"p{page:D2}.jpg", Encoding.ASCII.GetBytes(text(page)))).ToArray());

    private string SolidCbr(string name, int pages)
    {
        var path = Path.Join(_dir, name);
        File.WriteAllBytes(path, SolidRar5(pages, PageText));
        return path;
    }

    private static async Task<byte[]> ReadAsync(Task<ComicMaintainer.Core.Interfaces.ComicPageStream?> request)
    {
        var page = await request.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(page);
        await using var content = page.Value.Content;
        using var copy = new MemoryStream();
        await content.CopyToAsync(copy);
        return copy.ToArray();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the condition.");
            await Task.Delay(10);
        }
    }

    private void VerifyNoErrorLogged() =>
        _logger.Verify(
            l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
}
