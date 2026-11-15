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

    [Fact]
    public async Task ACbzPage_IsOpenedFromWhereTheListingFoundIt_WithoutReadingTheArchiveAgain()
    {
        var stored = Enumerable.Range(0, 5000).Select(i => (byte)(i % 251)).ToArray();
        var deflated = Enumerable.Range(0, 50_000).Select(i => (byte)(i / 100)).ToArray();
        var path = Path.Join(_dir, "located.cbz");
        File.WriteAllBytes(path, ArchiveEntryReaderTests.BuildZip(
            ("p1.jpg", stored, CompressionLevel.NoCompression),
            ("p2.png", deflated, CompressionLevel.Optimal)));
        Assert.Equal(2, await _service.GetPageCountAsync(path));

        // Without its end of central directory record the archive no longer opens, and its length and
        // time are unchanged: a page served now was read from where the listing found it.
        RewriteInPlace(path, bytes => BitConverter.GetBytes(0u).CopyTo(bytes, LastIndexOfSignature(bytes, 0x06054b50)));

        Assert.Equal(stored, (await _service.GetPageAsync(path, 1))!.Value.Data);
        Assert.Equal(deflated, (await _service.GetPageAsync(path, 2))!.Value.Data);
        Assert.Equal(2, _service.LocatedPageReadCount);
        Assert.Equal(1, _service.ListingCount);
    }

    [Theory]
    [InlineData(CompressionLevel.NoCompression)]
    [InlineData(CompressionLevel.Optimal)]
    public async Task ACbzPageRewrittenWithTheSameLengthAndTimestamp_ServesTheNewBytes(CompressionLevel level)
    {
        var path = Cbz($"rewritten-{level}.cbz", level, ("p1.jpg", Encoding.ASCII.GetBytes("one")));
        var mtime = File.GetLastWriteTimeUtc(path);
        var length = new FileInfo(path).Length;
        Assert.Equal(Encoding.ASCII.GetBytes("one"), (await _service.GetPageAsync(path, 1))!.Value.Data);

        Cbz($"rewritten-{level}.cbz", level, ("p1.jpg", Encoding.ASCII.GetBytes("uno")));
        File.SetLastWriteTimeUtc(path, mtime);
        Assert.Equal(length, new FileInfo(path).Length);

        // The local header no longer carries the CRC the listing recorded, so the archive is read again.
        Assert.Equal(Encoding.ASCII.GetBytes("uno"), (await _service.GetPageAsync(path, 1))!.Value.Data);
        Assert.Equal(1, _service.LocatedPageReadCount);
    }

    [Fact]
    public async Task ACbzWrittenWithDataDescriptors_IsAlsoOpenedFromWhereTheListingFoundIt()
    {
        var stored = Enumerable.Range(0, 3000).Select(i => (byte)i).ToArray();
        var deflated = new byte[40_000];
        var path = Path.Join(_dir, "streamed.cbz");
        using (var output = new MemoryStream())
        {
            // Written to a stream it cannot seek back in, ZipArchive leaves the CRC and sizes to a
            // data descriptor after each entry's data.
            using (var archive = new ZipArchive(new WriteOnlyStream(output), ZipArchiveMode.Create))
            {
                foreach (var (name, data, level) in new[] { ("p1.jpg", stored, CompressionLevel.NoCompression), ("p2.jpg", deflated, CompressionLevel.Optimal) })
                {
                    using var entry = archive.CreateEntry(name, level).Open();
                    entry.Write(data);
                }
            }

            File.WriteAllBytes(path, output.ToArray());
        }

        Assert.NotEqual(0, BitConverter.ToUInt16(File.ReadAllBytes(path), 6) & 8);

        Assert.Equal(stored, (await _service.GetPageAsync(path, 1))!.Value.Data);
        Assert.Equal(deflated, (await _service.GetPageAsync(path, 2))!.Value.Data);
        Assert.Equal(2, _service.LocatedPageReadCount);
    }

    [Fact]
    public async Task ADamagedDeflatedCbzPage_IsNotServed()
    {
        var page = Enumerable.Range(0, 20_000).Select(i => (byte)(i * 7 % 256)).ToArray();
        var path = Cbz("damaged.cbz", CompressionLevel.Optimal, ("p1.jpg", page));
        Assert.Equal(1, await _service.GetPageCountAsync(path));

        RewriteInPlace(path, bytes =>
        {
            var dataStart = 30 + BitConverter.ToUInt16(bytes, 26) + BitConverter.ToUInt16(bytes, 28);
            bytes[dataStart + 20] ^= 0x55;
        });

        Assert.Null(await _service.GetPageAsync(path, 1));
        Assert.Equal(0, _service.LocatedPageReadCount);
    }

    [Fact]
    public async Task ACbrListedAsASingleFile_IsOpenedAsOneWithoutLookingForVolumes()
    {
        var first = Path.Join(_dir, "set.part1.rar");
        ComicReaderServiceCoverageTests.StoredRar4.Write(first, ("p1.jpg", Encoding.ASCII.GetBytes("one")));
        Assert.Equal(1, await _service.GetPageCountAsync(first));

        // A next part that the volume check would refuse, appearing after the file was listed: the page
        // is read from the file that was listed, and the part is never opened.
        File.WriteAllBytes(Path.Join(_dir, "set.part2.rar"), ComicArchiveReaderTests.Rar5(13));

        Assert.Equal(Encoding.ASCII.GetBytes("one"), (await _service.GetPageAsync(first, 1))!.Value.Data);
        Assert.Throws<ArchiveLimitExceededException>(() => ComicArchiveReader.Open(first));
    }

    private static void RewriteInPlace(string path, Action<byte[]> change)
    {
        var mtime = File.GetLastWriteTimeUtc(path);
        var bytes = File.ReadAllBytes(path);
        change(bytes);
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, mtime);
    }

    private static int LastIndexOfSignature(byte[] bytes, uint signature)
    {
        for (var i = bytes.Length - 4; i >= 0; i--)
        {
            if (BitConverter.ToUInt32(bytes, i) == signature)
            {
                return i;
            }
        }

        throw new InvalidOperationException("Signature not found.");
    }

    /// <summary>A stream that can only be written forwards, as a network stream can.</summary>
    private sealed class WriteOnlyStream(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
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
