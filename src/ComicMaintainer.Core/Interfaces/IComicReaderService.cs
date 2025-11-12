namespace ComicMaintainer.Core.Interfaces;

/// <summary>
/// Service for reading and extracting pages from comic archives
/// </summary>
public interface IComicReaderService
{
    /// <summary>
    /// Gets the number of pages in a comic archive
    /// </summary>
    Task<int> GetPageCountAsync(string filePath);

    /// <summary>
    /// Gets a specific page from a comic archive as a byte array
    /// </summary>
    /// <param name="filePath">Path to the comic archive</param>
    /// <param name="pageNumber">Page number (1-based index)</param>
    Task<(byte[] Data, string ContentType)?> GetPageAsync(string filePath, int pageNumber);

    /// <summary>
    /// Gets all page names from a comic archive
    /// </summary>
    Task<List<string>> GetPageNamesAsync(string filePath);
}
