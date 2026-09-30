using System.Globalization;
using System.Text;

namespace ComicMaintainer.Core.Services.Kf8;

/// <summary>
/// One page image embedded in the book, in the order the pages are read.
/// </summary>
/// <param name="Bytes">Encoded image data, stored verbatim in its own record.</param>
/// <param name="MediaType">MIME type of <paramref name="Bytes"/> (e.g. image/jpeg).</param>
/// <param name="Width">Pixel width, used for the fixed-layout viewport.</param>
/// <param name="Height">Pixel height, used for the fixed-layout viewport.</param>
/// <param name="Title">Title of the generated page document.</param>
internal sealed record Kf8Page(byte[] Bytes, string MediaType, int Width, int Height, string Title);

/// <summary>
/// Everything the writer needs to emit one AZW3 file.
/// </summary>
internal sealed record Kf8Book(
    string Title,
    IReadOnlyList<Kf8Page> Pages,
    string? Creator = null,
    string? Publisher = null,
    string? Language = null);

/// <summary>
/// Writes a fixed-layout comic as a single-file AZW3 (Kindle Format 8) book.
/// </summary>
/// <remarks>
/// KF8 is a PalmDB database whose records hold, in order: the MOBI header, the
/// book text split into 4096 byte records, the chunk (FRAG) and skeleton (SKEL)
/// indices, the image resources, and finally the FDST/FLIS/FCIS/EOF markers.
/// Every page becomes one XHTML document that is stored split in two: the
/// "skeleton" (everything outside the &lt;body&gt; element's children) and one
/// "chunk" (the body's children). The reader rebuilds the document by inserting
/// the chunk back at the position recorded in the chunk index.
/// <para>
/// The format is undocumented; the layout implemented here follows Calibre's
/// KF8 writer, which is its de facto reference implementation.
/// </para>
/// </remarks>
internal static class Kf8ComicWriter
{
    private const int RecordSize = 0x1000;
    private const uint Null = 0xFFFFFFFF;
    private const int MobiHeaderLength = 264;
    private const int IndexHeaderLength = 192;
    private const string DocumentTail = "</body>\n</html>\n";

    private static readonly byte[] Flis =
    {
        0x46, 0x4C, 0x49, 0x53, 0x00, 0x00, 0x00, 0x08, 0x00, 0x41, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x01, 0x00, 0x03,
        0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01, 0xFF, 0xFF, 0xFF, 0xFF
    };

    private static readonly byte[] EofRecord = { 0xE9, 0x8E, 0x0D, 0x0A };

    /// <summary>
    /// Serializes <paramref name="book"/> and writes it to <paramref name="path"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The book has no pages.</exception>
    public static void Write(Kf8Book book, string path)
    {
        ArgumentNullException.ThrowIfNull(book);

        if (book.Pages.Count == 0)
        {
            throw new ArgumentException("A KF8 book needs at least one page.", nameof(book));
        }

        var records = BuildRecords(book);

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        WritePalmDatabase(stream, book.Title, records);
    }

    private static List<byte[]> BuildRecords(Kf8Book book)
    {
        // Flow 0 is the concatenation of every page document, each stored as
        // its skeleton bytes followed by its chunk bytes.
        using var flow = new MemoryStream();
        var skeletons = new List<SkeletonEntry>(book.Pages.Count);
        var chunks = new List<ChunkEntry>(book.Pages.Count);

        for (var i = 0; i < book.Pages.Count; i++)
        {
            var page = book.Pages[i];

            var bodyAid = ToBase32(i * 1_000_000);
            var containerAid = ToBase32((i * 1_000_000) + 1);
            var head = Encoding.UTF8.GetBytes(BuildDocumentHead(page, bodyAid));
            // Resource numbers are 1-based, and page i is resource i + 1.
            var body = Encoding.UTF8.GetBytes(BuildDocumentBody(page, i + 1, containerAid));
            var tail = Encoding.UTF8.GetBytes(DocumentTail);

            var skeletonStart = (int)flow.Length;
            flow.Write(head);
            flow.Write(tail);
            flow.Write(body);

            skeletons.Add(new SkeletonEntry(
                Name: string.Create(CultureInfo.InvariantCulture, $"SKEL{i:D10}"),
                ChunkCount: 1,
                StartPos: skeletonStart,
                Length: head.Length + tail.Length));

            chunks.Add(new ChunkEntry(
                // The chunk is re-inserted immediately after the ">" that ends
                // the <body> opening tag, which is exactly where the head stops.
                InsertPos: skeletonStart + head.Length,
                Selector: $"P-//*[@aid='{bodyAid}']",
                FileNumber: i,
                SequenceNumber: i,
                StartPos: 0,
                Length: body.Length));
        }

        var text = flow.ToArray();
        var records = new List<byte[]> { Array.Empty<byte>() };
        records.AddRange(CreateTextRecords(text));

        var lastTextRecord = records.Count - 1;
        var firstNonTextRecord = records.Count;

        var chunkIndex = records.Count;
        records.AddRange(BuildChunkIndex(chunks));

        var skelIndex = records.Count;
        records.AddRange(BuildSkeletonIndex(skeletons));

        var firstResourceRecord = records.Count;
        foreach (var page in book.Pages)
        {
            records.Add(page.Bytes);
        }

        var fdstRecord = records.Count;
        records.Add(BuildFdst(text.Length));

        var flisRecord = records.Count;
        records.Add(Flis);

        var fcisRecord = records.Count;
        records.Add(BuildFcis(text.Length));

        records.Add(EofRecord);

        records[0] = BuildRecord0(
            book,
            textLength: text.Length,
            lastTextRecord: lastTextRecord,
            firstNonTextRecord: firstNonTextRecord,
            chunkIndex: chunkIndex,
            skelIndex: skelIndex,
            firstResourceRecord: firstResourceRecord,
            fdstRecord: fdstRecord,
            flisRecord: flisRecord,
            fcisRecord: fcisRecord);

        return records;
    }

    private static string BuildDocumentHead(Kf8Page page, string bodyAid)
    {
        var width = page.Width.ToString(CultureInfo.InvariantCulture);
        var height = page.Height.ToString(CultureInfo.InvariantCulture);

        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
               "<html xmlns=\"http://www.w3.org/1999/xhtml\">\n" +
               "<head>\n" +
               $"<title>{Escape(page.Title)}</title>\n" +
               $"<meta name=\"viewport\" content=\"width={width}, height={height}\"/>\n" +
               "<style type=\"text/css\">html, body { margin: 0; padding: 0; }" +
               " div.page { margin: 0; padding: 0; text-align: center; }" +
               " img { margin: 0; padding: 0; }</style>\n" +
               "</head>\n" +
               $"<body aid=\"{bodyAid}\">";
    }

    private static string BuildDocumentBody(Kf8Page page, int resourceNumber, string containerAid)
    {
        var width = page.Width.ToString(CultureInfo.InvariantCulture);
        var height = page.Height.ToString(CultureInfo.InvariantCulture);
        var reference = $"kindle:embed:{ToBase32(resourceNumber, minDigits: 4)}?mime={page.MediaType}";

        return $"<div class=\"page\" aid=\"{containerAid}\">" +
               $"<img src=\"{reference}\" alt=\"{Escape(page.Title)}\" width=\"{width}\" height=\"{height}\"/>" +
               "</div>";
    }

    /// <summary>
    /// Splits the text into 4096 byte records. Every record carries a trailing
    /// entry holding the bytes that complete a UTF-8 character the 4096 byte
    /// boundary cut in half, followed by the length of that overlap; this is
    /// what bit 0b1 of the header's extra data flags announces.
    /// </summary>
    private static List<byte[]> CreateTextRecords(byte[] text)
    {
        var records = new List<byte[]>();

        for (var position = 0; position < text.Length; position += RecordSize)
        {
            var length = Math.Min(RecordSize, text.Length - position);
            var next = position + length;

            // A continuation byte (10xxxxxx) at the boundary means the last
            // character of this record started inside it and is incomplete.
            var overlap = 0;
            while (overlap < 3 && next + overlap < text.Length && (text[next + overlap] & 0b1100_0000) == 0b1000_0000)
            {
                overlap++;
            }

            var record = new byte[length + overlap + 1];
            Array.Copy(text, position, record, 0, length);
            Array.Copy(text, next, record, length, overlap);
            record[^1] = (byte)overlap;
            records.Add(record);
        }

        if (records.Count == 0)
        {
            records.Add(new byte[] { 0 });
        }

        return records;
    }

    private static byte[] BuildFdst(int textLength)
    {
        var record = new byte[20];
        WriteAscii(record, 0, "FDST");
        WriteUInt32(record, 4, 12);
        WriteUInt32(record, 8, 1);
        WriteUInt32(record, 12, 0);
        WriteUInt32(record, 16, (uint)textLength);
        return record;
    }

    private static byte[] BuildFcis(int textLength)
    {
        var record = new byte[]
        {
            0x46, 0x43, 0x49, 0x53, 0x00, 0x00, 0x00, 0x14, 0x00, 0x00, 0x00, 0x10,
            0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x28, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x28, 0x00, 0x00, 0x00, 0x08, 0x00, 0x01, 0x00, 0x01,
            0x00, 0x00, 0x00, 0x00
        };

        WriteUInt32(record, 20, (uint)textLength);
        return record;
    }

    private sealed record SkeletonEntry(string Name, int ChunkCount, int StartPos, int Length);

    private sealed record ChunkEntry(
        int InsertPos,
        string Selector,
        int FileNumber,
        int SequenceNumber,
        int StartPos,
        int Length);

    private sealed record IndexEntry(string Label, byte[] ControlBytes, IReadOnlyList<int> Values);

    private static List<byte[]> BuildSkeletonIndex(IReadOnlyList<SkeletonEntry> skeletons)
    {
        // Tag 1 is the chunk count (mask 0b11) and tag 6 the (start, length)
        // geometry of the skeleton (mask 0b1100). Both are stored twice, which
        // is what kindlegen emits and what readers expect.
        var tagx = BuildTagx(new (int Number, int ValuesPerEntry, int Mask)[]
        {
            (1, 1, 0b11),
            (6, 2, 0b1100)
        });

        var entries = new List<IndexEntry>(skeletons.Count);
        foreach (var skeleton in skeletons)
        {
            var values = new List<int>
            {
                skeleton.ChunkCount, skeleton.ChunkCount,
                skeleton.StartPos, skeleton.Length, skeleton.StartPos, skeleton.Length
            };

            // Control byte: two values for tag 1 (0b10) and two two-value
            // entries for tag 6 (0b10 shifted into the 0b1100 mask).
            entries.Add(new IndexEntry(skeleton.Name, new byte[] { 0b1010 }, values));
        }

        return BuildIndex(tagx, entries, cncxRecords: Array.Empty<byte[]>());
    }

    private static List<byte[]> BuildChunkIndex(IReadOnlyList<ChunkEntry> chunks)
    {
        var tagx = BuildTagx(new (int Number, int ValuesPerEntry, int Mask)[]
        {
            (2, 1, 0b1),
            (3, 1, 0b10),
            (4, 1, 0b100),
            (6, 2, 0b1000)
        });

        var cncx = new Cncx(chunks.Select(c => c.Selector));

        var entries = new List<IndexEntry>(chunks.Count);
        foreach (var chunk in chunks)
        {
            var values = new List<int>
            {
                cncx[chunk.Selector],
                chunk.FileNumber,
                chunk.SequenceNumber,
                chunk.StartPos,
                chunk.Length
            };

            entries.Add(new IndexEntry(
                chunk.InsertPos.ToString("D10", CultureInfo.InvariantCulture),
                new byte[] { 0b1111 },
                values));
        }

        return BuildIndex(tagx, entries, cncx.Records);
    }

    private static byte[] BuildTagx(IReadOnlyList<(int Number, int ValuesPerEntry, int Mask)> tags)
    {
        var body = new List<byte>();
        foreach (var (number, valuesPerEntry, mask) in tags)
        {
            body.Add((byte)number);
            body.Add((byte)valuesPerEntry);
            body.Add((byte)mask);
            body.Add(0);
        }

        // End of table marker.
        body.AddRange(new byte[] { 0, 0, 0, 1 });

        var tagx = new byte[12 + body.Count];
        WriteAscii(tagx, 0, "TAGX");
        WriteUInt32(tagx, 4, (uint)(12 + body.Count));
        WriteUInt32(tagx, 8, 1);
        body.CopyTo(tagx, 12);
        return tagx;
    }

    /// <summary>
    /// Builds one index: a header record describing the geometry of the data
    /// records, a single data record holding the entries, and the CNCX string
    /// records the entries point at.
    /// </summary>
    private static List<byte[]> BuildIndex(
        byte[] tagx,
        IReadOnlyList<IndexEntry> entries,
        IReadOnlyList<byte[]> cncxRecords)
    {
        using var block = new MemoryStream();
        var offsets = new List<int>(entries.Count);

        foreach (var entry in entries)
        {
            offsets.Add((int)block.Length);
            var label = Encoding.UTF8.GetBytes(entry.Label);
            block.WriteByte((byte)label.Length);
            block.Write(label);
            block.Write(entry.ControlBytes);
            foreach (var value in entry.Values)
            {
                block.Write(EncodeVariableWidth(value));
            }
        }

        var indexBlock = AlignBlock(block.ToArray());

        using var idxt = new MemoryStream();
        idxt.Write(Encoding.ASCII.GetBytes("IDXT"));
        foreach (var offset in offsets)
        {
            idxt.Write(UInt16Bytes(IndexHeaderLength + offset));
        }

        var idxtBlock = AlignBlock(idxt.ToArray());

        var dataHeader = new byte[IndexHeaderLength];
        WriteAscii(dataHeader, 0, "INDX");
        WriteUInt32(dataHeader, 4, IndexHeaderLength);
        WriteUInt32(dataHeader, 12, 1);
        WriteUInt32(dataHeader, 20, (uint)(IndexHeaderLength + indexBlock.Length));
        WriteUInt32(dataHeader, 24, (uint)entries.Count);
        for (var i = 28; i < 36; i++)
        {
            dataHeader[i] = 0xFF;
        }

        using var dataRecord = new MemoryStream();
        dataRecord.Write(dataHeader);
        dataRecord.Write(indexBlock);
        dataRecord.Write(idxtBlock);

        // The header record describes each data record by the label of its last
        // entry and its entry count, addressed through its own IDXT block.
        var lastLabel = Encoding.UTF8.GetBytes(entries.Count == 0 ? string.Empty : entries[^1].Label);
        using var geometry = new MemoryStream();
        geometry.WriteByte((byte)lastLabel.Length);
        geometry.Write(lastLabel);
        geometry.Write(UInt16Bytes(entries.Count));
        var geometryBlock = AlignBlock(geometry.ToArray());

        var alignedTagx = AlignBlock(tagx);

        using var headerIdxt = new MemoryStream();
        headerIdxt.Write(Encoding.ASCII.GetBytes("IDXT"));
        headerIdxt.Write(UInt16Bytes(IndexHeaderLength + alignedTagx.Length));
        var headerIdxtBlock = AlignBlock(headerIdxt.ToArray());

        var indexHeader = new byte[IndexHeaderLength];
        WriteAscii(indexHeader, 0, "INDX");
        WriteUInt32(indexHeader, 4, IndexHeaderLength);
        WriteUInt32(indexHeader, 16, 2);
        WriteUInt32(indexHeader, 20, (uint)(IndexHeaderLength + alignedTagx.Length + geometryBlock.Length));
        WriteUInt32(indexHeader, 24, 1);
        WriteUInt32(indexHeader, 28, 65001);
        WriteUInt32(indexHeader, 32, Null);
        WriteUInt32(indexHeader, 36, (uint)entries.Count);
        WriteUInt32(indexHeader, 52, (uint)cncxRecords.Count);
        WriteUInt32(indexHeader, 180, IndexHeaderLength);

        using var header = new MemoryStream();
        header.Write(indexHeader);
        header.Write(alignedTagx);
        header.Write(geometryBlock);
        header.Write(headerIdxtBlock);

        var records = new List<byte[]> { AlignBlock(header.ToArray()), dataRecord.ToArray() };
        records.AddRange(cncxRecords);
        return records;
    }

    /// <summary>
    /// Holds the strings an index refers to. Entries point at them by byte
    /// offset; a comic's selectors never come close to the 64K record limit, so
    /// a single record is always enough.
    /// </summary>
    private sealed class Cncx
    {
        private readonly Dictionary<string, int> _offsets = new(StringComparer.Ordinal);

        public Cncx(IEnumerable<string> strings)
        {
            using var buffer = new MemoryStream();
            foreach (var value in strings)
            {
                if (_offsets.ContainsKey(value))
                {
                    continue;
                }

                var utf8 = Encoding.UTF8.GetBytes(value);
                _offsets[value] = (int)buffer.Length;
                buffer.Write(EncodeVariableWidth(utf8.Length));
                buffer.Write(utf8);
            }

            Records = buffer.Length == 0
                ? Array.Empty<byte[]>()
                : new[] { AlignBlock(buffer.ToArray()) };
        }

        public IReadOnlyList<byte[]> Records { get; }

        public int this[string value] => _offsets[value];
    }

    private static byte[] BuildRecord0(
        Kf8Book book,
        int textLength,
        int lastTextRecord,
        int firstNonTextRecord,
        int chunkIndex,
        int skelIndex,
        int firstResourceRecord,
        int fdstRecord,
        int flisRecord,
        int fcisRecord)
    {
        var title = Encoding.UTF8.GetBytes(book.Title);
        var exth = BuildExth(book, resourceCount: book.Pages.Count);

        var header = new byte[280];

        // PalmDOC header; compression 1 stores the text records verbatim.
        WriteUInt16(header, 0, 1);
        WriteUInt32(header, 4, (uint)textLength);
        WriteUInt16(header, 8, lastTextRecord);
        WriteUInt16(header, 10, RecordSize);

        WriteAscii(header, 16, "MOBI");
        WriteUInt32(header, 20, MobiHeaderLength);
        WriteUInt32(header, 24, 2);
        WriteUInt32(header, 28, 65001);
        WriteUInt32(header, 32, (uint)Random.Shared.Next(1, int.MaxValue));
        WriteUInt32(header, 36, 8);
        WriteUInt32(header, 40, Null);
        WriteUInt32(header, 44, Null);
        for (var offset = 48; offset < 80; offset += 4)
        {
            WriteUInt32(header, offset, Null);
        }

        WriteUInt32(header, 80, (uint)firstNonTextRecord);
        WriteUInt32(header, 84, (uint)(header.Length + exth.Length));
        WriteUInt32(header, 88, (uint)title.Length);
        WriteUInt32(header, 92, GetLanguageCode(book.Language));
        WriteUInt32(header, 104, 8);
        WriteUInt32(header, 108, (uint)firstResourceRecord);
        WriteUInt32(header, 128, 0b1010000);
        WriteUInt32(header, 164, Null);
        WriteUInt32(header, 168, Null);
        WriteUInt32(header, 192, (uint)fdstRecord);
        WriteUInt32(header, 196, 1);
        WriteUInt32(header, 200, (uint)fcisRecord);
        WriteUInt32(header, 204, 1);
        WriteUInt32(header, 208, (uint)flisRecord);
        WriteUInt32(header, 212, 1);
        WriteUInt32(header, 224, Null);
        for (var offset = 232; offset < 240; offset += 4)
        {
            WriteUInt32(header, offset, Null);
        }

        WriteUInt32(header, 240, 0b1);
        WriteUInt32(header, 244, Null);
        WriteUInt32(header, 248, (uint)chunkIndex);
        WriteUInt32(header, 252, (uint)skelIndex);
        WriteUInt32(header, 256, Null);
        WriteUInt32(header, 260, Null);
        WriteUInt32(header, 264, Null);
        WriteUInt32(header, 272, Null);

        using var record = new MemoryStream();
        record.Write(header);
        record.Write(exth);
        record.Write(title);

        // Amazon's publishing pipeline expects room to add its own data here.
        record.Write(new byte[8192]);
        return record.ToArray();
    }

    private static byte[] BuildExth(Kf8Book book, int resourceCount)
    {
        var records = new List<(int Type, byte[] Data)>();

        void AddString(int type, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                records.Add((type, Encoding.UTF8.GetBytes(value)));
            }
        }

        void AddInt(int type, uint value) => records.Add((type, UInt32Bytes(value)));

        AddString(100, book.Creator);
        AddString(101, book.Publisher);
        AddString(503, book.Title);
        AddString(501, "EBOK");
        AddString(524, string.IsNullOrWhiteSpace(book.Language) ? "en" : book.Language);

        // Fixed-layout comic metadata: without it the Kindle reflows the pages
        // instead of showing one image per screen.
        AddString(122, "true");
        AddString(123, "comic");
        AddString(124, "none");
        AddString(126, string.Create(
            CultureInfo.InvariantCulture,
            $"{book.Pages[0].Width}x{book.Pages[0].Height}"));
        AddString(127, "true");
        AddString(128, "true");

        AddInt(125, (uint)resourceCount);
        AddInt(131, 0);

        // The first page image doubles as the cover. Cover offsets are relative
        // to the first resource record.
        AddInt(201, 0);
        AddInt(203, 0);
        AddString(129, $"kindle:embed:{ToBase32(1, minDigits: 4)}");

        using var body = new MemoryStream();
        foreach (var (type, data) in records)
        {
            body.Write(UInt32Bytes((uint)type));
            body.Write(UInt32Bytes((uint)(data.Length + 8)));
            body.Write(data);
        }

        using var exth = new MemoryStream();
        exth.Write(Encoding.ASCII.GetBytes("EXTH"));
        exth.Write(UInt32Bytes((uint)(body.Length + 12)));
        exth.Write(UInt32Bytes((uint)records.Count));
        exth.Write(body.ToArray());

        // Padded with at least one byte, up to a 4 byte boundary.
        exth.Write(new byte[4 - ((int)body.Length % 4)]);
        return exth.ToArray();
    }

    private static void WritePalmDatabase(Stream stream, string title, IReadOnlyList<byte[]> records)
    {
        var name = new byte[32];
        var asciiTitle = new string(title.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray());
        var nameBytes = Encoding.ASCII.GetBytes(asciiTitle);
        Array.Copy(nameBytes, name, Math.Min(nameBytes.Length, 31));
        stream.Write(name);

        var now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        stream.Write(UInt16Bytes(0));
        stream.Write(UInt16Bytes(0));
        stream.Write(UInt32Bytes(now));
        stream.Write(UInt32Bytes(now));
        stream.Write(new byte[16]);
        stream.Write(Encoding.ASCII.GetBytes("BOOKMOBI"));
        stream.Write(UInt32Bytes((uint)((2 * records.Count) - 1)));
        stream.Write(UInt32Bytes(0));
        stream.Write(UInt16Bytes(records.Count));

        // 78 bytes of database header, then one 8 byte entry per record and a
        // two byte gap before the record data itself.
        var offset = 78 + (8 * records.Count) + 2;
        for (var i = 0; i < records.Count; i++)
        {
            stream.Write(UInt32Bytes((uint)offset));
            stream.WriteByte(0);
            stream.Write(UInt32Bytes((uint)(2 * i)), 1, 3);
            offset += records[i].Length;
        }

        stream.Write(new byte[2]);

        foreach (var record in records)
        {
            stream.Write(record);
        }
    }

    /// <summary>
    /// Maps an IETF language tag onto the language code the MOBI header uses.
    /// An unknown language falls back to English, which only affects how the
    /// device labels the book.
    /// </summary>
    private static uint GetLanguageCode(string? language)
    {
        var primary = (language ?? string.Empty).Split('-')[0].ToLowerInvariant();
        return primary switch
        {
            "ar" => 1,
            "zh" => 4,
            "cs" => 5,
            "da" => 6,
            "de" => 7,
            "el" => 8,
            "es" => 10,
            "fi" => 11,
            "fr" => 12,
            "he" => 13,
            "hu" => 14,
            "it" => 16,
            "ja" => 17,
            "ko" => 18,
            "nl" => 19,
            "no" => 20,
            "pl" => 21,
            "pt" => 22,
            "ru" => 25,
            "sv" => 29,
            "th" => 30,
            "tr" => 31,
            _ => 9
        };
    }

    /// <summary>
    /// Variable width integer: seven bits per byte, big endian, with the high
    /// bit set on the first byte.
    /// </summary>
    private static byte[] EncodeVariableWidth(int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);

        var bytes = new List<byte>();
        var remaining = (uint)value;
        do
        {
            bytes.Add((byte)(remaining & 0b0111_1111));
            remaining >>= 7;
        }
        while (remaining != 0);

        bytes[0] |= 0b1000_0000;
        bytes.Reverse();
        return bytes.ToArray();
    }

    private static string ToBase32(int value, int? minDigits = null)
    {
        const string Digits = "0123456789ABCDEFGHIJKLMNOPQRSTUV";

        var digits = new List<char>();
        var remaining = value;
        while (remaining > 0)
        {
            digits.Add(Digits[remaining % 32]);
            remaining /= 32;
        }

        if (digits.Count == 0)
        {
            digits.Add('0');
        }

        while (minDigits is int min && digits.Count < min)
        {
            digits.Add('0');
        }

        digits.Reverse();
        return new string(digits.ToArray());
    }

    private static byte[] AlignBlock(byte[] raw)
    {
        var extra = raw.Length % 4;
        if (extra == 0)
        {
            return raw;
        }

        var padded = new byte[raw.Length + (4 - extra)];
        raw.CopyTo(padded, 0);
        return padded;
    }

    private static string Escape(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal);

    private static void WriteAscii(byte[] buffer, int offset, string value)
        => Encoding.ASCII.GetBytes(value).CopyTo(buffer, offset);

    private static void WriteUInt32(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static void WriteUInt16(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 8);
        buffer[offset + 1] = (byte)value;
    }

    private static byte[] UInt32Bytes(uint value)
    {
        var buffer = new byte[4];
        WriteUInt32(buffer, 0, value);
        return buffer;
    }

    private static byte[] UInt16Bytes(int value)
    {
        var buffer = new byte[2];
        WriteUInt16(buffer, 0, value);
        return buffer;
    }
}
