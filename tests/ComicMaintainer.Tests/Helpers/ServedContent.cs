using ComicMaintainer.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace ComicMaintainer.Tests.Helpers;

/// <summary>
/// Builds the streams that page and cover services hand to controllers, and reads back what a
/// controller sent, for tests of controllers that stream pages and covers.
/// </summary>
internal static class ServedContent
{
    /// <summary>A page as <see cref="IComicReaderService.OpenPageAsync"/> returns it.</summary>
    public static ComicPageStream? Page(byte[] data, string contentType) =>
        new ComicPageStream(new MemoryStream(data, writable: false), contentType);

    /// <summary>A cover as <see cref="IFileCoverCacheService.OpenAsync"/> returns it.</summary>
    public static FileCoverStream? Cover(FileCoverCacheEntry entry) =>
        new FileCoverStream(new MemoryStream(entry.Data, writable: false), entry.ContentType, entry.Validator);

    /// <summary>The bytes a file result sends.</summary>
    public static byte[] Bytes(FileStreamResult result)
    {
        using var copy = new MemoryStream();
        result.FileStream.CopyTo(copy);
        return copy.ToArray();
    }
}
