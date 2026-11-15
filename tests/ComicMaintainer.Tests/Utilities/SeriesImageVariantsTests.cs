using ComicMaintainer.Core.Utilities;

namespace ComicMaintainer.Tests.Utilities;

/// <summary>
/// The rules for serving a stored series image at a card's size: the widths offered, how a downscaled copy is
/// named, and the content hash in a stored name that versions the image's URL.
/// </summary>
public sealed class SeriesImageVariantsTests
{
    [Theory]
    [InlineData(160, true)]
    [InlineData(320, true)]
    [InlineData(480, true)]
    [InlineData(640, true)]
    [InlineData(0, false)]
    [InlineData(100, false)]
    [InlineData(2048, false)]
    [InlineData(-320, false)]
    public void OnlyTheOfferedWidthsAreAllowed(int width, bool allowed) =>
        Assert.Equal(allowed, SeriesImageVariants.IsAllowedWidth(width));

    [Fact]
    public void ADownscaledCopy_IsNamedAfterTheOriginalItsWidthAndTheVersionOfHowCopiesAreMade() =>
        Assert.Equal(
            $"saga-0123456789abcdef.w320v{SeriesImageVariants.Version}.webp",
            SeriesImageVariants.ResizedFileName("saga-0123456789abcdef.jpg", 320));

    [Theory]
    [InlineData("saga-0123456789abcdef.jpg", "0123456789abcdef")]
    [InlineData("the-walking-dead-fedcba9876543210.webp", "fedcba9876543210")]
    [InlineData("saga-0123456789ABCDEF.jpg", null)]
    [InlineData("saga-0123456789abcde.jpg", null)]
    [InlineData("saga-0123456789abcdeg.png", null)]
    [InlineData("batman-abc.png", null)]
    [InlineData("0123456789abcdef.png", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void TheContentVersion_IsTheHashInAStoredName(string? fileName, string? expected) =>
        Assert.Equal(expected, SeriesImageVariants.ContentVersionOf(fileName));
}
