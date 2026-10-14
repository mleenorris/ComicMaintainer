using ComicMaintainer.Core.Utilities;
using ImageMagick;

namespace ComicMaintainer.Tests.Utilities;

public class SeriesImageDownscalerTests
{
    [Fact]
    public void DownscaleToJpeg_ReducesLargePngUnderCap()
    {
        // A noisy 4000x4000 PNG is far larger than a small byte cap.
        var source = CreateNoisyImage(4000, 4000, asJpeg: false);
        const int maxBytes = 200 * 1024;

        var result = SeriesImageDownscaler.DownscaleToJpeg(source, maxBytes, maxDimension: 1024);

        Assert.True(result.Length <= maxBytes, $"Result {result.Length} exceeded cap {maxBytes}");
        using var img = new MagickImage(result);
        Assert.True(Math.Max(img.Width, img.Height) <= 1024);
    }

    [Fact]
    public void DownscaleToJpeg_PreservesAspectRatio()
    {
        var source = CreateNoisyImage(3000, 1500, asJpeg: true);

        var result = SeriesImageDownscaler.DownscaleToJpeg(source, 500 * 1024, maxDimension: 1000);

        using var img = new MagickImage(result);
        Assert.Equal(1000u, img.Width);
        Assert.Equal(500u, img.Height);
    }

    [Fact]
    public void DownscaleToJpeg_DoesNotUpscaleSmallImage()
    {
        var source = CreateNoisyImage(300, 200, asJpeg: true);

        var result = SeriesImageDownscaler.DownscaleToJpeg(source, 500 * 1024, maxDimension: 2048);

        using var img = new MagickImage(result);
        Assert.Equal(300u, img.Width);
        Assert.Equal(200u, img.Height);
    }

    [Fact]
    public void DownscaleToJpeg_ThrowsOnUndecodableInput()
    {
        var garbage = new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05 };

        Assert.Throws<InvalidOperationException>(() =>
            SeriesImageDownscaler.DownscaleToJpeg(garbage, 100 * 1024, 1024));
    }

    // Build a noisy image so it does not compress trivially, guaranteeing the
    // encoded payload is genuinely large before downscaling.
    private static byte[] CreateNoisyImage(int width, int height, bool asJpeg)
    {
        using var image = new MagickImage(MagickColors.Gray, (uint)width, (uint)height);
        image.AddNoise(NoiseType.Gaussian);
        image.Format = asJpeg ? MagickFormat.Jpeg : MagickFormat.Png;
        image.Quality = 100;
        return image.ToByteArray();
    }
}
