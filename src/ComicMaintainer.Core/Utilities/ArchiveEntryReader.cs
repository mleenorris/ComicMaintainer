using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Common.Rar.Headers;
using SharpCompress.IO;
using SharpCompress.Readers;

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
    /// See <see cref="EnsureRarHeadersWithinLimits"/>.
    /// </remarks>
    public const long MaxRarDictionaryBytes = 256L * 1024 * 1024;

    /// <summary>
    /// Largest archive comment a RAR 5 header may declare: 1 MiB. RAR itself writes at most
    /// 256 KiB of comment; SharpCompress allocates whatever the header says as soon as it gets
    /// there. See <see cref="EnsureRarHeadersWithinLimits"/>.
    /// </summary>
    public const long MaxRarCommentBytes = 1024L * 1024;

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

    // What SharpCompress's "128 KiB << n" comes to for the largest n: it works it out in 32 bits,
    // where 4 GiB wraps round to 0.
    private const long RarLargestDictionaryBytes = 4L * 1024 * 1024 * 1024;

    // The name of an archive comment's service header.
    private const string RarCommentHeaderName = "CMT";

    // A real archive has one header per entry and a few more; this also bounds the walk,
    // which a negative data size can send back over the same headers for ever.
    private const int MaxRarHeaders = (2 * MaxEntries) + 16;

    // The options of the archive itself, except that the check must not close the file.
    private static readonly ReaderOptions RarHeaderCheckOptions = new() { LeaveStreamOpen = true };

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
    /// Tied to the archive's size because that is what tells an omnibus from a bomb: a genuine 5 GiB
    /// omnibus expands to about 5 GiB, far under its 64 GiB budget, while a few
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
    /// Refuses a RAR 5 archive whose headers would make SharpCompress allocate more than a comic
    /// can need: a decompression dictionary above <see cref="MaxRarDictionaryBytes"/> for a
    /// compressed entry, or an archive comment above <see cref="MaxRarCommentBytes"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// SharpCompress allocates on the word of a header, whatever data follows: the unpack window
    /// when an entry is first opened (<c>Unpack.Init</c>), and a byte array for a comment as soon
    /// as listing the entries reaches its header (<c>RarVolume.GetVolumeFileParts</c>). A file of
    /// a hundred bytes can ask for gigabytes either way, and the entry limits never see it.
    /// </para>
    /// <para>
    /// The headers are read here by SharpCompress's own parser, over the same bytes, so what is
    /// checked is exactly what it is going to act on. A parser of our own could not stay in step
    /// with its quirks (block codes it accepts from RAR 4, headers it reads past their declared
    /// end, data areas it ignores), and whatever it read differently would escape the check.
    /// Stored entries and folders never allocate a window, and service headers are never
    /// unpacked, so those are not held to the dictionary limit.
    /// </para>
    /// <para>
    /// A header the parser rejects ends the check without complaint: SharpCompress cannot get
    /// past it either, so nothing after it is ever acted on, and the entries before it stay
    /// readable. Walks at most <paramref name="maxFileHeaders"/> file headers, and leaves the
    /// stream positioned wherever the walk stopped.
    /// </para>
    /// </remarks>
    /// <param name="stream">A seekable stream over the archive.</param>
    /// <param name="archiveName">File name used in the error message.</param>
    /// <param name="maxFileHeaders">How many file headers to check, for a caller reading one entry.</param>
    /// <returns>How many file headers were checked.</returns>
    /// <exception cref="ArchiveLimitExceededException">A header is over a limit, or there are too many headers.</exception>
    internal static int EnsureRarHeadersWithinLimits(Stream stream, string archiveName, int maxFileHeaders = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(stream);

        stream.Position = 0;
        var fileHeaders = 0;
        try
        {
            var headers = 0;
            foreach (var header in new RarHeaderFactory(StreamingMode.Seekable, RarHeaderCheckOptions).ReadHeaders(stream))
            {
                if (++headers > MaxRarHeaders)
                {
                    throw TooManyEntries(archiveName, MaxEntries);
                }

                if (header.HeaderType == HeaderType.File)
                {
                    EnsureRarDictionaryWithinLimit(header, archiveName);
                    if (++fileHeaders >= maxFileHeaders)
                    {
                        return fileHeaders;
                    }
                }
                else if (header.HeaderType == HeaderType.Service
                    && string.Equals(RarFileHeader.FileName(header), RarCommentHeaderName, StringComparison.Ordinal))
                {
                    StepOverRarComment(header, stream, archiveName);
                }
            }
        }
        catch (Exception ex) when (ex is SharpCompressException or EndOfStreamException or OverflowException or ArgumentException)
        {
            // See the remarks: this is where SharpCompress stops as well.
        }

        return fileHeaders;
    }

    private static void EnsureRarDictionaryWithinLimit(IRarHeader header, string archiveName)
    {
        if (RarFileHeader.IsDirectory(header) || RarFileHeader.IsStored(header))
        {
            return;
        }

        var windowBytes = RarFileHeader.WindowSize(header);
        var dictionaryBytes = windowBytes == 0 ? RarLargestDictionaryBytes : windowBytes;
        if (dictionaryBytes > MaxRarDictionaryBytes)
        {
            throw new ArchiveLimitExceededException(string.Create(
                CultureInfo.InvariantCulture,
                $"'{archiveName}' needs a {FormatBytes(dictionaryBytes)} decompression dictionary, over the {FormatBytes(MaxRarDictionaryBytes)} limit. Repack it as CBZ or with a smaller dictionary."));
        }
    }

    /// <summary>
    /// Refuses a comment header that declares too much, and moves past its data: the parser
    /// leaves that to the code listing the entries, which reads the whole comment into memory.
    /// </summary>
    private static void StepOverRarComment(IRarHeader header, Stream stream, string archiveName)
    {
        var commentBytes = RarFileHeader.CompressedSize(header);
        if (commentBytes <= 0)
        {
            throw new ArchiveLimitExceededException($"'{archiveName}' has a malformed archive comment; the archive is corrupt or crafted.");
        }

        if (commentBytes > MaxRarCommentBytes)
        {
            throw new ArchiveLimitExceededException(string.Create(
                CultureInfo.InvariantCulture,
                $"'{archiveName}' declares a {FormatBytes(commentBytes)} archive comment, over the {FormatBytes(MaxRarCommentBytes)} limit; the archive is corrupt or crafted."));
        }

        stream.Seek(commentBytes, SeekOrigin.Current);
    }

    /// <summary>
    /// The CRC32 a RAR entry's header carries, or null when there is none to check the data
    /// against: no checksum at all, a BLAKE2 hash instead, or an entry split over volumes.
    /// </summary>
    internal static uint? RarEntryCrc32(IArchiveEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return !entry.IsSplitAfter && RarFileHeader.FileCrc(RarFileHeader.Of(entry)) is { Length: sizeof(uint) } crc
            ? BitConverter.ToUInt32(crc)
            : null;
    }

    /// <summary>
    /// The fields of a parsed RAR header that SharpCompress keeps internal. The names are those
    /// of its <c>FileHeader</c>; should an upgrade rename one, opening a RAR 5 fails with a
    /// <see cref="MissingMethodException"/> instead of passing unchecked, and the tests say so.
    /// </summary>
    private static class RarFileHeader
    {
        private const string TypeName = "SharpCompress.Common.Rar.Headers.FileHeader, SharpCompress";

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_WindowSize")]
        public static extern uint WindowSize([UnsafeAccessorType(TypeName)] object header);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_IsStored")]
        public static extern bool IsStored([UnsafeAccessorType(TypeName)] object header);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_IsDirectory")]
        public static extern bool IsDirectory([UnsafeAccessorType(TypeName)] object header);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_CompressedSize")]
        public static extern long CompressedSize([UnsafeAccessorType(TypeName)] object header);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_FileName")]
        public static extern string? FileName([UnsafeAccessorType(TypeName)] object header);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_FileCrc")]
        public static extern byte[]? FileCrc([UnsafeAccessorType(TypeName)] object header);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_FileHeader")]
        [return: UnsafeAccessorType(TypeName)]
        public static extern object Of([UnsafeAccessorType("SharpCompress.Common.Rar.RarEntry, SharpCompress")] object entry);
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
    /// Reads a whole SharpCompress entry under the same limits as
    /// <see cref="ReadAllBytes(ZipArchiveEntry, long)"/>. The entry is opened on its own, so it
    /// must not be an entry of a solid RAR that depends on the entries before it; see
    /// <see cref="ComicArchiveReader"/>.
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
