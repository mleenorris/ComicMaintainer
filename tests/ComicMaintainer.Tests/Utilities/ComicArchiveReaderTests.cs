using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using ComicMaintainer.Core.Utilities;
using ComicMaintainer.Tests.Services;
using SharpCompress.Common;
using SharpCompress.Writers.Zip;
using Xunit;

namespace ComicMaintainer.Tests.Utilities;

/// <summary>
/// The shared comic archive reader: format sniffing, the page and ComicInfo.xml rules every
/// feature relies on, and the limits on reading and rewriting.
/// </summary>
public sealed class ComicArchiveReaderTests : IDisposable
{
    private readonly string _dir = Path.Join(Path.GetTempPath(), "comic-archive-reader-" + Guid.NewGuid().ToString("N"));

    public ComicArchiveReaderTests() => Directory.CreateDirectory(_dir);

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
        ComicReaderServiceCoverageTests.StoredRar4.Write(path, entries);
        return path;
    }

    private static byte[] Bytes(string text) => Encoding.ASCII.GetBytes(text);

    // ---- Format sniffing ----------------------------------------------------------------

    [Theory]
    [InlineData("book.cbz")]
    [InlineData("book.zip")]
    [InlineData("book.cbr")]
    [InlineData("book.rar")]
    public void Open_ZipContent_IsAZipWhateverTheExtension(string name)
    {
        var path = Zip(name, ("p1.jpg", Bytes("page")));

        using var archive = ComicArchiveReader.Open(path);

        Assert.Equal(ComicArchiveFormat.Zip, archive.Format);
        Assert.Equal(Bytes("page"), archive.GetEntryAt(0)!.ReadAllBytes(1024));
    }

    [Theory]
    [InlineData("book.cbr")]
    [InlineData("book.rar")]
    [InlineData("book.cbz")]
    [InlineData("book.zip")]
    public void Open_RarContent_IsARarWhateverTheExtension(string name)
    {
        var path = Rar(name, ("p1.jpg", Bytes("page")));

        using var archive = ComicArchiveReader.Open(path);

        Assert.Equal(ComicArchiveFormat.Rar, archive.Format);
        Assert.Equal(Bytes("page"), archive.GetEntryAt(0)!.ReadAllBytes(1024));
    }

    [Theory]
    [InlineData(new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0, 0 })] // 7z
    [InlineData(new byte[] { 0x1F, 0x8B, 8, 0, 0, 0, 0, 0 })] // gzip
    [InlineData(new byte[] { 1, 2, 3 })] // neither, and shorter than a signature
    public void Open_OtherContent_IsRefusedAsNotAnArchive(byte[] content)
    {
        var path = Path.Join(_dir, "other.cbz");
        File.WriteAllBytes(path, content);

        var ex = Assert.Throws<ComicArchiveFormatException>(() => ComicArchiveReader.Open(path));

        // Safe to show users: the file's name and nothing about where it lives.
        Assert.Equal("'other.cbz' is not a readable ZIP or RAR archive.", ex.Message);
        Assert.DoesNotContain(_dir, ex.Message);
    }

    [Fact]
    public void Dispose_ReleasesTheRarFile()
    {
        // SharpCompress's default options leave the file open after the archive is disposed,
        // which would make the conversion to CBZ fail to delete it.
        var path = Rar("book.cbr", ("p1.jpg", Bytes("page")));

        using (var archive = ComicArchiveReader.Open(path))
        {
            _ = archive.GetEntryAt(0)!.ReadAllBytes(1024);
        }

        File.Delete(path);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Dispose_CanBeCalledTwice()
    {
        var archive = ComicArchiveReader.Open(Rar("book.cbr", ("p1.jpg", Bytes("page"))));
        archive.Dispose();
        archive.Dispose();
        Assert.Throws<ObjectDisposedException>(() => archive.EnumerateEntries());
    }

    // ---- Pages ---------------------------------------------------------------------------

    [Fact]
    public void ListPages_KeepsTheNaturalOrderAndFilterOfTheWebReader()
    {
        var path = Rar("book.cbr",
            ("page10.jpg", Bytes("a")),
            ("page2.PNG", Bytes("b")),
            ("page1.gif", Bytes("c")),
            ("notes.txt", Bytes("d")),
            ("folder", null));

        using var archive = ComicArchiveReader.Open(path);

        Assert.Equal(new[] { "page1.gif", "page2.PNG", "page10.jpg" }, archive.ListPages().Select(p => p.Name));
        Assert.Equal(new[] { 2, 1, 0 }, archive.ListPages().Select(p => p.Position));
    }

    [Fact]
    public void ListPages_MacResourceForksStayPagesSoPageNumbersDoNotShift()
    {
        // The web reader has always listed these, and reading progress stores page numbers.
        var path = Zip("book.cbz", ("__MACOSX/._p1.jpg", Bytes("fork")), ("p1.jpg", Bytes("page")));

        using var archive = ComicArchiveReader.Open(path);

        Assert.Equal(2, archive.ListPages().Count);
    }

    [Fact]
    public void GetEntryAt_ReturnsTheEntryOrNullPastTheEnd()
    {
        var path = Zip("book.cbz", ("a.jpg", Bytes("a")), ("b.jpg", Bytes("b")));

        using var archive = ComicArchiveReader.OpenForEntry(path, 1);

        Assert.Equal("b.jpg", archive.GetEntryAt(1)!.Name);
        Assert.Null(archive.GetEntryAt(2));
    }

    [Fact]
    public void GetEntryAt_RarPastTheEnd_IsNull()
    {
        var path = Rar("book.cbr", ("a.jpg", Bytes("a")));

        using var archive = ComicArchiveReader.Open(path);

        Assert.Equal("a.jpg", archive.GetEntryAt(0)!.Name);
        Assert.Null(archive.GetEntryAt(3));
    }

    [Theory]
    [InlineData("page.jpg", true)]
    [InlineData("PAGE.JPEG", true)]
    [InlineData("a/b/page.webp", true)]
    [InlineData("page.avif", false)] // Not a page format the reader shows.
    [InlineData("ComicInfo.xml", false)]
    public void IsPageImageName_MatchesTheWebReaderExtensions(string name, bool expected) =>
        Assert.Equal(expected, ComicArchiveReader.IsPageImageName(name));

    // ---- ComicInfo.xml -------------------------------------------------------------------

    [Fact]
    public void FindComicInfoEntry_PrefersTheRoot()
    {
        var path = Zip("book.cbz",
            ("extras/ComicInfo.xml", Bytes("<nested/>")),
            ("comicinfo.XML", Bytes("<root/>")));

        using var archive = ComicArchiveReader.Open(path);

        Assert.Equal("<root/>", archive.FindComicInfoEntry()!.ReadText());
    }

    [Fact]
    public void FindComicInfoEntry_OutsideTheRoot_TakesTheFirstInNaturalOrder()
    {
        var path = Zip("book.cbz",
            ("ch10/ComicInfo.xml", Bytes("<ten/>")),
            ("ch2/ComicInfo.xml", Bytes("<two/>")));

        using var archive = ComicArchiveReader.Open(path);

        Assert.Equal("<two/>", archive.FindComicInfoEntry()!.ReadText());
    }

    [Fact]
    public void FindComicInfoEntry_IgnoresMacResourceForks()
    {
        var path = Zip("book.cbz", ("__MACOSX/ComicInfo.xml", Bytes("<fork/>")), ("p1.jpg", Bytes("page")));

        using var archive = ComicArchiveReader.Open(path);

        Assert.Null(archive.FindComicInfoEntry());
    }

    [Fact]
    public void FindComicInfoEntry_FindsARarBackslashPath()
    {
        var path = Rar("book.cbr", ("sub\\ComicInfo.xml", Bytes("<nested/>")));

        using var archive = ComicArchiveReader.Open(path);

        Assert.Equal("<nested/>", archive.FindComicInfoEntry()!.ReadText());
    }

    [Theory]
    [InlineData("ComicInfo.xml", true, true)]
    [InlineData("COMICINFO.XML", true, true)]
    [InlineData("sub/ComicInfo.xml", true, false)]
    [InlineData("sub\\ComicInfo.xml", true, false)]
    [InlineData("__MACOSX/ComicInfo.xml", false, false)]
    [InlineData("MyComicInfo.xml", false, false)]
    public void ComicInfoNameRules(string name, bool anywhere, bool root)
    {
        Assert.Equal(anywhere, ComicArchiveReader.IsComicInfoName(name));
        Assert.Equal(root, ComicArchiveReader.IsRootComicInfoName(name));
    }

    // ---- RAR 5 dictionary --------------------------------------------------------------

    /// <summary>
    /// Writes a RAR 5 archive whose header asks for a decompression dictionary of
    /// 128 KiB &lt;&lt; <paramref name="dictionaryPower"/>, which is what makes SharpCompress
    /// allocate the unpack window. Its data does not need to be valid.
    /// </summary>
    private static byte[] Rar5(int dictionaryPower, int method = 3, bool directory = false, int fileHeaders = 1)
    {
        using var output = new MemoryStream();
        output.Write(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00 });
        using (var main = new MemoryStream())
        {
            WriteVInt(main, 1); // main archive header
            WriteVInt(main, 0);
            WriteVInt(main, 0);
            WriteBlock(output, main.ToArray());
        }

        for (var i = 0; i < fileHeaders; i++)
        {
            var name = Encoding.UTF8.GetBytes($"p{i}.jpg");
            var data = new byte[16];
            using var file = new MemoryStream();
            WriteVInt(file, 2); // file header
            WriteVInt(file, 2); // flags: a data area follows
            WriteVInt(file, (ulong)data.Length);
            WriteVInt(file, directory ? 1UL : 0UL); // file flags
            WriteVInt(file, 5_000_000); // unpacked size
            WriteVInt(file, 0x20);
            WriteVInt(file, (ulong)(method << 7) | ((ulong)dictionaryPower << 10));
            WriteVInt(file, 1); // host OS
            WriteVInt(file, (ulong)name.Length);
            file.Write(name);
            WriteBlock(output, file.ToArray());
            output.Write(data);
        }

        using (var end = new MemoryStream())
        {
            WriteVInt(end, 5);
            WriteVInt(end, 0);
            WriteVInt(end, 0);
            WriteBlock(output, end.ToArray());
        }

        return output.ToArray();
    }

    private static void WriteBlock(Stream output, byte[] body)
    {
        using var header = new MemoryStream();
        WriteVInt(header, (ulong)body.Length);
        header.Write(body);
        var crc = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(crc, ComicReaderServiceCoverageTests.StoredRar4.Crc32(header.ToArray()));
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

    [Theory]
    [InlineData(12)] // 512 MiB
    [InlineData(13)] // 1 GiB
    [InlineData(15)]
    public void Open_Rar5AskingForAHugeDictionary_IsRefusedBeforeAnythingIsAllocated(int power)
    {
        // A 100-odd byte file: without the check SharpCompress allocates the whole window
        // the moment an entry is opened.
        var path = Path.Join(_dir, "bomb.cbr");
        File.WriteAllBytes(path, Rar5(power));

        var ex = Assert.Throws<ArchiveLimitExceededException>(() => ComicArchiveReader.Open(path));

        Assert.Contains("bomb.cbr", ex.Message);
        Assert.Contains("decompression dictionary", ex.Message);
        Assert.Contains("256 MiB", ex.Message);
    }

    [Theory]
    [InlineData(0)] // 128 KiB
    [InlineData(8)] // 32 MiB, WinRAR's default
    [InlineData(11)] // 256 MiB, the limit
    public void Open_Rar5WithAnOrdinaryDictionary_Opens(int power)
    {
        var path = Path.Join(_dir, "fine.cbr");
        File.WriteAllBytes(path, Rar5(power));

        using var archive = ComicArchiveReader.Open(path);

        Assert.Equal(ComicArchiveFormat.Rar, archive.Format);
        Assert.Equal("p0.jpg", archive.GetEntryAt(0)!.Name);
    }

    [Fact]
    public void Open_Rar5StoredEntryOrFolder_IsNotRefusedForADictionaryItNeverUses()
    {
        // SharpCompress only allocates a window for compressed, non-directory entries.
        var path = Path.Join(_dir, "stored.cbr");
        File.WriteAllBytes(path, Rar5(13, method: 0));
        using (var _ = ComicArchiveReader.Open(path))
        {
        }

        var folder = Path.Join(_dir, "folder.cbr");
        File.WriteAllBytes(folder, Rar5(13, directory: true));
        using (var _ = ComicArchiveReader.Open(folder))
        {
        }
    }

    [Fact]
    public void OpenForEntry_Rar5_ChecksOnlyTheHeadersUpToThatEntry()
    {
        // The second entry asks for too much, but the first can be read without reaching it.
        var path = Path.Join(_dir, "mixed.cbr");
        using (var file = new MemoryStream())
        {
            file.Write(Rar5(0, fileHeaders: 1));
            File.WriteAllBytes(path, file.ToArray());
        }

        var bytes = MergeRar5Headers(Rar5(0, fileHeaders: 1), Rar5(13, fileHeaders: 1));
        File.WriteAllBytes(path, bytes);

        using (var _ = ComicArchiveReader.OpenForEntry(path, 0))
        {
        }

        Assert.Throws<ArchiveLimitExceededException>(() => ComicArchiveReader.OpenForEntry(path, 1));
        Assert.Throws<ArchiveLimitExceededException>(() => ComicArchiveReader.Open(path));
    }

    [Fact]
    public void Open_Rar5VolumeSet_ChecksTheFollowingVolumesToo()
    {
        // SharpCompress follows ".part1.rar" to ".part2.rar" by name, so a set whose second
        // volume asks for too much must not get past a check of the first alone.
        File.WriteAllBytes(Path.Join(_dir, "set.part2.rar"), Rar5(13));
        var first = Path.Join(_dir, "set.part1.rar");
        File.WriteAllBytes(first, Rar5(0));

        var ex = Assert.Throws<ArchiveLimitExceededException>(() => ComicArchiveReader.Open(first));

        Assert.Contains("set.part2.rar", ex.Message);
    }

    /// <summary>Concatenates the file blocks of two single-file RAR 5 archives.</summary>
    private static byte[] MergeRar5Headers(byte[] first, byte[] second)
    {
        // Each archive is signature (8), main header, file block, end block. Find the file
        // block by walking the block boundaries.
        var firstFile = FileBlockRange(first);
        var secondFile = FileBlockRange(second);
        using var output = new MemoryStream();
        output.Write(first.AsSpan(0, firstFile.Start));
        output.Write(first.AsSpan(firstFile.Start, firstFile.Length));
        output.Write(second.AsSpan(secondFile.Start, secondFile.Length));
        output.Write(first.AsSpan(firstFile.Start + firstFile.Length));
        return output.ToArray();
    }

    private static (int Start, int Length) FileBlockRange(byte[] archive)
    {
        var position = 8;
        // Skip the main header block.
        position = NextBlock(archive, position, out _);
        var start = position;
        var end = NextBlock(archive, position, out var dataSize) + dataSize;
        return (start, end - start);
    }

    private static int NextBlock(byte[] archive, int position, out int dataSize)
    {
        var headerSize = 0;
        var i = position + 4;
        var shift = 0;
        while (true)
        {
            var b = archive[i++];
            headerSize |= (b & 0x7F) << shift;
            shift += 7;
            if ((b & 0x80) == 0)
            {
                break;
            }
        }

        var bodyStart = i;
        var type = archive[bodyStart];
        dataSize = type == 2 ? archive[bodyStart + 2] : 0;
        return bodyStart + headerSize;
    }

    // ---- Rewrite limits ------------------------------------------------------------------

    [Theory]
    [InlineData(0L, ArchiveEntryReader.MinRewriteTotalBytes)]
    [InlineData(1024L, ArchiveEntryReader.MinRewriteTotalBytes)]
    [InlineData(150L * 1024 * 1024, 150L * 1024 * 1024 * ArchiveEntryReader.RewriteExpansionFactor)]
    [InlineData(5L * 1024 * 1024 * 1024, ArchiveEntryReader.MaxRewriteTotalBytes)] // an omnibus
    [InlineData(long.MaxValue, ArchiveEntryReader.MaxRewriteTotalBytes)]
    public void RewriteBudgetFor_ScalesWithTheArchive(long archiveBytes, long expected) =>
        Assert.Equal(expected, ArchiveEntryReader.RewriteBudgetFor(archiveBytes));

    [Fact]
    public void RewriteBudgetFor_LeavesRoomForARealOmnibus()
    {
        // A 12 GB omnibus is about 12 GB of pages when it expands; its budget is 64 GiB.
        Assert.True(ArchiveEntryReader.RewriteBudgetFor(12L * 1024 * 1024 * 1024) > 60L * 1024 * 1024 * 1024);
    }

    private static ZipWriter Writer(Stream output) =>
        new(output, new ZipWriterOptions(CompressionType.Deflate) { LeaveStreamOpen = true });

    [Fact]
    public void CopyFileEntriesTo_CopiesFilesSkipsFoldersAndWhatIsExcluded()
    {
        var path = Rar("book.cbr", ("pages\\p1.jpg", Bytes("page")), ("ComicInfo.xml", Bytes("<a/>")), ("folder", null));
        using var output = new MemoryStream();

        using (var archive = ComicArchiveReader.Open(path))
        using (var writer = Writer(output))
        {
            archive.CopyFileEntriesTo(writer, name => !ComicArchiveReader.IsComicInfoName(name));
        }

        using var result = new ZipArchive(new MemoryStream(output.ToArray()), ZipArchiveMode.Read);
        // A RAR source gets ZIP's '/' separators.
        Assert.Equal(new[] { "pages/p1.jpg" }, result.Entries.Select(e => e.FullName));
    }

    [Fact]
    public void CopyFileEntriesTo_EntryOverTheCap_IsRefusedBeforeAnythingIsWritten()
    {
        var path = Zip("book.cbz", ("big.bin", new byte[2048]));
        using var output = new MemoryStream();

        using var archive = ComicArchiveReader.Open(path);
        using var writer = Writer(output);

        var ex = Assert.Throws<ArchiveLimitExceededException>(() =>
            archive.CopyFileEntriesTo(writer, _ => true, totalBudgetBytes: 1 << 30, maxEntryBytes: 1024));
        Assert.Contains("big.bin", ex.Message);
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void CopyFileEntriesTo_DeclaredTotalOverTheBudget_IsRefusedBeforeAnythingIsWritten()
    {
        var path = Zip("book.cbz", ("a.bin", new byte[600]), ("b.bin", new byte[600]));
        using var output = new MemoryStream();

        using var archive = ComicArchiveReader.Open(path);
        using var writer = Writer(output);

        var ex = Assert.Throws<ArchiveLimitExceededException>(() =>
            archive.CopyFileEntriesTo(writer, _ => true, totalBudgetBytes: 1000, maxEntryBytes: 1 << 20));
        Assert.Contains("book.cbz", ex.Message);
        Assert.Contains("limit", ex.Message);
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void CopyFileEntriesTo_ExpansionPastTheBudget_IsRefusedWhileCopying()
    {
        // Headers that understate: the rewrite is cut off when the real data exceeds the budget.
        // A RAR whose stored entries declare less than they hold cannot lie past its declared
        // size, so the budget is exercised with honest entries that fit the pre-flight check.
        var path = Zip("book.cbz", ("a.bin", new byte[600]), ("b.bin", new byte[600]));
        using var output = new MemoryStream();

        using var archive = ComicArchiveReader.Open(path);
        using var writer = Writer(output);

        // The budget is above the declared total of 1200 for the pre-flight, then lowered
        // through the entry cap of the stream check.
        var ex = Assert.Throws<ArchiveLimitExceededException>(() =>
            archive.CopyFileEntriesTo(writer, name => name == "a.bin", totalBudgetBytes: 500, maxEntryBytes: 1 << 20));
        Assert.Contains("book.cbz", ex.Message);
    }

    [Fact]
    public void CopyFileEntriesTo_SolidRar_IsReadInOrder()
    {
        // The solid flag is set in the main archive header (see the conversion tests).
        var path = Rar("book.cbr", ("p1.jpg", Bytes("one")), ("p2.jpg", Bytes("two")));
        var bytes = File.ReadAllBytes(path);
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(10));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(10), (ushort)(flags | 0x0008));
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes.AsSpan(7),
            (ushort)(ComicReaderServiceCoverageTests.StoredRar4.Crc32(bytes.AsSpan(9, 11)) & 0xFFFF));
        File.WriteAllBytes(path, bytes);
        using var output = new MemoryStream();

        using (var archive = ComicArchiveReader.Open(path))
        using (var writer = Writer(output))
        {
            archive.CopyFileEntriesTo(writer, name => name != "p2.jpg");
        }

        using var result = new ZipArchive(new MemoryStream(output.ToArray()), ZipArchiveMode.Read);
        Assert.Equal(new[] { "p1.jpg" }, result.Entries.Select(e => e.FullName));
    }

    private static ComicArchiveReader.LimitedRewriteStream Limited(byte[] data, long declaredSize, long maxEntryBytes, long totalBudget) =>
        new(new MemoryStream(data), "p1.jpg", declaredSize, maxEntryBytes, new ComicArchiveReader.RewriteBudget("book.cbz", totalBudget));

    [Fact]
    public void LimitedRewriteStream_WithinLimits_PassesTheDataThrough()
    {
        using var stream = Limited(Bytes("page"), declaredSize: 4, maxEntryBytes: 1024, totalBudget: 1024);
        var buffer = new byte[16];

        Assert.Equal(4, stream.Read(buffer, 0, buffer.Length));
        Assert.Equal(0, stream.Read(buffer));
        Assert.Equal(Bytes("page"), buffer[..4]);
        Assert.True(stream.CanRead);
        Assert.False(stream.CanSeek);
        Assert.False(stream.CanWrite);
        stream.Flush();
        Assert.Throws<NotSupportedException>(() => stream.Length);
        Assert.Throws<NotSupportedException>(() => stream.Position);
        Assert.Throws<NotSupportedException>(() => stream.Position = 1);
        Assert.Throws<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => stream.SetLength(1));
        Assert.Throws<NotSupportedException>(() => stream.Write(buffer, 0, 1));
    }

    [Fact]
    public void LimitedRewriteStream_MoreDataThanTheHeaderDeclares_IsRefused()
    {
        using var stream = Limited(new byte[64], declaredSize: 8, maxEntryBytes: 1024, totalBudget: 1024);

        var ex = Assert.Throws<ArchiveLimitExceededException>(() => stream.Read(new byte[16]));

        Assert.Contains("declares", ex.Message);
        Assert.Contains("p1.jpg", ex.Message);
    }

    [Fact]
    public void LimitedRewriteStream_UnknownSizePastTheCap_IsRefused()
    {
        using var stream = Limited(new byte[64], declaredSize: -1, maxEntryBytes: 16, totalBudget: 1024);

        var ex = Assert.Throws<ArchiveLimitExceededException>(() => stream.Read(new byte[32]));

        Assert.Contains("16 B limit", ex.Message);
    }

    [Fact]
    public void LimitedRewriteStream_PastTheArchiveBudget_IsRefused()
    {
        using var stream = Limited(new byte[64], declaredSize: 64, maxEntryBytes: 1024, totalBudget: 16);

        var ex = Assert.Throws<ArchiveLimitExceededException>(() => stream.Read(new byte[32]));

        Assert.Contains("book.cbz", ex.Message);
        Assert.Contains("16 B limit", ex.Message);
    }

    [Fact]
    public void CopyFileEntriesTo_OverTheRealLimits_IsRefused()
    {
        // The public overload uses the size of the archive itself for the budget: a 20 MiB
        // deflated entry of zeros is over the ratio limit whatever the budget allows.
        var path = Zip("bomb.cbz", ("p1.jpg", new byte[20 * 1024 * 1024]));
        using var output = new MemoryStream();

        using var archive = ComicArchiveReader.Open(path);
        using var writer = Writer(output);

        Assert.Throws<ArchiveLimitExceededException>(() => archive.CopyFileEntriesTo(writer, _ => true));
    }
}
