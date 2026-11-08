using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using SharpCompress.Archives;

namespace ComicMaintainer.Core.Utilities;

/// <summary>
/// Reads comic-archive entries into memory under hard limits, so an archive
/// whose entries expand to gigabytes (a deflate bomb dropped into the library,
/// or a header that lies about its size) is refused with an
/// <see cref="ArchiveLimitExceededException"/> instead of exhausting the
/// server's memory.
/// </summary>
/// <remarks>
/// Every read is checked twice: first against the sizes the archive declares
/// (so an honest oversized entry is refused before anything is decompressed),
/// then while copying, because declared sizes can lie. Entry counts are checked
/// before the entry list is built (<see cref="OpenZip"/> for ZIP; a running
/// count for lazily enumerated RAR headers). The limits are far above anything
/// a real comic needs: page images are typically well under 20 MiB and even
/// 2,000-page omnibus editions stay an order of magnitude under the entry cap.
/// </remarks>
public static class ArchiveEntryReader
{
    /// <summary>Largest page image, uncompressed, that is read into memory: 100 MiB.</summary>
    public const long MaxPageBytes = 100L * 1024 * 1024;

    /// <summary>
    /// Largest ComicInfo.xml, uncompressed, that is read into memory: 8 MiB. Even a
    /// 2,000-page omnibus with a page list takes a few hundred kilobytes.
    /// </summary>
    public const long MaxComicInfoBytes = 8L * 1024 * 1024;

    /// <summary>Most entries (files and directories) an archive may list: 20,000.</summary>
    public const int MaxEntries = 20_000;

    /// <summary>
    /// Highest uncompressed-to-compressed ratio accepted for an entry larger than
    /// <see cref="CompressionRatioCheckThresholdBytes"/>. Comic page formats
    /// (JPEG, PNG, WebP, GIF) are already compressed and store at close to 1:1;
    /// a large entry that expands more than 100-fold is a decompression bomb.
    /// </summary>
    /// <remarks>
    /// An uncompressed raster page (a BMP) over 16 MiB that is nearly blank can
    /// also deflate past 100:1. It is refused all the same: entry names are the
    /// archive author's choice, so exempting such formats by name would let any
    /// bomb pass the check. The refusal says how to repack the page instead.
    /// </remarks>
    public const long MaxCompressionRatio = 100;

    /// <summary>
    /// Entries at or below 16 MiB uncompressed are not ratio-checked: whatever
    /// their ratio, the memory they can cost is already small.
    /// </summary>
    public const long CompressionRatioCheckThresholdBytes = 16L * 1024 * 1024;

    /// <summary>
    /// Largest decompression dictionary a RAR 5 archive may ask for: 256 MiB. WinRAR
    /// defaults to 32 MiB, and page images, which are already compressed, gain nothing from
    /// a larger one.
    /// </summary>
    /// <remarks>
    /// SharpCompress allocates the unpack window straight from the header's dictionary
    /// size, before it looks at the data: a file of a few hundred bytes can ask for 1 GiB.
    /// See <see cref="EnsureRarDictionariesWithinLimit"/>.
    /// </remarks>
    public const long MaxRarDictionaryBytes = 256L * 1024 * 1024;

    /// <summary>
    /// Largest single entry, uncompressed, that a rewrite (tagging, converting to CBZ,
    /// embedding a cover) copies: 1 GiB. Pages are read under the much smaller
    /// <see cref="MaxPageBytes"/>; this is for whatever else an archive carries.
    /// </summary>
    public const long MaxRewriteEntryBytes = 1024L * 1024 * 1024;

    /// <summary>The smallest total a rewrite is allowed to decompress: 2 GiB, however small the archive.</summary>
    public const long MinRewriteTotalBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>The most a rewrite may decompress in total, whatever the archive's size: 64 GiB.</summary>
    public const long MaxRewriteTotalBytes = 64L * 1024 * 1024 * 1024;

    /// <summary>
    /// How many times its own size an archive may expand to when it is rewritten: 20. JPEG, PNG
    /// and WebP pages hardly compress, so a real comic expands by far less, even one made of
    /// uncompressed scans; a multi-GB omnibus stays comfortably inside its budget.
    /// </summary>
    public const long RewriteExpansionFactor = 20;

    private const int CopyBufferBytes = 81920;

    // RAR 5 block types and flags (https://www.rarlab.com/technote.htm).
    private const int Rar5SignatureLength = 8;
    private const ulong Rar5BlockFile = 2;
    private const ulong Rar5BlockService = 3;
    private const ulong Rar5BlockEncryption = 4;
    private const ulong Rar5BlockEndOfArchive = 5;
    private const ulong Rar5HasExtraArea = 0x01;
    private const ulong Rar5HasDataArea = 0x02;
    private const ulong Rar5FileIsDirectory = 0x01;
    private const ulong Rar5FileHasTime = 0x02;
    private const ulong Rar5FileHasCrc = 0x04;
    private const long Rar5MinDictionaryBytes = 128 * 1024;

    // Enough for the fixed fields ahead of the file name in any RAR 5 header.
    private const int Rar5HeaderPrefixLength = 96;

    // A real archive has one header per entry and a few more; this also bounds the walk.
    private const int MaxRar5Blocks = (2 * MaxEntries) + 16;

    // ZIP record signatures and sizes (APPNOTE.TXT 4.3.12-4.3.16).
    private const uint ZipCentralHeaderSignature = 0x02014b50;
    private const int ZipCentralHeaderLength = 46;
    private const uint ZipEndOfCentralDirectorySignature = 0x06054b50;
    private const int ZipEndOfCentralDirectoryLength = 22;
    private const uint Zip64LocatorSignature = 0x07064b50;
    private const int Zip64LocatorLength = 20;
    private const uint Zip64EndOfCentralDirectorySignature = 0x06064b50;
    private const int Zip64EndOfCentralDirectoryLength = 56;

    // How much of the end of a ZIP file is searched first for the end record:
    // enough for the record and a comment of about 1,000 bytes.
    private const int ZipEndSearchFirstChunkLength = 1024;

    /// <summary>
    /// Refuses an archive that lists more than <see cref="MaxEntries"/> entries.
    /// </summary>
    /// <param name="entryCount">Entries seen so far (callers enumerating lazily may pass a running count).</param>
    /// <param name="archiveName">File name used in the error message.</param>
    /// <exception cref="ArchiveLimitExceededException">The archive has too many entries.</exception>
    public static void EnsureEntryCountWithinLimit(long entryCount, string archiveName)
    {
        if (entryCount > MaxEntries)
        {
            throw TooManyEntries(archiveName, MaxEntries);
        }
    }

    private static ArchiveLimitExceededException TooManyEntries(string archiveName, int maxEntries) =>
        new(string.Create(
            CultureInfo.InvariantCulture,
            $"'{archiveName}' has more than {maxEntries:N0} entries, which is over the limit for a comic archive."));

    /// <summary>
    /// The most a rewrite of an archive of <paramref name="archiveBytes"/> may decompress in
    /// total: <see cref="RewriteExpansionFactor"/> times its size, but at least
    /// <see cref="MinRewriteTotalBytes"/> and at most <see cref="MaxRewriteTotalBytes"/>.
    /// </summary>
    /// <remarks>
    /// Tied to the archive's size because that is what tells an omnibus from a bomb: a 5 GiB
    /// omnibus that is genuinely 5 GiB of pages is 20 times under its budget, while a few
    /// hundred megabytes that claim to hold hundreds of gigabytes are not.
    /// </remarks>
    public static long RewriteBudgetFor(long archiveBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(archiveBytes);
        var expanded = archiveBytes > MaxRewriteTotalBytes / RewriteExpansionFactor
            ? MaxRewriteTotalBytes
            : archiveBytes * RewriteExpansionFactor;
        return Math.Clamp(expanded, MinRewriteTotalBytes, MaxRewriteTotalBytes);
    }

    /// <summary>
    /// Refuses a RAR 5 archive any of whose compressed entries asks for a decompression
    /// dictionary above <see cref="MaxRarDictionaryBytes"/>, before anything is decompressed.
    /// </summary>
    /// <remarks>
    /// SharpCompress sizes its unpack window from each entry's header and allocates it when the
    /// entry is first opened (<c>Unpack.Init</c>); the entry limits cannot see that allocation,
    /// because the header has no relation to the data that follows, and SharpCompress keeps the
    /// value to itself. So the block headers are walked here the way it walks them. Stored
    /// entries and folders never allocate a window, so they are not checked. Walks at most
    /// <paramref name="maxFileHeaders"/> file headers, and the stream is left positioned
    /// wherever the walk stopped.
    /// </remarks>
    /// <param name="stream">A seekable stream over the archive.</param>
    /// <param name="archiveName">File name used in the error message.</param>
    /// <param name="maxFileHeaders">How many file headers to check, for a caller reading one entry.</param>
    /// <exception cref="ArchiveLimitExceededException">The archive asks for too large a dictionary, or has too many headers.</exception>
    internal static void EnsureRarDictionariesWithinLimit(Stream stream, string archiveName, int maxFileHeaders = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(stream);

        Span<byte> buffer = stackalloc byte[Rar5HeaderPrefixLength];
        var position = (long)Rar5SignatureLength;
        var fileHeaders = 0;
        for (var blocks = 0; ; blocks++)
        {
            if (blocks >= MaxRar5Blocks)
            {
                throw TooManyEntries(archiveName, MaxEntries);
            }

            // CRC32 (4 bytes), then the header size as a variable-length integer of 3 bytes at most.
            stream.Position = position;
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            var header = (ReadOnlySpan<byte>)buffer[..read];
            if (read < 5 || !TryReadVInt(header, 4, 3, out var headerSize, out var bodyStart))
            {
                return;
            }

            if (!TryParseRar5Block(header[bodyStart..], out var block))
            {
                return;
            }

            if (block.Type == Rar5BlockEndOfArchive || block.Type == Rar5BlockEncryption)
            {
                // Everything after an encryption header is encrypted, and reading it needs a
                // password this application does not have.
                return;
            }

            if (block.HasCompressedWindow && block.DictionaryBytes > MaxRarDictionaryBytes)
            {
                throw new ArchiveLimitExceededException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"'{archiveName}' needs a {FormatBytes(block.DictionaryBytes)} decompression dictionary, over the {FormatBytes(MaxRarDictionaryBytes)} limit. Repack it as CBZ or with a smaller dictionary."));
            }

            if (block.Type == Rar5BlockFile && ++fileHeaders >= maxFileHeaders)
            {
                return;
            }

            var headerEnd = position + bodyStart + (long)headerSize;
            if (block.DataSize > (ulong)(long.MaxValue - headerEnd))
            {
                return;
            }

            // The header size counts from the end of its own field; the data area follows it.
            position = headerEnd + (long)block.DataSize;
        }
    }

    /// <summary>The fields of a RAR 5 block header this application acts on.</summary>
    private readonly record struct Rar5Block(ulong Type, ulong DataSize, bool HasCompressedWindow, long DictionaryBytes);

    /// <summary>
    /// Parses the start of a block header (from its type field) as SharpCompress does, returning
    /// false when it is malformed, at which point SharpCompress stops listing entries too.
    /// </summary>
    private static bool TryParseRar5Block(ReadOnlySpan<byte> header, out Rar5Block block)
    {
        block = default;
        var offset = 0;
        if (!TryReadVInt(header, offset, 2, out var type, out offset)
            || !TryReadVInt(header, offset, 2, out var flags, out offset))
        {
            return false;
        }

        if ((flags & Rar5HasExtraArea) != 0 && !TryReadVInt(header, offset, 5, out _, out offset))
        {
            return false;
        }

        var dataSize = 0UL;
        if ((flags & Rar5HasDataArea) != 0 && !TryReadVInt(header, offset, 10, out dataSize, out offset))
        {
            return false;
        }

        if (type != Rar5BlockFile && type != Rar5BlockService)
        {
            block = new Rar5Block(type, dataSize, false, 0);
            return true;
        }

        // File flags, unpacked size, attributes, optional time and CRC, compression information.
        if (!TryReadVInt(header, offset, 3, out var fileFlags, out offset)
            || !TryReadVInt(header, offset, 10, out _, out offset)
            || !TryReadVInt(header, offset, 5, out _, out offset))
        {
            return false;
        }

        if ((fileFlags & Rar5FileHasTime) != 0)
        {
            offset += sizeof(uint);
        }

        if ((fileFlags & Rar5FileHasCrc) != 0)
        {
            offset += sizeof(uint);
        }

        if (offset > header.Length || !TryReadVInt(header, offset, 3, out var compression, out _))
        {
            return false;
        }

        var method = (compression >> 7) & 0x7;
        var dictionary = Rar5MinDictionaryBytes << (int)((compression >> 10) & 0xF);
        var isDirectory = (fileFlags & Rar5FileIsDirectory) != 0;
        block = new Rar5Block(type, dataSize, method != 0 && !isDirectory, dictionary);
        return true;
    }

    /// <summary>
    /// Reads a RAR 5 variable-length integer (seven bits per byte, the high bit continues),
    /// occupying at most <paramref name="maxBytes"/> bytes.
    /// </summary>
    private static bool TryReadVInt(ReadOnlySpan<byte> data, int start, int maxBytes, out ulong value, out int next)
    {
        value = 0;
        next = start;
        for (var i = 0; i < maxBytes; i++)
        {
            if (start + i >= data.Length)
            {
                return false;
            }

            var b = data[start + i];
            value |= (ulong)(b & 0x7F) << (7 * i);
            if ((b & 0x80) == 0)
            {
                next = start + i + 1;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Opens a ZIP (CBZ) archive for reading, refusing it when its central
    /// directory lists more than <see cref="MaxEntries"/> entries.
    /// </summary>
    /// <remarks>
    /// <see cref="ZipArchive"/> builds an object for every central-directory
    /// record the first time <see cref="ZipArchive.Entries"/> is read, and keeps
    /// reading records until one does not start with the record signature,
    /// whatever count the archive declares. So the records are counted here,
    /// the same way and without allocating per record, before
    /// <see cref="ZipArchive"/> sees them, unless the directory is too short to
    /// hold that many (true of every real comic, so opening one for a page turn
    /// costs a read of its last kilobyte on top of what
    /// <see cref="ZipArchive"/> reads). The file is opened once and shared the
    /// way <see cref="ZipFile.OpenRead"/> shares it.
    /// </remarks>
    /// <exception cref="ArchiveLimitExceededException">The archive has too many entries.</exception>
    public static ZipArchive OpenZip(string filePath) => OpenZip(filePath, MaxEntries);

    /// <summary>
    /// <see cref="OpenZip(string)"/> for an archive the application generated itself, which may
    /// legitimately hold more entries than a comic (one image and one page document per page of
    /// a condensed book).
    /// </summary>
    /// <exception cref="ArchiveLimitExceededException">The archive has too many entries.</exception>
    internal static ZipArchive OpenZip(string filePath, int maxEntries)
    {
        var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096);
        try
        {
            return OpenZip(stream, Path.GetFileName(filePath), maxEntries);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens the ZIP archive in <paramref name="stream"/>, which the returned archive then owns,
    /// under the entry limit. The caller disposes the stream if this throws.
    /// </summary>
    /// <exception cref="ArchiveLimitExceededException">The archive has too many entries.</exception>
    internal static ZipArchive OpenZip(Stream stream, string archiveName, int maxEntries = MaxEntries)
    {
        EnsureZipEntryCountWithinLimit(stream, archiveName, maxEntries);
        stream.Position = 0;
        return new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
    }

    /// <summary>
    /// Refuses the ZIP archive in <paramref name="stream"/> when its central
    /// directory holds more than <see cref="MaxEntries"/> records.
    /// </summary>
    /// <remarks>
    /// <see cref="ZipArchive"/> reads records back to back from the start of the
    /// directory to the end of the file at most, and every record takes at least
    /// <see cref="ZipCentralHeaderLength"/> bytes. A span too short for more than
    /// <see cref="MaxEntries"/> records therefore cannot hold too many, and is not
    /// walked: only a directory region of about 900 KB or more is counted.
    /// </remarks>
    /// <exception cref="ArchiveLimitExceededException">The archive has too many entries.</exception>
    internal static void EnsureZipEntryCountWithinLimit(Stream stream, string archiveName, int maxEntries = MaxEntries)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var directoryStart = TryLocateZipCentralDirectory(stream);
        if (directoryStart is null || directoryStart.Value < 0 || directoryStart.Value >= stream.Length)
        {
            // ZipArchive rejects the archive itself, or finds no records.
            return;
        }

        if ((stream.Length - directoryStart.Value) / ZipCentralHeaderLength <= maxEntries)
        {
            return;
        }

        if (CountZipRecords(stream, directoryStart.Value, stopAfter: maxEntries) > maxEntries)
        {
            throw TooManyEntries(archiveName, maxEntries);
        }
    }

    /// <summary>
    /// Counts the central-directory records of the ZIP archive in
    /// <paramref name="stream"/> the way <see cref="ZipArchive"/> reads them
    /// (record after record until the signature stops matching, whatever count
    /// the archive declares), but stops once the count exceeds
    /// <paramref name="stopAfter"/>, so a hostile directory costs a bounded walk.
    /// Returns 0 for a stream without a recognisable end-of-central-directory
    /// record, which <see cref="ZipArchive"/> then rejects itself.
    /// </summary>
    internal static long CountZipEntries(Stream stream, long stopAfter)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var directoryStart = TryLocateZipCentralDirectory(stream);
        if (directoryStart is null || directoryStart.Value < 0 || directoryStart.Value >= stream.Length)
        {
            return 0;
        }

        return CountZipRecords(stream, directoryStart.Value, stopAfter);
    }

    /// <summary>
    /// Walks the central-directory records from <paramref name="directoryStart"/>
    /// and counts them, stopping once the count exceeds <paramref name="stopAfter"/>.
    /// </summary>
    private static long CountZipRecords(Stream stream, long directoryStart, long stopAfter)
    {
        stream.Position = directoryStart;
        Span<byte> header = stackalloc byte[ZipCentralHeaderLength];
        long count = 0;
        while (count <= stopAfter
            && stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) == header.Length
            && BinaryPrimitives.ReadUInt32LittleEndian(header) == ZipCentralHeaderSignature)
        {
            count++;
            // Skip the variable part: file name, extra field and comment.
            var variableLength = BinaryPrimitives.ReadUInt16LittleEndian(header[28..])
                + BinaryPrimitives.ReadUInt16LittleEndian(header[30..])
                + BinaryPrimitives.ReadUInt16LittleEndian(header[32..]);
            stream.Seek(variableLength, SeekOrigin.Current);
        }

        return count;
    }

    /// <summary>
    /// Finds where the central directory starts, from the end-of-central-
    /// directory record (searched backwards from the end of the file, like
    /// <see cref="ZipArchive"/> does) or, when that record defers to it, the
    /// ZIP64 record. Returns <c>null</c> when there is no such record.
    /// </summary>
    private static long? TryLocateZipCentralDirectory(Stream stream)
    {
        var length = stream.Length;
        if (length < ZipEndOfCentralDirectoryLength)
        {
            return null;
        }

        // The record ends the file, followed only by a comment of up to 64 KiB.
        // Comics rarely carry a comment, so the last kilobyte is searched first
        // and the whole window only when the record is not in it. Either way the
        // record found is the last one in the file, as ZipArchive finds it.
        var windowLength = (int)Math.Min(length, ZipEndOfCentralDirectoryLength + ushort.MaxValue);
        var firstLength = Math.Min(windowLength, ZipEndSearchFirstChunkLength);
        Span<byte> first = stackalloc byte[ZipEndSearchFirstChunkLength];
        var directoryStart = TryLocateZipCentralDirectoryIn(stream, first[..firstLength], out var found);
        if (found || firstLength == windowLength)
        {
            return directoryStart;
        }

        var window = ArrayPool<byte>.Shared.Rent(windowLength);
        try
        {
            return TryLocateZipCentralDirectoryIn(stream, window.AsSpan(0, windowLength), out _);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(window);
        }
    }

    /// <summary>
    /// Reads the last <c>tail.Length</c> bytes of the file into
    /// <paramref name="tail"/> and searches them backwards for the
    /// end-of-central-directory record, returning where the directory starts.
    /// </summary>
    private static long? TryLocateZipCentralDirectoryIn(Stream stream, Span<byte> tail, out bool found)
    {
        found = false;
        var length = stream.Length;
        var tailLength = tail.Length;
        stream.Position = length - tailLength;
        stream.ReadExactly(tail);

        for (var i = tailLength - ZipEndOfCentralDirectoryLength; i >= 0; i--)
        {
            var record = tail[i..];
            if (BinaryPrimitives.ReadUInt32LittleEndian(record) != ZipEndOfCentralDirectorySignature)
            {
                continue;
            }

            found = true;
            var recordStart = length - tailLength + i;
            var disk = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
            var entries = BinaryPrimitives.ReadUInt16LittleEndian(record[10..]);
            var directoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(record[16..]);
            if (disk == ushort.MaxValue || entries == ushort.MaxValue || directoryOffset == uint.MaxValue)
            {
                return TryReadZip64DirectoryStart(stream, recordStart) ?? directoryOffset;
            }

            return directoryOffset;
        }

        return null;
    }

    /// <summary>
    /// Reads the central-directory offset from the ZIP64 end-of-central-directory
    /// record that the locator just before <paramref name="recordStart"/> points to.
    /// Returns <c>null</c> when either is missing, as <see cref="ZipArchive"/>
    /// then also falls back to the classic record.
    /// </summary>
    private static long? TryReadZip64DirectoryStart(Stream stream, long recordStart)
    {
        if (recordStart < Zip64LocatorLength)
        {
            return null;
        }

        Span<byte> locator = stackalloc byte[Zip64LocatorLength];
        stream.Position = recordStart - Zip64LocatorLength;
        stream.ReadExactly(locator);
        if (BinaryPrimitives.ReadUInt32LittleEndian(locator) != Zip64LocatorSignature)
        {
            return null;
        }

        var zip64RecordStart = BinaryPrimitives.ReadUInt64LittleEndian(locator[8..]);
        if (zip64RecordStart > (ulong)(stream.Length - Zip64EndOfCentralDirectoryLength))
        {
            return null;
        }

        Span<byte> zip64Record = stackalloc byte[Zip64EndOfCentralDirectoryLength];
        stream.Position = (long)zip64RecordStart;
        stream.ReadExactly(zip64Record);
        if (BinaryPrimitives.ReadUInt32LittleEndian(zip64Record) != Zip64EndOfCentralDirectorySignature)
        {
            return null;
        }

        var directoryOffset = BinaryPrimitives.ReadUInt64LittleEndian(zip64Record[48..]);
        return directoryOffset > long.MaxValue ? null : (long)directoryOffset;
    }

    /// <summary>
    /// The entries of a SharpCompress archive, enumerated lazily, refusing the
    /// archive as soon as more than <see cref="MaxEntries"/> have been listed. A
    /// caller that stops early (looking for ComicInfo.xml) reads no further.
    /// </summary>
    /// <param name="archive">The open archive.</param>
    /// <param name="archiveName">File name used in the error message.</param>
    /// <exception cref="ArchiveLimitExceededException">The archive has too many entries.</exception>
    public static IEnumerable<IArchiveEntry> EnumerateEntries(IArchive archive, string archiveName)
    {
        ArgumentNullException.ThrowIfNull(archive);
        long count = 0;
        foreach (var entry in archive.Entries)
        {
            EnsureEntryCountWithinLimit(++count, archiveName);
            yield return entry;
        }
    }

    /// <summary>
    /// Reads a text entry such as ComicInfo.xml under <see cref="MaxComicInfoBytes"/>,
    /// decoding it as UTF-8 unless a byte order mark says otherwise.
    /// </summary>
    /// <exception cref="ArchiveLimitExceededException">The entry exceeds a limit.</exception>
    public static string ReadText(IArchiveEntry entry) => DecodeText(ReadAllBytes(entry, MaxComicInfoBytes));

    /// <summary>
    /// Reads a ZIP text entry such as ComicInfo.xml under <see cref="MaxComicInfoBytes"/>,
    /// decoding it as UTF-8 unless a byte order mark says otherwise.
    /// </summary>
    /// <exception cref="ArchiveLimitExceededException">The entry exceeds a limit.</exception>
    public static string ReadText(ZipArchiveEntry entry) => DecodeText(ReadAllBytes(entry, MaxComicInfoBytes));

    internal static string DecodeText(byte[] bytes)
    {
        using var buffer = new MemoryStream(bytes, writable: false);
        using var reader = new StreamReader(buffer, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Reads a whole ZIP (CBZ) entry, refusing it when it is, or turns out to be,
    /// larger than <paramref name="maxBytes"/> or expands suspiciously far.
    /// </summary>
    /// <exception cref="ArchiveLimitExceededException">The entry exceeds a limit.</exception>
    public static byte[] ReadAllBytes(ZipArchiveEntry entry, long maxBytes)
    {
        ArgumentNullException.ThrowIfNull(entry);
        EnsureWithinLimits(entry.FullName, entry.Length, entry.CompressedLength, maxBytes);
        using var stream = entry.Open();
        return ReadBounded(stream, entry.FullName, entry.Length, maxBytes);
    }

    /// <summary>
    /// Reads a whole SharpCompress (CBR/RAR, 7z, ...) entry under the same limits
    /// as <see cref="ReadAllBytes(ZipArchiveEntry, long)"/>.
    /// </summary>
    /// <exception cref="ArchiveLimitExceededException">The entry exceeds a limit.</exception>
    public static byte[] ReadAllBytes(IArchiveEntry entry, long maxBytes)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var name = entry.Key ?? string.Empty;
        // Entries of a solid archive are compressed as one stream, so a single
        // entry's packed size says nothing about how far it expands.
        var compressedLength = entry.IsSolid ? -1 : entry.CompressedSize;
        EnsureWithinLimits(name, entry.Size, compressedLength, maxBytes);
        using var stream = entry.OpenEntryStream();
        return ReadBounded(stream, name, entry.Size, maxBytes);
    }

    /// <summary>
    /// Checks the sizes an archive declares for an entry before it is opened.
    /// A negative <paramref name="declaredLength"/> or
    /// <paramref name="compressedLength"/> means "unknown" and skips the checks
    /// that need it; <see cref="ReadBounded"/> still enforces the cap.
    /// </summary>
    /// <exception cref="ArchiveLimitExceededException">The declared size exceeds a limit.</exception>
    public static void EnsureWithinLimits(string entryName, long declaredLength, long compressedLength, long maxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        if (declaredLength > maxBytes)
        {
            throw new ArchiveLimitExceededException(string.Create(
                CultureInfo.InvariantCulture,
                $"Archive entry '{entryName}' is {FormatBytes(declaredLength)} uncompressed, over the {FormatBytes(maxBytes)} limit."));
        }

        if (declaredLength > CompressionRatioCheckThresholdBytes
            && compressedLength > 0
            && declaredLength / compressedLength > MaxCompressionRatio)
        {
            throw new ArchiveLimitExceededException(string.Create(
                CultureInfo.InvariantCulture,
                $"Archive entry '{entryName}' expands from {FormatBytes(compressedLength)} to {FormatBytes(declaredLength)}, more than {MaxCompressionRatio}:1, which is refused as a likely decompression bomb. If it is a genuine page, repack the archive with that page saved as JPEG, PNG or WebP."));
        }
    }

    /// <summary>
    /// Copies an entry stream into a single array of exactly the size read,
    /// stopping with an error as soon as the data runs past
    /// <paramref name="declaredLength"/> (when known) or <paramref name="maxBytes"/>.
    /// A stream that ends early (a truncated entry) returns what was read.
    /// </summary>
    /// <exception cref="ArchiveLimitExceededException">The stream holds more data than allowed.</exception>
    public static byte[] ReadBounded(Stream source, string entryName, long declaredLength, long maxBytes)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        if (declaredLength >= 0 && declaredLength <= maxBytes)
        {
            // The size is known, so read straight into the result: one copy of
            // the page instead of a growing MemoryStream plus ToArray.
            var buffer = new byte[declaredLength];
            var read = source.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            if (read < buffer.Length)
            {
                Array.Resize(ref buffer, read);
                return buffer;
            }

            Span<byte> probe = stackalloc byte[1];
            if (source.Read(probe) > 0)
            {
                throw new ArchiveLimitExceededException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Archive entry '{entryName}' holds more data than the {FormatBytes(declaredLength)} its header declares; the archive is corrupt or crafted."));
            }

            return buffer;
        }

        EnsureWithinLimits(entryName, declaredLength, -1, maxBytes);

        // Unknown size: copy in chunks, never buffering more than the cap.
        using var output = new MemoryStream();
        var chunk = new byte[CopyBufferBytes];
        int count;
        while ((count = source.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (output.Length + count > maxBytes)
            {
                throw new ArchiveLimitExceededException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Archive entry '{entryName}' expands past the {FormatBytes(maxBytes)} limit."));
            }

            output.Write(chunk, 0, count);
        }

        return output.ToArray();
    }

    /// <summary>Formats a byte count as B, KiB, MiB or GiB for error messages.</summary>
    internal static string FormatBytes(long bytes)
    {
        const double KiB = 1024d;
        const double MiB = KiB * 1024;
        const double GiB = MiB * 1024;
        return bytes switch
        {
            >= (long)GiB => string.Create(CultureInfo.InvariantCulture, $"{bytes / GiB:0.#} GiB"),
            >= (long)MiB => string.Create(CultureInfo.InvariantCulture, $"{bytes / MiB:0.#} MiB"),
            >= (long)KiB => string.Create(CultureInfo.InvariantCulture, $"{bytes / KiB:0.#} KiB"),
            _ => string.Create(CultureInfo.InvariantCulture, $"{bytes} B"),
        };
    }
}
