using System.Globalization;
using SharpCompress.Archives;
using SharpCompress.Archives.Rar;
using SharpCompress.Readers;
using SharpCompress.Writers.Zip;
using SystemZipArchive = System.IO.Compression.ZipArchive;
using SystemZipArchiveEntry = System.IO.Compression.ZipArchiveEntry;

namespace ComicMaintainer.Core.Utilities;

/// <summary>The container format a comic archive actually is, whatever its extension says.</summary>
public enum ComicArchiveFormat
{
    /// <summary>A ZIP archive, usually named .cbz or .zip.</summary>
    Zip,

    /// <summary>A RAR archive (RAR 1.5 to 5.x), usually named .cbr or .rar.</summary>
    Rar,
}

/// <summary>One image entry of an archive: its name and its position in the archive's entry list.</summary>
/// <param name="Name">The entry's name as the archive stores it.</param>
/// <param name="Position">The entry's position in <see cref="ComicArchiveReader.EnumerateEntries"/>.</param>
public readonly record struct ComicArchivePage(string Name, int Position);

/// <summary>One entry of an open comic archive. Only valid while its reader is open.</summary>
public abstract class ComicArchiveEntry
{
    private protected ComicArchiveEntry(string name, int position)
    {
        Name = name;
        Position = position;
    }

    /// <summary>The entry's name as the archive stores it (RAR names may use backslashes).</summary>
    public string Name { get; }

    /// <summary>The entry's position in <see cref="ComicArchiveReader.EnumerateEntries"/>.</summary>
    public int Position { get; }

    /// <summary>True for a folder entry.</summary>
    public abstract bool IsDirectory { get; }

    /// <summary>
    /// Reads the whole entry under the limits of <see cref="ArchiveEntryReader"/>.
    /// </summary>
    /// <exception cref="ArchiveLimitExceededException">The entry exceeds a limit.</exception>
    public abstract byte[] ReadAllBytes(long maxBytes);

    /// <summary>Reads a text entry such as ComicInfo.xml under <see cref="ArchiveEntryReader.MaxComicInfoBytes"/>.</summary>
    /// <exception cref="ArchiveLimitExceededException">The entry exceeds a limit.</exception>
    public abstract string ReadText();
}

/// <summary>
/// The one way the application opens a comic archive. It works out what the file really is from
/// its content, so a ZIP named .cbr and a RAR named .cbz open like any other, and it holds the
/// rules every feature has to agree on: which entries are pages, what order they come in, and
/// which entry is the archive's ComicInfo.xml.
/// </summary>
/// <remarks>
/// <para>
/// Only ZIP and RAR are opened, the two formats the library's extensions stand for. A file that
/// starts with a RAR signature is opened as RAR and anything else is handed to the ZIP reader,
/// which refuses what is not a ZIP; other containers (7z, tar) are refused the same way, so
/// their decompressors, whose memory use is set by the file, are never reached.
/// </para>
/// <para>
/// ZIP goes through <see cref="System.IO.Compression"/>, which only implements Store, Deflate
/// and Deflate64: none of them can be made to allocate much by a crafted header. An entry using
/// another method (LZMA, BZip2, PPMd, ...) is refused when it is read. RAR goes through
/// SharpCompress. Every read is bounded by <see cref="ArchiveEntryReader"/> (entry count,
/// declared size, ratio, RAR5 dictionary size), and a rewrite by
/// <see cref="CopyFileEntriesTo"/>.
/// </para>
/// </remarks>
public sealed class ComicArchiveReader : IDisposable
{
    // RAR 1.5-4.x and RAR 5.x file signatures.
    private static ReadOnlySpan<byte> Rar4Signature => new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00 };
    private static ReadOnlySpan<byte> Rar5Signature => new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00 };

    private const int SignatureLength = 8;
    private const string MacResourceForkFolder = "__MACOSX/";

    private static readonly string[] PageImageExtensions = { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp" };

    // SharpCompress's default leaves the file open after the archive is disposed.
    private static readonly ReaderOptions RarReaderOptions = new() { LeaveStreamOpen = false };

    private readonly string _archiveName;
    private readonly long _archiveBytes;
    private readonly SystemZipArchive? _zip;
    private readonly IArchive? _rar;
    private bool _disposed;

    private ComicArchiveReader(string archiveName, long archiveBytes, SystemZipArchive? zip, IArchive? rar)
    {
        _archiveName = archiveName;
        _archiveBytes = archiveBytes;
        _zip = zip;
        _rar = rar;
    }

    /// <summary>What the file actually is, as told by its content.</summary>
    public ComicArchiveFormat Format => _rar is null ? ComicArchiveFormat.Zip : ComicArchiveFormat.Rar;

    /// <summary>Opens an archive, sniffing its format, and checks its read limits.</summary>
    /// <exception cref="ArchiveLimitExceededException">The archive is over a read limit.</exception>
    /// <exception cref="ComicArchiveFormatException">The file is not a readable ZIP or RAR archive.</exception>
    public static ComicArchiveReader Open(string filePath) => OpenCore(filePath, int.MaxValue);

    /// <summary>
    /// Opens an archive to read the entry at <paramref name="position"/>: for a RAR only the
    /// headers up to that entry are checked, the ones the read parses anyway, so reading page 3
    /// of a long archive does not cost a walk over all of its headers.
    /// </summary>
    /// <exception cref="ArchiveLimitExceededException">The archive is over a read limit.</exception>
    /// <exception cref="ComicArchiveFormatException">The file is not a readable ZIP or RAR archive.</exception>
    public static ComicArchiveReader OpenForEntry(string filePath, int position)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        return OpenCore(filePath, position + 1);
    }

    private static ComicArchiveReader OpenCore(string filePath, int rarFileHeadersToCheck)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        var archiveName = Path.GetFileName(filePath);
        var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096);
        try
        {
            var archiveBytes = stream.Length;
            Span<byte> signature = stackalloc byte[SignatureLength];
            var read = stream.ReadAtLeast(signature, SignatureLength, throwOnEndOfStream: false);
            signature = signature[..read];

            if (signature.StartsWith(Rar4Signature) || signature.StartsWith(Rar5Signature))
            {
                if (signature.StartsWith(Rar5Signature))
                {
                    ArchiveEntryReader.EnsureRarDictionariesWithinLimit(stream, archiveName, rarFileHeadersToCheck);
                }

                return new ComicArchiveReader(archiveName, archiveBytes, zip: null, OpenRarArchive(filePath, stream));
            }

            stream.Position = 0;
            return new ComicArchiveReader(archiveName, archiveBytes, ArchiveEntryReader.OpenZip(stream, archiveName), rar: null);
        }
        catch (InvalidDataException ex)
        {
            stream.Dispose();
            throw new ComicArchiveFormatException($"'{archiveName}' is not a readable ZIP or RAR archive.", ex);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens the RAR archive in <paramref name="firstFile"/>, whose dictionaries the caller has
    /// checked, so that SharpCompress only ever parses headers that have been checked too.
    /// </summary>
    /// <remarks>
    /// Opened by path, SharpCompress looks for the parts that follow by name. When the next part
    /// starts with a RAR signature each part is a volume of its own, and those volumes are
    /// checked here. When it does not, SharpCompress reads all the parts as one continuous file,
    /// headers included, past the end of the file that was checked. Real volumes always have the
    /// signature, so that case gets only the file that was asked for: it is opened from its
    /// already checked stream, which SharpCompress cannot follow to another file.
    /// </remarks>
    private static IArchive OpenRarArchive(string filePath, FileStream firstFile)
    {
        var set = RarArchive.OpenArchive(filePath, RarReaderOptions);
        try
        {
            var volumes = ((IArchive)set).Volumes.ToList();
            if (volumes.Count > 1)
            {
                foreach (var volume in volumes.Skip(1))
                {
                    EnsureRar5VolumeWithinLimits(volume.FileName!);
                }

                firstFile.Dispose();
                return set;
            }
        }
        catch
        {
            set.Dispose();
            throw;
        }

        set.Dispose();
        firstFile.Position = 0;
        return RarArchive.OpenArchive(firstFile, RarReaderOptions);
    }

    private static void EnsureRar5VolumeWithinLimits(string volumePath)
    {
        using var volume = new FileStream(volumePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096);
        Span<byte> signature = stackalloc byte[SignatureLength];
        if (volume.ReadAtLeast(signature, SignatureLength, throwOnEndOfStream: false) == SignatureLength
            && signature.StartsWith(Rar5Signature))
        {
            ArchiveEntryReader.EnsureRarDictionariesWithinLimit(volume, Path.GetFileName(volumePath));
        }
    }

    /// <summary>
    /// The archive's entries in archive order, refusing the archive as soon as more than
    /// <see cref="ArchiveEntryReader.MaxEntries"/> have been listed. RAR headers are parsed as
    /// they are enumerated, so a caller that stops early reads no further.
    /// </summary>
    /// <exception cref="ArchiveLimitExceededException">The archive has too many entries.</exception>
    public IEnumerable<ComicArchiveEntry> EnumerateEntries()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _zip is not null ? EnumerateZipEntries(_zip) : EnumerateRarEntries();
    }

    private IEnumerable<ComicArchiveEntry> EnumerateZipEntries(SystemZipArchive zip)
    {
        var entries = zip.Entries;
        for (var position = 0; position < entries.Count; position++)
        {
            yield return new ZipEntry(entries[position], position);
        }
    }

    private IEnumerable<ComicArchiveEntry> EnumerateRarEntries()
    {
        var position = 0;
        foreach (var entry in ArchiveEntryReader.EnumerateEntries(_rar!, _archiveName))
        {
            yield return new RarEntry(entry, position++);
        }
    }

    /// <summary>
    /// The entry at <paramref name="position"/> in archive order, or <c>null</c> when the
    /// archive has fewer entries. Enumerating a RAR stops at that entry.
    /// </summary>
    public ComicArchiveEntry? GetEntryAt(int position)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_zip is not null)
        {
            var entries = _zip.Entries;
            return position < entries.Count ? new ZipEntry(entries[position], position) : null;
        }

        return EnumerateRarEntries().Skip(position).FirstOrDefault();
    }

    /// <summary>
    /// The archive's pages: every image entry (see <see cref="IsPageImageName"/>), in natural
    /// name order. Entries with equal names keep their archive order.
    /// </summary>
    /// <remarks>
    /// Reading progress stores page numbers, and <see cref="ComicMaintainer.Core.Services.ComicReaderService.PageListVersion"/>
    /// has to change whenever these rules do.
    /// </remarks>
    public IReadOnlyList<ComicArchivePage> ListPages()
    {
        var pages = new List<ComicArchivePage>();
        foreach (var entry in EnumerateEntries())
        {
            if (!entry.IsDirectory && IsPageImageName(entry.Name))
            {
                pages.Add(new ComicArchivePage(entry.Name, entry.Position));
            }
        }

        // OrderBy is stable, so entries with equal names keep their archive order.
        return pages.OrderBy(p => p.Name, new NaturalStringComparer()).ToList();
    }

    /// <summary>
    /// The archive's ComicInfo.xml entry: one in the archive's root if there is one, otherwise
    /// the first in natural name order among those in folders, so the choice does not depend on
    /// the order the entries happen to be stored in. Returns <c>null</c> when it has none.
    /// </summary>
    /// <exception cref="ArchiveLimitExceededException">The archive has too many entries.</exception>
    public ComicArchiveEntry? FindComicInfoEntry()
    {
        var comparer = new NaturalStringComparer();
        ComicArchiveEntry? nested = null;
        foreach (var entry in EnumerateEntries())
        {
            if (entry.IsDirectory || !IsComicInfoName(entry.Name))
            {
                continue;
            }

            if (IsRootComicInfoName(entry.Name))
            {
                return entry;
            }

            if (nested is null || comparer.Compare(entry.Name, nested.Name) < 0)
            {
                nested = entry;
            }
        }

        return nested;
    }

    /// <summary>
    /// Copies the file entries whose name <paramref name="include"/> accepts into
    /// <paramref name="writer"/>, streaming them one at a time, under the rewrite limits of
    /// <see cref="ArchiveEntryReader"/>: each entry's declared size and ratio, its real size
    /// while it is copied, and a budget for the whole archive.
    /// </summary>
    /// <remarks>
    /// A solid RAR is read from the start, in order. Names get ZIP's '/' separators.
    /// </remarks>
    /// <exception cref="ArchiveLimitExceededException">The archive is over a rewrite limit.</exception>
    public void CopyFileEntriesTo(ZipWriter writer, Func<string, bool> include) =>
        CopyFileEntriesTo(writer, include, ArchiveEntryReader.RewriteBudgetFor(_archiveBytes), ArchiveEntryReader.MaxRewriteEntryBytes);

    /// <summary>
    /// Copies entries under explicit limits, so tests can exercise them with small files.
    /// See <see cref="CopyFileEntriesTo(ZipWriter, Func{string, bool})"/>.
    /// </summary>
    internal void CopyFileEntriesTo(ZipWriter writer, Func<string, bool> include, long totalBudgetBytes, long maxEntryBytes)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(include);
        ObjectDisposedException.ThrowIf(_disposed, this);

        // A solid archive is decompressed from the start, the entries left out included.
        var solid = _rar is { IsSolid: true };

        // Refused from the headers alone when they already give the archive away, before a
        // byte is written. The streams below catch headers that understate their sizes.
        var declaredTotal = 0L;
        foreach (var entry in EnumerateEntries())
        {
            if (entry.IsDirectory || (!solid && !include(entry.Name)))
            {
                continue;
            }

            var sizes = ((ICopyableEntry)entry).DeclaredSizes;
            ArchiveEntryReader.EnsureWithinLimits(entry.Name, sizes.Uncompressed, sizes.Compressed, maxEntryBytes);
            if (sizes.Uncompressed > 0)
            {
                declaredTotal = long.CreateSaturating((Int128)declaredTotal + sizes.Uncompressed);
            }
        }

        EnsureWithinTotalBudget(declaredTotal, totalBudgetBytes);

        var budget = new RewriteBudget(_archiveName, totalBudgetBytes);
        if (solid)
        {
            using var reader = _rar!.ExtractAllEntries();
            while (reader.MoveToNextEntry())
            {
                var entry = reader.Entry;
                var name = entry.Key ?? string.Empty;
                if (entry.IsDirectory)
                {
                    continue;
                }

                if (include(name))
                {
                    CopyEntry(writer, name, entry.Size, entry.LastModifiedTime, reader.OpenEntryStream, budget, maxEntryBytes);
                }
                else
                {
                    DiscardEntry(name, entry.Size, reader.OpenEntryStream, budget, maxEntryBytes);
                }
            }

            return;
        }

        foreach (var entry in EnumerateEntries())
        {
            if (!entry.IsDirectory && include(entry.Name))
            {
                var copyable = (ICopyableEntry)entry;
                CopyEntry(writer, entry.Name, copyable.DeclaredSizes.Uncompressed, copyable.LastModified, copyable.OpenStream, budget, maxEntryBytes);
            }
        }
    }

    private void EnsureWithinTotalBudget(long declaredTotal, long totalBudgetBytes)
    {
        if (declaredTotal > totalBudgetBytes)
        {
            throw new ArchiveLimitExceededException(string.Create(
                CultureInfo.InvariantCulture,
                $"Rewriting '{_archiveName}' would expand it to {ArchiveEntryReader.FormatBytes(declaredTotal)}, over the {ArchiveEntryReader.FormatBytes(totalBudgetBytes)} limit for this archive."));
        }
    }

    private void CopyEntry(
        ZipWriter writer,
        string name,
        long declaredSize,
        DateTime? lastModified,
        Func<Stream> open,
        RewriteBudget budget,
        long maxEntryBytes)
    {
        var targetName = Format == ComicArchiveFormat.Zip ? name : name.Replace('\\', '/');
        using var source = open();
        using var bounded = new LimitedRewriteStream(source, name, declaredSize, maxEntryBytes, budget);
        writer.Write(targetName, bounded, lastModified);
    }

    /// <summary>
    /// Decompresses a solid archive's entry that is left out of the copy, which the entries after
    /// it depend on, under the same limits as one that is copied.
    /// </summary>
    private static void DiscardEntry(string name, long declaredSize, Func<Stream> open, RewriteBudget budget, long maxEntryBytes)
    {
        using var source = open();
        using var bounded = new LimitedRewriteStream(source, name, declaredSize, maxEntryBytes, budget);
        bounded.CopyTo(Stream.Null);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _zip?.Dispose();
        _rar?.Dispose();
    }

    /// <summary>True when <paramref name="name"/> is an image the reader shows as a page.</summary>
    public static bool IsPageImageName(string name) =>
        PageImageExtensions.Contains(Path.GetExtension(name).ToLowerInvariant());

    /// <summary>True for an entry in the <c>__MACOSX/</c> folder macOS adds to archives.</summary>
    public static bool IsMacResourceForkName(string name) =>
        name.Replace('\\', '/').StartsWith(MacResourceForkFolder, StringComparison.OrdinalIgnoreCase);

    /// <summary>True for a file called ComicInfo.xml in the archive's root or any folder.</summary>
    public static bool IsComicInfoName(string name) =>
        !IsMacResourceForkName(name)
        && string.Equals(GetEntryFileName(name), ComicInfoXml.EntryName, StringComparison.OrdinalIgnoreCase);

    /// <summary>True for a ComicInfo.xml entry in the archive's root.</summary>
    public static bool IsRootComicInfoName(string name) =>
        string.Equals(name, ComicInfoXml.EntryName, StringComparison.OrdinalIgnoreCase);

    private static string GetEntryFileName(string name)
    {
        var separator = name.LastIndexOfAny(new[] { '/', '\\' });
        return separator < 0 ? name : name[(separator + 1)..];
    }

    private interface ICopyableEntry
    {
        (long Uncompressed, long Compressed) DeclaredSizes { get; }

        DateTime? LastModified { get; }

        Stream OpenStream();
    }

    private sealed class ZipEntry : ComicArchiveEntry, ICopyableEntry
    {
        private readonly SystemZipArchiveEntry _entry;

        public ZipEntry(SystemZipArchiveEntry entry, int position)
            : base(entry.FullName, position)
        {
            _entry = entry;
        }

        // The same test ZipArchive's callers have always used, plus the zero-length
        // "name\" folders some Windows tools write.
        public override bool IsDirectory =>
            Name.EndsWith('/') || (_entry.Length == 0 && Name.EndsWith('\\'));

        public (long Uncompressed, long Compressed) DeclaredSizes => (_entry.Length, _entry.CompressedLength);

        // Local, as the DOS time in the entry is; the ZIP writer treats any other kind as UTC
        // and would shift the timestamp.
        public DateTime? LastModified => _entry.LastWriteTime.LocalDateTime;

        public override byte[] ReadAllBytes(long maxBytes) => ArchiveEntryReader.ReadAllBytes(_entry, maxBytes);

        public override string ReadText() => ArchiveEntryReader.ReadText(_entry);

        public Stream OpenStream() => _entry.Open();
    }

    private sealed class RarEntry : ComicArchiveEntry, ICopyableEntry
    {
        private readonly IArchiveEntry _entry;

        public RarEntry(IArchiveEntry entry, int position)
            : base(entry.Key ?? string.Empty, position)
        {
            _entry = entry;
        }

        public override bool IsDirectory => _entry.IsDirectory;

        // Entries of a solid archive are compressed as one stream, so a single
        // entry's packed size says nothing about how far it expands.
        public (long Uncompressed, long Compressed) DeclaredSizes =>
            (_entry.Size, _entry.IsSolid ? -1 : _entry.CompressedSize);

        public DateTime? LastModified => _entry.LastModifiedTime;

        public override byte[] ReadAllBytes(long maxBytes) => ArchiveEntryReader.ReadAllBytes(_entry, maxBytes);

        public override string ReadText() => ArchiveEntryReader.ReadText(_entry);

        public Stream OpenStream() => _entry.OpenEntryStream();
    }

    /// <summary>What a rewrite of one archive may still decompress, across all of its entries.</summary>
    internal sealed class RewriteBudget
    {
        private readonly string _archiveName;
        private readonly long _limitBytes;
        private long _usedBytes;

        public RewriteBudget(string archiveName, long limitBytes)
        {
            _archiveName = archiveName;
            _limitBytes = limitBytes;
        }

        public void Consume(int count)
        {
            _usedBytes += count;
            if (_usedBytes > _limitBytes)
            {
                throw new ArchiveLimitExceededException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Rewriting '{_archiveName}' expands past the {ArchiveEntryReader.FormatBytes(_limitBytes)} limit for this archive."));
            }
        }
    }

    /// <summary>
    /// Passes an entry's data to the writer, stopping with an error as soon as it runs past the
    /// size its header declared, the per-entry cap, or the archive's budget. Sizes in headers
    /// can lie, and what is decompressed here goes to disk.
    /// </summary>
    internal sealed class LimitedRewriteStream : Stream
    {
        private readonly Stream _inner;
        private readonly string _entryName;
        private readonly long _declaredSize;
        private readonly long _maxEntryBytes;
        private readonly RewriteBudget _budget;
        private long _read;

        public LimitedRewriteStream(Stream inner, string entryName, long declaredSize, long maxEntryBytes, RewriteBudget budget)
        {
            _inner = inner;
            _entryName = entryName;
            _declaredSize = declaredSize;
            _maxEntryBytes = maxEntryBytes;
            _budget = budget;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var count = _inner.Read(buffer);
            if (count > 0)
            {
                _read += count;
                if (_declaredSize >= 0 && _read > _declaredSize)
                {
                    throw new ArchiveLimitExceededException(string.Create(
                        CultureInfo.InvariantCulture,
                        $"Archive entry '{_entryName}' holds more data than the {ArchiveEntryReader.FormatBytes(_declaredSize)} its header declares; the archive is corrupt or crafted."));
                }

                if (_read > _maxEntryBytes)
                {
                    throw new ArchiveLimitExceededException(string.Create(
                        CultureInfo.InvariantCulture,
                        $"Archive entry '{_entryName}' expands past the {ArchiveEntryReader.FormatBytes(_maxEntryBytes)} limit."));
                }

                _budget.Consume(count);
            }

            return count;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
