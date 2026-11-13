namespace ComicMaintainer.Core.Interfaces;

/// <summary>
/// Result of a successful <see cref="IFileCoverCacheService.GetOrCreateAsync"/>
/// call: the cover thumbnail bytes (page 1 of the source archive, downscaled
/// and re-encoded according to the <c>FileCoverThumbnail*</c> settings), the
/// associated content type, the underlying file's last-write timestamp and
/// length at the moment the cache entry was generated, and the thumbnail
/// <paramref name="Variant"/> token (format/width/quality, or <c>orig</c>
/// for untouched page bytes).
/// </summary>
public readonly record struct FileCoverCacheEntry(
    byte[] Data,
    string ContentType,
    DateTimeOffset LastModified,
    long SourceLength,
    string Variant = "orig")
{
    /// <summary>
    /// The validator of the source version this cover was generated from. It
    /// equals what <see cref="IFileCoverCacheService.GetValidator"/> reported
    /// for that version, so an ETag built from either one matches.
    /// </summary>
    public FileCoverValidator Validator => new(SourceLength, LastModified, Variant);
}

/// <summary>
/// What determines the cover <see cref="IFileCoverCacheService"/> serves for a
/// file right now: the source archive's length and last-write time and the
/// current thumbnail <see cref="Variant"/>. A cover is a function of these
/// alone (cache entries are regenerated whenever any of them changes), so
/// they are a sound HTTP validator for ETag/Last-Modified and are obtained
/// from a <c>stat</c> of the source file, without reading the thumbnail.
/// </summary>
public readonly record struct FileCoverValidator(
    long SourceLength,
    DateTimeOffset SourceLastModified,
    string Variant);

/// <summary>Which encoding of a cover is wanted.</summary>
public enum FileCoverFormat
{
    /// <summary>The format the <c>FileCoverThumbnail*</c> settings choose (WebP by default), as the web UI shows it.</summary>
    Configured,

    /// <summary>
    /// JPEG, for OPDS clients, which may not be able to show WebP. Same width and quality as the
    /// configured thumbnail; with thumbnails turned off, the page itself, converted to JPEG when
    /// it is not one already. Kept in the cache beside the configured one, so it is encoded once.
    /// </summary>
    Jpeg,
}

/// <summary>
/// A cover opened for sending: its bytes as a stream the receiver disposes (the cached file
/// itself when the cover was already cached), its content type, and the validator of the
/// source version it was made from.
/// </summary>
public readonly record struct FileCoverStream(Stream Content, string ContentType, FileCoverValidator Validator);

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
    /// Generates and stores the configured cover of <paramref name="filePath"/> ahead of the first
    /// request for it, when the cache is enabled and does not hold the cover of this version of the
    /// file yet. For background warm-up: it does nothing for a cover that is already cached, and
    /// waits while covers someone asked for are being generated.
    /// </summary>
    /// <exception cref="OperationCanceledException">Cancelled.</exception>
    Task WarmUpAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the validator for the cover of <paramref name="filePath"/>, built
    /// from the file's metadata and the current thumbnail settings. Neither the
    /// archive nor the cache directory is read. Returns <c>null</c> when the
    /// file does not exist or cannot be inspected. Use it to answer conditional
    /// requests before calling <see cref="GetOrCreateAsync"/>.
    /// </summary>
    FileCoverValidator? GetValidator(string filePath);

    /// <summary>
    /// <see cref="GetValidator(string)"/> for the cover in <paramref name="format"/>. The
    /// validator's variant names the format, so the ETags of the two encodings differ.
    /// </summary>
    FileCoverValidator? GetValidator(string filePath, FileCoverFormat format);

    /// <summary>
    /// Opens the cover of <paramref name="filePath"/> in <paramref name="format"/> for sending,
    /// generating and persisting it first when it is not cached. A cached cover is streamed
    /// from its file instead of being read into memory. Returns <c>null</c> when the archive
    /// does not exist or has no extractable page. Concurrent calls for the same file and format
    /// are coalesced like <see cref="GetOrCreateAsync"/>.
    /// </summary>
    /// <exception cref="ComicMaintainer.Core.Utilities.ArchiveLimitExceededException">
    /// The archive or its first page is over the archive read limits.
    /// </exception>
    Task<FileCoverStream?> OpenAsync(string filePath, FileCoverFormat format, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the cached cover for <paramref name="filePath"/>, generating
    /// and persisting it on first access. Returns <c>null</c> when the
    /// underlying archive does not exist or has no extractable page.
    /// Concurrent calls for the same file are coalesced so the archive is
    /// only opened once.
    /// </summary>
    /// <exception cref="ComicMaintainer.Core.Utilities.ArchiveLimitExceededException">
    /// The archive or its first page is over the archive read limits.
    /// </exception>
    Task<FileCoverCacheEntry?> GetOrCreateAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes any cached cover for <paramref name="filePath"/>. Safe to
    /// call even when no entry exists.
    /// </summary>
    Task InvalidateAsync(string filePath, CancellationToken cancellationToken = default);
}
