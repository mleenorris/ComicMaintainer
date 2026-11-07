using System.Collections.Concurrent;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using SharpCompress.Archives.Rar;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Service for reading and extracting pages from comic archives (CBZ/CBR).
/// </summary>
/// <remarks>
/// <para>
/// Listing an archive (opening it, filtering its image entries and natural-sorting
/// them) used to happen on every page request, every page count and every
/// progress save. The sorted page list of each archive version is now kept in a
/// small, size-bounded in-memory cache keyed on the file path and validated
/// against the file's length and last-write time, so a page turn costs one
/// <c>stat</c> plus the read of the one entry it shows, and counting pages costs
/// only the <c>stat</c>. Concurrent requests for an archive that is not cached
/// yet (the reader asks for the page count and prefetches several pages at once)
/// share one listing.
/// </para>
/// <para>
/// Entries are read through <see cref="ArchiveEntryReader"/>, which refuses
/// archives with too many entries and entries that would expand past
/// <see cref="ArchiveEntryReader.MaxPageBytes"/> with an
/// <see cref="ArchiveLimitExceededException"/>. Those propagate to the caller so it
/// can report a clear error; a refused listing is remembered for that version of
/// the file, so it is not re-read on every request. Every other failure is
/// logged and reported as "no pages" exactly as before.
/// </para>
/// </remarks>
public class ComicReaderService : IComicReaderService, IDisposable
{
    /// <summary>Default memory budget for cached page lists: 16 MiB of estimated size.</summary>
    internal const long DefaultPageIndexCacheBytes = 16L * 1024 * 1024;

    /// <summary>A page list nobody has asked for in this long is dropped.</summary>
    private static readonly TimeSpan PageIndexSlidingExpiration = TimeSpan.FromHours(1);

    private static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp" };

    private readonly ILogger<ComicReaderService> _logger;
    private readonly MemoryCache _pageIndexes;

    // Listings in progress, per file version, so concurrent cold requests share one.
    private readonly ConcurrentDictionary<FileVersion, Lazy<Task<PageIndex>>> _listings = new();

    private int _listingCount;

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

    public async Task<int> GetPageCountAsync(string filePath)
    {
        try
        {
            var index = await GetPageIndexAsync(filePath);
            return index?.Names.Length ?? 0;
        }
        catch (ArchiveLimitExceededException ex)
        {
            LogLimitExceeded(ex, filePath);
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
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

                var data = await Task.Run(() => ReadPage(index, pageNumber));
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
        catch (ArchiveLimitExceededException ex)
        {
            LogLimitExceeded(ex, filePath);
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
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
        catch (ArchiveLimitExceededException ex)
        {
            LogLimitExceeded(ex, filePath);
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
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
    /// archive read limits, which is rethrown for as long as the file is unchanged.
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

        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        var kind = extension switch
        {
            ".cbz" => ArchiveKind.Zip,
            ".cbr" => ArchiveKind.Rar,
            _ => (ArchiveKind?)null,
        };
        if (kind is null)
        {
            _logger.LogWarning("Unsupported file extension: {Extension}", LoggingHelper.SanitizeForLog(extension));
            return null;
        }

        var version = new FileVersion(filePath, kind.Value, info.Length, info.LastWriteTimeUtc.Ticks);
        if (!_pageIndexes.TryGetValue(filePath, out PageIndex? index)
            || index is null
            || index.Version != version)
        {
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
        }

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
            index = await Task.Run(() => version.Kind == ArchiveKind.Zip ? BuildZipIndex(version) : BuildRarIndex(version));
        }
        catch (ArchiveLimitExceededException ex)
        {
            index = PageIndex.Refused(version, ex.Message);
        }

        Interlocked.Increment(ref _listingCount);
        _pageIndexes.Set(version.Path, index, new MemoryCacheEntryOptions
        {
            Size = index.EstimatedBytes,
            SlidingExpiration = PageIndexSlidingExpiration,
        });
        return index;
    }

    private static PageIndex BuildZipIndex(FileVersion version)
    {
        using var archive = ArchiveEntryReader.OpenZip(version.Path);
        var entries = archive.Entries;

        var pages = new List<(string Name, int Position)>();
        for (var i = 0; i < entries.Count; i++)
        {
            var name = entries[i].FullName;
            if (!name.EndsWith('/') && IsImageFile(name))
            {
                pages.Add((name, i));
            }
        }

        return CreateIndex(version, pages);
    }

    private static PageIndex BuildRarIndex(FileVersion version)
    {
        using var archive = RarArchive.OpenArchive(version.Path);
        var archiveName = Path.GetFileName(version.Path);

        var pages = new List<(string Name, int Position)>();
        var position = 0;
        // Entries are parsed lazily, so the count is enforced while walking the
        // headers rather than after all of them have been read.
        foreach (var entry in archive.Entries)
        {
            ArchiveEntryReader.EnsureEntryCountWithinLimit(position + 1, archiveName);
            var key = entry.Key ?? string.Empty;
            if (!entry.IsDirectory && IsImageFile(key))
            {
                pages.Add((key, position));
            }

            position++;
        }

        return CreateIndex(version, pages);
    }

    private static PageIndex CreateIndex(FileVersion version, List<(string Name, int Position)> pages)
    {
        // OrderBy is stable, so entries with equal names keep their archive order.
        var sorted = pages.OrderBy(p => p.Name, new NaturalStringComparer()).ToArray();
        return new PageIndex(
            version,
            sorted.Select(p => p.Name).ToArray(),
            sorted.Select(p => p.Position).ToArray(),
            refusal: null);
    }

    /// <summary>
    /// Reads one page through the cached index. Returns <c>null</c> when the entry
    /// at the cached position is no longer the expected one (the file changed
    /// without its length or timestamp changing), so the caller can rebuild.
    /// </summary>
    private static byte[]? ReadPage(PageIndex index, int pageNumber)
    {
        var filePath = index.Version.Path;
        var expectedName = index.Names[pageNumber - 1];
        var position = index.EntryPositions[pageNumber - 1];

        if (index.Version.Kind == ArchiveKind.Zip)
        {
            using var archive = ArchiveEntryReader.OpenZip(filePath);
            var entries = archive.Entries;
            if (position >= entries.Count || !string.Equals(entries[position].FullName, expectedName, StringComparison.Ordinal))
            {
                return null;
            }

            return ArchiveEntryReader.ReadAllBytes(entries[position], ArchiveEntryReader.MaxPageBytes);
        }

        using var rar = RarArchive.OpenArchive(filePath);
        // Enumerating stops at the requested entry, so early pages do not parse
        // every header of the archive.
        var entry = rar.Entries.ElementAtOrDefault(position);
        if (entry is null || !string.Equals(entry.Key ?? string.Empty, expectedName, StringComparison.Ordinal))
        {
            return null;
        }

        return ArchiveEntryReader.ReadAllBytes(entry, ArchiveEntryReader.MaxPageBytes);
    }

    private void LogLimitExceeded(ArchiveLimitExceededException ex, string filePath) =>
        _logger.LogWarning("Refused to read {FilePath}: {Reason}",
            LoggingHelper.SanitizePathForLog(filePath), LoggingHelper.SanitizeForLog(ex.Message));

    private static bool IsImageFile(string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return ImageExtensions.Contains(extension);
    }

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

    private enum ArchiveKind
    {
        Zip,
        Rar,
    }

    /// <summary>One version of an archive file: its path, format, length and last-write time.</summary>
    private readonly record struct FileVersion(string Path, ArchiveKind Kind, long Length, long LastWriteUtcTicks);

    /// <summary>
    /// The image entries of one version of an archive, in reading order, with the
    /// position of each in the archive's entry list; or, when the read limits
    /// refused the archive, the reason.
    /// </summary>
    private sealed class PageIndex
    {
        public PageIndex(FileVersion version, string[] names, int[] entryPositions, string? refusal)
        {
            Version = version;
            Names = names;
            EntryPositions = entryPositions;
            Refusal = refusal;
            // Rough managed footprint: the object and arrays, plus each string.
            EstimatedBytes = 96 + 2L * (refusal?.Length ?? 0) + names.Sum(n => 40L + 2L * n.Length) + 4L * entryPositions.Length;
        }

        public static PageIndex Refused(FileVersion version, string reason) =>
            new(version, Array.Empty<string>(), Array.Empty<int>(), reason);

        public FileVersion Version { get; }
        public string[] Names { get; }
        public int[] EntryPositions { get; }
        public string? Refusal { get; }
        public long EstimatedBytes { get; }
    }
}
