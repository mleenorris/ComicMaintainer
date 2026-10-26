using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using ComicMaintainer.Core.Services;
using Microsoft.Extensions.Logging;
using Moq;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Covers the CBR (RAR) paths, the remaining content types and the error
/// handling of <see cref="ComicReaderService"/>.
/// </summary>
public class ComicReaderServiceCoverageTests : IDisposable
{
    private readonly Mock<ILogger<ComicReaderService>> _logger = new();
    private readonly ComicReaderService _service;
    private readonly string _testDir;

    public ComicReaderServiceCoverageTests()
    {
        _service = new ComicReaderService(_logger.Object);
        _testDir = Path.Combine(Path.GetTempPath(), $"comic-reader-cov-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    [Fact]
    public async Task GetPageNamesAsync_WithCbr_ListsOnlyImagesInNaturalOrder()
    {
        var cbr = CreateCbr();

        var names = await _service.GetPageNamesAsync(cbr);

        Assert.Equal(new[] { "page1.gif", "page2.png", "page10.jpg" }, names);
        Assert.Equal(3, await _service.GetPageCountAsync(cbr));
    }

    [Fact]
    public async Task GetPageAsync_WithCbr_ReturnsThePageBytesAndContentType()
    {
        var cbr = CreateCbr();

        var first = await _service.GetPageAsync(cbr, 1);
        var second = await _service.GetPageAsync(cbr, 2);
        var third = await _service.GetPageAsync(cbr, 3);

        Assert.NotNull(first);
        Assert.Equal(Encoding.ASCII.GetBytes("gif page"), first.Value.Data);
        Assert.Equal("image/gif", first.Value.ContentType);
        Assert.Equal(Encoding.ASCII.GetBytes("png page"), second!.Value.Data);
        Assert.Equal("image/png", second.Value.ContentType);
        Assert.Equal(Encoding.ASCII.GetBytes("jpg page ten"), third!.Value.Data);
        Assert.Equal("image/jpeg", third.Value.ContentType);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public async Task GetPageAsync_WithCbrAndOutOfRangePage_ReturnsNull(int pageNumber)
    {
        var cbr = CreateCbr();

        Assert.Null(await _service.GetPageAsync(cbr, pageNumber));
        VerifyLogged(LogLevel.Warning, Times.Once());
    }

    [Theory]
    [InlineData("page.jpeg", "image/jpeg")]
    [InlineData("PAGE.JPG", "image/jpeg")]
    [InlineData("page.gif", "image/gif")]
    [InlineData("page.bmp", "image/bmp")]
    [InlineData("page.webp", "image/webp")]
    public async Task GetPageAsync_WithCbz_ReportsTheContentTypeOfEachImageFormat(string entryName, string expected)
    {
        var cbz = Path.Combine(_testDir, $"{Guid.NewGuid():N}.cbz");
        using (var archive = ZipFile.Open(cbz, ZipArchiveMode.Create))
        {
            using var stream = archive.CreateEntry(entryName).Open();
            stream.Write(new byte[] { 1, 2, 3 });
        }

        var page = await _service.GetPageAsync(cbz, 1);

        Assert.NotNull(page);
        Assert.Equal(expected, page.Value.ContentType);
        Assert.Equal(new byte[] { 1, 2, 3 }, page.Value.Data);
    }

    [Theory]
    [InlineData("broken.cbz")]
    [InlineData("broken.cbr")]
    public async Task CorruptArchives_AreReportedAsEmptyInsteadOfThrowing(string fileName)
    {
        var path = Path.Combine(_testDir, fileName);
        await File.WriteAllBytesAsync(path, Encoding.ASCII.GetBytes("this is not an archive at all"));

        var names = await _service.GetPageNamesAsync(path);
        var page = await _service.GetPageAsync(path, 1);
        var count = await _service.GetPageCountAsync(path);

        Assert.Empty(names);
        Assert.Null(page);
        Assert.Equal(0, count);
        VerifyLogged(LogLevel.Error, Times.Exactly(3));
    }

    private string CreateCbr()
    {
        var path = Path.Combine(_testDir, $"{Guid.NewGuid():N}.cbr");
        StoredRar4.Write(
            path,
            ("extras", null),
            ("page10.jpg", Encoding.ASCII.GetBytes("jpg page ten")),
            ("ComicInfo.xml", Encoding.ASCII.GetBytes("<ComicInfo />")),
            ("page2.png", Encoding.ASCII.GetBytes("png page")),
            ("page1.gif", Encoding.ASCII.GetBytes("gif page")));
        return path;
    }

    private void VerifyLogged(LogLevel level, Times times) =>
        _logger.Verify(
            l => l.Log(
                level,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            times);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort cleanup.
        }
    }

    /// <summary>
    /// Writes a minimal RAR 4.x archive whose entries are all stored
    /// (uncompressed), which is enough for readers to list and extract them
    /// without a RAR encoder. An entry with null data is written as a directory.
    /// </summary>
    internal static class StoredRar4
    {
        private static readonly uint[] CrcTable = BuildCrcTable();

        public static void Write(string path, params (string Name, byte[]? Data)[] entries)
        {
            using var output = new MemoryStream();
            output.Write(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00 });

            // Main archive header: two reserved fields (HighPosAV, PosAV).
            WriteBlock(output, 0x73, 0x0000, new byte[6]);

            foreach (var (name, data) in entries)
            {
                var isDirectory = data is null;
                var payload = data ?? Array.Empty<byte>();
                var nameBytes = Encoding.ASCII.GetBytes(name);

                var body = new byte[25 + nameBytes.Length];
                var span = body.AsSpan();
                BinaryPrimitives.WriteUInt32LittleEndian(span[0..], (uint)payload.Length); // packed size
                BinaryPrimitives.WriteUInt32LittleEndian(span[4..], (uint)payload.Length); // unpacked size
                body[8] = 0; // host OS: MS-DOS
                BinaryPrimitives.WriteUInt32LittleEndian(span[9..], Crc32(payload));
                BinaryPrimitives.WriteUInt32LittleEndian(span[13..], 0x50210000); // 2020-01-01 (DOS time)
                body[17] = 20; // version needed to extract
                body[18] = 0x30; // method: store
                BinaryPrimitives.WriteUInt16LittleEndian(span[19..], (ushort)nameBytes.Length);
                BinaryPrimitives.WriteUInt32LittleEndian(span[21..], isDirectory ? 0x10u : 0x20u);
                nameBytes.CopyTo(body, 25);

                // 0x8000: data follows the header; 0x00E0: directory entry.
                WriteBlock(output, 0x74, (ushort)(0x8000 | (isDirectory ? 0x00E0 : 0)), body);
                output.Write(payload);
            }

            WriteBlock(output, 0x7B, 0x4000, Array.Empty<byte>());
            File.WriteAllBytes(path, output.ToArray());
        }

        private static void WriteBlock(Stream output, byte type, ushort flags, byte[] body)
        {
            // HEAD_CRC covers everything after itself: type, flags, size and body.
            var header = new byte[5 + body.Length];
            header[0] = type;
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(1), flags);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(3), (ushort)(header.Length + 2));
            body.CopyTo(header, 5);

            var crc = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(crc, (ushort)(Crc32(header) & 0xFFFF));
            output.Write(crc);
            output.Write(header);
        }

        /// <summary>The standard (zip/PNG/RAR) CRC-32.</summary>
        internal static uint Crc32(ReadOnlySpan<byte> data)
        {
            var crc = 0xFFFFFFFFu;
            foreach (var b in data)
            {
                crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
            }

            return ~crc;
        }

        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                var value = i;
                for (var bit = 0; bit < 8; bit++)
                {
                    value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
                }

                table[i] = value;
            }

            return table;
        }
    }
}
