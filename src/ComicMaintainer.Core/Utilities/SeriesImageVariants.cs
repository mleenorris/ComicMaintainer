using System.Globalization;

namespace ComicMaintainer.Core.Utilities;

/// <summary>
/// The rules for serving a stored series image: the widths it can be asked for, how a downscaled
/// copy is named and versioned, and the content hash in the stored file's name that versions its
/// URL.
/// </summary>
/// <remarks>
/// Stored images are named <c>&lt;key&gt;-&lt;16 hex digits of SHA-256&gt;.&lt;ext&gt;</c> (see
/// <c>SeriesImageStore</c>), so a stored file never changes under the same name. The library puts
/// that hash in the image URL as <c>v</c>, and a response for the current <c>v</c> can be cached
/// for as long as a browser likes. A card asks for <c>w</c>, one of <see cref="Widths"/>, and gets
/// a copy at most that wide, made once and stored beside the original.
/// </remarks>
public static class SeriesImageVariants
{
    /// <summary>The widths a series image can be asked for, in pixels.</summary>
    public static readonly IReadOnlyList<int> Widths = [160, 320, 480, 640];

    /// <summary>
    /// Version of how downscaled copies are made (format, quality, resampling). It is part of their
    /// file names and ETags: bump it when that changes, so stored copies and browser caches renew.
    /// </summary>
    public const int Version = 1;

    /// <summary>The content type of a downscaled copy.</summary>
    public const string ContentType = "image/webp";

    /// <summary>The quality a downscaled copy is encoded at.</summary>
    public const int Quality = 80;

    /// <summary>True when <paramref name="width"/> is one of <see cref="Widths"/>.</summary>
    public static bool IsAllowedWidth(int width) => Widths.Contains(width);

    /// <summary>
    /// The file name of the copy of <paramref name="fileName"/> at most <paramref name="width"/>
    /// pixels wide, beside it in the same folder.
    /// </summary>
    public static string ResizedFileName(string fileName, int width) =>
        string.Create(CultureInfo.InvariantCulture, $"{Path.GetFileNameWithoutExtension(fileName)}.w{width}v{Version}.webp");

    /// <summary>
    /// The content hash in a stored image's name (the 16 hex digits before the extension), or
    /// <c>null</c> for a name without one.
    /// </summary>
    public static string? ContentVersionOf(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return null;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var dash = stem.LastIndexOf('-');
        if (dash < 0 || stem.Length - dash - 1 != 16)
        {
            return null;
        }

        var hash = stem[(dash + 1)..];
        return hash.All(char.IsAsciiHexDigitLower) ? hash : null;
    }
}
