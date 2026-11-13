using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Win32.SafeHandles;
using SharpCompress.Archives;
using SharpCompress.Archives.Rar;
using SharpCompress.Crypto;
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

/// <summary>
/// Where a CBZ page lies in its archive file, as the central directory says, so the page can be
/// opened again without reading the whole directory: the offset of the entry's local header, its
/// packed and unpacked sizes and its CRC32. Only valid for the version of the file it was read from;
/// <see cref="ComicArchiveReader.TryOpenZipPage"/> checks the local header against it before using it.
/// </summary>
internal readonly record struct ZipPageLocator(long LocalHeaderOffset, long CompressedLength, long Length, uint Crc32);

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

    /// <summary>
    /// True for an entry of a solid RAR that depends on the entries before it, which can only be
    /// decompressed by a pass over them (see <see cref="ComicArchiveReader"/>).
    /// </summary>
    internal virtual bool NeedsSolidPass => false;

    /// <summary>
    /// <see cref="ReadAllBytes"/> into a pooled buffer, for a page that is sent and dropped,
    /// checking <paramref name="cancellationToken"/> while it decompresses.
    /// </summary>
    /// <exception cref="ArchiveLimitExceededException">The entry exceeds a limit.</exception>
    /// <exception cref="OperationCanceledException">The read was cancelled.</exception>
    internal abstract RentedBufferStream ReadPooled(long maxBytes, CancellationToken cancellationToken);
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
/// declared size, ratio, and for RAR 5 the dictionary and comment its headers ask for), and a
/// rewrite by <see cref="CopyFileEntriesTo"/>.
/// </para>
/// <para>
/// The entries of a solid RAR can only be decompressed in archive order: opened on its own,
/// such an entry comes back wrong (RAR 5) or fails (RAR 4). They are read in order instead,
/// from the entry listing the header check covered, under the rewrite limits; see
/// <see cref="ComicArchiveEntry.ReadAllBytes"/>. They are also checked against the CRC32 in
/// their headers, because one damaged entry garbles every entry decompressed after it.
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

    // A ZIP local file header: signature, and the offsets of its fields (APPNOTE.TXT 4.3.7).
    private const uint ZipLocalHeaderSignature = 0x04034b50;
    private const int ZipLocalHeaderLength = 30;

    // The data descriptor that follows an entry whose local header leaves its CRC and sizes to it
    // (general purpose flag bit 3), with its optional signature (APPNOTE.TXT 4.3.9).
    private const uint ZipDataDescriptorSignature = 0x08074b50;
    private const ushort ZipFlagEncrypted = 1;
    private const ushort ZipFlagDataDescriptor = 1 << 3;
    private const ushort ZipMethodStored = 0;
    private const ushort ZipMethodDeflated = 8;

    private readonly string _filePath;
    private readonly string _archiveName;
    private readonly long _archiveBytes;
    private readonly int _rarFileHeadersToCheck;
    private readonly bool _isMultiVolume;
    private readonly SystemZipArchive? _zip;
    private readonly FileStream? _zipFile;
    private readonly IArchive? _rar;
    private SolidRarCursor? _solidCursor;
    private bool _disposed;

    private ComicArchiveReader(string filePath, long archiveBytes, int rarFileHeadersToCheck, SystemZipArchive? zip, FileStream? zipFile, IArchive? rar, bool isMultiVolume = false)
    {
        _filePath = filePath;
        _archiveName = Path.GetFileName(filePath);
        _archiveBytes = archiveBytes;
        _rarFileHeadersToCheck = rarFileHeadersToCheck;
        _isMultiVolume = isMultiVolume;
        _zip = zip;
        _zipFile = zipFile;
        _rar = rar;
    }

    /// <summary>What the file actually is, as told by its content.</summary>
    public ComicArchiveFormat Format => _rar is null ? ComicArchiveFormat.Zip : ComicArchiveFormat.Rar;

    /// <summary>How big the archive is on disk, every volume of a set included.</summary>
    internal long ArchiveBytes => _archiveBytes;

    /// <summary>True for a RAR opened as a set of volumes, its first file followed by others.</summary>
    internal bool IsMultiVolume => _isMultiVolume;

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
    public static ComicArchiveReader OpenForEntry(string filePath, int position) => OpenForEntry(filePath, position, singleVolume: false);

    /// <summary>
    /// <see cref="OpenForEntry(string, int)"/>, told whether the archive was a single file when it was
    /// last listed. A single file is opened as one without looking for volumes that follow it (an
    /// extra open of the file by SharpCompress): that can only leave out a volume that appeared
    /// since, never read a file whose headers were not checked. A ZIP ignores it.
    /// </summary>
    /// <exception cref="ArchiveLimitExceededException">The archive is over a read limit.</exception>
    /// <exception cref="ComicArchiveFormatException">The file is not a readable ZIP or RAR archive.</exception>
    internal static ComicArchiveReader OpenForEntry(string filePath, int position, bool singleVolume)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        return OpenCore(filePath, position + 1, probeVolumes: !singleVolume);
    }

    private static ComicArchiveReader OpenCore(string filePath, int rarFileHeadersToCheck, FileShare share = FileShare.Read, bool probeVolumes = true)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        var archiveName = Path.GetFileName(filePath);
        var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, share, bufferSize: 4096);
        try
        {
            Span<byte> signature = stackalloc byte[SignatureLength];
            var read = stream.ReadAtLeast(signature, SignatureLength, throwOnEndOfStream: false);
            signature = signature[..read];

            if (signature.StartsWith(Rar4Signature) || signature.StartsWith(Rar5Signature))
            {
                if (signature.StartsWith(Rar5Signature))
                {
                    ArchiveEntryReader.EnsureRarHeadersWithinLimits(stream, archiveName, rarFileHeadersToCheck);
                }

                var (rar, totalBytes, isMultiVolume) = OpenRarArchive(filePath, stream, probeVolumes);
                return new ComicArchiveReader(filePath, totalBytes, rarFileHeadersToCheck, zip: null, zipFile: null, rar, isMultiVolume);
            }

            stream.Position = 0;
            return new ComicArchiveReader(filePath, stream.Length, rarFileHeadersToCheck, ArchiveEntryReader.OpenZip(stream, archiveName), stream, rar: null);
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
    /// Opens the RAR archive in <paramref name="firstFile"/>, whose headers the caller has
    /// checked, so that SharpCompress only ever parses headers that have been checked too.
    /// Also returns the size of everything opened, every volume of a set included.
    /// </summary>
    /// <remarks>
    /// Opened by path, SharpCompress looks for the parts that follow by name. When the next part
    /// starts with a RAR signature each part is a volume of its own, and those volumes are
    /// checked here. When it does not, SharpCompress reads all the parts as one continuous file,
    /// headers included, past the end of the file that was checked. Real volumes always have the
    /// signature, so that case gets only the file that was asked for: it is opened from its
    /// already checked stream, which SharpCompress cannot follow to another file. Without
    /// <paramref name="probeVolumes"/> it is opened that way straight away.
    /// </remarks>
    private static (IArchive Archive, long TotalBytes, bool IsMultiVolume) OpenRarArchive(string filePath, FileStream firstFile, bool probeVolumes)
    {
        var totalBytes = firstFile.Length;
        if (probeVolumes)
        {
            using var set = new OwnedUntilReleased<IArchive>(RarArchive.OpenArchive(filePath, RarReaderOptions));
            var volumes = set.Value.Volumes.ToList();
            if (volumes.Count > 1)
            {
                foreach (var volume in volumes.Skip(1))
                {
                    totalBytes += EnsureRarVolumeWithinLimits(volume.FileName!);
                }

                // Only a multi-volume set is handed to the caller; a refusal or a single file closes it.
                firstFile.Dispose();
                return (set.Release(), totalBytes, true);
            }
        }

        firstFile.Position = 0;
        return (RarArchive.OpenArchive(firstFile, RarReaderOptions), totalBytes, false);
    }

    /// <summary>
    /// Owns a disposable until <see cref="Release"/> hands it on: disposing the wrapper disposes the value only when it
    /// was not handed on, which a using statement alone cannot say.
    /// </summary>
    private sealed class OwnedUntilReleased<T>(T value) : IDisposable
        where T : class, IDisposable
    {
        private T? _value = value;

        public T Value => _value ?? throw new ObjectDisposedException(nameof(OwnedUntilReleased<T>));

        public T Release()
        {
            var released = Value;
            _value = null;
            return released;
        }

        public void Dispose()
        {
            _value?.Dispose();
            _value = null;
        }
    }

    /// <summary>Checks a following volume the way the first was checked, and returns its size.</summary>
    private static long EnsureRarVolumeWithinLimits(string volumePath)
    {
        using var volume = new FileStream(volumePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096);
        Span<byte> signature = stackalloc byte[SignatureLength];
        if (volume.ReadAtLeast(signature, SignatureLength, throwOnEndOfStream: false) == SignatureLength
            && signature.StartsWith(Rar5Signature))
        {
            ArchiveEntryReader.EnsureRarHeadersWithinLimits(volume, Path.GetFileName(volumePath));
        }

        return volume.Length;
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
            yield return new RarEntry(this, entry, position++);
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
    public IReadOnlyList<ComicArchivePage> ListPages() =>
        ListPagesForReading().Select(p => p.Page).ToList();

    /// <summary>
    /// <see cref="ListPages"/>, also telling for each page whether its entry can only be
    /// decompressed by a pass over the entries before it (an entry of a solid RAR that
    /// depends on them), and for a CBZ page where it lies in the file (see
    /// <see cref="TryOpenZipPage"/>), when that can be had.
    /// </summary>
    internal IReadOnlyList<(ComicArchivePage Page, bool NeedsSolidPass, ZipPageLocator? Locator)> ListPagesForReading()
    {
        var pages = EnumerateEntries()
            .Where(entry => !entry.IsDirectory && IsPageImageName(entry.Name))
            .Select(entry => (
                Page: new ComicArchivePage(entry.Name, entry.Position),
                entry.NeedsSolidPass,
                Locator: entry is ZipEntry zipEntry ? zipEntry.Locator : null))
            .ToList();

        // OrderBy is stable, so entries with equal names keep their archive order.
        return pages.OrderBy(p => p.Page.Name, new NaturalStringComparer()).ToList();
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
    /// A solid RAR is decompressed from the start, in order, the entries left out included.
    /// Names get ZIP's '/' separators.
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

        // Listed in full before anything is read: reading an entry moves the position headers
        // are parsed from. Refused from the headers alone when they already give the archive
        // away, before a byte is written; the streams below catch headers that understate.
        var entries = EnumerateEntries().ToList();
        var declaredTotal = 0L;
        foreach (var entry in entries)
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

        var budget = new DecompressionBudget(_archiveName, totalBudgetBytes);
        foreach (var entry in entries)
        {
            if (entry.IsDirectory)
            {
                continue;
            }

            var copyable = (ICopyableEntry)entry;
            var declaredSize = copyable.DeclaredSizes.Uncompressed;
            if (include(entry.Name))
            {
                CopyEntry(writer, entry.Name, declaredSize, copyable, budget, maxEntryBytes);
            }
            else if (solid)
            {
                DiscardEntry(entry.Name, declaredSize, copyable, budget, maxEntryBytes);
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
        ICopyableEntry entry,
        DecompressionBudget budget,
        long maxEntryBytes)
    {
        var targetName = Format == ComicArchiveFormat.Zip ? name : name.Replace('\\', '/');
        using var source = entry.OpenStream();
        using var bounded = new LimitedRewriteStream(source, name, declaredSize, maxEntryBytes, budget, entry.ExpectedCrc);
        writer.Write(targetName, bounded, entry.LastModified);
    }

    /// <summary>
    /// Decompresses a solid archive's entry that is left out of the copy, which the entries after
    /// it depend on, under the same limits as one that is copied.
    /// </summary>
    private static void DiscardEntry(
        string name,
        long declaredSize,
        ICopyableEntry entry,
        DecompressionBudget budget,
        long maxEntryBytes,
        CancellationToken cancellationToken = default)
    {
        using var source = entry.OpenStream();
        using var bounded = new LimitedRewriteStream(source, name, declaredSize, maxEntryBytes, budget, entry.ExpectedCrc);
        ArchiveEntryReader.Discard(bounded, cancellationToken);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _solidCursor?.Dispose();
        _zip?.Dispose();
        _rar?.Dispose();
    }

    /// <summary>True for an entry of a solid RAR that depends on the entries before it.</summary>
    private bool IsSolidlyCompressed(IArchiveEntry entry) => entry.IsSolid && _rar is { IsSolid: true };

    /// <summary>
    /// Reads the entry at <paramref name="position"/> of a solid RAR by decompressing the archive
    /// in order, the only way it decompresses correctly. The position reached is kept, so that a
    /// caller reading through the pages (an EPUB conversion) decompresses the archive once
    /// instead of once per page; only a step back starts over.
    /// </summary>
    /// <exception cref="ArchiveLimitExceededException">The entry or what comes before it exceeds a limit.</exception>
    internal byte[] ReadSolidEntry(int position, string name, long declaredSize, long maxBytes, long? totalBudgetBytes = null)
    {
        ArchiveEntryReader.EnsureWithinLimits(name, declaredSize, -1, maxBytes);

        if (_solidCursor is null || _solidCursor.Position >= position)
        {
            _solidCursor?.Dispose();
            _solidCursor = null;
            _solidCursor = new SolidRarCursor(
                OpenCore(_filePath, _rarFileHeadersToCheck),
                new DecompressionBudget(_archiveName, totalBudgetBytes ?? ArchiveEntryReader.RewriteBudgetFor(_archiveBytes)));
        }

        try
        {
            return _solidCursor.Read(position, name, declaredSize, maxBytes);
        }
        catch
        {
            _solidCursor.Dispose();
            _solidCursor = null;
            throw;
        }
    }

    /// <summary>
    /// <see cref="ReadSolidEntry"/> into a pooled buffer, with cancellation. The cursor is not kept:
    /// a reader that turns pages of a solid archive keeps one through <see cref="OpenSolidCursor"/>.
    /// </summary>
    private RentedBufferStream ReadSolidEntryPooled(int position, string name, long maxBytes, CancellationToken cancellationToken)
    {
        using var cursor = new SolidRarCursor(
            OpenCore(_filePath, _rarFileHeadersToCheck),
            new DecompressionBudget(_archiveName, ArchiveEntryReader.RewriteBudgetFor(_archiveBytes)));
        return cursor.ReadPage(position, name, maxBytes, cancellationToken)
            ?? throw new ComicArchiveFormatException(
                $"'{_archiveName}' does not list '{name}' where its headers put it.", innerException: null);
    }

    /// <summary>
    /// Opens a forward pass over the solid RAR at <paramref name="filePath"/> for reading its pages
    /// in order, which a caller can keep open between reads (see <see cref="SolidRarCursorPool"/>).
    /// Every header is checked and listed up front, as <see cref="Open"/> does, and the pass gets
    /// the decompression budget of one rewrite of the archive.
    /// </summary>
    /// <remarks>
    /// The file is opened so that the app can still rewrite, replace, rename or delete it while
    /// the pass is open, as for an OPDS download: a pass may stay open between page turns. Its
    /// owner closes a pass that outlives its file version instead of reading it again, and data
    /// changed under an open pass fails the entries' CRC check, where they carry one, like any
    /// other damage.
    /// </remarks>
    /// <exception cref="ArchiveLimitExceededException">The archive is over a read limit.</exception>
    /// <exception cref="ComicArchiveFormatException">The file is not a readable ZIP or RAR archive.</exception>
    internal static SolidRarCursor OpenSolidCursor(string filePath)
    {
        var reader = OpenCore(filePath, int.MaxValue, FileShare.ReadWrite | FileShare.Delete);
        try
        {
            if (reader._rar is null)
            {
                throw new ComicArchiveFormatException($"'{reader._archiveName}' is not a RAR archive.", innerException: null);
            }

            return new SolidRarCursor(reader, new DecompressionBudget(reader._archiveName, ArchiveEntryReader.RewriteBudgetFor(reader._archiveBytes)));
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    /// <summary>
    /// When <paramref name="entry"/> is a ZIP entry stored without compression or encryption, a
    /// stream over its bytes where they lie in the archive file, so a page can be sent straight
    /// from the file instead of being copied into memory first. The stream takes this reader
    /// over and disposes it when it is disposed. Returns <c>null</c> for any other entry, which
    /// is then read the ordinary way.
    /// </summary>
    /// <remarks>
    /// The entry's sizes come from the central directory that <see cref="System.IO.Compression"/>
    /// read; only its local header, which says where the data starts and how it is stored, is
    /// read here. A stored entry is the same length packed and unpacked, so nothing can expand,
    /// and the range is checked to lie inside the file. The same per-entry cap applies as to any
    /// other page.
    /// </remarks>
    /// <exception cref="ArchiveLimitExceededException">The entry is over <paramref name="maxBytes"/>.</exception>
    internal Stream? TryOpenStoredRange(ComicArchiveEntry entry, long maxBytes)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (entry is not ZipEntry zipEntry || _zipFile is null || zipEntry.IsEncrypted)
        {
            return null;
        }

        var (length, compressed) = zipEntry.DeclaredSizes;
        if (length != compressed || zipEntry.LocalHeaderOffset is not { } headerOffset || headerOffset < 0)
        {
            return null;
        }

        ArchiveEntryReader.EnsureWithinLimits(entry.Name, length, compressed, maxBytes);

        var handle = _zipFile.SafeFileHandle;
        var fileLength = RandomAccess.GetLength(handle);
        Span<byte> header = stackalloc byte[ZipLocalHeaderLength];
        if (headerOffset > fileLength - ZipLocalHeaderLength
            || RandomAccess.Read(handle, header, headerOffset) != ZipLocalHeaderLength
            || BinaryPrimitives.ReadUInt32LittleEndian(header) != ZipLocalHeaderSignature
            || (BinaryPrimitives.ReadUInt16LittleEndian(header[6..]) & 1) != 0 // encrypted
            || BinaryPrimitives.ReadUInt16LittleEndian(header[8..]) != 0) // not stored
        {
            return null;
        }

        var dataStart = headerOffset + ZipLocalHeaderLength
            + BinaryPrimitives.ReadUInt16LittleEndian(header[26..])
            + BinaryPrimitives.ReadUInt16LittleEndian(header[28..]);
        if (dataStart > fileLength - length)
        {
            return null;
        }

        return new ArchiveRangeStream(this, handle, dataStart, length);
    }

    /// <summary>
    /// Opens a CBZ page from where <paramref name="locator"/> says it lies, without opening the
    /// archive or reading its central directory: a page stored without compression as a stream over
    /// its bytes in the file (as <see cref="TryOpenStoredRange"/>), a deflated page decompressed into
    /// a pooled buffer and checked against its CRC32. Returns <c>null</c> whenever the file is not
    /// exactly as the locator describes it (another length, another local header, another name at
    /// that offset, another method, data that does not inflate to the declared size and CRC), and
    /// then the caller reads the page the ordinary way, which decides what happens to it.
    /// </summary>
    /// <remarks>
    /// The locator comes from a listing of this version of the file (its length and last-write
    /// time), which counted its entries under <see cref="ArchiveEntryReader.MaxEntries"/>. The
    /// declared sizes are checked against the page limits first, the same check on the same sizes
    /// as the ordinary read; a stored page cannot expand, a deflated one is read under
    /// <paramref name="maxBytes"/> and its declared size, and the range is checked to lie inside the
    /// file.
    /// </remarks>
    /// <exception cref="ArchiveLimitExceededException">The page's declared size is over a limit.</exception>
    /// <exception cref="OperationCanceledException">The read was cancelled.</exception>
    internal static Stream? TryOpenZipPage(string filePath, long expectedFileLength, string name, ZipPageLocator locator, long maxBytes, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        ArgumentNullException.ThrowIfNull(name);
        ArchiveEntryReader.EnsureWithinLimits(name, locator.Length, locator.CompressedLength, maxBytes);

        SafeFileHandle handle;
        try
        {
            handle = File.OpenHandle(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var handedOver = false;
        try
        {
            var fileLength = RandomAccess.GetLength(handle);
            if (fileLength != expectedFileLength || LocateZipPageData(handle, fileLength, name, locator) is not { } data)
            {
                return null;
            }

            if (data.Method == ZipMethodStored)
            {
                handedOver = true;
                return new ArchiveRangeStream(handle, handle, data.Start, locator.Length);
            }

            RentedBufferStream page;
            using (var compressed = new ArchiveRangeStream(owner: null, handle, data.Start, locator.CompressedLength))
            using (var inflater = new System.IO.Compression.DeflateStream(compressed, System.IO.Compression.CompressionMode.Decompress))
            {
                try
                {
                    page = ArchiveEntryReader.ReadBoundedPooled(inflater, name, locator.Length, maxBytes, cancellationToken);
                }
                catch (Exception ex) when (ex is InvalidDataException or ArchiveLimitExceededException)
                {
                    // Data that does not inflate to what the directory declares: the ordinary read decides.
                    return null;
                }
            }

            if (page.Length != locator.Length
                || Crc32Stream.Compute(Crc32Stream.DEFAULT_POLYNOMIAL, Crc32Stream.DEFAULT_SEED, page.Bytes) != locator.Crc32)
            {
                page.Dispose();
                return null;
            }

            return page;
        }
        finally
        {
            if (!handedOver)
            {
                handle.Dispose();
            }
        }
    }

    /// <summary>
    /// Reads the local header at the locator's offset and returns where the entry's data starts and
    /// how it is stored, or <c>null</c> unless the header is that of an unencrypted stored or
    /// deflated entry called <paramref name="name"/> whose CRC and sizes (in the header, or in the
    /// data descriptor after the data) are those of the locator, with its data inside the file.
    /// </summary>
    private static (long Start, ushort Method)? LocateZipPageData(SafeFileHandle handle, long fileLength, string name, ZipPageLocator locator)
    {
        var nameBytes = System.Text.Encoding.UTF8.GetBytes(name);
        var offset = locator.LocalHeaderOffset;
        if (offset < 0 || locator.CompressedLength < 0 || offset > fileLength - ZipLocalHeaderLength - nameBytes.Length)
        {
            return null;
        }

        var header = new byte[ZipLocalHeaderLength + nameBytes.Length];
        if (RandomAccess.Read(handle, header, offset) != header.Length)
        {
            return null;
        }

        ReadOnlySpan<byte> fields = header;
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(fields[6..]);
        var method = BinaryPrimitives.ReadUInt16LittleEndian(fields[8..]);
        if (BinaryPrimitives.ReadUInt32LittleEndian(fields) != ZipLocalHeaderSignature
            || (flags & ZipFlagEncrypted) != 0
            || (method == ZipMethodStored ? locator.CompressedLength != locator.Length : method != ZipMethodDeflated)
            || BinaryPrimitives.ReadUInt16LittleEndian(fields[26..]) != nameBytes.Length
            || !fields[ZipLocalHeaderLength..].SequenceEqual(nameBytes))
        {
            return null;
        }

        var dataStart = offset + ZipLocalHeaderLength + nameBytes.Length + BinaryPrimitives.ReadUInt16LittleEndian(fields[28..]);
        if (dataStart > fileLength - locator.CompressedLength)
        {
            return null;
        }

        var described = (flags & ZipFlagDataDescriptor) == 0
            ? SizesMatch(fields[14..26], locator)
            : DataDescriptorMatches(handle, fileLength, dataStart + locator.CompressedLength, locator);
        return described ? (dataStart, method) : null;
    }

    /// <summary>
    /// True when a CRC32, packed size and unpacked size, as a local header or a data descriptor
    /// stores them, are the locator's. A ZIP64 entry's sizes are elsewhere; its CRC is still compared.
    /// </summary>
    private static bool SizesMatch(ReadOnlySpan<byte> crcAndSizes, ZipPageLocator locator)
    {
        var crc = BinaryPrimitives.ReadUInt32LittleEndian(crcAndSizes);
        var compressed = BinaryPrimitives.ReadUInt32LittleEndian(crcAndSizes[4..]);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(crcAndSizes[8..]);
        return crc == locator.Crc32
            && ((compressed == uint.MaxValue && length == uint.MaxValue)
                || (compressed == locator.CompressedLength && length == locator.Length));
    }

    /// <summary>The data descriptor after an entry's data, with or without its signature, matches the locator.</summary>
    private static bool DataDescriptorMatches(SafeFileHandle handle, long fileLength, long position, ZipPageLocator locator)
    {
        Span<byte> descriptor = stackalloc byte[16];
        var available = (int)Math.Min(descriptor.Length, fileLength - position);
        if (available < 12 || RandomAccess.Read(handle, descriptor[..available], position) != available)
        {
            return false;
        }

        return SizesMatch(descriptor[..12], locator)
            || (available == 16
                && BinaryPrimitives.ReadUInt32LittleEndian(descriptor) == ZipDataDescriptorSignature
                && SizesMatch(descriptor[4..16], locator));
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

        /// <summary>The CRC32 to check the entry's data against, or null to take it as it comes.</summary>
        uint? ExpectedCrc { get; }

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

        public uint? ExpectedCrc => null;

        public bool IsEncrypted => _entry.IsEncrypted;

        /// <summary>
        /// Where the entry lies in the file, for <see cref="TryOpenZipPage"/>; <c>null</c> for an
        /// encrypted entry or when the offset cannot be had, which are then always read the ordinary way.
        /// </summary>
        public ZipPageLocator? Locator =>
            !IsEncrypted && LocalHeaderOffset is { } offset && offset >= 0
                ? new ZipPageLocator(offset, _entry.CompressedLength, _entry.Length, _entry.Crc32)
                : null;

        /// <summary>
        /// Where the entry's local header starts in the file, which <see cref="SystemZipArchiveEntry"/>
        /// read from the central directory but does not make public; <c>null</c> when it cannot
        /// be had, and the entry is then read the ordinary way.
        /// </summary>
        public long? LocalHeaderOffset
        {
            get
            {
                if (!s_localHeaderOffsetAvailable)
                {
                    return null;
                }

                try
                {
                    return ZipEntryInternals.OffsetOfLocalHeader(_entry);
                }
                catch (MissingFieldException)
                {
                    // A runtime that renamed the field: pages are read into memory instead.
                    s_localHeaderOffsetAvailable = false;
                    return null;
                }
            }
        }

        public override byte[] ReadAllBytes(long maxBytes)
        {
            EnsureNotEncrypted();
            return ArchiveEntryReader.ReadAllBytes(_entry, maxBytes);
        }

        internal override RentedBufferStream ReadPooled(long maxBytes, CancellationToken cancellationToken)
        {
            EnsureNotEncrypted();
            ArchiveEntryReader.EnsureWithinLimits(Name, _entry.Length, _entry.CompressedLength, maxBytes);
            using var stream = _entry.Open();
            return ArchiveEntryReader.ReadBoundedPooled(stream, Name, _entry.Length, maxBytes, cancellationToken);
        }

        public override string ReadText()
        {
            EnsureNotEncrypted();
            return ArchiveEntryReader.ReadText(_entry);
        }

        public Stream OpenStream()
        {
            EnsureNotEncrypted();
            return _entry.Open();
        }

        /// <summary>
        /// System.IO.Compression ignores the encrypted flag and inflates the ciphertext, which at
        /// best fails with a misleading error and at worst comes back as garbage.
        /// </summary>
        private void EnsureNotEncrypted()
        {
            if (_entry.IsEncrypted)
            {
                throw new ComicArchiveFormatException(
                    $"Archive entry '{Name}' is encrypted, and password-protected archives are not supported.",
                    innerException: null);
            }
        }
    }

    private sealed class RarEntry : ComicArchiveEntry, ICopyableEntry
    {
        private readonly ComicArchiveReader _owner;
        private readonly IArchiveEntry _entry;

        public RarEntry(ComicArchiveReader owner, IArchiveEntry entry, int position)
            : base(entry.Key ?? string.Empty, position)
        {
            _owner = owner;
            _entry = entry;
        }

        public override bool IsDirectory => _entry.IsDirectory;

        // Entries of a solid archive are compressed as one stream, so a single
        // entry's packed size says nothing about how far it expands.
        public (long Uncompressed, long Compressed) DeclaredSizes =>
            (_entry.Size, _entry.IsSolid ? -1 : _entry.CompressedSize);

        public DateTime? LastModified => _entry.LastModifiedTime;

        public uint? ExpectedCrc => _owner.IsSolidlyCompressed(_entry) ? ArchiveEntryReader.RarEntryCrc32(_entry) : null;

        internal override bool NeedsSolidPass => _owner.IsSolidlyCompressed(_entry);

        public override byte[] ReadAllBytes(long maxBytes) =>
            _owner.IsSolidlyCompressed(_entry)
                ? _owner.ReadSolidEntry(Position, Name, _entry.Size, maxBytes)
                : ArchiveEntryReader.ReadAllBytes(_entry, maxBytes);

        internal override RentedBufferStream ReadPooled(long maxBytes, CancellationToken cancellationToken)
        {
            if (_owner.IsSolidlyCompressed(_entry))
            {
                return _owner.ReadSolidEntryPooled(Position, Name, maxBytes, cancellationToken);
            }

            var compressed = _entry.IsSolid ? -1 : _entry.CompressedSize;
            ArchiveEntryReader.EnsureWithinLimits(Name, _entry.Size, compressed, maxBytes);
            using var stream = _entry.OpenEntryStream();
            return ArchiveEntryReader.ReadBoundedPooled(stream, Name, _entry.Size, maxBytes, cancellationToken);
        }

        /// <summary>
        /// How much memory decompressing this entry needs for its window, as its header asks for
        /// it: none for a folder or a stored entry.
        /// </summary>
        public long WindowBytes => _entry.IsDirectory ? 0 : ArchiveEntryReader.RarWindowBytes(_entry);

        public override string ReadText() => ArchiveEntryReader.DecodeText(ReadAllBytes(ArchiveEntryReader.MaxComicInfoBytes));

        public Stream OpenStream() => _entry.OpenEntryStream();
    }

    /// <summary>
    /// A forward pass over a solid RAR, on its own archive instance: its decoder carries over
    /// from entry to entry, and reading moves the file position that the instance's headers are
    /// parsed from, so they are all listed before the first read.
    /// </summary>
    /// <remarks>
    /// This does not go through SharpCompress's streaming reader. That one parses the next
    /// header wherever the decoder happened to stop, so an entry packing more than it unpacks
    /// could smuggle headers that the check in <see cref="ArchiveEntryReader.EnsureRarHeadersWithinLimits"/>
    /// never saw; every header read here comes from the archive listing that check covers.
    /// </remarks>
    internal sealed class SolidRarCursor : IDisposable
    {
        // What a pass is assumed to hold besides its window: the listing, buffers, the file.
        private const long BaseEstimatedBytes = 1024L * 1024;

        private readonly ComicArchiveReader _source;
        private readonly List<ComicArchiveEntry> _entries;
        private readonly DecompressionBudget _budget;

        public SolidRarCursor(ComicArchiveReader source, DecompressionBudget budget)
        {
            _source = source;
            _entries = source.EnumerateRarEntries().Take(source._rarFileHeadersToCheck).ToList();
            _budget = budget;
            EstimatedBytes = BaseEstimatedBytes
                + _entries.Select(e => ((RarEntry)e).WindowBytes).DefaultIfEmpty(0).Max()
                + 256L * _entries.Count;
        }

        /// <summary>
        /// The entry the pass has reached, or -1 before the first. An entry before or at this
        /// position can only be read by a new pass.
        /// </summary>
        public int Position { get; private set; } = -1;

        /// <summary>
        /// Roughly how much memory the open pass holds: the decompression window its headers ask
        /// for, which the decoder keeps from entry to entry, plus the listing.
        /// </summary>
        public long EstimatedBytes { get; }

        /// <summary>How many entries this pass has decompressed, read or passed over (for tests).</summary>
        public int EntriesDecompressed { get; private set; }

        public byte[] Read(int position, string name, long declaredSize, long maxBytes)
        {
            var entry = AdvanceTo(position, CancellationToken.None);
            if (entry is null || !string.Equals(entry.Name, name, StringComparison.Ordinal))
            {
                throw new ComicArchiveFormatException(
                    $"'{_source._archiveName}' does not list '{name}' where its headers put it.", innerException: null);
            }

            var copyable = (ICopyableEntry)entry;
            using var stream = copyable.OpenStream();
            using var bounded = new LimitedRewriteStream(stream, name, declaredSize, maxBytes, _budget, copyable.ExpectedCrc);
            return ArchiveEntryReader.ReadBounded(bounded, name, declaredSize, maxBytes);
        }

        /// <summary>
        /// Moves the pass on to the page at <paramref name="position"/> and reads it into a pooled
        /// buffer, under <paramref name="maxBytes"/> and the pass's budget. Returns <c>null</c>
        /// when the archive does not hold <paramref name="name"/> there (the file changed since
        /// its pages were listed); the pass cannot be used after that, nor after an exception.
        /// </summary>
        /// <exception cref="ArchiveLimitExceededException">The page or an entry before it exceeds a limit.</exception>
        /// <exception cref="OperationCanceledException">The read was cancelled.</exception>
        public RentedBufferStream? ReadPage(int position, string name, long maxBytes, CancellationToken cancellationToken)
        {
            var entry = AdvanceTo(position, cancellationToken);
            if (entry is null || entry.IsDirectory || !string.Equals(entry.Name, name, StringComparison.Ordinal))
            {
                return null;
            }

            var copyable = (ICopyableEntry)entry;
            var declaredSize = copyable.DeclaredSizes.Uncompressed;
            ArchiveEntryReader.EnsureWithinLimits(name, declaredSize, -1, maxBytes);
            using var stream = copyable.OpenStream();
            using var bounded = new LimitedRewriteStream(stream, name, declaredSize, maxBytes, _budget, copyable.ExpectedCrc);
            return ArchiveEntryReader.ReadBoundedPooled(bounded, name, declaredSize, maxBytes, cancellationToken);
        }

        /// <summary>
        /// Decompresses and drops every entry between the current position and
        /// <paramref name="position"/>, and returns the entry there, not yet read; <c>null</c>
        /// when the archive ends first.
        /// </summary>
        private ComicArchiveEntry? AdvanceTo(int position, CancellationToken cancellationToken)
        {
            if (position <= Position)
            {
                throw new InvalidOperationException("A solid archive pass only moves forward.");
            }

            while (Position + 1 < _entries.Count)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = _entries[++Position];
                if (Position == position)
                {
                    if (!entry.IsDirectory)
                    {
                        EntriesDecompressed++;
                    }

                    return entry;
                }

                if (!entry.IsDirectory)
                {
                    var copyable = (ICopyableEntry)entry;
                    EntriesDecompressed++;
                    DiscardEntry(entry.Name, copyable.DeclaredSizes.Uncompressed, copyable, _budget, ArchiveEntryReader.MaxRewriteEntryBytes, cancellationToken);
                }
            }

            return null;
        }

        public void Dispose() => _source.Dispose();
    }

    /// <summary>
    /// Reads a range of the archive file, the bytes of one entry, through positional reads on a
    /// handle, and disposes what holds the handle open (the reader, or the handle itself) with it.
    /// </summary>
    internal sealed class ArchiveRangeStream : Stream
    {
        private readonly IDisposable? _owner;
        private readonly SafeFileHandle _handle;
        private readonly long _start;
        private readonly long _length;
        private long _position;
        private bool _disposed;

        public ArchiveRangeStream(IDisposable? owner, SafeFileHandle handle, long start, long length)
        {
            _owner = owner;
            _handle = handle;
            _start = start;
            _length = length;
        }

        public override bool CanRead => !_disposed;

        public override bool CanSeek => !_disposed;

        public override bool CanWrite => false;

        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set
            {
                ArgumentOutOfRangeException.ThrowIfNegative(value);
                _position = value;
            }
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var remaining = _length - _position;
            if (remaining <= 0 || buffer.IsEmpty)
            {
                return 0;
            }

            if (buffer.Length > remaining)
            {
                buffer = buffer[..(int)remaining];
            }

            var read = RandomAccess.Read(_handle, buffer, _start + _position);
            _position += read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var remaining = _length - _position;
            if (remaining <= 0 || buffer.IsEmpty)
            {
                return 0;
            }

            if (buffer.Length > remaining)
            {
                buffer = buffer[..(int)remaining];
            }

            var read = await RandomAccess.ReadAsync(_handle, buffer, _start + _position, cancellationToken).ConfigureAwait(false);
            _position += read;
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override long Seek(long offset, SeekOrigin origin)
        {
            var target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => _length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            ArgumentOutOfRangeException.ThrowIfNegative(target, nameof(offset));
            _position = target;
            return target;
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                _owner?.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// The field of <see cref="SystemZipArchiveEntry"/> that holds where its local header starts.
    /// Should a runtime rename it, reading it fails with <see cref="MissingFieldException"/>,
    /// pages are read into memory instead, and the tests say so.
    /// </summary>
    private static class ZipEntryInternals
    {
        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_offsetOfLocalHeader")]
        public static extern ref long OffsetOfLocalHeader(SystemZipArchiveEntry entry);
    }

    private static volatile bool s_localHeaderOffsetAvailable = true;

    /// <summary>What one pass over an archive may still decompress, across all of its entries.</summary>
    internal sealed class DecompressionBudget
    {
        private readonly string _archiveName;
        private readonly long _limitBytes;
        private long _usedBytes;

        public DecompressionBudget(string archiveName, long limitBytes)
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
                    $"'{_archiveName}' expands past the {ArchiveEntryReader.FormatBytes(_limitBytes)} limit for this archive."));
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
        private readonly DecompressionBudget _budget;
        private readonly uint? _expectedCrc;
        private long _read;
        private uint _crc = Crc32Stream.DEFAULT_SEED;

        public LimitedRewriteStream(Stream inner, string entryName, long declaredSize, long maxEntryBytes, DecompressionBudget budget, uint? expectedCrc = null)
        {
            _inner = inner;
            _entryName = entryName;
            _declaredSize = declaredSize;
            _maxEntryBytes = maxEntryBytes;
            _budget = budget;
            _expectedCrc = expectedCrc;
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
                if (_expectedCrc is not null)
                {
                    // Compute hands back the finished checksum; the running value is its inverse.
                    _crc = ~Crc32Stream.Compute(Crc32Stream.DEFAULT_POLYNOMIAL, _crc, buffer[..count]);
                }
            }
            else if (!buffer.IsEmpty && _expectedCrc is { } expected && ~_crc != expected)
            {
                throw new ComicArchiveFormatException(
                    $"Archive entry '{_entryName}' fails its CRC check; the archive is damaged.", innerException: null);
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
