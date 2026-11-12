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
    /// Gets a specific page from a comic archive as a byte array
    /// </summary>
    /// <param name="filePath">Path to the comic archive</param>
    /// <param name="pageNumber">Page number (1-based index)</param>
    /// <exception cref="ArchiveLimitExceededException">The archive or the page is over the read limits.</exception>
    Task<(byte[] Data, string ContentType)?> GetPageAsync(string filePath, int pageNumber);

    /// <summary>
    /// Gets all page names from a comic archive
    /// </summary>
    /// <exception cref="ArchiveLimitExceededException">The archive lists too many entries.</exception>
    Task<List<string>> GetPageNamesAsync(string filePath);
}
