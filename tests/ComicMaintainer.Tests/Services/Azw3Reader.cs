using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// A minimal KF8 (AZW3) parser used by the tests to verify the generated books
/// the same way a reader does: it walks the PalmDB records, reads the MOBI
/// header and EXTH metadata, and rebuilds the page documents from the text
/// records using the skeleton and chunk indices.
/// </summary>
internal sealed class Azw3Reader
{
    private readonly List<byte[]> _records;
    private readonly byte[] _text;
    private readonly List<SkeletonRow> _skeletons = new();
    private readonly List<ChunkRow> _chunks = new();

    private Azw3Reader(List<byte[]> records, string typeAndCreator)
    {
        _records = records;
        TypeAndCreator = typeAndCreator;

        var header = records[0];
        Compression = ReadUInt16(header, 0);
        TextLength = (int)ReadUInt32(header, 4);
        var lastTextRecord = ReadUInt16(header, 8);
        RecordSize = ReadUInt16(header, 10);
        Identifier = Encoding.ASCII.GetString(header, 16, 4);
        TextEncoding = ReadUInt32(header, 28);
        FileVersion = ReadUInt32(header, 36);
        MinimumVersion = ReadUInt32(header, 104);
        var firstResourceRecord = (int)ReadUInt32(header, 108);
        ExtraDataFlags = ReadUInt32(header, 240);
        var chunkIndex = (int)ReadUInt32(header, 248);
        var skelIndex = (int)ReadUInt32(header, 252);

        Exth = ReadExth(header, out var exthLength);

        var titleOffset = (int)ReadUInt32(header, 84);
        var titleLength = (int)ReadUInt32(header, 88);
        Assert.Equal(280 + exthLength, titleOffset);
        Title = Encoding.UTF8.GetString(header, titleOffset, titleLength);

        _text = ReadText(records, lastTextRecord);
        Assert.Equal(TextLength, _text.Length);

        var resourceCount = int.Parse(Exth[125], CultureInfo.InvariantCulture);
        Resources = records.Skip(firstResourceRecord).Take(resourceCount).ToList();

        foreach (var entry in ReadIndex(chunkIndex))
        {
            _chunks.Add(new ChunkRow(
                InsertPos: int.Parse(entry.Label, CultureInfo.InvariantCulture),
                FileNumber: entry.Values[3][0],
                SequenceNumber: entry.Values[4][0],
                StartPos: entry.Values[6][0],
                Length: entry.Values[6][1]));
        }

        foreach (var entry in ReadIndex(skelIndex))
        {
            _skeletons.Add(new SkeletonRow(
                Name: entry.Label,
                ChunkCount: entry.Values[1][0],
                StartPos: entry.Values[6][0],
                Length: entry.Values[6][1]));
        }
    }

    public string TypeAndCreator { get; }

    public string Identifier { get; }

    public int Compression { get; }

    public int RecordSize { get; }

    public int TextLength { get; }

    public uint TextEncoding { get; }

    public uint FileVersion { get; }

    public uint MinimumVersion { get; }

    public uint ExtraDataFlags { get; }

    public string Title { get; }

    public IReadOnlyDictionary<int, string> Exth { get; }

    public IReadOnlyList<byte[]> Resources { get; }

    public static Azw3Reader Read(string path)
    {
        var raw = File.ReadAllBytes(path);
        var recordCount = ReadUInt16(raw, 0x4C);

        var offsets = new List<int>(recordCount + 1);
        for (var i = 0; i < recordCount; i++)
        {
            offsets.Add((int)ReadUInt32(raw, 78 + (8 * i)));
        }

        offsets.Add(raw.Length);

        var records = new List<byte[]>(recordCount);
        for (var i = 0; i < recordCount; i++)
        {
            records.Add(raw[offsets[i]..offsets[i + 1]]);
        }

        return new Azw3Reader(records, Encoding.ASCII.GetString(raw, 0x3C, 8));
    }

    /// <summary>
    /// Rebuilds each page document by re-inserting its chunks into its skeleton,
    /// exactly as KindleUnpack (and the device) does.
    /// </summary>
    public List<string> RebuildDocuments()
    {
        var documents = new List<string>();
        var chunkIndex = 0;

        foreach (var skeleton in _skeletons)
        {
            var basePointer = skeleton.StartPos + skeleton.Length;
            var document = _text[skeleton.StartPos..basePointer].ToList();

            for (var i = 0; i < skeleton.ChunkCount; i++)
            {
                var chunk = _chunks[chunkIndex++];
                var slice = _text[basePointer..(basePointer + chunk.Length)];
                var insertPos = chunk.InsertPos - skeleton.StartPos;

                // A reader repairs the insert position when it does not fall
                // between two tags, so make sure it never has to.
                var head = Encoding.UTF8.GetString(document.Take(insertPos).ToArray());
                var tail = Encoding.UTF8.GetString(document.Skip(insertPos).ToArray());
                Assert.True(head.LastIndexOf('>') > head.LastIndexOf('<'));
                Assert.True(tail.IndexOf('>') > tail.IndexOf('<'));

                document.InsertRange(insertPos, slice);
                basePointer += chunk.Length;
            }

            documents.Add(Encoding.UTF8.GetString(document.ToArray()));
        }

        return documents;
    }

    private static byte[] ReadText(IReadOnlyList<byte[]> records, int lastTextRecord)
    {
        using var buffer = new MemoryStream();
        for (var i = 1; i <= lastTextRecord; i++)
        {
            var record = records[i];

            // Extra data flag 0b1: the last byte holds the length of the
            // trailing multibyte overlap, which is stripped along with it.
            var trailing = (record[^1] & 3) + 1;
            buffer.Write(record, 0, record.Length - trailing);
        }

        return buffer.ToArray();
    }

    private static Dictionary<int, string> ReadExth(byte[] header, out int exthLength)
    {
        Assert.Equal("EXTH", Encoding.ASCII.GetString(header, 280, 4));

        var length = (int)ReadUInt32(header, 284);
        var count = (int)ReadUInt32(header, 288);
        var offset = 292;
        var records = new Dictionary<int, string>();

        for (var i = 0; i < count; i++)
        {
            var type = (int)ReadUInt32(header, offset);
            var size = (int)ReadUInt32(header, offset + 4);
            var data = header[(offset + 8)..(offset + size)];

            records[type] = type is 125 or 131 or 201 or 202 or 203
                ? ReadUInt32(data, 0).ToString(CultureInfo.InvariantCulture)
                : Encoding.UTF8.GetString(data);

            offset += size;
        }

        Assert.Equal(length, offset - 280);

        // The body is padded with at least one byte, up to a four byte boundary.
        exthLength = length + (4 - ((length - 12) % 4));
        return records;
    }

    /// <summary>
    /// Reads one index: the header record supplies the tag definitions and the
    /// data record that follows it holds the entries.
    /// </summary>
    private List<IndexRow> ReadIndex(int headerRecord)
    {
        var header = _records[headerRecord];
        Assert.Equal("INDX", Encoding.ASCII.GetString(header, 0, 4));

        var tagxOffset = (int)ReadUInt32(header, 180);
        Assert.Equal("TAGX", Encoding.ASCII.GetString(header, tagxOffset, 4));
        var tagxLength = (int)ReadUInt32(header, tagxOffset + 4);
        Assert.Equal(1u, ReadUInt32(header, tagxOffset + 8));

        var tags = new List<(int Number, int ValuesPerEntry, int Mask)>();
        for (var offset = tagxOffset + 12; offset < tagxOffset + tagxLength; offset += 4)
        {
            if (header[offset + 3] == 1)
            {
                break;
            }

            tags.Add((header[offset], header[offset + 1], header[offset + 2]));
        }

        var data = _records[headerRecord + 1];
        Assert.Equal("INDX", Encoding.ASCII.GetString(data, 0, 4));
        var idxtOffset = (int)ReadUInt32(data, 20);
        var entryCount = (int)ReadUInt32(data, 24);
        Assert.Equal("IDXT", Encoding.ASCII.GetString(data, idxtOffset, 4));
        Assert.Equal((int)ReadUInt32(header, 36), entryCount);

        var rows = new List<IndexRow>(entryCount);
        for (var i = 0; i < entryCount; i++)
        {
            var offset = ReadUInt16(data, idxtOffset + 4 + (2 * i));
            var labelLength = data[offset];
            var label = Encoding.UTF8.GetString(data, offset + 1, labelLength);

            var position = offset + 1 + labelLength;
            var control = data[position++];

            var values = new Dictionary<int, List<int>>();
            foreach (var (number, valuesPerEntry, mask) in tags)
            {
                var masked = control & mask;
                if (masked == 0)
                {
                    continue;
                }

                var entries = masked >> BitOperations.TrailingZeroCount(mask);
                var list = new List<int>();
                for (var value = 0; value < entries * valuesPerEntry; value++)
                {
                    list.Add(ReadVariableWidth(data, ref position));
                }

                values[number] = list;
            }

            rows.Add(new IndexRow(label, values));
        }

        return rows;
    }

    /// <summary>
    /// Reads a forward variable width integer: seven bits per byte, terminated
    /// by the byte that has its high bit set.
    /// </summary>
    private static int ReadVariableWidth(byte[] buffer, ref int position)
    {
        var value = 0;
        while (true)
        {
            var current = buffer[position++];
            value = (value << 7) | (current & 0b0111_1111);

            if ((current & 0b1000_0000) != 0)
            {
                return value;
            }
        }
    }

    private static uint ReadUInt32(byte[] buffer, int offset)
        => BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(offset, 4));

    private static int ReadUInt16(byte[] buffer, int offset)
        => BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(offset, 2));

    private sealed record IndexRow(string Label, Dictionary<int, List<int>> Values);

    private sealed record SkeletonRow(string Name, int ChunkCount, int StartPos, int Length);

    private sealed record ChunkRow(int InsertPos, int FileNumber, int SequenceNumber, int StartPos, int Length);
}
