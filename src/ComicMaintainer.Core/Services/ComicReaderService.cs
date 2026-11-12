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
/// that entry), with no filtering or sorting. Concurrent requests for an
/// archive that is not cached yet (the reader asks for the page count and
/// prefetches several pages at once) share one listing.
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

    // Listings in progress, per file version, so concurrent cold requests share one.
    private readonly ConcurrentDictionary<FileVersion, Lazy<Task<PageIndex>>> _listings = new();

    private int _listingCount;
    private int _pageReadCount;

    public ComicReaderService(ILogger<ComicReaderService> logger)
        : this(logger, DefaultPageIndexCacheBytes)
    {
    }

    /// <summary>
    /// Creates the service with a custom page-list cache budget (in estimated
    /// bytes). Exposed for tests that exercise eviction.
    /// </summary>
    internal ComicReaderService(ILogger<ComicReaderService> logger, long pageIndexCacheBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageIndexCacheBytes);
        _logger = logger;
        _pageIndexes = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = pageIndexCacheBytes,
            TrackStatistics = true,
        });
    }

    /// <summary>Estimated bytes currently held by cached page lists (for tests).</summary>
    internal long CachedPageIndexBytes => _pageIndexes.GetCurrentStatistics()?.CurrentEstimatedSize ?? 0;

    /// <summary>Number of archives whose page list is currently cached (for tests).</summary>
    internal int CachedPageIndexCount => _pageIndexes.Count;

    /// <summary>Number of times an archive has been listed (for tests).</summary>
    internal int ListingCount => Volatile.Read(ref _listingCount);

    /// <summary>Number of times a page entry has been opened (for tests).</summary>
    internal int PageReadCount => Volatile.Read(ref _pageReadCount);

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

                byte[]? data;
                try
                {
                    Interlocked.Increment(ref _pageReadCount);
                    data = await Task.Run(() => ReadPage(index, pageNumber));
                }
                catch (ArchiveLimitExceededException ex)
                {
                    LogRefused(filePath, ex.Message);
                    RememberPageRefusal(index, pageNumber, ex.Message);
                    throw;
                }

                if (data is not null)
                {
                    return (data, GetContentType(index.Names[pageNumber - 1]));
                }

                _pageIndexes.Remove(filePath);
            }

            _logger.LogWarning("Page {PageNumber} of {FilePath} changed while it was being read",
                pageNumber, LoggingHelper.SanitizePathForLog(filePath));
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not ArchiveLimitExceededException)
        {
            _logger.LogError(ex, "Error getting page {PageNumber} from {FilePath}", pageNumber, LoggingHelper.SanitizePathForLog(filePath));
            return null;
        }
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
        var pages = archive.ListPages();
        return new PageIndex(
            version,
            pages.Select(p => p.Name).ToArray(),
            pages.Select(p => p.Position).ToArray(),
            refusal: null);
    }

    /// <summary>
    /// Reads one page through the cached index. Returns <c>null</c> when the entry
    /// at the cached position is no longer the expected one (the file changed
    /// without its length or timestamp changing), so the caller can rebuild.
    /// </summary>
    private static byte[]? ReadPage(PageIndex index, int pageNumber)
    {
        var expectedName = index.Names[pageNumber - 1];
        var position = index.EntryPositions[pageNumber - 1];

        using var archive = ComicArchiveReader.OpenForEntry(index.Version.Path, position);
        var entry = archive.GetEntryAt(position);
        if (entry is null || !string.Equals(entry.Name, expectedName, StringComparison.Ordinal))
        {
            return null;
        }

        return entry.ReadAllBytes(ArchiveEntryReader.MaxPageBytes);
    }

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
        public PageIndex(FileVersion version, string[] names, int[] entryPositions, string? refusal)
            : this(
                version,
                names,
                entryPositions,
                refusal,
                ImmutableDictionary<int, string>.Empty,
                // Rough managed footprint: the object and arrays, plus each string.
                96 + 2L * (refusal?.Length ?? 0) + names.Sum(n => 40L + 2L * n.Length) + 4L * entryPositions.Length)
        {
        }

        private PageIndex(
            FileVersion version,
            string[] names,
            int[] entryPositions,
            string? refusal,
            ImmutableDictionary<int, string> pageRefusals,
            long estimatedBytes)
        {
            Version = version;
            Names = names;
            EntryPositions = entryPositions;
            Refusal = refusal;
            PageRefusals = pageRefusals;
            EstimatedBytes = estimatedBytes;
        }

        public static PageIndex Refused(FileVersion version, string reason) =>
            new(version, Array.Empty<string>(), Array.Empty<int>(), reason);

        public FileVersion Version { get; }
        public string[] Names { get; }
        public int[] EntryPositions { get; }
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

            return new PageIndex(Version, Names, EntryPositions, Refusal, PageRefusals.SetItem(pageNumber, reason), estimatedBytes);
        }

        // Rough managed footprint of one recorded refusal: a dictionary node and the message.
        private static long RefusalBytes(string reason) => 64 + 2L * reason.Length;
    }
}
