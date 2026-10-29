using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Utilities;
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
/// </summary>
public class FileCoverCacheService : IFileCoverCacheService
{
    private readonly IComicReaderService _readerService;
    private readonly IOptionsMonitor<AppSettings> _settings;
    private readonly ILogger<FileCoverCacheService> _logger;

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _keyLocks = new(StringComparer.Ordinal);
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

    public async Task<FileCoverCacheEntry?> GetOrCreateAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        FileInfo info;
        try
        {
            info = new FileInfo(filePath);
            if (!info.Exists)
            {
                return null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            _logger.LogDebug(ex, "FileCoverCache: stat failed for {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            return null;
        }

        var settings = _settings.CurrentValue;
        var sourceMtime = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
        var sourceLength = info.Length;
        var variant = CoverThumbnailEncoder.BuildVariant(
            settings.FileCoverThumbnailMaxWidth,
            settings.FileCoverThumbnailFormat,
            settings.FileCoverThumbnailQuality);

        if (!settings.FileCoverCacheEnabled)
        {
            return await ExtractAsync(filePath, sourceMtime, settings, variant, cancellationToken);
        }

        var key = ComputeKey(filePath);
        var dir = ResolveCacheDirectory(settings);
        var (blobPath, metaPath) = GetPaths(dir, key);

        // Fast path: read without taking the per-key lock.
        var cached = TryReadCached(blobPath, metaPath, sourceMtime, sourceLength, variant);
        if (cached is not null)
        {
            return cached;
        }

        var keyLock = _keyLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await keyLock.WaitAsync(cancellationToken);
        try
        {
            // Re-check after acquiring the lock in case another caller raced ahead.
            cached = TryReadCached(blobPath, metaPath, sourceMtime, sourceLength, variant);
            if (cached is not null)
            {
                return cached;
            }

            var generated = await ExtractAsync(filePath, sourceMtime, settings, variant, cancellationToken);
            if (generated is null)
            {
                return null;
            }

            try
            {
                Directory.CreateDirectory(dir);
                WriteAtomic(blobPath, generated.Value.Data);
                var meta = new CacheMeta
                {
                    SourceLength = sourceLength,
                    SourceMtimeUtcTicks = sourceMtime.UtcTicks,
                    ContentType = generated.Value.ContentType,
                    Variant = variant,
                };
                WriteAtomic(metaPath, JsonSerializer.SerializeToUtf8Bytes(meta));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
            {
                _logger.LogWarning(ex, "FileCoverCache: failed to persist cover for {FilePath}",
                    LoggingHelper.SanitizePathForLog(filePath));
                // Still return the freshly generated bytes so the request succeeds.
                return generated;
            }

            // Opportunistic prune: every 32 writes check the directory size.
            if (Interlocked.Increment(ref _writesSinceLastPrune) % 32 == 0)
            {
                try { PruneIfOverLimit(dir, settings.FileCoverCacheMaxMegabytes); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException) { _logger.LogDebug(ex, "FileCoverCache: prune failed"); }
            }

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
            var (blobPath, metaPath) = GetPaths(dir, ComputeKey(filePath));
            TryDelete(blobPath);
            TryDelete(metaPath);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "FileCoverCache: invalidate failed for {FilePath}",
                LoggingHelper.SanitizePathForLog(filePath));
        }
        return Task.CompletedTask;
    }

    // ---- internals -------------------------------------------------------

    private async Task<FileCoverCacheEntry?> ExtractAsync(
        string filePath,
        DateTimeOffset sourceMtime,
        AppSettings settings,
        string variant,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var page = await _readerService.GetPageAsync(filePath, 1);
        if (page is null)
        {
            return null;
        }
        var (data, contentType) = page.Value;

        var maxWidth = settings.FileCoverThumbnailMaxWidth;
        if (maxWidth > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var thumb = CoverThumbnailEncoder.TryCreateThumbnail(
                data, maxWidth, settings.FileCoverThumbnailFormat, settings.FileCoverThumbnailQuality);
            if (thumb is not null)
            {
                return new FileCoverCacheEntry(thumb.Value.Data, thumb.Value.ContentType, sourceMtime, variant);
            }
            // Encoding failed (unsupported/corrupt image): serve the original
            // bytes. The entry is still stamped with the current variant so we
            // don't retry the failing encode on every request.
            _logger.LogDebug("FileCoverCache: thumbnail encode failed for {FilePath}; using original bytes",
                LoggingHelper.SanitizePathForLog(filePath));
        }
        return new FileCoverCacheEntry(data, contentType, sourceMtime, variant);
    }

    private FileCoverCacheEntry? TryReadCached(
        string blobPath,
        string metaPath,
        DateTimeOffset sourceMtime,
        long sourceLength,
        string variant)
    {
        try
        {
            if (!File.Exists(blobPath) || !File.Exists(metaPath))
            {
                return null;
            }

            CacheMeta? meta;
            using (var metaStream = File.OpenRead(metaPath))
            {
                meta = JsonSerializer.Deserialize<CacheMeta>(metaStream);
            }
            if (meta is null
                || meta.SourceLength != sourceLength
                || meta.SourceMtimeUtcTicks != sourceMtime.UtcTicks
                || string.IsNullOrEmpty(meta.ContentType)
                || !string.Equals(meta.Variant, variant, StringComparison.Ordinal))
            {
                return null;
            }

            var data = File.ReadAllBytes(blobPath);
            // Best-effort: bump last-access so LRU-style pruning is accurate.
            try { File.SetLastAccessTimeUtc(blobPath, DateTime.UtcNow); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* ignore */ }

            return new FileCoverCacheEntry(data, meta.ContentType, sourceMtime, variant);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogDebug(ex, "FileCoverCache: failed to read cached entry");
            return null;
        }
    }

    private static (string BlobPath, string MetaPath) GetPaths(string dir, string key)
    {
        return (Path.Join(dir, key + ".img"), Path.Join(dir, key + ".meta"));
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

    private void PruneIfOverLimit(string dir, int maxMegabytes)
    {
        if (maxMegabytes <= 0 || !Directory.Exists(dir))
        {
            return;
        }
        var maxBytes = (long)maxMegabytes * 1024L * 1024L;

        // Gather blob entries (the .meta sidecars are small; pair them with the blob).
        var blobs = Directory.EnumerateFiles(dir, "*.img")
            .Select(p =>
            {
                var fi = new FileInfo(p);
                return new { Path = p, fi.Length, LastAccess = SafeAccessTime(fi) };
            })
            .ToList();

        var total = blobs.Sum(b => b.Length);
        if (total <= maxBytes)
        {
            return;
        }

        foreach (var entry in blobs.OrderBy(b => b.LastAccess))
        {
            if (total <= maxBytes) break;
            TryDelete(entry.Path);
            TryDelete(Path.ChangeExtension(entry.Path, ".meta"));
            total -= entry.Length;
        }
        _logger.LogDebug("FileCoverCache: pruned cache to under {Mb} MiB", maxMegabytes);
    }

    private static DateTime SafeAccessTime(FileInfo fi)
    {
        try { return fi.LastAccessTimeUtc; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return DateTime.UtcNow; }
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
