using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using ComicMaintainer.Core.Utilities;
using ComicMaintainer.Tests.Helpers;
using ComicMaintainer.Tests.Services;
using SharpCompress.Archives.Rar;

namespace ComicMaintainer.Tests.Utilities;

/// <summary>
/// The archive read limits: entries are refused by their declared size and
/// compression ratio before anything is decompressed, by the data actually read
/// when a header lies, and ZIP entry counts are checked before
/// <see cref="ZipArchive"/> builds its entry list.
/// </summary>
public sealed class ArchiveEntryReaderTests : IDisposable
{
    private readonly string _dir = Path.Join(Path.GetTempPath(), "archive-limits-" + Guid.NewGuid().ToString("N"));

    public ArchiveEntryReaderTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => TestIO.TryDeleteDirectory(_dir);

    // ---- EnumerateEntries / ReadText --------------------------------------------------

    [Fact]
    public void EnumerateEntries_PastTheEntryLimit_IsRefusedOnlyWhenItGetsThere()
    {
        var path = Path.Join(_dir, "many.cbz");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            for (var i = 0; i <= ArchiveEntryReader.MaxEntries; i++)
            {
                zip.CreateEntry($"p{i}.jpg");
            }
        }

        using var archive = SharpCompress.Archives.Zip.ZipArchive.OpenArchive(path);

        // Lazy, so a caller that finds what it wants early pays nothing for the rest.
        Assert.Equal("p0.jpg", ArchiveEntryReader.EnumerateEntries(archive, "many.cbz").First().Key);
        var ex = Assert.Throws<ArchiveLimitExceededException>(
            () => ArchiveEntryReader.EnumerateEntries(archive, "many.cbz").ToList());
        Assert.Contains("many.cbz", ex.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadText_DecodesUtf8WithOrWithoutAByteOrderMark(bool withBom)
    {
        var path = Path.Join(_dir, "tags.cbz");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry("ComicInfo.xml").Open(), new UTF8Encoding(withBom));
            writer.Write("<ComicInfo>Caf\u00e9</ComicInfo>");
        }

        using var archive = SharpCompress.Archives.Zip.ZipArchive.OpenArchive(path);

        Assert.Equal("<ComicInfo>Caf\u00e9</ComicInfo>", ArchiveEntryReader.ReadText(archive.Entries.Single()));
    }

    [Fact]
    public void ReadText_OverTheLimit_IsRefused()
    {
        var path = Path.Join(_dir, "huge.cbz");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using var entry = zip.CreateEntry("ComicInfo.xml").Open();
            entry.Write(new byte[ArchiveEntryReader.MaxComicInfoBytes + 1]);
        }

        using var archive = SharpCompress.Archives.Zip.ZipArchive.OpenArchive(path);

        var ex = Assert.Throws<ArchiveLimitExceededException>(() => ArchiveEntryReader.ReadText(archive.Entries.Single()));
        Assert.Contains("ComicInfo.xml", ex.Message);
    }

    // ---- ReadBounded ----------------------------------------------------------------

    [Fact]
    public void ReadBounded_KnownLength_ReturnsExactlyTheData()
    {
        var data = Encoding.ASCII.GetBytes("page bytes");

        var read = ArchiveEntryReader.ReadBounded(new MemoryStream(data), "p1.jpg", data.Length, maxBytes: 1024);

        Assert.Equal(data, read);
    }

    [Fact]
    public void ReadBounded_StreamLongerThanItsDeclaredLength_IsRefused()
    {
        // A header that understates the size must not let the data through.
        var stream = new MemoryStream(new byte[100]);

        var ex = Assert.Throws<ArchiveLimitExceededException>(
            () => ArchiveEntryReader.ReadBounded(stream, "p1.jpg", declaredLength: 10, maxBytes: 1024));

        Assert.Contains("p1.jpg", ex.Message);
        Assert.Contains("more data than", ex.Message);
    }

    [Fact]
    public void ReadBounded_TruncatedStream_ReturnsWhatWasRead()
    {
        var read = ArchiveEntryReader.ReadBounded(new MemoryStream(new byte[] { 1, 2, 3 }), "p1.jpg", declaredLength: 10, maxBytes: 1024);

        Assert.Equal(new byte[] { 1, 2, 3 }, read);
    }

    [Fact]
    public void ReadBounded_UnknownLengthWithinTheCap_ReturnsTheData()
    {
        var data = new byte[200_000];
        Random.Shared.NextBytes(data);

        var read = ArchiveEntryReader.ReadBounded(new MemoryStream(data), "p1.jpg", declaredLength: -1, maxBytes: data.Length);

        Assert.Equal(data, read);
    }

    [Fact]
    public void ReadBounded_UnknownLengthPastTheCap_StopsReadingAndIsRefused()
    {
        var stream = new CountingStream(new MemoryStream(new byte[1_000_000]));

        var ex = Assert.Throws<ArchiveLimitExceededException>(
            () => ArchiveEntryReader.ReadBounded(stream, "p1.jpg", declaredLength: -1, maxBytes: 100_000));

        Assert.Contains("expands past", ex.Message);
        // Refused as soon as the cap is passed, not after reading everything.
        Assert.True(stream.BytesRead < 300_000, $"read {stream.BytesRead} bytes");
    }

    [Fact]
    public void ReadBounded_DeclaredLengthOverTheCap_IsRefusedWithoutReading()
    {
        var stream = new CountingStream(new MemoryStream(new byte[10]));

        Assert.Throws<ArchiveLimitExceededException>(
            () => ArchiveEntryReader.ReadBounded(stream, "p1.jpg", declaredLength: 2048, maxBytes: 1024));

        Assert.Equal(0, stream.BytesRead);
    }

    // ---- EnsureWithinLimits -----------------------------------------------------------

    [Fact]
    public void EnsureWithinLimits_DeclaredSizeOverTheCap_IsRefusedWithAClearReason()
    {
        // Stored (1:1), so only the size cap applies.
        const long size = 150L * 1024 * 1024;
        var ex = Assert.Throws<ArchiveLimitExceededException>(() => ArchiveEntryReader.EnsureWithinLimits(
            "big.png", size, size, ArchiveEntryReader.MaxPageBytes));

        Assert.Equal("Archive entry 'big.png' is 150 MiB uncompressed, over the 100 MiB limit.", ex.Message);
    }

    [Fact]
    public void EnsureWithinLimits_LargeEntryThatExpandsLikeABomb_IsRefused()
    {
        const long size = 32L * 1024 * 1024;

        var ex = Assert.Throws<ArchiveLimitExceededException>(
            () => ArchiveEntryReader.EnsureWithinLimits("bomb.png", size, size / 1000, ArchiveEntryReader.MaxPageBytes));

        Assert.Contains("decompression bomb", ex.Message);
    }

    [Fact]
    public void EnsureWithinLimits_AnUncompressedRasterPageThatExpandsFar_IsRefusedWithTheRemedy()
    {
        // A genuine near-blank BMP page over 16 MiB can deflate past 100:1 too. Its
        // name cannot tell it from a bomb (names are the archive author's choice),
        // so it is refused, and the reason says how to make the page readable.
        const long size = 72L * 1024 * 1024;

        var ex = Assert.Throws<ArchiveLimitExceededException>(
            () => ArchiveEntryReader.EnsureWithinLimits("blank.bmp", size, size / 400, ArchiveEntryReader.MaxPageBytes));

        Assert.Equal(
            "Archive entry 'blank.bmp' expands from 184.3 KiB to 72 MiB, more than 100:1, which is refused as a likely "
                + "decompression bomb. If it is a genuine page, repack the archive with that page saved as JPEG, PNG or WebP.",
            ex.Message);
    }

    [Theory]
    [InlineData(16L * 1024 * 1024, 1024)]        // at the threshold: ratio not checked
    [InlineData(64L * 1024 * 1024, 32L * 1024 * 1024)] // 2:1, like a PNG
    [InlineData(64L * 1024 * 1024, -1)]           // packed size unknown (solid archive)
    [InlineData(-1, -1)]                          // nothing declared
    public void EnsureWithinLimits_PlausibleEntries_AreAccepted(long declared, long compressed) =>
        ArchiveEntryReader.EnsureWithinLimits("p.png", declared, compressed, ArchiveEntryReader.MaxPageBytes);

    // ---- ZIP entries ------------------------------------------------------------------

    [Fact]
    public void ReadAllBytes_ZipEntryWithinTheCap_ReturnsItsBytes()
    {
        var data = Encoding.ASCII.GetBytes("a page");
        using var archive = new ZipArchive(new MemoryStream(BuildZip(("p1.jpg", data, CompressionLevel.Optimal))));

        Assert.Equal(data, ArchiveEntryReader.ReadAllBytes(archive.Entries[0], 1024));
    }

    [Fact]
    public void ReadAllBytes_ZipEntryOverTheCap_IsRefused()
    {
        using var archive = new ZipArchive(new MemoryStream(BuildZip(("p1.jpg", new byte[2048], CompressionLevel.NoCompression))));

        Assert.Throws<ArchiveLimitExceededException>(() => ArchiveEntryReader.ReadAllBytes(archive.Entries[0], 1024));
    }

    [Fact]
    public void ReadAllBytes_ZipDeflateBomb_IsRefusedBeforeItIsInflated()
    {
        // 32 MiB of zeros deflates to a few dozen KiB: far past any image's ratio.
        using var archive = new ZipArchive(new MemoryStream(BuildZip(("p1.png", new byte[32 * 1024 * 1024], CompressionLevel.SmallestSize))));
        var entry = archive.Entries[0];
        Assert.True(entry.Length / entry.CompressedLength > ArchiveEntryReader.MaxCompressionRatio);

        var ex = Assert.Throws<ArchiveLimitExceededException>(() => ArchiveEntryReader.ReadAllBytes(entry, ArchiveEntryReader.MaxPageBytes));

        Assert.Contains("p1.png", ex.Message);
    }

    [Fact]
    public void CountZipEntries_CountsTheCentralDirectory()
    {
        var zip = BuildZip(Pages(5));

        Assert.Equal(5, ArchiveEntryReader.CountZipEntries(new MemoryStream(zip), stopAfter: 100));
    }

    [Fact]
    public void CountZipEntries_StopsJustPastTheLimit()
    {
        var zip = BuildZip(Pages(50));

        Assert.Equal(11, ArchiveEntryReader.CountZipEntries(new MemoryStream(zip), stopAfter: 10));
    }

    [Fact]
    public void CountZipEntries_IgnoresACountTheArchiveUnderstates()
    {
        // ZipArchive reads records until the signature stops matching, whatever the
        // end-of-central-directory record claims, so the walk must not trust it either.
        var zip = BuildZip(Pages(7));
        var eocd = zip.Length - 22;
        BinaryPrimitives.WriteUInt16LittleEndian(zip.AsSpan(eocd + 8), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(zip.AsSpan(eocd + 10), 1);

        Assert.Equal(7, ArchiveEntryReader.CountZipEntries(new MemoryStream(zip), stopAfter: 100));
    }

    [Fact]
    public void CountZipEntries_FollowsTheZip64Records()
    {
        var zip = WithZip64Records(BuildZip(Pages(3)));
        // The crafted archive is valid: ZipArchive finds the same three entries.
        using (var archive = new ZipArchive(new MemoryStream(zip)))
        {
            Assert.Equal(3, archive.Entries.Count);
        }

        Assert.Equal(3, ArchiveEntryReader.CountZipEntries(new MemoryStream(zip), stopAfter: 100));
    }

    [Fact]
    public void CountZipEntries_FindsTheRecordBeforeAnArchiveComment()
    {
        var zip = BuildZip(Pages(4), comment: "scanned by someone");

        Assert.Equal(4, ArchiveEntryReader.CountZipEntries(new MemoryStream(zip), stopAfter: 100));
    }

    [Fact]
    public void CountZipEntries_FindsTheRecordBeforeALongArchiveComment()
    {
        // Past the first kilobyte searched, so the whole 64 KiB window is.
        var zip = BuildZip(Pages(4), comment: new string('c', 30_000));

        Assert.Equal(4, ArchiveEntryReader.CountZipEntries(new MemoryStream(zip), stopAfter: 100));
    }

    [Fact]
    public void EnsureZipEntryCountWithinLimit_ARealComic_ReadsOnlyItsLastKilobyte()
    {
        // Opening a comic for a page turn must not read and walk its central
        // directory a second time on top of ZipArchive: a directory this short
        // cannot hold too many records, so only the end record is looked up.
        var zip = BuildZip(Pages(200, size: 2048));
        Assert.True(zip.Length > 256 * 1024);
        using var stream = new ReadCountingStream(zip);

        ArchiveEntryReader.EnsureZipEntryCountWithinLimit(stream, "a.cbz");

        Assert.InRange(stream.BytesRead, 22, 1024);
    }

    [Fact]
    public void EnsureZipEntryCountWithinLimit_TooManyEntriesBehindALongComment_IsRefused()
    {
        var zip = BuildZip(Pages(ArchiveEntryReader.MaxEntries + 1, size: 0), comment: new string('c', 5_000));

        Assert.Throws<ArchiveLimitExceededException>(
            () => ArchiveEntryReader.EnsureZipEntryCountWithinLimit(new MemoryStream(zip), "many.cbz"));
    }

    [Fact]
    public void EnsureZipEntryCountWithinLimit_WalksEveryDirectoryLongEnoughForTooManyRecords()
    {
        // Records of the smallest possible size (no name, extra field or comment):
        // the shortest directory that can hold one record too many is still walked.
        var full = BuildBareCentralDirectory(ArchiveEntryReader.MaxEntries);
        var overfull = BuildBareCentralDirectory(ArchiveEntryReader.MaxEntries + 1);
        using var fullStream = new ReadCountingStream(full);

        ArchiveEntryReader.EnsureZipEntryCountWithinLimit(fullStream, "full.cbz");
        var ex = Assert.Throws<ArchiveLimitExceededException>(
            () => ArchiveEntryReader.EnsureZipEntryCountWithinLimit(new MemoryStream(overfull), "overfull.cbz"));

        Assert.InRange(fullStream.BytesRead, 22, 1024);
        Assert.Contains("overfull.cbz", ex.Message);
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23 })]
    public void CountZipEntries_NotAZip_ReturnsZero(byte[] bytes) =>
        Assert.Equal(0, ArchiveEntryReader.CountZipEntries(new MemoryStream(bytes), stopAfter: 100));

    [Fact]
    public void OpenZip_TooManyEntries_IsRefusedBeforeTheEntryListIsBuilt()
    {
        var path = Path.Join(_dir, "many.cbz");
        File.WriteAllBytes(path, BuildZip(Pages(ArchiveEntryReader.MaxEntries + 1, size: 0)));

        var ex = Assert.Throws<ArchiveLimitExceededException>(() => ArchiveEntryReader.OpenZip(path));

        Assert.Equal("'many.cbz' has more than 20,000 entries, which is over the limit for a comic archive.", ex.Message);
        // The file is not left open.
        File.Delete(path);
    }

    [Fact]
    public void OpenZip_AtTheEntryLimit_Opens()
    {
        var path = Path.Join(_dir, "full.cbz");
        File.WriteAllBytes(path, BuildZip(Pages(ArchiveEntryReader.MaxEntries, size: 0)));

        using var archive = ArchiveEntryReader.OpenZip(path);

        Assert.Equal(ArchiveEntryReader.MaxEntries, archive.Entries.Count);
    }

    [Fact]
    public void OpenZip_NotAZip_FailsLikeZipFileOpenRead()
    {
        var path = Path.Join(_dir, "junk.cbz");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("this is not an archive at all"));

        Assert.Throws<InvalidDataException>(() => ArchiveEntryReader.OpenZip(path));
        File.Delete(path);
    }

    // ---- RAR entries ------------------------------------------------------------------

    [Fact]
    public void ReadAllBytes_RarEntryWithinTheCap_ReturnsItsBytes()
    {
        var path = Path.Join(_dir, "ok.cbr");
        ComicReaderServiceCoverageTests.StoredRar4.Write(path, ("p1.jpg", Encoding.ASCII.GetBytes("rar page")));
        using var archive = RarArchive.OpenArchive(path);

        Assert.Equal(Encoding.ASCII.GetBytes("rar page"), ArchiveEntryReader.ReadAllBytes(archive.Entries.Single(), 1024));
    }

    [Fact]
    public void ReadAllBytes_RarEntryDeclaredOverTheCap_IsRefusedBeforeItIsRead()
    {
        var path = Path.Join(_dir, "huge.cbr");
        ComicReaderServiceCoverageTests.StoredRar4.WriteDeclaringSizes(path,
            ("p1.jpg", Encoding.ASCII.GetBytes("tiny"), (uint)(ArchiveEntryReader.MaxPageBytes + 1)));
        using var archive = RarArchive.OpenArchive(path);

        var ex = Assert.Throws<ArchiveLimitExceededException>(
            () => ArchiveEntryReader.ReadAllBytes(archive.Entries.Single(), ArchiveEntryReader.MaxPageBytes));

        Assert.Contains("over the 100 MiB limit", ex.Message);
    }

    // ---- helpers ----------------------------------------------------------------------

    private static (string Name, byte[] Data, CompressionLevel Level)[] Pages(int count, int size = 8) =>
        Enumerable.Range(1, count)
            .Select(i => ($"p{i:D5}.jpg", new byte[size], CompressionLevel.NoCompression))
            .ToArray();

    internal static byte[] BuildZip(params (string Name, byte[] Data, CompressionLevel Level)[] entries) =>
        BuildZip(entries, comment: null);

    internal static byte[] BuildZip((string Name, byte[] Data, CompressionLevel Level)[] entries, string? comment)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data, level) in entries)
            {
                using var stream = archive.CreateEntry(name, level).Open();
                stream.Write(data);
            }

            if (comment is not null)
            {
                archive.Comment = comment;
            }
        }

        return output.ToArray();
    }

    /// <summary>
    /// A central directory of <paramref name="records"/> minimal records (46 bytes,
    /// no name) starting at offset 0, followed by its end record: what
    /// <see cref="ZipArchive"/> would walk, without any file data.
    /// </summary>
    private static byte[] BuildBareCentralDirectory(int records)
    {
        const int RecordLength = 46;
        var bytes = new byte[(records * RecordLength) + 22];
        for (var i = 0; i < records; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * RecordLength), 0x02014b50);
        }

        var eocd = bytes.AsSpan(records * RecordLength);
        BinaryPrimitives.WriteUInt32LittleEndian(eocd, 0x06054b50);
        BinaryPrimitives.WriteUInt16LittleEndian(eocd[8..], (ushort)records);
        BinaryPrimitives.WriteUInt16LittleEndian(eocd[10..], (ushort)records);
        BinaryPrimitives.WriteUInt32LittleEndian(eocd[12..], (uint)(records * RecordLength));
        BinaryPrimitives.WriteUInt32LittleEndian(eocd[16..], 0);
        return bytes;
    }

    /// <summary>
    /// A read-only in-memory stream that counts the bytes read from it. Span reads
    /// need no override: on a type derived from <see cref="MemoryStream"/> they are
    /// routed through <see cref="Read(byte[], int, int)"/>, so counting them here
    /// as well would count every byte twice.
    /// </summary>
    private sealed class ReadCountingStream(byte[] data) : MemoryStream(data, writable: false)
    {
        public long BytesRead { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = base.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }

        public override int ReadByte()
        {
            var value = base.ReadByte();
            if (value >= 0)
            {
                BytesRead++;
            }

            return value;
        }
    }

    /// <summary>
    /// Rewrites an archive (without a comment) so its end-of-central-directory
    /// record defers to ZIP64 records, as an archive with more than 65,534
    /// entries or over 4 GiB would.
    /// </summary>
    private static byte[] WithZip64Records(byte[] zip)
    {
        var eocdStart = zip.Length - 22;
        var eocd = zip.AsSpan(eocdStart, 22);
        var entries = BinaryPrimitives.ReadUInt16LittleEndian(eocd[10..]);
        var directorySize = BinaryPrimitives.ReadUInt32LittleEndian(eocd[12..]);
        var directoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(eocd[16..]);

        var zip64Record = new byte[56];
        BinaryPrimitives.WriteUInt32LittleEndian(zip64Record, 0x06064b50);
        BinaryPrimitives.WriteUInt64LittleEndian(zip64Record.AsSpan(4), 44); // size of the rest of the record
        BinaryPrimitives.WriteUInt16LittleEndian(zip64Record.AsSpan(12), 45); // version made by
        BinaryPrimitives.WriteUInt16LittleEndian(zip64Record.AsSpan(14), 45); // version needed
        BinaryPrimitives.WriteUInt64LittleEndian(zip64Record.AsSpan(24), entries);
        BinaryPrimitives.WriteUInt64LittleEndian(zip64Record.AsSpan(32), entries);
        BinaryPrimitives.WriteUInt64LittleEndian(zip64Record.AsSpan(40), directorySize);
        BinaryPrimitives.WriteUInt64LittleEndian(zip64Record.AsSpan(48), directoryOffset);

        var locator = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(locator, 0x07064b50);
        BinaryPrimitives.WriteUInt64LittleEndian(locator.AsSpan(8), (ulong)eocdStart);
        BinaryPrimitives.WriteUInt32LittleEndian(locator.AsSpan(16), 1);

        var classic = eocd.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(classic.AsSpan(8), ushort.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(classic.AsSpan(10), ushort.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(classic.AsSpan(12), uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(classic.AsSpan(16), uint.MaxValue);

        return zip[..eocdStart].Concat(zip64Record).Concat(locator).Concat(classic).ToArray();
    }

    /// <summary>Counts the bytes read through it.</summary>
    private sealed class CountingStream(Stream inner) : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
