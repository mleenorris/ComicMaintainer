using ComicMaintainer.Core.Utilities;

namespace ComicMaintainer.Core.Interfaces;

/// <summary>
/// Service for reading and extracting pages from comic archives
/// </summary>
/// <remarks>
/// The sorted page list of each archive is cached per file version (path,
/// length and last-write time), so counting pages or turning a page does not
/// re-list the archive. Reads are capped by <see cref="ArchiveEntryReader"/>:
/// an archive or entry over those limits raises
/// <see cref="ArchiveLimitExceededException"/>, whose message is safe to show to
/// the user. Any other failure (missing, unsupported or corrupt file) is logged
/// and reported as "no pages": an empty list, a count of 0, or <c>null</c>.
/// </remarks>
public interface IComicReaderService
{
    /// <summary>
    /// Gets the number of pages in a comic archive
    /// </summary>
    /// <exception cref="ArchiveLimitExceededException">The archive lists too many entries.</exception>
    Task<int> GetPageCountAsync(string filePath);

    /// <summary>
    /// Gets a specific page from a comic archive as a byte array, for a caller that keeps or
    /// transforms the image (a cover thumbnail). To send a page as it is, use
    /// <see cref="OpenPageAsync"/>.
    /// </summary>
    /// <param name="filePath">Path to the comic archive</param>
    /// <param name="pageNumber">Page number (1-based index)</param>
    /// <exception cref="ArchiveLimitExceededException">The archive or the page is over the read limits.</exception>
    Task<(byte[] Data, string ContentType)?> GetPageAsync(string filePath, int pageNumber);

    /// <summary>
    /// Opens a specific page of a comic archive for sending. A page stored uncompressed in a ZIP
    /// (the usual JPEG page of a CBZ) comes back as a stream over its bytes in the archive file,
    /// read as it is sent; any other page is decompressed into a pooled buffer first. The caller
    /// owns the stream and must dispose it. Reading stops when
    /// <paramref name="cancellationToken"/> is cancelled, including while a solid RAR is being
    /// decompressed up to the page, and while waiting for another request on the same solid RAR.
    /// </summary>
    /// <param name="filePath">Path to the comic archive</param>
    /// <param name="pageNumber">Page number (1-based index)</param>
    /// <param name="cancellationToken">Cancelled when nobody wants the page any more.</param>
    /// <exception cref="ArchiveLimitExceededException">The archive or the page is over the read limits.</exception>
    /// <exception cref="OperationCanceledException">The request was cancelled.</exception>
    Task<ComicPageStream?> OpenPageAsync(string filePath, int pageNumber, CancellationToken cancellationToken);

    /// <summary>
    /// Gets all page names from a comic archive
    /// </summary>
    /// <exception cref="ArchiveLimitExceededException">The archive lists too many entries.</exception>
    Task<List<string>> GetPageNamesAsync(string filePath);
}

/// <summary>
/// A page ready to send: its bytes, as a seekable stream of known length that the receiver
/// disposes, and its content type.
/// </summary>
public readonly record struct ComicPageStream(Stream Content, string ContentType);
