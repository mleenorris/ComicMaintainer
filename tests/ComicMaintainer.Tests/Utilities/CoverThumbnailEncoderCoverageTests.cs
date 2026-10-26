using System.Buffers.Binary;
using ComicMaintainer.Core.Utilities;
using ImageMagick;

namespace ComicMaintainer.Tests.Utilities;

public class CoverThumbnailEncoderCoverageTests
{
    [Theory]
    [InlineData(null, "webp")]
    [InlineData("", "webp")]
    [InlineData("webp", "webp")]
    [InlineData("png", "webp")]
    [InlineData("jpg", "jpeg")]
    [InlineData(" JPG ", "jpeg")]
    [InlineData("JPEG", "jpeg")]
    public void NormalizeFormat_MapsKnownTokensAndFallsBackToWebp(string? format, string expected)
    {
        Assert.Equal(expected, CoverThumbnailEncoder.NormalizeFormat(format));
    }

    [Theory]
    [InlineData(0, 80)]
    [InlineData(-5, 80)]
    [InlineData(1, 1)]
    [InlineData(55, 55)]
    [InlineData(100, 100)]
    [InlineData(150, 100)]
    public void NormalizeQuality_ClampsToValidRange(int quality, int expected)
    {
        Assert.Equal(expected, CoverThumbnailEncoder.NormalizeQuality(quality));
    }

    [Theory]
    [InlineData(0, "webp", 80, "orig")]
    [InlineData(-1, "jpeg", 80, "orig")]
    [InlineData(200, "jpg", 0, "jpeg-w200-q80")]
    [InlineData(320, null, 500, "webp-w320-q100")]
    public void BuildVariant_EncodesSettings(int maxWidth, string? format, int quality, string expected)
    {
        Assert.Equal(expected, CoverThumbnailEncoder.BuildVariant(maxWidth, format, quality));
    }

    [Fact]
    public void TryCreateThumbnail_NullSource_ReturnsNull()
    {
        Assert.Null(CoverThumbnailEncoder.TryCreateThumbnail(null!, 100, "webp", 80));
    }

    [Fact]
    public void TryCreateThumbnail_EmptySource_ReturnsNull()
    {
        Assert.Null(CoverThumbnailEncoder.TryCreateThumbnail(Array.Empty<byte>(), 100, "webp", 80));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    public void TryCreateThumbnail_NonPositiveWidth_ReturnsNull(int maxWidth)
    {
        var png = CreatePng(40, 60);

        Assert.Null(CoverThumbnailEncoder.TryCreateThumbnail(png, maxWidth, "webp", 80));
    }

    [Fact]
    public void TryCreateThumbnail_UndecodableBytes_ReturnsNull()
    {
        var garbage = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 };

        Assert.Null(CoverThumbnailEncoder.TryCreateThumbnail(garbage, 100, "jpeg", 80));
    }

    [Fact]
    public void TryCreateThumbnail_DecompressionBombHeader_ReturnsNull()
    {
        // The header declares 20000x10000 (200 MP), well over the decode limit.
        var bomb = PngFixture.WithDimensions(CreatePng(8, 8), 20000, 10000);

        Assert.Null(CoverThumbnailEncoder.TryCreateThumbnail(bomb, 100, "webp", 80));
    }

    [Fact]
    public void TryCreateThumbnail_JpegFromTransparentPng_FlattensAlphaOntoWhite()
    {
        using var source = new MagickImage(MagickColors.Transparent, 64, 32);
        source.Format = MagickFormat.Png;
        var png = source.ToByteArray();

        var result = CoverThumbnailEncoder.TryCreateThumbnail(png, 32, "jpg", 90);

        Assert.NotNull(result);
        Assert.Equal("image/jpeg", result.Value.ContentType);
        using var decoded = new MagickImage(result.Value.Data);
        Assert.Equal(MagickFormat.Jpeg, decoded.Format);
        Assert.Equal(32u, decoded.Width);
        Assert.Equal(16u, decoded.Height);
        Assert.False(decoded.HasAlpha);
        var pixel = decoded.GetPixels().GetPixel(0, 0).ToColor();
        Assert.NotNull(pixel);
        Assert.True(pixel.R > 240 && pixel.G > 240 && pixel.B > 240, $"Expected white background but got {pixel}");
    }

    [Fact]
    public void TryCreateThumbnail_JpegFromOpaqueImage_DoesNotUpscale()
    {
        var png = CreatePng(40, 20);

        var result = CoverThumbnailEncoder.TryCreateThumbnail(png, 400, "jpeg", 0);

        Assert.NotNull(result);
        Assert.Equal("image/jpeg", result.Value.ContentType);
        using var decoded = new MagickImage(result.Value.Data);
        Assert.Equal(40u, decoded.Width);
        Assert.Equal(20u, decoded.Height);
    }

    [Fact]
    public void TryCreateThumbnail_WebpDefault_ResizesPreservingAspect()
    {
        var png = CreatePng(300, 450);

        var result = CoverThumbnailEncoder.TryCreateThumbnail(png, 100, null, 75);

        Assert.NotNull(result);
        Assert.Equal("image/webp", result.Value.ContentType);
        using var decoded = new MagickImage(result.Value.Data);
        Assert.Equal(MagickFormat.WebP, decoded.Format);
        Assert.Equal(100u, decoded.Width);
        Assert.Equal(150u, decoded.Height);
    }

    private static byte[] CreatePng(uint width, uint height)
    {
        using var image = new MagickImage(MagickColors.SteelBlue, width, height);
        image.Format = MagickFormat.Png;
        return image.ToByteArray();
    }
}

file static class PngFixture
{
    // Rewrites the IHDR width/height of a PNG (and its CRC) without touching
    // the pixel data, so only the header advertises the new dimensions.
    public static byte[] WithDimensions(byte[] png, uint width, uint height)
    {
        var patched = (byte[])png.Clone();
        BinaryPrimitives.WriteUInt32BigEndian(patched.AsSpan(16, 4), width);
        BinaryPrimitives.WriteUInt32BigEndian(patched.AsSpan(20, 4), height);
        BinaryPrimitives.WriteUInt32BigEndian(patched.AsSpan(29, 4), Crc32(patched.AsSpan(12, 17)));
        return patched;
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
