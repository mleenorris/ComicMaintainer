using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Core.Utilities;
using ImageMagick;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using StoredRar4 = ComicMaintainer.Tests.Services.ComicReaderServiceCoverageTests.StoredRar4;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// .zip, .rar and misnamed archives, and the crafted ones the limits refuse, in the features
/// that read archives directly: the web reader, EPUB conversion, the series cover writer and
/// the Suwayomi import check.
/// </summary>
public sealed class ArchiveFormatSupportTests : IDisposable
{
    private readonly string _dir = Path.Join(Path.GetTempPath(), "archive-formats-" + Guid.NewGuid().ToString("N"));

    public ArchiveFormatSupportTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best effort cleanup.
        }
    }

    private string Zip(string name, params (string Name, byte[] Data)[] entries)
    {
        var path = Path.Join(_dir, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entryName, data) in entries)
        {
            using var stream = zip.CreateEntry(entryName, CompressionLevel.Fastest).Open();
            stream.Write(data);
        }

        return path;
    }

    private string Rar(string name, params (string Name, byte[]? Data)[] entries)
    {
        var path = Path.Join(_dir, name);
        StoredRar4.Write(path, entries);
        return path;
    }

    private static byte[] Bytes(string text) => Encoding.ASCII.GetBytes(text);

    private static byte[] Jpeg()
    {
        using var image = new MagickImage(MagickColors.Red, 20, 30);
        return image.ToByteArray(MagickFormat.Jpeg);
    }

    private static ComicReaderService CreateReader() => new(NullLogger<ComicReaderService>.Instance);

    // ---- Web reader and covers -------------------------------------------------------------

    [Theory]
    [InlineData("zip", "book.zip")]
    [InlineData("zip", "book.cbr")]
    [InlineData("rar", "book.rar")]
    [InlineData("rar", "book.cbz")]
    public async Task Reader_OpensZipAndRarFilesWhateverTheirExtension(string format, string name)
    {
        (string, byte[])[] pages = { ("page2.jpg", Bytes("two")), ("page10.jpg", Bytes("ten")), ("page1.png", Bytes("one")) };
        var path = format == "zip"
            ? Zip(name, pages)
            : Rar(name, pages.Select(p => (p.Item1, (byte[]?)p.Item2)).ToArray());
        using var reader = CreateReader();

        Assert.Equal(3, await reader.GetPageCountAsync(path));
        Assert.Equal(new[] { "page1.png", "page2.jpg", "page10.jpg" }, await reader.GetPageNamesAsync(path));
        var page = await reader.GetPageAsync(path, 2);
        Assert.Equal("image/jpeg", page!.Value.ContentType);
        Assert.Equal(Bytes("two"), page.Value.Data);
    }

    [Fact]
    public async Task Reader_RefusesARar5WithAHugeDictionaryBeforeAllocatingIt()
    {
        var path = Path.Join(_dir, "bomb.cbr");
        File.WriteAllBytes(path, Rar5WithDictionaryPower(13));
        using var reader = CreateReader();

        var ex = await Assert.ThrowsAsync<ArchiveLimitExceededException>(() => reader.GetPageCountAsync(path));

        Assert.Contains("decompression dictionary", ex.Message);
        await Assert.ThrowsAsync<ArchiveLimitExceededException>(() => reader.GetPageCountAsync(path));
        Assert.Equal(1, reader.ListingCount);
    }

    private static byte[] Rar5WithDictionaryPower(int power)
    {
        using var output = new MemoryStream();
        output.Write(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00 });
        WriteBlock(output, new byte[] { 1, 0, 0 });

        var name = Encoding.UTF8.GetBytes("p1.jpg");
        using var file = new MemoryStream();
        file.WriteByte(2);
        file.WriteByte(2);
        file.WriteByte(16);
        file.WriteByte(0);
        foreach (var part in new ulong[] { 5_000_000, 0x20, (3UL << 7) | ((ulong)power << 10), 1, (ulong)name.Length })
        {
            WriteVInt(file, part);
        }

        file.Write(name);
        WriteBlock(output, file.ToArray());
        output.Write(new byte[16]);
        WriteBlock(output, new byte[] { 5, 0, 0 });
        return output.ToArray();
    }

    private static void WriteBlock(Stream output, byte[] body)
    {
        using var header = new MemoryStream();
        WriteVInt(header, (ulong)body.Length);
        header.Write(body);
        var crc = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(crc, StoredRar4.Crc32(header.ToArray()));
        output.Write(crc);
        header.WriteTo(output);
    }

    private static void WriteVInt(Stream output, ulong value)
    {
        do
        {
            var next = (byte)(value & 0x7F);
            value >>= 7;
            output.WriteByte(value != 0 ? (byte)(next | 0x80) : next);
        }
        while (value != 0);
    }

    // ---- EPUB conversion ---------------------------------------------------------------------

    [Theory]
    [InlineData("book.cbz")]
    [InlineData("book.zip")]
    [InlineData("zip-as.cbr")]
    public async Task Epub_ConvertsZipFiles(string name)
    {
        var path = Zip(name, ("001.jpg", Jpeg()), ("__MACOSX/._001.jpg", new byte[] { 0, 5, 22 }));

        var epub = await new EpubConversionService(NullLogger<EpubConversionService>.Instance)
            .ConvertToEpubAsync(path, Path.Join(_dir, "out-" + Path.GetFileNameWithoutExtension(name)));

        using var archive = ZipFile.OpenRead(epub);
        // The resource fork is not a page of the book.
        Assert.Single(archive.Entries, e => e.FullName.StartsWith("OEBPS/images/page", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("book.rar")]
    [InlineData("rar-as.cbz")]
    public async Task Epub_ConvertsRarFiles(string name)
    {
        var path = Rar(name, ("001.jpg", Jpeg()), ("ComicInfo.xml", Bytes("<ComicInfo><Series>Rar Series</Series></ComicInfo>")));

        var epub = await new EpubConversionService(NullLogger<EpubConversionService>.Instance)
            .ConvertToEpubAsync(path, Path.Join(_dir, "out-" + Path.GetFileNameWithoutExtension(name)));

        using var archive = ZipFile.OpenRead(epub);
        Assert.Contains(archive.Entries, e => e.FullName == "OEBPS/images/page0001.jpg");
        using var opf = new StreamReader(archive.GetEntry("OEBPS/content.opf")!.Open());
        Assert.Contains("Rar Series", await opf.ReadToEndAsync());
    }

    // ---- Series cover writer -----------------------------------------------------------------

    [Fact]
    public void CoverWriter_RewritesARarNamedCbzAsARealCbz()
    {
        var path = Rar("book.cbz", ("page.jpg", Bytes("page")));

        SeriesArchiveCoverWriter.RewriteArchive(path, "cover.jpg", Bytes("cover"));

        using var zip = ZipFile.OpenRead(path);
        Assert.Equal(new[] { "page.jpg", "cover.jpg" }, zip.Entries.Select(e => e.FullName));
    }

    [Fact]
    public void CoverWriter_ABombIsRefusedLeavingTheArchiveAndNoTempFile()
    {
        var path = Zip("bomb.cbz", ("page.jpg", new byte[32 * 1024 * 1024]));
        var before = File.ReadAllBytes(path);

        var ex = Assert.Throws<ArchiveLimitExceededException>(() =>
            SeriesArchiveCoverWriter.RewriteArchive(path, "cover.jpg", Bytes("cover")));

        Assert.Contains("more than 100:1", ex.Message);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.DoesNotContain(Directory.GetFiles(_dir), f => f != path);
    }

    // ---- Suwayomi import check ---------------------------------------------------------------

    [Fact]
    public void IsCompleteArchive_FollowsTheContentNotTheExtension()
    {
        Assert.True(SuwayomiDownloadImportService.IsCompleteArchive(Rar("rar-as.cbz", ("page.jpg", Bytes("page")))));
        Assert.True(SuwayomiDownloadImportService.IsCompleteArchive(Zip("zip-as.cbr", ("page.jpg", Bytes("page")))));
    }

    [Fact]
    public void IsCompleteArchive_EmptyArchiveIsNotComplete()
    {
        var path = Path.Join(_dir, "empty.cbz");
        ZipFile.Open(path, ZipArchiveMode.Create).Dispose();

        Assert.False(SuwayomiDownloadImportService.IsCompleteArchive(path));
    }

    [Fact]
    public void IsCompleteArchive_ArchiveOverTheEntryLimitIsNotComplete()
    {
        var path = Path.Join(_dir, "too-many.cbz");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            for (var i = 0; i <= ArchiveEntryReader.MaxEntries; i++)
            {
                zip.CreateEntry($"p{i}.jpg", CompressionLevel.NoCompression);
            }
        }

        Assert.False(SuwayomiDownloadImportService.IsCompleteArchive(path));
    }
}
