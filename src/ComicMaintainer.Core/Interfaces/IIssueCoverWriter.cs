namespace ComicMaintainer.Core.Interfaces;

/// <summary>What replacing an archive's cover did.</summary>
public enum IssueCoverWriteOutcome
{
    /// <summary>Page 1 now holds the new cover.</summary>
    Replaced,

    /// <summary>Page 1 already held exactly this image; the archive was not rewritten.</summary>
    Unchanged,

    /// <summary>Only a CBZ can be rewritten; a CBR, RAR or ZIP is left as it is.</summary>
    NotWritable,

    /// <summary>The archive has no image to replace.</summary>
    NoPages,

    /// <summary>The image is not a JPEG, PNG or WebP.</summary>
    UnsupportedImage,

    /// <summary>The archive is gone.</summary>
    NotFound,
}

/// <summary>
/// Replaces the cover of a comic archive: its first page, as the reader and every cover
/// thumbnail take it (pages in natural name order).
/// </summary>
public interface IIssueCoverWriter
{
    /// <summary>
    /// Replaces page 1 of the CBZ at <paramref name="archivePath"/> with <paramref name="image"/>.
    /// The page keeps its name, with the extension of the new image, so it stays first; every
    /// other entry is kept as it is.
    /// </summary>
    Task<IssueCoverWriteOutcome> ReplaceFirstPageAsync(
        string archivePath,
        byte[] image,
        string contentType,
        CancellationToken cancellationToken = default);
}
