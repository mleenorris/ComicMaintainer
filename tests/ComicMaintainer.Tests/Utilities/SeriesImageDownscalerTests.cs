using ComicMaintainer.Core.Utilities;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

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
        using var img = Image.Load(result);
        Assert.True(Math.Max(img.Width, img.Height) <= 1024);
    }

    [Fact]
    public void DownscaleToJpeg_PreservesAspectRatio()
    {
        var source = CreateNoisyImage(3000, 1500, asJpeg: true);

        var result = SeriesImageDownscaler.DownscaleToJpeg(source, 500 * 1024, maxDimension: 1000);

        using var img = Image.Load(result);
        Assert.Equal(1000, img.Width);
        Assert.Equal(500, img.Height);
    }

    [Fact]
    public void DownscaleToJpeg_DoesNotUpscaleSmallImage()
    {
        var source = CreateNoisyImage(300, 200, asJpeg: true);

        var result = SeriesImageDownscaler.DownscaleToJpeg(source, 500 * 1024, maxDimension: 2048);

        using var img = Image.Load(result);
        Assert.Equal(300, img.Width);
        Assert.Equal(200, img.Height);
    }

    [Fact]
    public void DownscaleToJpeg_ThrowsOnUndecodableInput()
    {
        var garbage = new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05 };

        Assert.Throws<InvalidOperationException>(() =>
            SeriesImageDownscaler.DownscaleToJpeg(garbage, 100 * 1024, 1024));
    }

    // Build an image full of random pixels so it does not compress trivially,
    // guaranteeing the encoded payload is genuinely large before downscaling.
    private static byte[] CreateNoisyImage(int width, int height, bool asJpeg)
    {
        using var image = new Image<Rgba32>(width, height);
        var rng = new Random(1234);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    row[x] = new Rgba32(
                        (byte)rng.Next(256),
                        (byte)rng.Next(256),
                        (byte)rng.Next(256),
                        255);
                }
            }
        });

        using var ms = new MemoryStream();
        if (asJpeg)
        {
            image.Save(ms, new JpegEncoder { Quality = 100 });
        }
        else
        {
            image.Save(ms, new PngEncoder());
        }
        return ms.ToArray();
    }
}
