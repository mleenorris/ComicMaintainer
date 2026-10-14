using ImageMagick;

namespace ComicMaintainer.Core.Utilities;

/// <summary>
/// Downscales an oversized cover image so it fits within a byte budget. Used
/// when an external series cover source image is larger than the configured
/// per-image cap: rather than rejecting the download (which leaves the cover
/// unpopulated), the image is resized down to a maximum dimension and
/// re-encoded as JPEG, retrying at progressively lower quality/dimensions
/// until the payload fits under the cap.
/// </summary>
public static class SeriesImageDownscaler
{
    // Quality steps tried in order. JPEG re-encode is lossy so this trades a
    // little fidelity for a smaller payload.
    private static readonly int[] QualitySteps = { 85, 75, 65, 55, 45 };

    // Upper bound on decoded pixel count. A small (byte-capped) but pathological
    // source could otherwise declare enormous dimensions and exhaust memory when
    // decoded (a "decompression bomb"). 100 megapixels is far larger than any
    // real cover yet cheap to reject.
    private const long MaxDecodedPixels = 100L * 1000 * 1000;

    /// <summary>
    /// Decode <paramref name="source"/> (jpeg/png/webp), resize so its largest
    /// dimension is at most <paramref name="maxDimension"/> while preserving
    /// aspect ratio, and re-encode as JPEG under <paramref name="maxBytes"/>.
    /// </summary>
    /// <returns>JPEG-encoded bytes guaranteed to be at most
    /// <paramref name="maxBytes"/> in length.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the image cannot
    /// be decoded or cannot be reduced under the cap.</exception>
    public static byte[] DownscaleToJpeg(
        ReadOnlySpan<byte> source,
        int maxBytes,
        int maxDimension)
    {
        if (maxBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        }
        if (maxDimension <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDimension));
        }

        // Cheaply inspect the header dimensions before a full decode so a
        // decompression bomb is rejected without allocating its pixel buffer.
        var sourceBytes = source.ToArray();
        try
        {
            var info = new MagickImageInfo(sourceBytes);
            if (info.Height > 0 && info.Width > MaxDecodedPixels / info.Height)
            {
                throw new InvalidOperationException(
                    $"Source image dimensions ({info.Width}x{info.Height}) exceed the decode limit");
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Could not read the source image header for downscaling", ex);
        }

        MagickImage image;
        try
        {
            image = new MagickImage(sourceBytes);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Could not decode the source image for downscaling", ex);
        }

        using (image)
        {
            // Clamp the target dimension: never upscale beyond the source.
            var largestSide = Math.Max(image.Width, image.Height);
            var targetDimension = Math.Min((uint)maxDimension, largestSide);

            for (var attempt = 0; attempt < QualitySteps.Length + 2; attempt++)
            {
                ResizeInPlace(image, targetDimension);

                foreach (var quality in QualitySteps)
                {
                    var encoded = EncodeJpeg(image, quality);
                    if (encoded.Length <= maxBytes)
                    {
                        return encoded;
                    }
                }

                // Still too big at the lowest quality: shrink further and retry.
                var reduced = Math.Max(1u, (uint)(targetDimension * 0.75));
                if (reduced == targetDimension)
                {
                    break;
                }
                targetDimension = reduced;
            }

            throw new InvalidOperationException(
                $"Could not downscale the image under {maxBytes} bytes");
        }
    }

    private static void ResizeInPlace(MagickImage image, uint maxDimension)
    {
        if (Math.Max(image.Width, image.Height) <= maxDimension)
        {
            return;
        }

        var scale = (double)maxDimension / Math.Max(image.Width, image.Height);
        var width = Math.Max(1u, (uint)Math.Round(image.Width * scale));
        var height = Math.Max(1u, (uint)Math.Round(image.Height * scale));
        image.Resize(width, height);
    }

    private static byte[] EncodeJpeg(MagickImage image, int quality)
    {
        image.Format = MagickFormat.Jpeg;
        image.Quality = (uint)quality;
        return image.ToByteArray();
    }
}
