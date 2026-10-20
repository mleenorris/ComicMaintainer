namespace ComicMaintainer.Core.Interfaces;

/// <summary>
/// Result of a successful <see cref="IFileCoverCacheService.GetOrCreateAsync"/>
/// call: the cover thumbnail bytes (page 1 of the source archive, downscaled
/// and re-encoded according to the <c>FileCoverThumbnail*</c> settings), the
/// associated content type, the underlying file's last-write timestamp at
/// the moment the cache entry was generated, and the thumbnail
/// <paramref name="Variant"/> token (format/width/quality, or <c>orig</c>
/// for untouched page bytes). Timestamp + variant are suitable for
/// ETag/Last-Modified headers because cache entries are invalidated whenever
/// the source file's mtime or length, or the thumbnail settings, change.
/// </summary>
public readonly record struct FileCoverCacheEntry(
    byte[] Data,
    string ContentType,
    DateTimeOffset LastModified,
    string Variant = "orig");

/// <summary>
/// Persistent, on-disk cache of comic-file cover thumbnails (page 1 of each
/// archive, downscaled and re-encoded — WebP by default). Consumers that
/// need full-resolution page bytes must use
/// <see cref="IComicReaderService.GetPageAsync"/> instead. The cache speeds up file-list rendering by avoiding a fresh
/// archive open + image extract on every request. Cached entries
/// self-invalidate when the source file's <c>LastWriteTimeUtc</c> or
/// <c>Length</c> changes.
/// </summary>
public interface IFileCoverCacheService
{
    /// <summary>
    /// Returns the cached cover for <paramref name="filePath"/>, generating
    /// and persisting it on first access. Returns <c>null</c> when the
    /// underlying archive does not exist or has no extractable page.
    /// Concurrent calls for the same file are coalesced so the archive is
    /// only opened once.
    /// </summary>
    Task<FileCoverCacheEntry?> GetOrCreateAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes any cached cover for <paramref name="filePath"/>. Safe to
    /// call even when no entry exists.
    /// </summary>
    Task InvalidateAsync(string filePath, CancellationToken cancellationToken = default);
}
