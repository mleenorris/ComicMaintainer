using System.Numerics;
using ImageMagick;

namespace ComicMaintainer.Core.Utilities;

/// <summary>
/// A perceptual fingerprint of a cover (a difference hash): the image shrunk to 9 by 8 grey pixels,
/// one bit per pair of neighbours, set when the left one is brighter. Re-encoding, resizing and
/// small colour changes keep it within a few bits; another cover differs in about half of the 64.
/// It tells the same cover in two places (your file, ComicVine's thumbnail) from another issue's.
/// </summary>
public static class CoverFingerprint
{
    /// <summary>At most this many bits apart, two fingerprints are the same cover.</summary>
    public const int SameCover = 10;

    /// <summary>At least this many bits apart, two fingerprints are different covers.</summary>
    public const int OtherCover = 20;

    /// <summary>The fingerprint of an image; null when it cannot be read as one.</summary>
    public static ulong? Of(byte[]? image)
    {
        if (image is not { Length: > 0 })
        {
            return null;
        }

        try
        {
            using var picture = new MagickImage(image);
            picture.Grayscale();
            picture.Resize(new MagickGeometry(9, 8) { IgnoreAspectRatio = true });
            using var pixels = picture.GetPixels();
            ulong hash = 0;
            var bit = 0;
            for (var y = 0; y < 8; y++)
            {
                for (var x = 0; x < 8; x++)
                {
                    var left = pixels.GetPixel(x, y).GetChannel(0);
                    var right = pixels.GetPixel(x + 1, y).GetChannel(0);
                    if (left > right)
                    {
                        hash |= 1UL << bit;
                    }

                    bit++;
                }
            }

            return hash;
        }
        catch (MagickException)
        {
            return null;
        }
    }

    /// <summary>How many of the 64 bits differ.</summary>
    public static int Distance(ulong a, ulong b) => BitOperations.PopCount(a ^ b);
}
