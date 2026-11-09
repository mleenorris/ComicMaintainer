using ComicMaintainer.Core.Utilities;

namespace ComicMaintainer.WebApi.Opds;

/// <summary>The media types of the OPDS catalog and of the files it links to.</summary>
internal static class OpdsMediaTypes
{
    public const string NavigationFeed = "application/atom+xml;profile=opds-catalog;kind=navigation";
    public const string AcquisitionFeed = "application/atom+xml;profile=opds-catalog;kind=acquisition";
    public const string OpenSearchDescription = "application/opensearchdescription+xml";

    public const string Jpeg = "image/jpeg";
    public const string Png = "image/png";
    public const string Gif = "image/gif";

    public const string ComicBookZip = "application/vnd.comicbook+zip";
    public const string ComicBookRar = "application/vnd.comicbook-rar";
    public const string OctetStream = "application/octet-stream";

    /// <summary>The media type of a comic archive, by its extension (<c>.zip</c> and <c>.rar</c> as the comic types they hold).</summary>
    public static string ForArchive(string fileName) => ComicFileExtensions.NormalizeExtension(Path.GetExtension(fileName)) switch
    {
        ".cbz" => ComicBookZip,
        ".cbr" => ComicBookRar,
        _ => OctetStream,
    };

    /// <summary>The OPDS-PSE stream type a page image with this name can be served as unchanged, or null when it cannot.</summary>
    public static string? ForStreamablePage(string pageName) => Path.GetExtension(pageName).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => Jpeg,
        ".png" => Png,
        ".gif" => Gif,
        _ => null,
    };

    /// <summary>
    /// The one type a stream of these pages is served as: the pages' own type when they all have the same type that
    /// OPDS-PSE allows, otherwise JPEG, which pages of any other type are converted to.
    /// </summary>
    public static string ForPageStream(IEnumerable<string> pageNames)
    {
        var types = pageNames.Select(ForStreamablePage).Distinct().ToArray();
        return types is [{ } single] ? single : Jpeg;
    }
}
