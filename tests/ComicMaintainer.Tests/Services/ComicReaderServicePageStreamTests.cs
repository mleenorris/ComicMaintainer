using System.IO.Compression;
using System.Text;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Core.Utilities;
using ComicMaintainer.Tests.Helpers;
using ComicMaintainer.Tests.Utilities;
using Microsoft.Extensions.Logging;
using Moq;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// <see cref="ComicReaderService.OpenPageAsync"/> opens a page for sending: a page stored uncompressed in a CBZ is
/// streamed from where it lies in the archive file, any other page is read into a pooled buffer, and a request
/// nobody waits for any more stops instead of running to completion.
/// </summary>
public sealed class ComicReaderServicePageStreamTests : IDisposable
{
    private readonly string _dir = Path.Join(Path.GetTempPath(), "reader-stream-" + Guid.NewGuid().ToString("N"));
    private readonly Mock<ILogger<ComicReaderService>> _logger = new();
    private readonly ComicReaderService _service;

    public ComicReaderServicePageStreamTests()
    {
        Directory.CreateDirectory(_dir);
        _service = new ComicReaderService(_logger.Object);
    }

    public void Dispose()
    {
        _service.Dispose();
        TestIO.TryDeleteDirectory(_dir);
    }

    [Fact]
    public async Task AStoredCbzPage_IsSentStraightFromTheArchiveFile()
    {
        var page = Enumerable.Range(0, 200_000).Select(i => (byte)(i % 251)).ToArray();
        var cbz = Cbz("stored.cbz", CompressionLevel.NoCompression, ("p1.jpg", page));

        var opened = await _service.OpenPageAsync(cbz, 1, default);

        await using (var content = opened!.Value.Content)
        {
            Assert.IsType<ComicArchiveReader.ArchiveRangeStream>(content);
            Assert.Equal("image/jpeg", opened.Value.ContentType);
            Assert.Equal(page.Length, content.Length);
            Assert.Equal(page, await ReadAllAsync(content));
        }

        // Sent and disposed, it no longer holds the archive.
        File.Delete(cbz);
    }

    [Fact]
    public async Task ACompressedCbzPage_IsReadIntoAPooledBuffer()
    {
        var page = new byte[100_000];
        var cbz = Cbz("deflated.cbz", CompressionLevel.SmallestSize, ("p1.png", page));

        var opened = await _service.OpenPageAsync(cbz, 1, default);

        await using var content = Assert.IsType<RentedBufferStream>(opened!.Value.Content);
        Assert.Equal("image/png", opened.Value.ContentType);
        Assert.Equal(page, content.ToExactArray());
        File.Delete(cbz);
    }

    [Fact]
    public async Task ACbrPage_IsReadIntoAPooledBuffer()
    {
        var cbr = Path.Join(_dir, "plain.cbr");
        ComicReaderServiceCoverageTests.StoredRar4.Write(cbr, ("p1.jpg", Encoding.ASCII.GetBytes("one")));

        var opened = await _service.OpenPageAsync(cbr, 1, default);

        await using var content = Assert.IsType<RentedBufferStream>(opened!.Value.Content);
        Assert.Equal(Encoding.ASCII.GetBytes("one"), content.ToExactArray());
    }

    [Theory]
    [InlineData(CompressionLevel.NoCompression)]
    [InlineData(CompressionLevel.Optimal)]
    public async Task GetPageAsync_ReturnsExactlyThePagesBytes(CompressionLevel level)
    {
        var page = Enumerable.Range(0, 5000).Select(i => (byte)i).ToArray();
        var cbz = Cbz($"bytes-{level}.cbz", level, ("p1.jpg", page));

        var read = await _service.GetPageAsync(cbz, 1);

        Assert.Equal(page, read!.Value.Data);
    }

    [Fact]
    public async Task AStoredPageOverTheCap_IsStillRefused()
    {
        // Streaming does not get around the per-page limit: the declared size is checked before anything is sent.
        var cbr = Path.Join(_dir, "huge.cbz");
        File.WriteAllBytes(cbr, ArchiveEntryReaderTests.BuildZip(("p1.jpg", new byte[16], CompressionLevel.NoCompression)));
        var bytes = File.ReadAllBytes(cbr);
        // Declare the entry over the cap in the central directory, which is where the reader takes sizes from.
        var central = IndexOfSignature(bytes, 0x02014b50);
        BitConverter.GetBytes((uint)(ArchiveEntryReader.MaxPageBytes + 1)).CopyTo(bytes, central + 20);
        BitConverter.GetBytes((uint)(ArchiveEntryReader.MaxPageBytes + 1)).CopyTo(bytes, central + 24);
        File.WriteAllBytes(cbr, bytes);

        var ex = await Assert.ThrowsAsync<ArchiveLimitExceededException>(() => _service.OpenPageAsync(cbr, 1, default));

        Assert.Contains("p1.jpg", ex.Message);
    }

    [Fact]
    public async Task ACancelledRequest_IsCancelledWithoutBeingLoggedAsAnError()
    {
        var cbz = Cbz("cancelled.cbz", CompressionLevel.Optimal, ("p1.jpg", new byte[1000]));
        await _service.GetPageCountAsync(cbz);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _service.OpenPageAsync(cbz, 1, new CancellationToken(canceled: true)));

        _logger.Verify(
            l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
    }

    private string Cbz(string name, CompressionLevel level, params (string Name, byte[] Data)[] pages)
    {
        var path = Path.Join(_dir, name);
        File.WriteAllBytes(path, ArchiveEntryReaderTests.BuildZip(pages.Select(p => (p.Name, p.Data, level)).ToArray()));
        return path;
    }

    private static int IndexOfSignature(byte[] bytes, uint signature)
    {
        for (var i = 0; i <= bytes.Length - 4; i++)
        {
            if (BitConverter.ToUInt32(bytes, i) == signature)
            {
                return i;
            }
        }

        throw new InvalidOperationException("Signature not found.");
    }

    private static async Task<byte[]> ReadAllAsync(Stream content)
    {
        using var copy = new MemoryStream();
        await content.CopyToAsync(copy);
        return copy.ToArray();
    }
}
