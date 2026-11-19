using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Core.Utilities;
using ImageMagick;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ComicMaintainer.Tests.Utilities;

/// <summary>Cover fingerprints: the same cover in two places, told from another.</summary>
public class CoverFingerprintTests
{
    // A cover that darkens from left to right, and its mirror image, as a reader would encode them.
    private static byte[] Cover(bool darkening, int width, int height, MagickFormat format)
    {
        using var image = new MagickImage("gradient:white-black", (uint)height, (uint)width);
        image.Rotate(darkening ? 270 : 90);
        image.Format = format;
        return image.ToByteArray();
    }

    [Fact]
    public void Of_TheSameCoverResizedAndReencoded_IsTheSame_AndAnotherDiffers()
    {
        var large = CoverFingerprint.Of(Cover(darkening: true, 600, 900, MagickFormat.Png))!.Value;
        var thumbnail = CoverFingerprint.Of(Cover(darkening: true, 100, 150, MagickFormat.Jpeg))!.Value;
        var other = CoverFingerprint.Of(Cover(darkening: false, 600, 900, MagickFormat.Jpeg))!.Value;

        Assert.True(CoverFingerprint.Distance(large, thumbnail) <= CoverFingerprint.SameCover);
        Assert.True(CoverFingerprint.Distance(large, other) >= CoverFingerprint.OtherCover);
    }

    [Fact]
    public void Of_NoImage_IsNull()
    {
        Assert.Null(CoverFingerprint.Of(null));
        Assert.Null(CoverFingerprint.Of([]));
        Assert.Null(CoverFingerprint.Of([1, 2, 3, 4, 5]));
        Assert.Equal(64, CoverFingerprint.Distance(0, ulong.MaxValue));
    }

    [Fact]
    public async Task Service_ReadsTheFilesCover_AndDownloadsComicVinesThroughTheImageStore()
    {
        var covers = new Mock<IFileCoverCacheService>();
        var images = new Mock<ISeriesImageStore>();
        var cover = Cover(darkening: true, 200, 300, MagickFormat.Jpeg);
        covers.Setup(c => c.GetOrCreateAsync("/c/a.cbz", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FileCoverCacheEntry(cover, "image/jpeg", DateTimeOffset.UtcNow, 1));
        covers.Setup(c => c.GetOrCreateAsync("/c/broken.cbz", It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidDataException("bad archive"));
        images.Setup(i => i.FetchImageAsync("https://comicvine.example/c.jpg", It.IsAny<CancellationToken>())).ReturnsAsync((cover, "image/jpeg"));
        images.Setup(i => i.FetchImageAsync("https://comicvine.example/gone.jpg", It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("404"));
        var service = new CoverFingerprintService(covers.Object, images.Object, NullLogger<CoverFingerprintService>.Instance);

        var file = await service.OfFileAsync("/c/a.cbz");
        Assert.NotNull(file);
        Assert.Equal(file, await service.OfImageAsync("https://comicvine.example/c.jpg"));
        Assert.Null(await service.OfFileAsync("/c/missing.cbz"));
        Assert.Null(await service.OfFileAsync("/c/broken.cbz"));
        Assert.Null(await service.OfImageAsync("https://comicvine.example/gone.jpg"));
    }
}
