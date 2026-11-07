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
/// then while copying, because declared sizes can lie. The limits are far above
/// anything a real comic needs: page images are typically well under 20 MiB and
/// even 2,000-page omnibus editions stay an order of magnitude under the entry
/// cap.
/// </remarks>
public static class ArchiveEntryReader
{
    /// <summary>Largest page image, uncompressed, that is read into memory: 128 MiB.</summary>
    public const long MaxPageBytes = 128L * 1024 * 1024;

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
