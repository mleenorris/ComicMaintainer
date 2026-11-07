using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
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

    /// <summary>Most entries (files and directories) an archive may list: 20,000.</summary>
    public const int MaxEntries = 20_000;

    /// <summary>
    /// Highest uncompressed-to-compressed ratio accepted for an entry larger than
    /// <see cref="CompressionRatioCheckThresholdBytes"/>. Comic page formats
    /// (JPEG, PNG, WebP, GIF) are already compressed and store at close to 1:1;
    /// a large entry that expands more than 100-fold is a decompression bomb.
    /// </summary>
    public const long MaxCompressionRatio = 100;

    /// <summary>
    /// Entries at or below 16 MiB uncompressed are not ratio-checked: whatever
    /// their ratio, the memory they can cost is already small.
    /// </summary>
    public const long CompressionRatioCheckThresholdBytes = 16L * 1024 * 1024;

    private const int CopyBufferBytes = 81920;

    // ZIP record signatures and sizes (APPNOTE.TXT 4.3.12-4.3.16).
    private const uint ZipCentralHeaderSignature = 0x02014b50;
    private const int ZipCentralHeaderLength = 46;
    private const uint ZipEndOfCentralDirectorySignature = 0x06054b50;
    private const int ZipEndOfCentralDirectoryLength = 22;
    private const uint Zip64LocatorSignature = 0x07064b50;
    private const int Zip64LocatorLength = 20;
    private const uint Zip64EndOfCentralDirectorySignature = 0x06064b50;
    private const int Zip64EndOfCentralDirectoryLength = 56;

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
            throw new ArchiveLimitExceededException(string.Create(
                CultureInfo.InvariantCulture,
                $"'{archiveName}' has more than {MaxEntries:N0} entries, which is over the limit for a comic archive."));
        }
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
    /// <see cref="ZipArchive"/> sees them. The file is opened once and shared
    /// the way <see cref="ZipFile.OpenRead"/> shares it.
    /// </remarks>
    /// <exception cref="ArchiveLimitExceededException">The archive has too many entries.</exception>
    public static ZipArchive OpenZip(string filePath)
    {
        var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096);
        try
        {
            EnsureZipEntryCountWithinLimit(stream, Path.GetFileName(filePath));
            stream.Position = 0;
            return new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Refuses the ZIP archive in <paramref name="stream"/> when its central
    /// directory holds more than <see cref="MaxEntries"/> records.
    /// </summary>
    /// <exception cref="ArchiveLimitExceededException">The archive has too many entries.</exception>
    internal static void EnsureZipEntryCountWithinLimit(Stream stream, string archiveName) =>
        EnsureEntryCountWithinLimit(CountZipEntries(stream, stopAfter: MaxEntries), archiveName);

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

        stream.Position = directoryStart.Value;
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
        var tailLength = (int)Math.Min(length, ZipEndOfCentralDirectoryLength + ushort.MaxValue);
        var tail = new byte[tailLength];
        stream.Position = length - tailLength;
        stream.ReadExactly(tail);

        for (var i = tailLength - ZipEndOfCentralDirectoryLength; i >= 0; i--)
        {
            var record = tail.AsSpan(i);
            if (BinaryPrimitives.ReadUInt32LittleEndian(record) != ZipEndOfCentralDirectorySignature)
            {
                continue;
            }

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
                $"Archive entry '{entryName}' expands from {FormatBytes(compressedLength)} to {FormatBytes(declaredLength)}, more than {MaxCompressionRatio}:1, which is refused as a likely decompression bomb."));
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
