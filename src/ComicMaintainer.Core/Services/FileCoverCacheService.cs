using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Default <see cref="IFileCoverCacheService"/> implementation.
///
/// Layout under the cache directory:
///   <hash>.img   cover thumbnail: page 1 of the source archive downscaled to
///                <see cref="AppSettings.FileCoverThumbnailMaxWidth"/> and
///                re-encoded (WebP by default), or the raw page bytes when
///                the max width is 0 or encoding fails
///   <hash>.meta  JSON sidecar { sourceLength, sourceMtimeUtcTicks, contentType, variant }
///   <hash>.jpeg.img, <hash>.jpeg.meta
///                the same cover as JPEG for OPDS clients (<see cref="FileCoverFormat.Jpeg"/>),
///                when the configured format is not JPEG already, so a feed's covers are
///                converted once instead of on every request
///
/// Cache key is SHA-256 of the absolute file path (avoids long filenames,
/// path separators, and platform case-sensitivity gotchas). Each entry is
/// validated on read against the current file length and last-write-time:
/// any mismatch triggers a regeneration, so the cache self-heals when a
/// file is replaced or modified even if an explicit invalidation hook is
/// missed. The thumbnail variant (format + width + quality) is stored in
/// the sidecar too, so changing the thumbnail settings (or upgrading from a
/// version that cached raw page bytes) is treated as a cache miss.
///
/// Concurrent requests for the same file are coalesced via a
/// <see cref="SemaphoreSlim"/> registered per cache key.
///
/// Because an entry is a function of the source's length and mtime and the
/// variant alone, <see cref="GetValidator"/> answers conditional requests from
/// a <c>stat</c> of the source: a 304 reads neither the blob nor its sidecar.
///
/// The sidecars of the covers served or written lately are kept in memory (at
/// most <see cref="MaxRememberedSidecars"/>), so a cache hit opens the blob and
/// nothing else; the remembered sidecar is checked against the source's length,
/// mtime and the variant exactly as the file would be. This process is the only
/// writer of the cache directory: a blob deleted under it is noticed when it
/// cannot be opened, and the cover is generated again.
///
/// Pruning runs in the background, one pass at a time. The cache's size is
/// counted by the first pass (after 32 writes) and kept up to date as covers are
/// written, so the directory is only scanned again once that count goes over
/// <see cref="AppSettings.FileCoverCacheMaxMegabytes"/>; a pass then removes the
/// least recently used covers until the cache is under 90% of the cap.
/// </summary>
public class FileCoverCacheService : IFileCoverCacheService
{
    /// <summary>
    /// Pruning granularity: a cached blob's last-access time is refreshed at most
    /// once per this interval, so serving a cover is not also a metadata write.
    /// </summary>
    internal static readonly TimeSpan AccessTimeRefreshInterval = TimeSpan.FromDays(1);

    /// <summary>How many sidecars are kept in memory: a few MiB for a library of this many comics.</summary>
    internal const int MaxRememberedSidecars = 20_000;

    /// <summary>Until the cache's size is known, the directory is scanned once every this many writes.</summary>
    internal const int WritesBetweenSizeScans = 32;

    /// <summary>A prune removes covers until the cache is at most this share of the cap, so it is not due again at once.</summary>
    internal const double PruneTargetFraction = 0.9;

    /// <summary>How long a background warm-up waits before looking again while covers someone asked for are generated.</summary>
    internal static readonly TimeSpan WarmUpYieldDelay = TimeSpan.FromMilliseconds(250);

    private readonly IComicReaderService _readerService;
    private readonly IOptionsMonitor<AppSettings> _settings;
    private readonly ILogger<FileCoverCacheService> _logger;
    private readonly MemoryCache _sidecars = new(new MemoryCacheOptions { SizeLimit = MaxRememberedSidecars });

    // Bytes the cache directory holds, as of the last scan plus what was written since; -1 until a scan counts them.
    private long _estimatedCacheBytes = -1;
    private int _pruneRunning;
    private Task _pruneTask = Task.CompletedTask;

    // Covers being generated or waiting to be, which a background warm-up gives way to.
    private int _activeGenerations;

    /// <summary>
    /// How many covers are generated at once, across all files. A library page asks for dozens of covers together;
    /// with none of them cached, each would otherwise read an archive and decode a full-size page at the same time,
    /// competing for the CPU and memory the covers already generated need to be served.
    /// </summary>
    internal static int MaxConcurrentGenerations { get; } = Math.Max(2, Environment.ProcessorCount);

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _keyLocks = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _generationSlots = new(MaxConcurrentGenerations, MaxConcurrentGenerations);
    private long _writesSinceLastPrune;

    public FileCoverCacheService(
        IComicReaderService readerService,
        IOptionsMonitor<AppSettings> settings,
        ILogger<FileCoverCacheService> logger)
    {
        _readerService = readerService;
        _settings = settings;
        _logger = logger;
    }

    public FileCoverValidator? GetValidator(string filePath) => GetValidator(filePath, FileCoverFormat.Configured);

    public FileCoverValidator? GetValidator(string filePath, FileCoverFormat format)
    {
        var info = TryStat(filePath);
        if (info is null)
        {
            return null;
        }

        return new FileCoverValidator(
            info.Length,
            new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
            CoverSpec.For(_settings.CurrentValue, format).Variant);
    }

    public async Task<FileCoverCacheEntry?> GetOrCreateAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var cover = await GetCoverAsync(filePath, FileCoverFormat.Configured, openStream: false, cancellationToken);
        return cover is { Data: { } data } found
            ? new FileCoverCacheEntry(data, found.ContentType, found.Validator.SourceLastModified, found.Validator.SourceLength, found.Validator.Variant)
            : null;
    }

    public async Task<FileCoverStream?> OpenAsync(string filePath, FileCoverFormat format, CancellationToken cancellationToken = default)
    {
        var cover = await GetCoverAsync(filePath, format, openStream: true, cancellationToken);
        if (cover is not { } found)
        {
            return null;
        }

        var content = found.Content ?? new MemoryStream(found.Data!, writable: false);
        return new FileCoverStream(content, found.ContentType, found.Validator);
    }

    /// <summary>
    /// The cover of <paramref name="filePath"/> in <paramref name="format"/>: from the cache when
    /// it holds this version (opened as a stream when <paramref name="openStream"/>, otherwise
    /// read into memory), else generated, persisted and returned as bytes.
    /// </summary>
    private async Task<Cover?> GetCoverAsync(string filePath, FileCoverFormat format, bool openStream, CancellationToken cancellationToken)
    {
        var info = TryStat(filePath);
        if (info is null)
        {
            return null;
        }

        var settings = _settings.CurrentValue;
        var sourceMtime = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
        var sourceLength = info.Length;
        var spec = CoverSpec.For(settings, format);

        if (!settings.FileCoverCacheEnabled)
        {
            return await ExtractAsync(filePath, sourceMtime, sourceLength, settings, spec, cancellationToken);
        }

        var key = ComputeKey(filePath);
        var dir = ResolveCacheDirectory(settings);
        var (blobPath, metaPath) = GetPaths(dir, key, spec.BlobSuffix);

        // Fast path: read without taking the per-key lock.
        var cached = TryReadCached(blobPath, metaPath, sourceMtime, sourceLength, spec.Variant, openStream);
        if (cached is not null)
        {
            return cached;
        }

        var keyLock = _keyLocks.GetOrAdd(key + spec.BlobSuffix, _ => new SemaphoreSlim(1, 1));
        await keyLock.WaitAsync(cancellationToken);
        try
        {
            // Re-check after acquiring the lock in case another caller raced ahead.
            cached = TryReadCached(blobPath, metaPath, sourceMtime, sourceLength, spec.Variant, openStream);
            if (cached is not null)
            {
                return cached;
            }

            var generated = await ExtractAsync(filePath, sourceMtime, sourceLength, settings, spec, cancellationToken);
            if (generated is null)
            {
                return null;
            }

            try
            {
                Directory.CreateDirectory(dir);
                WriteAtomic(blobPath, generated.Value.Data!);
                var meta = new CacheMeta
                {
                    SourceLength = sourceLength,
                    SourceMtimeUtcTicks = sourceMtime.UtcTicks,
                    ContentType = generated.Value.ContentType,
                    Variant = spec.Variant,
                };
                WriteAtomic(metaPath, JsonSerializer.SerializeToUtf8Bytes(meta));
                Remember(blobPath, meta);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
            {
                Forget(blobPath);
                _logger.LogWarning(ex, "FileCoverCache: failed to persist cover for {FilePath}",
                    LoggingHelper.SanitizePathForLog(filePath));
                // Still return the freshly generated bytes so the request succeeds.
                return generated;
            }

            CountWriteAndPruneIfDue(dir, generated.Value.Data!.Length, settings.FileCoverCacheMaxMegabytes);
            return generated;
        }
        finally
        {
            keyLock.Release();
            // We don't remove the semaphore — keeping it cheap avoids repeated
            // allocation for hot keys and the dictionary's working set is
            // bounded by the number of distinct comic files ever requested.
        }
    }

    public Task InvalidateAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return Task.CompletedTask;
        }

        try
        {
            var settings = _settings.CurrentValue;
            var dir = ResolveCacheDirectory(settings);
            var key = ComputeKey(filePath);
            foreach (var suffix in CoverSpec.BlobSuffixes)
            {
                var (blobPath, metaPath) = GetPaths(dir, key, suffix);
                Forget(blobPath);
                TryDelete(blobPath);
                TryDelete(metaPath);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "FileCoverCache: invalidate failed for {FilePath}",
                LoggingHelper.SanitizePathForLog(filePath));
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Gives way to covers someone is waiting for: while any are being generated, it waits
    /// <see cref="WarmUpYieldDelay"/> and looks again before it starts its own.
    /// </remarks>
    public async Task WarmUpAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var settings = _settings.CurrentValue;
        if (!settings.FileCoverCacheEnabled || TryStat(filePath) is not { } info)
        {
            return;
        }

        var spec = CoverSpec.For(settings, FileCoverFormat.Configured);
        var (blobPath, metaPath) = GetPaths(ResolveCacheDirectory(settings), ComputeKey(filePath), spec.BlobSuffix);
        if (IsCached(blobPath, metaPath, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), info.Length, spec.Variant))
        {
            return;
        }

        while (Volatile.Read(ref _activeGenerations) > 0)
        {
            await Task.Delay(WarmUpYieldDelay, cancellationToken);
        }

        // Generates and stores it, unless a request did so meanwhile.
        _ = await GetCoverAsync(filePath, FileCoverFormat.Configured, openStream: false, cancellationToken);
    }

    // ---- internals -------------------------------------------------------

    /// <summary>
    /// Stats the source file. Returns <c>null</c> for a blank path, a missing
    /// file or a path that cannot be inspected.
    /// </summary>
    private FileInfo? TryStat(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        try
        {
            var info = new FileInfo(filePath);
            return info.Exists ? info : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            _logger.LogDebug(ex, "FileCoverCache: stat failed for {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            return null;
        }
    }

    /// <summary>
    /// What one encoding of a cover is: its variant token (format, width and quality, which the
    /// sidecar and the ETag carry), the format it is encoded in (null for the page as it is),
    /// and the suffix that keeps its blob apart from the other encoding's.
    /// </summary>
    private readonly record struct CoverSpec(string Variant, string? Format, string BlobSuffix)
    {
        /// <summary>The blob-name suffixes of every encoding, for invalidation.</summary>
        public static readonly string[] BlobSuffixes = [string.Empty, JpegBlobSuffix];

        private const string JpegBlobSuffix = ".jpeg";

        public static CoverSpec For(AppSettings settings, FileCoverFormat format)
        {
            var maxWidth = settings.FileCoverThumbnailMaxWidth;
            var configuredFormat = CoverThumbnailEncoder.NormalizeFormat(settings.FileCoverThumbnailFormat);
            var quality = settings.FileCoverThumbnailQuality;
            if (format == FileCoverFormat.Configured || (maxWidth > 0 && configuredFormat == "jpeg"))
            {
                // The configured thumbnail, which is already JPEG when that is what was asked for.
                return new CoverSpec(
                    WithPageListVersion(CoverThumbnailEncoder.BuildVariant(maxWidth, configuredFormat, quality), ComicReaderService.PageListVersion),
                    maxWidth > 0 ? configuredFormat : null,
                    string.Empty);
            }

            // JPEG for OPDS: the thumbnail re-encoded, or with thumbnails off the page itself as JPEG.
            var variant = maxWidth > 0
                ? CoverThumbnailEncoder.BuildVariant(maxWidth, "jpeg", quality)
                : "orig-jpeg";
            return new CoverSpec(WithPageListVersion(variant, ComicReaderService.PageListVersion), "jpeg", JpegBlobSuffix);
        }
    }

    /// <summary>A cover, as bytes (generated, or read from the cache) or as an open cached file.</summary>
    private readonly record struct Cover(byte[]? Data, Stream? Content, string ContentType, FileCoverValidator Validator);

    /// <summary>
    /// Adds the reader's page-list version to a thumbnail variant token. The
    /// cover is page 1 of the page list, so when the rules that build that list
    /// change, cached covers must miss and their ETags must change. Version 1
    /// adds nothing: every cover cached so far was made under the version-1
    /// rules, so those entries stay valid.
    /// </summary>
    internal static string WithPageListVersion(string thumbnailVariant, int pageListVersion) =>
        pageListVersion <= 1
            ? thumbnailVariant
            : thumbnailVariant + "-l" + pageListVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private async Task<Cover?> ExtractAsync(
        string filePath,
        DateTimeOffset sourceMtime,
        long sourceLength,
        AppSettings settings,
        CoverSpec spec,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _activeGenerations);
        try
        {
            await _generationSlots.WaitAsync(cancellationToken);
            try
            {
                return await GenerateAsync(filePath, sourceMtime, sourceLength, settings, spec, cancellationToken);
            }
            finally
            {
                _generationSlots.Release();
            }
        }
        finally
        {
            Interlocked.Decrement(ref _activeGenerations);
        }
    }

    private async Task<Cover?> GenerateAsync(
        string filePath,
        DateTimeOffset sourceMtime,
        long sourceLength,
        AppSettings settings,
        CoverSpec spec,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var page = await _readerService.GetPageAsync(filePath, 1);
        if (page is null)
        {
            return null;
        }
        var (data, contentType) = page.Value;
        var validator = new FileCoverValidator(sourceLength, sourceMtime, spec.Variant);

        var maxWidth = settings.FileCoverThumbnailMaxWidth;
        var isAlreadyJpeg = spec.Format == "jpeg" && string.Equals(contentType, "image/jpeg", StringComparison.OrdinalIgnoreCase);
        if (spec.Format is not null && !(maxWidth <= 0 && isAlreadyJpeg))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var thumb = CoverThumbnailEncoder.TryCreateThumbnail(
                data, maxWidth > 0 ? maxWidth : int.MaxValue, spec.Format, settings.FileCoverThumbnailQuality);
            if (thumb is not null)
            {
                return new Cover(thumb.Value.Data, null, thumb.Value.ContentType, validator);
            }
            // Encoding failed (unsupported/corrupt image): serve the original
            // bytes. The entry is still stamped with the current variant so we
            // don't retry the failing encode on every request.
            _logger.LogDebug("FileCoverCache: thumbnail encode failed for {FilePath}; using original bytes",
                LoggingHelper.SanitizePathForLog(filePath));
        }
        return new Cover(data, null, contentType, validator);
    }

    private Cover? TryReadCached(
        string blobPath,
        string metaPath,
        DateTimeOffset sourceMtime,
        long sourceLength,
        string variant,
        bool openStream)
    {
        try
        {
            var remembered = _sidecars.Get<RememberedSidecar>(blobPath);
            var meta = remembered?.Meta ?? ReadMeta(metaPath);
            if (meta is null || !Describes(meta, sourceMtime, sourceLength, variant))
            {
                return null;
            }

            var validator = new FileCoverValidator(sourceLength, sourceMtime, variant);
            Cover cover;
            if (openStream)
            {
                // Opened before anything else can happen to it: a prune or a regeneration that
                // removes or replaces the file now leaves this handle reading the cover it checked.
                var content = new FileStream(blobPath, new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.Read | FileShare.Delete,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                });
                cover = new Cover(null, content, meta.ContentType, validator);
            }
            else
            {
                cover = new Cover(File.ReadAllBytes(blobPath), null, meta.ContentType, validator);
            }

            remembered ??= Remember(blobPath, meta);
            remembered.RefreshAccessTimeIfDue(blobPath, DateTime.UtcNow);
            return cover;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Gone (pruned, invalidated, deleted) or unreadable: whatever was remembered no longer holds.
            Forget(blobPath);
            if (ex is not (FileNotFoundException or DirectoryNotFoundException))
            {
                _logger.LogDebug(ex, "FileCoverCache: failed to read cached entry");
            }

            return null;
        }
    }

    /// <summary>
    /// True when a cached cover is the cover of this version of the source in this variant.
    /// </summary>
    private static bool Describes(CacheMeta meta, DateTimeOffset sourceMtime, long sourceLength, string variant) =>
        meta.SourceLength == sourceLength
        && meta.SourceMtimeUtcTicks == sourceMtime.UtcTicks
        && !string.IsNullOrEmpty(meta.ContentType)
        && string.Equals(meta.Variant, variant, StringComparison.Ordinal);

    /// <summary>Reads a sidecar, or <c>null</c> when there is none.</summary>
    /// <exception cref="JsonException">The sidecar is not valid JSON.</exception>
    private static CacheMeta? ReadMeta(string metaPath)
    {
        try
        {
            using var metaStream = File.OpenRead(metaPath);
            return JsonSerializer.Deserialize<CacheMeta>(metaStream);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Keeps a cover's sidecar in memory, so serving it again does not read the sidecar.</summary>
    private RememberedSidecar Remember(string blobPath, CacheMeta meta)
    {
        var remembered = new RememberedSidecar(meta);
        _sidecars.Set(blobPath, remembered, new MemoryCacheEntryOptions { Size = 1 });
        return remembered;
    }

    private void Forget(string blobPath) => _sidecars.Remove(blobPath);

    /// <summary>
    /// Whether the cache holds the configured cover of this version of the source, from the
    /// remembered sidecar or the sidecar on disk; the blob itself is not read.
    /// </summary>
    private bool IsCached(string blobPath, string metaPath, DateTimeOffset sourceMtime, long sourceLength, string variant)
    {
        if (_sidecars.Get<RememberedSidecar>(blobPath) is { } remembered)
        {
            return Describes(remembered.Meta, sourceMtime, sourceLength, variant);
        }

        try
        {
            if (ReadMeta(metaPath) is not { } meta || !Describes(meta, sourceMtime, sourceLength, variant) || !File.Exists(blobPath))
            {
                return false;
            }

            Remember(blobPath, meta);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Keeps a served blob's last-access time recent enough for
    /// <see cref="PruneIfOverLimit"/> to evict least-recently-used covers first,
    /// with at most one metadata write per blob per
    /// <see cref="AccessTimeRefreshInterval"/> (like the <c>relatime</c> mount
    /// option) instead of one per request. Best-effort.
    /// </summary>
    internal static void RefreshAccessTime(string blobPath, DateTime nowUtc)
    {
        try
        {
            if (nowUtc - File.GetLastAccessTimeUtc(blobPath) >= AccessTimeRefreshInterval)
            {
                File.SetLastAccessTimeUtc(blobPath, nowUtc);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Ignore: LRU order is a pruning hint, never a reason to fail a read.
        }
    }

    private static (string BlobPath, string MetaPath) GetPaths(string dir, string key, string suffix = "")
    {
        return (Path.Join(dir, key + suffix + ".img"), Path.Join(dir, key + suffix + ".meta"));
    }

    private static string ComputeKey(string filePath)
    {
        // SHA-256 hex over the absolute, OS-normalized path. We don't lowercase
        // because case-sensitivity is platform-dependent; treating different
        // casings as different cache keys is safe (worst case: one extra
        // generation).
        var absolute = Path.GetFullPath(filePath);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(absolute));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ResolveCacheDirectory(AppSettings settings)
    {
        var configured = settings.FileCoverCacheDirectory;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }
        var configDir = settings.ConfigDirectory ?? "/Config";
        return Path.Join(configDir, "file-covers");
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        var tempPath = path + ".tmp";
        try
        {
            File.WriteAllBytes(tempPath, bytes);
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* ignore */ }
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* ignore */ }
    }

    /// <summary>The background prune started last, for tests; completed when none has run.</summary>
    internal Task PruneTask => Volatile.Read(ref _pruneTask);

    /// <summary>
    /// Counts a cover just written towards the cache's size, and starts a prune in the background
    /// when one is due: every <see cref="WritesBetweenSizeScans"/> writes until a scan has counted
    /// the cache, then whenever the count goes over the cap. At most one prune runs at a time.
    /// </summary>
    private void CountWriteAndPruneIfDue(string dir, long writtenBytes, int maxMegabytes)
    {
        var writes = Interlocked.Increment(ref _writesSinceLastPrune);
        long estimate;
        do
        {
            estimate = Volatile.Read(ref _estimatedCacheBytes);
        }
        while (estimate >= 0 && Interlocked.CompareExchange(ref _estimatedCacheBytes, estimate + writtenBytes, estimate) != estimate);

        if (maxMegabytes <= 0)
        {
            return;
        }

        var due = estimate < 0
            ? writes % WritesBetweenSizeScans == 0
            : estimate + writtenBytes > MegabytesToBytes(maxMegabytes);
        if (due && Interlocked.CompareExchange(ref _pruneRunning, 1, 0) == 0)
        {
            Volatile.Write(ref _pruneTask, Task.Run(() => RunPrune(dir, maxMegabytes)));
        }
    }

    private void RunPrune(string dir, int maxMegabytes)
    {
        try
        {
            PruneIfOverLimit(dir, maxMegabytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            _logger.LogDebug(ex, "FileCoverCache: prune failed");
        }
        finally
        {
            Volatile.Write(ref _pruneRunning, 0);
        }
    }

    private static long MegabytesToBytes(int megabytes) => megabytes * 1024L * 1024L;

    private void PruneIfOverLimit(string dir, int maxMegabytes)
    {
        if (maxMegabytes <= 0 || !Directory.Exists(dir))
        {
            return;
        }
        var maxBytes = MegabytesToBytes(maxMegabytes);

        // Gather blob entries (the .meta sidecars are small; pair them with the blob).
        var blobs = Directory.EnumerateFiles(dir, "*.img")
            .Select(p =>
            {
                var fi = new FileInfo(p);
                return new { Path = p, fi.Length, LastAccess = SafeAccessTime(fi) };
            })
            .ToList();

        var total = blobs.Sum(b => b.Length);
        if (total > maxBytes)
        {
            var target = (long)(maxBytes * PruneTargetFraction);
            foreach (var entry in blobs.OrderBy(b => b.LastAccess))
            {
                if (total <= target) break;
                Forget(entry.Path);
                TryDelete(entry.Path);
                TryDelete(Path.ChangeExtension(entry.Path, ".meta"));
                total -= entry.Length;
            }
            _logger.LogDebug("FileCoverCache: pruned cache to under {Mb} MiB", maxMegabytes);
        }

        // Covers written while the directory was being scanned may be missed; the next scan counts them.
        Volatile.Write(ref _estimatedCacheBytes, total);
    }

    private static DateTime SafeAccessTime(FileInfo fi)
    {
        try { return fi.LastAccessTimeUtc; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return DateTime.UtcNow; }
    }

    /// <summary>
    /// A sidecar kept in memory, and when the blob's last-access time was last looked at, so a
    /// cover served again and again does not stat its blob each time either.
    /// </summary>
    private sealed class RememberedSidecar(CacheMeta meta)
    {
        // Ticks of the last look; 0 until the first, so a newly written cover is looked at when first served.
        private long _accessCheckedTicks;

        public CacheMeta Meta { get; } = meta;

        public void RefreshAccessTimeIfDue(string blobPath, DateTime nowUtc)
        {
            var checkedTicks = Volatile.Read(ref _accessCheckedTicks);
            if (checkedTicks != 0 && nowUtc.Ticks - checkedTicks < AccessTimeRefreshInterval.Ticks)
            {
                return;
            }

            Volatile.Write(ref _accessCheckedTicks, nowUtc.Ticks);
            RefreshAccessTime(blobPath, nowUtc);
        }
    }

    private sealed class CacheMeta
    {
        public long SourceLength { get; set; }
        public long SourceMtimeUtcTicks { get; set; }
        public string ContentType { get; set; } = string.Empty;
        // Null for entries written before thumbnails existed -> cache miss.
        public string? Variant { get; set; }
    }
}
