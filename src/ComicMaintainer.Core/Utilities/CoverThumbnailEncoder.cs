using ImageMagick;

namespace ComicMaintainer.Core.Utilities;

/// <summary>
/// Produces small cover thumbnails for the file-list UI: the source image is
/// downscaled to a maximum width (never upscaled) and re-encoded as WebP
/// (default) or JPEG.
/// </summary>
public static class CoverThumbnailEncoder
{
    // Same decompression-bomb guard as SeriesImageDownscaler.
    private const long MaxDecodedPixels = 100L * 1000 * 1000;

    /// <summary>
    /// Returns a normalized format token ("webp" or "jpeg") for a configured
    /// format string. Unknown values fall back to "webp".
    /// </summary>
    public static string NormalizeFormat(string? format)
    {
        var f = (format ?? string.Empty).Trim().ToLowerInvariant();
        return f is "jpeg" or "jpg" ? "jpeg" : "webp";
    }

    /// <summary>Clamps a configured quality to the 1..100 range (default 80).</summary>
    public static int NormalizeQuality(int quality) => quality <= 0 ? 80 : Math.Min(quality, 100);

    /// <summary>
    /// Builds the cache/ETag variant token for the given settings. A
    /// <paramref name="maxWidth"/> of 0 (or less) means "original bytes".
    /// </summary>
    public static string BuildVariant(int maxWidth, string? format, int quality)
    {
        if (maxWidth <= 0)
        {
            return "orig";
        }
        return $"{NormalizeFormat(format)}-w{maxWidth}-q{NormalizeQuality(quality)}";
    }

    /// <summary>
    /// Downscales <paramref name="source"/> so its width is at most
    /// <paramref name="maxWidth"/> (preserving aspect ratio, never upscaling)
    /// and encodes it in <paramref name="format"/>. Returns <c>null</c> when the
    /// image cannot be decoded or encoded, so callers can fall back to the
    /// original bytes.
    /// </summary>
    public static (byte[] Data, string ContentType)? TryCreateThumbnail(
        byte[] source,
        int maxWidth,
        string? format,
        int quality)
    {
        if (source is null || source.Length == 0 || maxWidth <= 0)
        {
            return null;
        }

        try
        {
            var info = new MagickImageInfo(source);
            if (info.Width == 0 || info.Height == 0
                || info.Width > MaxDecodedPixels / info.Height)
            {
                return null;
            }

            using var image = new MagickImage(source);
            image.AutoOrient();
            if (image.Width > (uint)maxWidth)
            {
                var scale = (double)maxWidth / image.Width;
                var height = Math.Max(1u, (uint)Math.Round(image.Height * scale));
                image.Resize((uint)maxWidth, height);
            }
            image.Strip();

            var normalized = NormalizeFormat(format);
            image.Quality = (uint)NormalizeQuality(quality);
            if (normalized == "jpeg")
            {
                if (image.HasAlpha)
                {
                    image.BackgroundColor = MagickColors.White;
                    image.Alpha(AlphaAction.Remove);
                }
                image.Format = MagickFormat.Jpeg;
                return (image.ToByteArray(), "image/jpeg");
            }

            image.Format = MagickFormat.WebP;
            return (image.ToByteArray(), "image/webp");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }
}
