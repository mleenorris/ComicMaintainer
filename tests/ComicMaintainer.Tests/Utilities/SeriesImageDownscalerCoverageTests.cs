using System.Buffers.Binary;
using System.Text;
using ComicMaintainer.Core.Utilities;
using ImageMagick;

namespace ComicMaintainer.Tests.Utilities;

public class SeriesImageDownscalerCoverageTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void DownscaleToJpeg_NonPositiveMaxBytes_Throws(int maxBytes)
    {
        var png = CreateImage(8, 8);

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            SeriesImageDownscaler.DownscaleToJpeg(png, maxBytes, 100));
        Assert.Equal("maxBytes", ex.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void DownscaleToJpeg_NonPositiveMaxDimension_Throws(int maxDimension)
    {
        var png = CreateImage(8, 8);

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            SeriesImageDownscaler.DownscaleToJpeg(png, 1024, maxDimension));
        Assert.Equal("maxDimension", ex.ParamName);
    }

    [Fact]
    public void DownscaleToJpeg_DecompressionBombHeader_ThrowsWithoutDecoding()
    {
        var bomb = PngFixture.WithDimensions(CreateImage(8, 8), 20000, 10000);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            SeriesImageDownscaler.DownscaleToJpeg(bomb, 1024 * 1024, 1024));
        Assert.Contains("exceed the decode limit", ex.Message);
        Assert.Null(ex.InnerException);
    }

    [Fact]
    public void DownscaleToJpeg_ReadableHeaderButCorruptPixelData_ThrowsDecodeError()
    {
        var corrupt = PngFixture.WithCorruptIdatCrc(CreateImage(16, 16));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            SeriesImageDownscaler.DownscaleToJpeg(corrupt, 1024 * 1024, 1024));
        Assert.Contains("Could not decode", ex.Message);
        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public void DownscaleToJpeg_CapTooSmallForTinyImage_StopsWhenItCannotShrinkFurther()
    {
        var png = CreateImage(2, 2);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            SeriesImageDownscaler.DownscaleToJpeg(png, 1, 2));
        Assert.Contains("Could not downscale", ex.Message);
    }

    [Fact]
    public void DownscaleToJpeg_CapTooSmall_GivesUpAfterAllShrinkAttempts()
    {
        var png = CreateImage(64, 64);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            SeriesImageDownscaler.DownscaleToJpeg(png, 1, 64));
        Assert.Contains("under 1 bytes", ex.Message);
    }

    [Fact]
    public void DownscaleToJpeg_FitsAfterShrinking_ReturnsSmallerJpeg()
    {
        // A noisy image cannot fit a tight budget at the source size, forcing at
        // least one shrink pass before an encode fits.
        var source = CreateNoisyImage(400, 400);
        var oneShot = EncodeJpeg(source, 45);
        var maxBytes = oneShot / 3;

        var result = SeriesImageDownscaler.DownscaleToJpeg(source, maxBytes, 400);

        Assert.True(result.Length <= maxBytes);
        using var decoded = new MagickImage(result);
        Assert.Equal(MagickFormat.Jpeg, decoded.Format);
        Assert.True(decoded.Width < 400);
    }

    private static byte[] CreateImage(uint width, uint height)
    {
        using var image = new MagickImage(MagickColors.SteelBlue, width, height);
        image.Format = MagickFormat.Png;
        return image.ToByteArray();
    }

    private static byte[] CreateNoisyImage(uint width, uint height)
    {
        using var image = new MagickImage(MagickColors.Gray, width, height);
        image.AddNoise(NoiseType.Gaussian);
        image.Format = MagickFormat.Png;
        return image.ToByteArray();
    }

    private static int EncodeJpeg(byte[] source, uint quality)
    {
        using var image = new MagickImage(source);
        image.Format = MagickFormat.Jpeg;
        image.Quality = quality;
        return image.ToByteArray().Length;
    }
}

file static class PngFixture
{
    public static byte[] WithDimensions(byte[] png, uint width, uint height)
    {
        var patched = (byte[])png.Clone();
        BinaryPrimitives.WriteUInt32BigEndian(patched.AsSpan(16, 4), width);
        BinaryPrimitives.WriteUInt32BigEndian(patched.AsSpan(20, 4), height);
        BinaryPrimitives.WriteUInt32BigEndian(patched.AsSpan(29, 4), Crc32(patched.AsSpan(12, 17)));
        return patched;
    }

    // Flips the stored CRC of the first IDAT chunk. The header (IHDR) stays
    // valid, so the image can be pinged, but a full decode fails.
    public static byte[] WithCorruptIdatCrc(byte[] png)
    {
        var patched = (byte[])png.Clone();
        var offset = 8;
        while (offset + 8 <= patched.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(patched.AsSpan(offset, 4));
            if (Encoding.ASCII.GetString(patched, offset + 4, 4) == "IDAT")
            {
                var crcOffset = offset + 8 + length;
                for (var i = 0; i < 4; i++)
                {
                    patched[crcOffset + i] ^= 0xFF;
                }
                return patched;
            }
            offset += 12 + length;
        }
        throw new InvalidOperationException("PNG has no IDAT chunk");
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }
        return ~crc;
    }
}
