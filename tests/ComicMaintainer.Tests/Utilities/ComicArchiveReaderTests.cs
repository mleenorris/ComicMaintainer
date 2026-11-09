using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using ComicMaintainer.Core.Utilities;
using ComicMaintainer.Tests.Services;
using SharpCompress.Archives;
using SharpCompress.Archives.Rar;
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
    public void Dispose_ReleasesTheFileOfASolidRarThatWasReadInOrder()
    {
        var path = SolidBook();

        using (var archive = ComicArchiveReader.Open(path))
        {
            Assert.Equal(Bytes("two"), archive.GetEntryAt(1)!.ReadAllBytes(1024));
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

    // ---- Encrypted ZIP entries -----------------------------------------------------------

    /// <summary>A ZIP whose entries have the encrypted flag set, in both places a reader may look.</summary>
    private string EncryptedZip(string name, params (string Name, byte[] Data)[] entries)
    {
        var path = Zip(name, entries);
        var bytes = File.ReadAllBytes(path);
        for (var i = 0; i + 8 < bytes.Length; i++)
        {
            var signature = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i));
            if (signature == 0x04034b50)
            {
                bytes[i + 6] |= 1;
            }
            else if (signature == 0x02014b50)
            {
                bytes[i + 8] |= 1;
            }
        }

        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void ReadAllBytes_EncryptedZipEntry_IsRefusedWithAClearReason()
    {
        // System.IO.Compression would inflate the ciphertext as if it were the page.
        using var archive = ComicArchiveReader.Open(EncryptedZip("locked.cbz", ("p1.jpg", new byte[32])));

        var ex = Assert.Throws<ComicArchiveFormatException>(() => archive.GetEntryAt(0)!.ReadAllBytes(1024));

        Assert.Contains("p1.jpg", ex.Message);
        Assert.Contains("encrypted", ex.Message);
    }

    [Fact]
    public void ReadText_EncryptedZipEntry_IsRefusedWithAClearReason()
    {
        using var archive = ComicArchiveReader.Open(EncryptedZip("locked.cbz", ("ComicInfo.xml", Bytes("<ComicInfo/>"))));

        var ex = Assert.Throws<ComicArchiveFormatException>(() => archive.FindComicInfoEntry()!.ReadText());

        Assert.Contains("encrypted", ex.Message);
    }

    [Fact]
    public void CopyFileEntriesTo_EncryptedZipEntry_IsRefusedWithAClearReason()
    {
        using var archive = ComicArchiveReader.Open(EncryptedZip("locked.cbz", ("p1.jpg", new byte[32])));
        using var output = new MemoryStream();
        using var writer = new ZipWriter(output, new ZipWriterOptions(CompressionType.Deflate) { LeaveStreamOpen = true });

        var ex = Assert.Throws<ComicArchiveFormatException>(() => archive.CopyFileEntriesTo(writer, _ => true));

        Assert.Contains("encrypted", ex.Message);
    }

    // ---- RAR 5 dictionary --------------------------------------------------------------

    /// <summary>
    /// Writes a RAR 5 archive whose header asks for a decompression dictionary of
    /// 128 KiB &lt;&lt; <paramref name="dictionaryPower"/>, which is what makes SharpCompress
    /// allocate the unpack window. Its data does not need to be valid.
    /// </summary>
    /// <param name="dictionaryPower">Size of the dictionary, as a power of two over 128 KiB.</param>
    /// <param name="method">Compression method; 0 stores the data.</param>
    /// <param name="directory">Whether the entries are folders.</param>
    /// <param name="fileHeaders">How many file entries to write.</param>
    /// <param name="name">Name for the entries, instead of "p0.jpg", "p1.jpg", ...</param>
    /// <param name="splitAfter">Marks the entries as continuing in a next volume.</param>
    internal static byte[] Rar5(int dictionaryPower, int method = 3, bool directory = false, int fileHeaders = 1, string? name = null, bool splitAfter = false)
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
            var nameBytes = Encoding.UTF8.GetBytes(name ?? $"p{i}.jpg");
            var data = new byte[16];
            using var file = new MemoryStream();
            WriteVInt(file, 2); // file header
            WriteVInt(file, splitAfter ? 0x12UL : 0x02UL); // flags: a data area follows, continued in the next volume
            WriteVInt(file, (ulong)data.Length);
            WriteVInt(file, directory ? 1UL : 0UL); // file flags
            WriteVInt(file, 5_000_000); // unpacked size
            WriteVInt(file, 0x20);
            WriteVInt(file, ((ulong)(uint)method << 7) | ((ulong)(uint)dictionaryPower << 10));
            WriteVInt(file, 1); // host OS
            WriteVInt(file, (ulong)nameBytes.Length);
            file.Write(nameBytes);
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

    /// <summary>
    /// Writes a RAR 5 block: its CRC, the size of its header and the header itself.
    /// <paramref name="declaredSize"/> is a size to claim instead of the real one.
    /// </summary>
    private static void WriteBlock(Stream output, byte[] body, int? declaredSize = null)
    {
        using var header = new MemoryStream();
        WriteVInt(header, (ulong)(declaredSize ?? body.Length));
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

    private static byte[] Rar5Bytes(params byte[][] blocks)
    {
        using var output = new MemoryStream();
        output.Write(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00 });
        foreach (var block in blocks)
        {
            WriteBlock(output, block);
        }

        return output.ToArray();
    }

    /// <summary>The body of a RAR 5 file or service header, complete, as SharpCompress parses it.</summary>
    private static byte[] FileHeaderBody(
        ulong type,
        ulong headerFlags,
        ulong fileFlags,
        int dictionaryPower,
        int method = 3,
        bool extraArea = false,
        string name = "p0.jpg",
        ulong dataSize = 16)
    {
        using var body = new MemoryStream();
        WriteVInt(body, type);
        WriteVInt(body, headerFlags);
        if (extraArea)
        {
            WriteVInt(body, 0); // extra area size
        }

        if ((headerFlags & 0x02) != 0)
        {
            WriteVInt(body, dataSize);
        }

        WriteVInt(body, fileFlags);
        WriteVInt(body, 1000); // unpacked size
        WriteVInt(body, 0x20); // attributes
        if ((fileFlags & 0x02) != 0)
        {
            body.Write(new byte[4]); // mtime
        }

        if ((fileFlags & 0x04) != 0)
        {
            body.Write(new byte[4]); // CRC
        }

        WriteVInt(body, ((ulong)(uint)method << 7) | ((ulong)(uint)dictionaryPower << 10));
        WriteVInt(body, 1); // host OS
        var nameBytes = Encoding.UTF8.GetBytes(name);
        WriteVInt(body, (ulong)nameBytes.Length);
        body.Write(nameBytes);
        return body.ToArray();
    }

    private static byte[] Block(byte[] body, int? declaredSize = null)
    {
        using var output = new MemoryStream();
        WriteBlock(output, body, declaredSize);
        return output.ToArray();
    }

    /// <summary>A RAR 5 archive made of ready-made blocks and data areas, in order.</summary>
    private static byte[] Rar5Of(params byte[][] parts) =>
        new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00 }.Concat(parts.SelectMany(p => p)).ToArray();

    private static byte[] MainBlock(ulong code = 1, ulong? dataAreaSize = null)
    {
        using var body = new MemoryStream();
        WriteVInt(body, code);
        WriteVInt(body, dataAreaSize is null ? 0UL : 0x02UL);
        if (dataAreaSize is { } size)
        {
            WriteVInt(body, size);
        }

        WriteVInt(body, 0); // archive flags
        return Block(body.ToArray());
    }

    private static byte[] EndBlock(ulong code = 5) => Block(new byte[] { (byte)code, 0, 0 });

    /// <summary>A RAR 5 archive whose comment header declares <paramref name="declaredSize"/> bytes, with none there.</summary>
    internal static byte[] Rar5WithComment(ulong declaredSize) =>
        Rar5Of(MainBlock(), ServiceBlock("CMT", declaredSize), EndBlock());

    /// <summary>A file block and its data area.</summary>
    private static byte[] FileBlock(string name = "p0.jpg", ulong code = 2, int dictionaryPower = 0, int method = 3, ulong fileFlags = 0) =>
        Block(FileHeaderBody(code, headerFlags: 0x02, fileFlags, dictionaryPower, method, name: name)).Concat(new byte[16]).ToArray();

    /// <summary>A service block called <paramref name="name"/>, with <paramref name="data"/> after it.</summary>
    private static byte[] ServiceBlock(string name, ulong declaredSize, byte[]? data = null, ulong code = 3) =>
        Block(FileHeaderBody(code, headerFlags: 0x02, fileFlags: 0, dictionaryPower: 0, method: 0, name: name, dataSize: declaredSize))
            .Concat(data ?? Array.Empty<byte>())
            .ToArray();

    /// <summary>
    /// A file block with an extra record that makes SharpCompress read 32 bytes past the header
    /// size it declares, the CRC taking them in, which a header parsed by its size would miss.
    /// </summary>
    private static byte[] FileBlockReadingPastItsHeader()
    {
        using var body = new MemoryStream();
        WriteVInt(body, 2);
        WriteVInt(body, 0x03); // an extra area and a data area
        WriteVInt(body, 3); // the extra area's size
        WriteVInt(body, 16); // the data area's size
        WriteVInt(body, 0);
        WriteVInt(body, 1000);
        WriteVInt(body, 0x20);
        WriteVInt(body, 3UL << 7);
        WriteVInt(body, 1);
        WriteVInt(body, 6);
        body.Write(Encoding.UTF8.GetBytes("p1.jpg"));
        var declared = (int)body.Length + 3;
        WriteVInt(body, 1); // record size
        WriteVInt(body, 2); // record type: a hash
        WriteVInt(body, 0); // hash type: BLAKE2, 32 bytes read whatever the record size says
        body.Write(new byte[32]);
        return Block(body.ToArray(), declared).Concat(new byte[16]).ToArray();
    }

    /// <summary>How many entries SharpCompress itself lists before it stops, however it stops.</summary>
    private static int EntriesSharpCompressLists(byte[] archive)
    {
        using var rar = RarArchive.OpenArchive(new MemoryStream(archive));
        var count = 0;
        try
        {
            foreach (var _ in ((IArchive)rar).Entries)
            {
                count++;
            }
        }
        catch (Exception ex) when (ex is SharpCompressException or EndOfStreamException or OverflowException or ArgumentException)
        {
            // Listing stopped at a header it cannot read.
        }

        return count;
    }

    public static TheoryData<string, byte[]> Rar5HeaderLayouts => new()
    {
        { "plain", Rar5Of(MainBlock(), FileBlock("p0.jpg"), FileBlock("p1.jpg"), EndBlock()) },
        { "comment first", Rar5Of(MainBlock(), ServiceBlock("CMT", 5, new byte[5]), FileBlock("p0.jpg"), FileBlock("p1.jpg"), EndBlock()) },
        { "comment between", Rar5Of(MainBlock(), FileBlock("p0.jpg"), ServiceBlock("CMT", 5, new byte[5]), FileBlock("p1.jpg"), EndBlock()) },
        { "other service block", Rar5Of(MainBlock(), ServiceBlock("QO", 3, new byte[3]), FileBlock("p0.jpg"), EndBlock()) },
        { "RAR 4 service code", Rar5Of(MainBlock(), ServiceBlock("RR", 3, new byte[3], code: 122), FileBlock("p0.jpg"), EndBlock()) },
        { "RAR 4 main code", Rar5Of(MainBlock(code: 115), FileBlock("p0.jpg"), EndBlock()) },
        { "RAR 4 file code", Rar5Of(MainBlock(), FileBlock("p0.jpg", code: 116), FileBlock("p1.jpg"), EndBlock()) },
        { "RAR 4 end code", Rar5Of(MainBlock(), FileBlock("p0.jpg"), EndBlock(code: 123), FileBlock("p1.jpg")) },
        { "main claiming a data area", Rar5Of(MainBlock(dataAreaSize: 1UL << 40), FileBlock("p0.jpg"), EndBlock()) },
        { "main shorter than it says", Rar5Of(Block(new byte[] { 1, 0, 0 }, declaredSize: 2), FileBlock("p0.jpg"), EndBlock()) },
        { "header read past its end", Rar5Of(MainBlock(), FileBlockReadingPastItsHeader(), FileBlock("p0.jpg"), EndBlock()) },
        { "folder and stored", Rar5Of(MainBlock(), FileBlock("dir", fileFlags: 1), FileBlock("p0.jpg", method: 0), EndBlock()) },
        { "unknown block", Rar5Of(MainBlock(), FileBlock("p0.jpg"), Block(new byte[] { 9, 0, 0 }), FileBlock("p1.jpg")) },
        { "recovery block", Rar5Of(MainBlock(), FileBlock("p0.jpg"), Block(new byte[] { 120, 0, 0 }), FileBlock("p1.jpg")) },
        { "encrypted headers", Rar5Of(MainBlock(), FileBlock("p0.jpg"), Block(new byte[] { 4, 0, 0 }), FileBlock("p1.jpg")) },
        { "no end", Rar5Of(MainBlock(), FileBlock("p0.jpg")) },
    };

    [Theory]
    [MemberData(nameof(Rar5HeaderLayouts))]
    public void EnsureRarHeaders_ChecksEveryEntrySharpCompressLists(string layout, byte[] archive)
    {
        var listed = EntriesSharpCompressLists(archive);

        var checkedHeaders = ArchiveEntryReader.EnsureRarHeadersWithinLimits(new MemoryStream(archive), "x.cbr");

        Assert.True(listed == checkedHeaders, $"{layout}: SharpCompress lists {listed}, the check saw {checkedHeaders}");
    }

    [Fact]
    public void Open_Rar5MainHeaderClaimingADataArea_StillSeesTheHeaderAfterIt()
    {
        // SharpCompress does not skip the area a main header claims, so what follows is parsed.
        var path = Path.Join(_dir, "main-data.cbr");
        File.WriteAllBytes(path, Rar5Of(MainBlock(dataAreaSize: 1UL << 40), FileBlock(dictionaryPower: 13), EndBlock()));

        Assert.Throws<ArchiveLimitExceededException>(() => ComicArchiveReader.Open(path));
    }

    [Fact]
    public void Open_Rar5FileUnderARar4HeaderCode_IsCheckedLikeAnyOther()
    {
        // SharpCompress goes by the code alone, and parses this one with the RAR 5 reader.
        var path = Path.Join(_dir, "code-116.cbr");
        File.WriteAllBytes(path, Rar5Of(MainBlock(), FileBlock(code: 116, dictionaryPower: 13), EndBlock()));

        Assert.Throws<ArchiveLimitExceededException>(() => ComicArchiveReader.Open(path));
    }

    [Theory]
    [InlineData("a0.jpg")]
    [InlineData("a1.jpg")]
    [InlineData("a2.jpg")]
    [InlineData("a3.jpg")]
    public void Open_Rar5MainHeaderShorterThanItsFields_StillSeesTheHeaderAfterIt(string name)
    {
        // A size that is too small is not rewound to: the next header starts where the fields end.
        // The names vary the byte a parse going by the declared size would land on.
        var path = Path.Join(_dir, "main-short.cbr");
        File.WriteAllBytes(path, Rar5Of(Block(new byte[] { 1, 0, 0 }, declaredSize: 2), FileBlock(name, dictionaryPower: 13), EndBlock()));

        Assert.Throws<ArchiveLimitExceededException>(() => ComicArchiveReader.Open(path));
    }

    [Fact]
    public void Open_Rar5HeaderReadPastItsDeclaredEnd_StillSeesTheHeaderAfterIt()
    {
        var path = Path.Join(_dir, "overrun.cbr");
        File.WriteAllBytes(path, Rar5Of(MainBlock(), FileBlockReadingPastItsHeader(), FileBlock(dictionaryPower: 13), EndBlock()));

        Assert.Throws<ArchiveLimitExceededException>(() => ComicArchiveReader.Open(path));
    }

    [Fact]
    public void Open_Rar5HeaderSeekingBackToItself_IsRefusedInsteadOfBeingParsedForEver()
    {
        // A data size of minus the block's own length: left alone, listing never ends.
        var length = Block(FileHeaderBody(3, 0x02, 0, 0, 0, name: "QO", dataSize: ulong.MaxValue)).Length;
        var loop = Block(FileHeaderBody(3, 0x02, 0, 0, 0, name: "QO", dataSize: unchecked((ulong)-(long)length)));
        var path = Path.Join(_dir, "loop.cbr");
        File.WriteAllBytes(path, Rar5Of(MainBlock(), loop, EndBlock()));

        var ex = Assert.Throws<ArchiveLimitExceededException>(() => ComicArchiveReader.Open(path));

        Assert.Contains("loop.cbr", ex.Message);
    }

    [Theory]
    [InlineData(300L * 1024 * 1024)]
    [InlineData(1024L * 1024 * 1024)]
    [InlineData(ArchiveEntryReader.MaxRarCommentBytes + 1)]
    public void Open_Rar5DeclaringAHugeComment_IsRefusedBeforeAnythingIsAllocated(long declared)
    {
        // A 46-byte file: SharpCompress allocates what the header says the moment it lists it.
        var path = Path.Join(_dir, "comment.cbr");
        File.WriteAllBytes(path, Rar5Of(MainBlock(), ServiceBlock("CMT", (ulong)declared), EndBlock()));

        var before = GC.GetAllocatedBytesForCurrentThread();
        var ex = Assert.Throws<ArchiveLimitExceededException>(() => ComicArchiveReader.Open(path));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Contains("comment.cbr", ex.Message);
        Assert.Contains("archive comment", ex.Message);
        Assert.True(allocated < 512 * 1024, $"Allocated {allocated} bytes refusing the archive.");
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(ulong.MaxValue)] // -1 once SharpCompress has it in a long
    public void Open_Rar5CommentWithANonsenseSize_IsRefused(ulong declared)
    {
        var path = Path.Join(_dir, "comment.cbr");
        File.WriteAllBytes(path, Rar5Of(MainBlock(), ServiceBlock("CMT", declared), EndBlock()));

        var ex = Assert.Throws<ArchiveLimitExceededException>(() => ComicArchiveReader.Open(path));

        Assert.Contains("malformed archive comment", ex.Message);
    }

    [Fact]
    public void Open_Rar5WithARealComment_Opens()
    {
        var path = Path.Join(_dir, "commented.cbr");
        var comment = new byte[ArchiveEntryReader.MaxRarCommentBytes];
        File.WriteAllBytes(path, Rar5Of(MainBlock(), ServiceBlock("CMT", (ulong)comment.Length, comment), FileBlock("p0.jpg"), EndBlock()));

        using var archive = ComicArchiveReader.Open(path);

        Assert.Equal("p0.jpg", archive.GetEntryAt(0)!.Name);
    }

    [Fact]
    public void Open_Rar5EntryAfterAComment_IsCheckedToo()
    {
        var path = Path.Join(_dir, "comment-bomb.cbr");
        File.WriteAllBytes(path, Rar5Of(MainBlock(), ServiceBlock("CMT", 5, new byte[5]), FileBlock(dictionaryPower: 13), EndBlock()));

        Assert.Throws<ArchiveLimitExceededException>(() => ComicArchiveReader.Open(path));
    }

    [Fact]
    public void EnsureRarHeaders_NothingAfterTheSignature_IsFine() =>
        Assert.Equal(0, ArchiveEntryReader.EnsureRarHeadersWithinLimits(new MemoryStream(Rar5Bytes()), "x.cbr"));

    [Fact]
    public void EnsureRarHeaders_AHeaderSizeThatIsNotAValidNumber_EndsTheCheck()
    {
        // SharpCompress stops listing entries at a header like this one.
        using var output = new MemoryStream();
        output.Write(Rar5Bytes());
        output.Write(new byte[] { 1, 2, 3, 4, 0xFF, 0xFF, 0xFF, 0xFF });

        Assert.Equal(0, ArchiveEntryReader.EnsureRarHeadersWithinLimits(new MemoryStream(output.ToArray()), "x.cbr"));
    }

    [Fact]
    public void EnsureRarHeaders_HeaderWithExtraAreaTimeAndCrc_IsStillParsed()
    {
        var body = FileHeaderBody(type: 2, headerFlags: 0x03, fileFlags: 0x06, dictionaryPower: 13, extraArea: true);

        Assert.Throws<ArchiveLimitExceededException>(() =>
            ArchiveEntryReader.EnsureRarHeadersWithinLimits(new MemoryStream(Rar5Bytes(body)), "x.cbr"));
    }

    [Fact]
    public void EnsureRarHeaders_ServiceHeaderAskingForAHugeDictionary_IsNotRefused()
    {
        // SharpCompress never unpacks a service block, so no window is ever allocated for one.
        var body = FileHeaderBody(type: 3, headerFlags: 0, fileFlags: 0, dictionaryPower: 13, name: "QO");

        Assert.Equal(0, ArchiveEntryReader.EnsureRarHeadersWithinLimits(new MemoryStream(Rar5Bytes(body)), "x.cbr"));
    }

    [Theory]
    [InlineData(4UL)] // Encrypted headers follow: SharpCompress wants a password to go on.
    [InlineData(9UL)] // SharpCompress refuses a block type it does not know.
    public void EnsureRarHeaders_BlocksSharpCompressCannotPass_EndTheCheck(ulong type)
    {
        var archive = Rar5Of(Block(new byte[] { (byte)type, 0, 0 }), FileBlock(dictionaryPower: 13));

        Assert.Equal(0, EntriesSharpCompressLists(archive));
        Assert.Equal(0, ArchiveEntryReader.EnsureRarHeadersWithinLimits(new MemoryStream(archive), "x.cbr"));
    }

    [Fact]
    public void EnsureRarHeaders_TruncatedFileHeader_EndsTheCheck()
    {
        // Declares a time and a CRC that are not there.
        using var body = new MemoryStream();
        WriteVInt(body, 2);
        WriteVInt(body, 0);
        WriteVInt(body, 0x06);
        WriteVInt(body, 1000);
        WriteVInt(body, 0x20);

        Assert.Equal(0, ArchiveEntryReader.EnsureRarHeadersWithinLimits(new MemoryStream(Rar5Bytes(body.ToArray())), "x.cbr"));
    }

    [Fact]
    public void EnsureRarHeaders_ADataAreaPastAnyFile_EndsTheCheck()
    {
        var body = FileHeaderBody(2, headerFlags: 0x02, fileFlags: 0, dictionaryPower: 0, dataSize: ulong.MaxValue >> 1);

        Assert.Equal(0, ArchiveEntryReader.EnsureRarHeadersWithinLimits(new MemoryStream(Rar5Bytes(body)), "x.cbr"));
    }

    [Fact]
    public void EnsureRarHeaders_MoreHeadersThanAnArchiveCouldHold_IsRefused()
    {
        var mainHeader = new byte[] { 1, 0, 0 };
        var blocks = Enumerable.Repeat(mainHeader, (2 * ArchiveEntryReader.MaxEntries) + 17).ToArray();

        var ex = Assert.Throws<ArchiveLimitExceededException>(() =>
            ArchiveEntryReader.EnsureRarHeadersWithinLimits(new MemoryStream(Rar5Bytes(blocks)), "many.cbr"));

        Assert.Contains("many.cbr", ex.Message);
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xFF, 0x01 })] // type does not fit
    [InlineData(new byte[] { 2, 0xFF, 0xFF, 0x01 })] // header flags do not fit
    [InlineData(new byte[] { 2, 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01 })] // extra area size does not fit
    [InlineData(new byte[] { 2, 0x02, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01 })] // data size does not fit
    [InlineData(new byte[] { 2, 0, 0xFF, 0xFF, 0xFF, 0x01 })] // file flags do not fit
    public void EnsureRarHeaders_MalformedFields_EndTheCheck(byte[] body) =>
        Assert.Equal(0, ArchiveEntryReader.EnsureRarHeadersWithinLimits(new MemoryStream(Rar5Bytes(body)), "x.cbr"));

    [Fact]
    public void Open_Rar5VolumeSet_WithinTheLimits_Opens()
    {
        File.WriteAllBytes(Path.Join(_dir, "fine.part2.rar"), Rar5(8));
        var first = Path.Join(_dir, "fine.part1.rar");
        File.WriteAllBytes(first, Rar5(8));

        using var archive = ComicArchiveReader.Open(first);

        Assert.Equal(ComicArchiveFormat.Rar, archive.Format);
    }

    [Fact]
    public void ArchiveBytes_OfAVolumeSet_CountsEveryVolume()
    {
        // The rewrite budget comes from this: a set of 50 volumes is not as small as its first.
        var second = Rar5(8);
        var first = Rar5(8, fileHeaders: 3);
        File.WriteAllBytes(Path.Join(_dir, "sum.part2.rar"), second);
        var path = Path.Join(_dir, "sum.part1.rar");
        File.WriteAllBytes(path, first);

        using var archive = ComicArchiveReader.Open(path);

        Assert.Equal(first.Length + second.Length, archive.ArchiveBytes);
    }

    [Fact]
    public void ArchiveBytes_OfASingleFile_IsItsLength()
    {
        var rar = Path.Join(_dir, "single.cbr");
        File.WriteAllBytes(rar, Rar5(8));
        var zip = Zip("single.cbz", ("p1.jpg", new byte[32]));

        using var rarArchive = ComicArchiveReader.Open(rar);
        using var zipArchive = ComicArchiveReader.Open(zip);

        Assert.Equal(new FileInfo(rar).Length, rarArchive.ArchiveBytes);
        Assert.Equal(new FileInfo(zip).Length, zipArchive.ArchiveBytes);
    }

    [Fact]
    public void Open_Rar5VolumeFollowedByAnOlderVolume_OnlyChecksRar5Headers()
    {
        ComicReaderServiceCoverageTests.StoredRar4.Write(Path.Join(_dir, "mixed.part2.rar"), ("p2.jpg", Bytes("two")));
        var first = Path.Join(_dir, "mixed.part1.rar");
        File.WriteAllBytes(first, Rar5(8));

        using var archive = ComicArchiveReader.Open(first);

        Assert.Equal(ComicArchiveFormat.Rar, archive.Format);
    }

    [Fact]
    public void OpenZip_MaxEntries_LimitsAGeneratedArchive()
    {
        var path = Zip("book.epub", ("a", new byte[1]), ("b", new byte[1]), ("c", new byte[1]));

        Assert.Throws<ArchiveLimitExceededException>(() => ArchiveEntryReader.OpenZip(path, maxEntries: 2));
        using var archive = ArchiveEntryReader.OpenZip(path, maxEntries: 3);
        Assert.Equal(3, archive.Entries.Count);
    }

    [Fact]
    public void EnumerateEntries_ZipFolderWithBackslashName_IsADirectory()
    {
        var path = Path.Join(_dir, "folders.cbz");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            zip.CreateEntry("sub\\");
            using var page = zip.CreateEntry("sub\\p1.jpg").Open();
            page.Write(new byte[] { 1 });
        }

        using var archive = ComicArchiveReader.Open(path);

        Assert.True(archive.EnumerateEntries().Single(e => e.Name == "sub\\").IsDirectory);
        Assert.False(archive.EnumerateEntries().Single(e => e.Name == "sub\\p1.jpg").IsDirectory);
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

    [Fact]
    public void Open_Rar5FollowedByARawSplitPart_ReadsOnlyTheFileItWasAskedFor()
    {
        // A next part without a RAR signature is not a volume. Opened by path, SharpCompress
        // reads it as more of this file, headers included, where a header the dictionary check
        // never saw could ask for a 1 GiB window. The check has to see what gets parsed.
        var whole = Rar5(0);
        var (start, length) = FileBlockRange(whole);
        var cut = start + length - 6;
        var bomb = Rar5(13);
        var (bombStart, bombLength) = FileBlockRange(bomb);

        var first = Path.Join(_dir, "split.part1.rar");
        File.WriteAllBytes(first, whole[..cut]);
        using (var rest = new MemoryStream())
        {
            rest.Write(whole.AsSpan(cut, 6));
            rest.Write(bomb.AsSpan(bombStart, bombLength));
            File.WriteAllBytes(Path.Join(_dir, "split.part2.rar"), rest.ToArray());
        }

        using var archive = ComicArchiveReader.Open(first);

        // The file ends inside its first entry's data, and that is all that is ever read of it:
        // SharpCompress reports the seek past its end instead of going on to the next part.
        Assert.Throws<ArchiveOperationException>(() => archive.EnumerateEntries().ToList());
    }

    [Fact]
    public void Dispose_ReleasesTheFileOfARar5()
    {
        var path = Path.Join(_dir, "single.cbr");
        File.WriteAllBytes(path, Rar5(0));

        using (var archive = ComicArchiveReader.Open(path))
        {
            Assert.Equal("p0.jpg", archive.GetEntryAt(0)!.Name);
        }

        File.Delete(path);
        Assert.False(File.Exists(path));
    }

    // ---- Solid RAR ------------------------------------------------------------------------

    /// <summary>
    /// A solid RAR 5 whose only entry packs more bytes than it says it unpacks to, with a block
    /// asking for a 1 GiB dictionary in the part of its data that is never unpacked.
    /// </summary>
    private string SolidRar5WithAHeaderHiddenInItsData(string fileName)
    {
        var data = new byte[] { 1, 2, 3 }.Concat(FileBlock("hidden.jpg", dictionaryPower: 13)).ToArray();
        using var main = new MemoryStream();
        WriteVInt(main, 1);
        WriteVInt(main, 0);
        WriteVInt(main, 0x04); // archive flags: solid

        using var file = new MemoryStream();
        WriteVInt(file, 2);
        WriteVInt(file, 0x02); // a data area follows
        WriteVInt(file, (ulong)data.Length);
        WriteVInt(file, 0x04); // file flags: CRC present
        WriteVInt(file, 3); // unpacked size: fewer bytes than are packed
        WriteVInt(file, 0x20);
        var crc = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(crc, ComicReaderServiceCoverageTests.StoredRar4.Crc32(data.AsSpan(0, 3)));
        file.Write(crc);
        WriteVInt(file, 0x40); // stored, solid
        WriteVInt(file, 1);
        var name = Encoding.UTF8.GetBytes("a.jpg");
        WriteVInt(file, (ulong)name.Length);
        file.Write(name);

        var path = Path.Join(_dir, fileName);
        File.WriteAllBytes(path, Rar5Of(Block(main.ToArray()), Block(file.ToArray()), data, EndBlock()));
        return path;
    }

    [Fact]
    public void CopyFileEntriesTo_SolidEntryPackingMoreThanItUnpacks_NeverParsesItsDataAsHeaders()
    {
        // Pre-checked from the headers alone, this archive only has a.jpg. Read through a
        // streaming reader, the next header would start where the three unpacked bytes end.
        var path = SolidRar5WithAHeaderHiddenInItsData("hidden.cbr");
        using var output = new MemoryStream();

        var allocated = GC.GetAllocatedBytesForCurrentThread();
        using (var archive = ComicArchiveReader.Open(path))
        using (var writer = Writer(output))
        {
            archive.CopyFileEntriesTo(writer, _ => true);
        }

        Assert.True(GC.GetAllocatedBytesForCurrentThread() - allocated < 64L * 1024 * 1024, "Allocated a window from a hidden header.");
        using var result = new ZipArchive(new MemoryStream(output.ToArray()), ZipArchiveMode.Read);
        Assert.Equal(new[] { "a.jpg" }, result.Entries.Select(e => e.FullName));
    }

    [Fact]
    public void ReadSolidEntry_EntryPackingMoreThanItUnpacks_NeverParsesItsDataAsHeaders()
    {
        var path = SolidRar5WithAHeaderHiddenInItsData("hidden-read.cbr");

        using var archive = ComicArchiveReader.Open(path);

        Assert.Equal(new byte[] { 1, 2, 3 }, archive.GetEntryAt(0)!.ReadAllBytes(1024));
        Assert.Equal("a.jpg", Assert.Single(archive.EnumerateEntries()).Name);
    }

    /// <summary>
    /// A solid RAR 5 of stored entries. Stored data does not need the entries before it, but
    /// entries flagged solid can only be read through the archive in order, which, unlike
    /// opening one by itself, also checks its CRC: <paramref name="badCrc"/> tells them apart.
    /// </summary>
    private string StoredSolidRar5(string fileName, string? badCrc, params (string Name, byte[] Data)[] entries) =>
        StoredSolidRar5(fileName, badCrc, Rar5Checksum.Crc32, entries);

    private enum Rar5Checksum
    {
        Crc32,
        None,
        Blake2,
    }

    private string StoredSolidRar5(string fileName, string? badCrc, Rar5Checksum checksum, (string Name, byte[] Data)[] entries)
    {
        using var output = new MemoryStream();
        output.Write(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00 });
        using (var main = new MemoryStream())
        {
            WriteVInt(main, 1); // main archive header
            WriteVInt(main, 0);
            WriteVInt(main, 0x04); // archive flags: solid
            WriteBlock(output, main.ToArray());
        }

        for (var i = 0; i < entries.Length; i++)
        {
            var (entryName, data) = entries[i];
            var nameBytes = Encoding.UTF8.GetBytes(entryName);
            using var file = new MemoryStream();
            var blake2 = checksum == Rar5Checksum.Blake2;
            WriteVInt(file, 2); // file header
            WriteVInt(file, blake2 ? 3UL : 2UL); // flags: a data area follows, and an extra area
            if (blake2)
            {
                WriteVInt(file, 35); // extra area size
            }

            WriteVInt(file, (ulong)data.Length);
            WriteVInt(file, checksum == Rar5Checksum.Crc32 ? 0x04UL : 0UL); // file flags: CRC present
            WriteVInt(file, (ulong)data.Length); // unpacked size
            WriteVInt(file, 0x20);
            if (checksum == Rar5Checksum.Crc32)
            {
                var crc = new byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(crc, ComicReaderServiceCoverageTests.StoredRar4.Crc32(data) + (entryName == badCrc ? 1u : 0u));
                file.Write(crc);
            }

            WriteVInt(file, i == 0 ? 0UL : 0x40UL); // compression: stored; solid after the first
            WriteVInt(file, 1); // host OS
            WriteVInt(file, (ulong)nameBytes.Length);
            file.Write(nameBytes);
            if (blake2)
            {
                WriteVInt(file, 34); // record size
                WriteVInt(file, 2); // record type: a hash
                WriteVInt(file, 0); // hash type: BLAKE2
                file.Write(new byte[32]);
            }

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

        var path = Path.Join(_dir, fileName);
        File.WriteAllBytes(path, output.ToArray());
        return path;
    }

    private string SolidBook(string? badCrc = null) => StoredSolidRar5(
        "solid.cbr",
        badCrc,
        ("p1.jpg", Bytes("one")),
        ("p2.jpg", Bytes("two")),
        ("p3.jpg", Bytes("three")));

    [Fact]
    public void ReadAllBytes_SolidEntry_IsReadThroughTheArchiveInOrder()
    {
        // Opened by itself this entry would come back with its CRC never checked.
        using var archive = ComicArchiveReader.Open(SolidBook(badCrc: "p2.jpg"));

        var ex = Assert.Throws<ComicArchiveFormatException>(() => archive.GetEntryAt(1)!.ReadAllBytes(1024));

        Assert.Contains("p2.jpg", ex.Message);
        Assert.Contains("CRC", ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ReadAllBytes_SolidEntryWithNothingToCheckItsDataAgainst_IsReadAsIs(int checksum)
    {
        // Some archives carry a BLAKE2 hash instead of a CRC32, or nothing at all.
        var kind = checksum == 0 ? Rar5Checksum.None : Rar5Checksum.Blake2;
        var entries = new[] { ("p1.jpg", Bytes("one")), ("p2.jpg", Bytes("two")) };
        using var archive = ComicArchiveReader.Open(StoredSolidRar5($"unchecked{checksum}.cbr", null, kind, entries));

        Assert.Equal(Bytes("two"), archive.GetEntryAt(1)!.ReadAllBytes(1024));
    }

    [Fact]
    public void ReadAllBytes_SolidFirstEntry_NeedsNoEarlierEntries()
    {
        using var archive = ComicArchiveReader.Open(SolidBook(badCrc: "p1.jpg"));

        Assert.Equal(Bytes("one"), archive.GetEntryAt(0)!.ReadAllBytes(1024));
    }

    [Fact]
    public void ReadAllBytes_SolidEntries_CanBeReadInAnyOrder()
    {
        using var archive = ComicArchiveReader.Open(SolidBook());

        Assert.Equal(Bytes("two"), archive.GetEntryAt(1)!.ReadAllBytes(1024));
        Assert.Equal(Bytes("three"), archive.GetEntryAt(2)!.ReadAllBytes(1024));
        Assert.Equal(Bytes("two"), archive.GetEntryAt(1)!.ReadAllBytes(1024));
    }

    [Fact]
    public void ReadAllBytes_SolidArchiveWithFolders_CountsThemWhenReadingInOrder()
    {
        // The stored-RAR fixture sets no per-entry solid flag, so go through the internal read.
        var path = SolidRar("foldered.cbr", ("folder", null), ("p1.jpg", Bytes("one")), ("p2.jpg", Bytes("two")));
        using var archive = ComicArchiveReader.Open(path);

        Assert.Equal(Bytes("two"), archive.ReadSolidEntry(2, "p2.jpg", 3, 1024));
    }

    [Fact]
    public void ReadText_SolidEntry_IsReadInOrderToo()
    {
        var path = StoredSolidRar5("info.cbr", badCrc: null, ("p1.jpg", Bytes("one")), ("ComicInfo.xml", Bytes("<ComicInfo/>")));
        using var archive = ComicArchiveReader.Open(path);

        Assert.Equal("<ComicInfo/>", archive.FindComicInfoEntry()!.ReadText());

        var damaged = StoredSolidRar5("damaged.cbr", badCrc: "ComicInfo.xml", ("p1.jpg", Bytes("one")), ("ComicInfo.xml", Bytes("<ComicInfo/>")));
        using var damagedArchive = ComicArchiveReader.Open(damaged);

        Assert.Throws<ComicArchiveFormatException>(() => damagedArchive.FindComicInfoEntry()!.ReadText());
    }

    [Fact]
    public void ReadAllBytes_SolidEntryOverTheCap_IsRefusedBeforeAnythingIsDecompressed()
    {
        using var archive = ComicArchiveReader.Open(SolidBook());

        var ex = Assert.Throws<ArchiveLimitExceededException>(() => archive.GetEntryAt(2)!.ReadAllBytes(2));

        Assert.Contains("p3.jpg", ex.Message);
    }

    [Fact]
    public void ReadSolidEntry_APositionPastTheEnd_IsReportedAsDamage()
    {
        using var archive = ComicArchiveReader.Open(SolidBook());

        var ex = Assert.Throws<ComicArchiveFormatException>(() => archive.ReadSolidEntry(5, "p9.jpg", 1, 1024));

        Assert.Contains("p9.jpg", ex.Message);
        Assert.Equal(Bytes("one"), archive.ReadSolidEntry(0, "p1.jpg", 3, 1024));
    }

    [Fact]
    public void ReadSolidEntry_PastTheBudget_IsRefusedAndALaterReadStartsOver()
    {
        var path = StoredSolidRar5("budget.cbr", null, ("a.bin", new byte[4096]), ("b.bin", new byte[16]));
        using var archive = ComicArchiveReader.Open(path);

        var ex = Assert.Throws<ArchiveLimitExceededException>(() => archive.ReadSolidEntry(1, "b.bin", 16, 1024, totalBudgetBytes: 100));

        Assert.Contains("budget.cbr", ex.Message);
        Assert.Equal(new byte[16], archive.ReadSolidEntry(1, "b.bin", 16, 1024));
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

    /// <summary>A RAR 4 archive with the solid flag set in its main archive header.</summary>
    private string SolidRar(string name, params (string Name, byte[]? Data)[] entries)
    {
        var path = Rar(name, entries);
        var bytes = File.ReadAllBytes(path);
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(10));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(10), (ushort)(flags | 0x0008));
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes.AsSpan(7),
            (ushort)(ComicReaderServiceCoverageTests.StoredRar4.Crc32(bytes.AsSpan(9, 11)) & 0xFFFF));
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void CopyFileEntriesTo_SolidRar_EntriesLeftOutAreDecompressedUnderTheLimitsToo()
    {
        // The rest of a solid archive depends on them, so they cost as much as what is copied.
        var path = SolidRar("book.cbr", ("skip.bin", new byte[600]), ("p1.jpg", new byte[100]));
        using var output = new MemoryStream();
        using var archive = ComicArchiveReader.Open(path);
        using var writer = Writer(output);

        Assert.Throws<ArchiveLimitExceededException>(() =>
            archive.CopyFileEntriesTo(writer, name => name == "p1.jpg", totalBudgetBytes: 1024, maxEntryBytes: 500));
    }

    [Fact]
    public void CopyFileEntriesTo_SolidRar_IsReadInOrder()
    {
        var path = SolidRar("book.cbr", ("folder", null), ("p1.jpg", Bytes("one")), ("p2.jpg", Bytes("two")));
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
        new(new MemoryStream(data), "p1.jpg", declaredSize, maxEntryBytes, new ComicArchiveReader.DecompressionBudget("book.cbz", totalBudget));

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
