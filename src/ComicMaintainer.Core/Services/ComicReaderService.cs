using System.Collections.Concurrent;
using System.Collections.Immutable;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Service for reading and extracting pages from comic archives. Archives are opened through
/// <see cref="ComicArchiveReader"/>, so what a file is depends on its content, not its name.
/// </summary>
/// <remarks>
/// <para>
/// Listing an archive (opening it, filtering its image entries and natural-sorting
/// them) used to happen on every page request, every page count and every
/// progress save. The sorted page list of each archive version is now kept in a
/// small, size-bounded in-memory cache keyed on the file path and validated
/// against the file's length and last-write time. Counting pages costs only a
/// <c>stat</c>; a page turn opens the archive only to read the one entry it
/// shows, at the position the list recorded (for a CBR, enumeration stops at
/// that entry), with no filtering or sorting. For a CBZ the list also records
/// where each page lies in the file, so a page turn reads that page's local
/// header and data without reading the archive's directory again. Concurrent requests for an
/// archive that is not cached yet (the reader asks for the page count and
/// prefetches several pages at once) share one listing.
/// </para>
/// <para>
/// A page sent to a client is opened with <see cref="OpenPageAsync"/>: a page stored
/// uncompressed in a ZIP is streamed from its place in the archive file, any other is
/// decompressed into a pooled buffer, and the read stops when the request is cancelled. The
/// pages of a solid RAR, which can only be decompressed after every entry before them, are
/// read through a pass kept open per archive (<see cref="SolidRarCursorPool"/>), so turning
/// pages walks the archive forward once instead of from its start for every page.
/// </para>
/// <para>
/// Entries are read through <see cref="ArchiveEntryReader"/>, which refuses
/// archives with too many entries and entries that would expand past
/// <see cref="ArchiveEntryReader.MaxPageBytes"/> with an
/// <see cref="ArchiveLimitExceededException"/>. Those propagate to the caller so it
/// can report a clear error. A refused listing, and a refused page, is remembered
/// for that version of the file, so it is neither re-read nor logged again on every
/// request (a refused cover would otherwise be retried on every library view): it is
/// logged at Warning when it is found and at Debug when it is repeated. Every other
/// failure is logged and reported as "no pages" exactly as before.
/// </para>
/// </remarks>
public class ComicReaderService : IComicReaderService, IDisposable
{
    /// <summary>
    /// Version of the rules that turn an archive's entries into its page list:
    /// which entries count as pages and the order they are read in, both defined by
    /// <see cref="ComicArchiveReader.ListPages"/>.
    /// </summary>
    /// <remarks>
    /// Page N of an unchanged file is only the same image while these rules are
    /// the same, so the version is part of every page ETag and of the cover
    /// cache's variant. Bump it whenever the rules change: browsers then refetch
    /// pages instead of revalidating them against the unchanged file, and cached
    /// covers are regenerated.
    /// </remarks>
    public const int PageListVersion = 1;

    /// <summary>Default memory budget for cached page lists: 16 MiB of estimated size.</summary>
    internal const long DefaultPageIndexCacheBytes = 16L * 1024 * 1024;

    /// <summary>A page list nobody has asked for in this long is dropped.</summary>
    private static readonly TimeSpan PageIndexSlidingExpiration = TimeSpan.FromHours(1);

    private readonly ILogger<ComicReaderService> _logger;
    private readonly MemoryCache _pageIndexes;
    private readonly SolidRarCursorPool _solidPasses;

    // Listings in progress, per file version, so concurrent cold requests share one.
    private readonly ConcurrentDictionary<FileVersion, Lazy<Task<PageIndex>>> _listings = new();

    private int _listingCount;
    private int _pageReadCount;
    private int _locatedPageReadCount;

    public ComicReaderService(ILogger<ComicReaderService> logger)
        : this(logger, DefaultPageIndexCacheBytes)
    {
    }

    /// <summary>
    /// Creates the service with a custom page-list cache budget (in estimated
    /// bytes), and optionally its own pool of solid RAR passes. Exposed for tests.
    /// </summary>
    internal ComicReaderService(ILogger<ComicReaderService> logger, long pageIndexCacheBytes, SolidRarCursorPool? solidPasses = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageIndexCacheBytes);
        _logger = logger;
        _pageIndexes = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = pageIndexCacheBytes,
            TrackStatistics = true,
        });
        _solidPasses = solidPasses ?? new SolidRarCursorPool();
    }

    /// <summary>The passes over solid RAR archives kept open between page reads (for tests).</summary>
    internal SolidRarCursorPool SolidPasses => _solidPasses;

    /// <summary>Estimated bytes currently held by cached page lists (for tests).</summary>
    internal long CachedPageIndexBytes => _pageIndexes.GetCurrentStatistics()?.CurrentEstimatedSize ?? 0;

    /// <summary>Number of archives whose page list is currently cached (for tests).</summary>
    internal int CachedPageIndexCount => _pageIndexes.Count;

    /// <summary>Number of times an archive has been listed (for tests).</summary>
    internal int ListingCount => Volatile.Read(ref _listingCount);

    /// <summary>Number of times a page entry has been opened (for tests).</summary>
    internal int PageReadCount => Volatile.Read(ref _pageReadCount);

    /// <summary>
    /// Number of CBZ pages opened from where the listing found them, without opening the archive (for tests).
    /// </summary>
    internal int LocatedPageReadCount => Volatile.Read(ref _locatedPageReadCount);

    // Refusals by the archive read limits pass through every public method
    // unlogged: they were logged where they were found (see LogRefused).

    public async Task<int> GetPageCountAsync(string filePath)
    {
        try
        {
            var index = await GetPageIndexAsync(filePath);
            return index?.Names.Length ?? 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not ArchiveLimitExceededException)
        {
            _logger.LogError(ex, "Error getting page count for {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            return 0;
        }
    }

    public async Task<(byte[] Data, string ContentType)?> GetPageAsync(string filePath, int pageNumber)
    {
        var page = await ReadPageAsync(filePath, pageNumber, CancellationToken.None);
        if (page is null)
        {
            return null;
        }

        using var content = page.Value.Content;
        return (ToArray(content), page.Value.ContentType);
    }

    public Task<ComicPageStream?> OpenPageAsync(string filePath, int pageNumber, CancellationToken cancellationToken) =>
        ReadPageAsync(filePath, pageNumber, cancellationToken);

    private async Task<ComicPageStream?> ReadPageAsync(string filePath, int pageNumber, CancellationToken cancellationToken)
    {
        try
        {
            // Two attempts: the cached page list is validated against the file's
            // length and last-write time, but a file replaced by one with the same
            // size and timestamp is only noticed when the entry name at the cached
            // position no longer matches. Then the list is rebuilt once.
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var index = await GetPageIndexAsync(filePath);
                if (index is null)
                {
                    return null;
                }

                if (pageNumber < 1 || pageNumber > index.Names.Length)
                {
                    _logger.LogWarning("Page number {PageNumber} out of range (1-{Count})", pageNumber, index.Names.Length);
                    return null;
                }

                if (index.PageRefusals.TryGetValue(pageNumber, out var refusal))
                {
                    LogRefusedAgain(filePath, refusal);
                    throw new ArchiveLimitExceededException(refusal);
                }

                Stream? content;
                try
                {
                    Interlocked.Increment(ref _pageReadCount);
                    content = index.NeedsSolidPass[pageNumber - 1]
                        ? await ReadSolidPageAsync(index, pageNumber, cancellationToken)
                        : await Task.Run(() => OpenPage(index, pageNumber, cancellationToken), cancellationToken);
                }
                catch (ArchiveLimitExceededException ex)
                {
                    LogRefused(filePath, ex.Message);
                    RememberPageRefusal(index, pageNumber, ex.Message);
                    throw;
                }

                if (content is not null)
                {
                    return new ComicPageStream(content, GetContentType(index.Names[pageNumber - 1]));
                }

                _pageIndexes.Remove(filePath);
            }

            _logger.LogWarning("Page {PageNumber} of {FilePath} changed while it was being read",
                pageNumber, LoggingHelper.SanitizePathForLog(filePath));
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Nobody wants the page any more (the reader moved on); not a failure.
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not ArchiveLimitExceededException)
        {
            _logger.LogError(ex, "Error getting page {PageNumber} from {FilePath}", pageNumber, LoggingHelper.SanitizePathForLog(filePath));
            return null;
        }
    }

    private static byte[] ToArray(Stream content)
    {
        if (content is RentedBufferStream rented)
        {
            return rented.ToExactArray();
        }

        var data = new byte[content.Length];
        content.ReadExactly(data);
        return data;
    }

    public async Task<List<string>> GetPageNamesAsync(string filePath)
    {
        try
        {
            var index = await GetPageIndexAsync(filePath);
            // A copy: callers own the list they get, the cached array stays intact.
            return index is null ? new List<string>() : new List<string>(index.Names);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not ArchiveLimitExceededException)
        {
            _logger.LogError(ex, "Error getting page names from {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            return new List<string>();
        }
    }

    public void Dispose()
    {
        _pageIndexes.Dispose();
        _solidPasses.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Returns the sorted page list for the current version of the file, from the
    /// cache when the file's length and last-write time still match, otherwise by
    /// listing the archive. Returns <c>null</c> when the file is missing or is not
    /// a supported archive. Failures are not cached, except a refusal by the
    /// archive read limits, which is rethrown for as long as the file is unchanged
    /// (logged at Warning by the listing that found it, at Debug afterwards).
    /// </summary>
    /// <exception cref="ArchiveLimitExceededException">The archive is over the read limits.</exception>
    private async Task<PageIndex?> GetPageIndexAsync(string filePath)
    {
        var info = new FileInfo(filePath);
        if (!info.Exists)
        {
            _logger.LogWarning("File not found: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            return null;
        }

        if (!ComicFileExtensions.IsComicArchive(filePath))
        {
            _logger.LogWarning("Unsupported file extension: {Extension}", LoggingHelper.SanitizeForLog(Path.GetExtension(filePath)));
            return null;
        }

        var version = new FileVersion(filePath, info.Length, info.LastWriteTimeUtc.Ticks);
        if (_pageIndexes.TryGetValue(filePath, out PageIndex? index)
            && index is not null
            && index.Version == version)
        {
            if (index.Refusal is not null)
            {
                LogRefusedAgain(filePath, index.Refusal);
                throw new ArchiveLimitExceededException(index.Refusal);
            }

            return index;
        }

        var listing = _listings.GetOrAdd(version, v => new Lazy<Task<PageIndex>>(() => ListAndCacheAsync(v)));
        try
        {
            index = await listing.Value;
        }
        finally
        {
            // Only this listing: a later one for the same version may have started since.
            _listings.TryRemove(KeyValuePair.Create(version, listing));
        }

        // A refusal found by the listing this request waited for, which logged it.
        if (index.Refusal is not null)
        {
            throw new ArchiveLimitExceededException(index.Refusal);
        }

        return index;
    }

    private async Task<PageIndex> ListAndCacheAsync(FileVersion version)
    {
        // A request that missed the cache just before another listing of this
        // version finished (and left the in-progress set) finds it cached here.
        if (_pageIndexes.TryGetValue(version.Path, out PageIndex? cached) && cached?.Version == version)
        {
            return cached;
        }

        PageIndex index;
        try
        {
            index = await Task.Run(() => BuildIndex(version));
        }
        catch (ArchiveLimitExceededException ex)
        {
            LogRefused(version.Path, ex.Message);
            index = PageIndex.Refused(version, ex.Message);
        }

        Interlocked.Increment(ref _listingCount);
        CacheIndex(index);
        return index;
    }

    private void CacheIndex(PageIndex index) =>
        _pageIndexes.Set(index.Version.Path, index, new MemoryCacheEntryOptions
        {
            Size = index.EstimatedBytes,
            SlidingExpiration = PageIndexSlidingExpiration,
        });

    /// <summary>
    /// Records that the read limits refused a page of the cached version of the
    /// file, so later requests for it (the library grid asks for every cover on
    /// every view) are answered without opening the entry or logging another
    /// warning. The cached list is replaced by a copy carrying the refusal, so its
    /// size stays accounted for. Nothing is recorded once the cache holds another
    /// version; losing a refusal to a concurrent update only means one more read.
    /// </summary>
    private void RememberPageRefusal(PageIndex index, int pageNumber, string reason)
    {
        if (_pageIndexes.TryGetValue(index.Version.Path, out PageIndex? current)
            && current is not null
            && current.Version == index.Version
            && current.Refusal is null)
        {
            CacheIndex(current.WithPageRefusal(pageNumber, reason));
        }
    }

    private static PageIndex BuildIndex(FileVersion version)
    {
        using var archive = ComicArchiveReader.Open(version.Path);
        var pages = archive.ListPagesForReading();
        var locators = pages.Select(p => p.Locator).ToArray();
        return new PageIndex(
            version,
            pages.Select(p => p.Page.Name).ToArray(),
            pages.Select(p => p.Page.Position).ToArray(),
            pages.Select(p => p.NeedsSolidPass).ToArray(),
            refusal: null,
            locators.Any(l => l is not null) ? locators : null,
            singleVolume: !archive.IsMultiVolume);
    }

    /// <summary>
    /// Opens one page through the cached index: a stream over a stored ZIP entry's bytes in the
    /// file, or the page decompressed into a pooled buffer. Returns <c>null</c> when the entry at
    /// the cached position is no longer the expected one (the file changed without its length or
    /// timestamp changing), so the caller can rebuild.
    /// </summary>
    /// <remarks>
    /// A CBZ page is first opened from where the listing found it in the file, which reads its
    /// local header and its data and nothing else (see <see cref="ComicArchiveReader.TryOpenZipPage"/>).
    /// Only when the file does not match what the listing recorded is the archive opened and its
    /// directory read. A RAR that was a single file when it was listed is opened as one, without
    /// looking for volumes that follow it.
    /// </remarks>
    private Stream? OpenPage(PageIndex index, int pageNumber, CancellationToken cancellationToken)
    {
        var expectedName = index.Names[pageNumber - 1];
        var position = index.EntryPositions[pageNumber - 1];

        if (index.ZipLocators?[pageNumber - 1] is { } locator
            && ComicArchiveReader.TryOpenZipPage(index.Version.Path, index.Version.Length, expectedName, locator, ArchiveEntryReader.MaxPageBytes, cancellationToken) is { } located)
        {
            Interlocked.Increment(ref _locatedPageReadCount);
            return located;
        }

        var archive = ComicArchiveReader.OpenForEntry(index.Version.Path, position, index.SingleVolume);
        var handedOver = false;
        try
        {
            var entry = archive.GetEntryAt(position);
            if (entry is null || !string.Equals(entry.Name, expectedName, StringComparison.Ordinal))
            {
                return null;
            }

            if (archive.TryOpenStoredRange(entry, ArchiveEntryReader.MaxPageBytes) is { } range)
            {
                // The range reads from the archive's file as it is sent, so it keeps the archive open.
                handedOver = true;
                return range;
            }

            return entry.ReadPooled(ArchiveEntryReader.MaxPageBytes, cancellationToken);
        }
        finally
        {
            if (!handedOver)
            {
                archive.Dispose();
            }
        }
    }

    /// <summary>
    /// Reads a page of a solid RAR through the pass kept open for the archive (see
    /// <see cref="SolidRarCursorPool"/>), so turning pages walks the archive forward once.
    /// </summary>
    private async Task<Stream?> ReadSolidPageAsync(PageIndex index, int pageNumber, CancellationToken cancellationToken) =>
        await _solidPasses.ReadPageAsync(
            index.Version.Path,
            index.Version.Length,
            index.Version.LastWriteUtcTicks,
            index.EntryPositions[pageNumber - 1],
            index.Names[pageNumber - 1],
            ArchiveEntryReader.MaxPageBytes,
            cancellationToken);

    /// <summary>Logs a refusal by the read limits where it is found: once per file version (and page).</summary>
    private void LogRefused(string filePath, string reason) =>
        _logger.LogWarning("Refused to read {FilePath}: {Reason}",
            LoggingHelper.SanitizePathForLog(filePath), LoggingHelper.SanitizeForLog(reason));

    /// <summary>Logs a remembered refusal being repeated for another request.</summary>
    private void LogRefusedAgain(string filePath, string reason) =>
        _logger.LogDebug("Still refusing {FilePath}, unchanged since it was refused: {Reason}",
            LoggingHelper.SanitizePathForLog(filePath), LoggingHelper.SanitizeForLog(reason));

    private static string GetContentType(string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return extension switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".webp" => "image/webp",
            _ => "application/octet-stream"
        };
    }

    /// <summary>One version of an archive file: its path, length and last-write time.</summary>
    private readonly record struct FileVersion(string Path, long Length, long LastWriteUtcTicks);

    /// <summary>
    /// The image entries of one version of an archive, in reading order, with the
    /// position of each in the archive's entry list; or, when the read limits
    /// refused the archive, the reason. Pages the limits refused are recorded on
    /// a copy (<see cref="WithPageRefusal"/>), so a cached index is never mutated.
    /// </summary>
    private sealed class PageIndex
    {
        public PageIndex(
            FileVersion version,
            string[] names,
            int[] entryPositions,
            bool[] needsSolidPass,
            string? refusal,
            ZipPageLocator?[]? zipLocators = null,
            bool singleVolume = true)
            : this(
                version,
                names,
                entryPositions,
                needsSolidPass,
                refusal,
                zipLocators,
                singleVolume,
                ImmutableDictionary<int, string>.Empty,
                // Rough managed footprint: the object and arrays, plus each string and locator.
                96 + 2L * (refusal?.Length ?? 0) + names.Sum(n => 40L + 2L * n.Length) + 4L * entryPositions.Length + needsSolidPass.Length
                    + (zipLocators is null ? 0 : 24 + 40L * zipLocators.Length))
        {
        }

        private PageIndex(
            FileVersion version,
            string[] names,
            int[] entryPositions,
            bool[] needsSolidPass,
            string? refusal,
            ZipPageLocator?[]? zipLocators,
            bool singleVolume,
            ImmutableDictionary<int, string> pageRefusals,
            long estimatedBytes)
        {
            Version = version;
            Names = names;
            EntryPositions = entryPositions;
            NeedsSolidPass = needsSolidPass;
            Refusal = refusal;
            ZipLocators = zipLocators;
            SingleVolume = singleVolume;
            PageRefusals = pageRefusals;
            EstimatedBytes = estimatedBytes;
        }

        public static PageIndex Refused(FileVersion version, string reason) =>
            new(version, Array.Empty<string>(), Array.Empty<int>(), Array.Empty<bool>(), reason);

        public FileVersion Version { get; }
        public string[] Names { get; }
        public int[] EntryPositions { get; }

        /// <summary>For each page, whether it can only be read by a pass over a solid RAR.</summary>
        public bool[] NeedsSolidPass { get; }

        /// <summary>For each page of a CBZ, where it lies in the file, when that was known; null for a RAR.</summary>
        public ZipPageLocator?[]? ZipLocators { get; }

        /// <summary>False for a RAR that was listed as a set of volumes.</summary>
        public bool SingleVolume { get; }

        public string? Refusal { get; }

        /// <summary>Pages (1-based) the read limits refused, with the reason.</summary>
        public ImmutableDictionary<int, string> PageRefusals { get; }

        public long EstimatedBytes { get; }

        /// <summary>A copy of this index that also records a refused page; the page list is shared.</summary>
        public PageIndex WithPageRefusal(int pageNumber, string reason)
        {
            var estimatedBytes = EstimatedBytes + RefusalBytes(reason);
            if (PageRefusals.TryGetValue(pageNumber, out var previous))
            {
                estimatedBytes -= RefusalBytes(previous);
            }

            return new PageIndex(Version, Names, EntryPositions, NeedsSolidPass, Refusal, ZipLocators, SingleVolume, PageRefusals.SetItem(pageNumber, reason), estimatedBytes);
        }

        // Rough managed footprint of one recorded refusal: a dictionary node and the message.
        private static long RefusalBytes(string reason) => 64 + 2L * reason.Length;
    }
}
