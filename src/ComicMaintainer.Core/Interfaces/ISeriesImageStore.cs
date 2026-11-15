namespace ComicMaintainer.Core.Interfaces;

/// <summary>
/// Result of a successful image download/upload to the local series-image cache.
/// </summary>
public record SeriesImageStoreResult(string FileName, string ContentType, long SizeBytes);

/// <summary>
/// Persists series-level cover images (downloaded from external metadata
/// providers or uploaded by users) to a local cache directory.
///
/// Implementations are responsible for SSRF protection on remote downloads,
/// content-type / magic-byte validation, size capping, and cleanup of replaced
/// images. Callers should treat all returned filenames as opaque tokens.
/// </summary>
public interface ISeriesImageStore
{
    /// <summary>
    /// Download <paramref name="remoteImageUrl"/> and persist it under a
    /// filename derived from <paramref name="normalizedKey"/> and the content
    /// hash. Returns the stored filename (relative to the cache directory)
    /// and content-type. Throws on validation failure.
    /// </summary>
    /// <param name="normalizedKey">Series cache key (used as filename prefix).</param>
    /// <param name="remoteImageUrl">Public http(s) URL of the image.</param>
    /// <param name="previousFile">Optional previous local filename to delete on success.</param>
    Task<SeriesImageStoreResult> DownloadAsync(
        string normalizedKey,
        string remoteImageUrl,
        string? previousFile,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Persist a user-supplied image. <paramref name="content"/> must be a
    /// valid JPEG / PNG / WEBP smaller than the configured cap.
    /// </summary>
    Task<SeriesImageStoreResult> SaveUserImageAsync(
        string normalizedKey,
        Stream content,
        string declaredContentType,
        string? previousFile,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the absolute path of a stored image (or null when missing or
    /// when the filename escapes the cache directory).
    /// </summary>
    string? ResolveAbsolutePath(string fileName);

    /// <summary>
    /// A copy of a stored image at most <paramref name="width"/> pixels wide (one of
    /// <see cref="Utilities.SeriesImageVariants.Widths"/>), made on first use and kept beside the
    /// original until the original is replaced or deleted. An image that is no wider already is
    /// never upscaled: the original is returned, and so it is when the copy cannot be made.
    /// Returns null when the stored image is missing.
    /// </summary>
    /// <param name="fileName">The stored image, as <see cref="SeriesImageStoreResult.FileName"/> named it.</param>
    /// <param name="contentType">The stored image's content type.</param>
    /// <param name="width">The largest width wanted.</param>
    /// <param name="cancellationToken">Cancels waiting for a turn to make the copy.</param>
    Task<SeriesImageFile?> GetResizedAsync(string fileName, string contentType, int width, CancellationToken cancellationToken = default);

    /// <summary>Delete a stored image by filename, and any downscaled copies of it. Safe to call when missing.</summary>
    void Delete(string? fileName);
}

/// <summary>A stored series image file to send: its absolute path and content type.</summary>
public record SeriesImageFile(string Path, string ContentType);
