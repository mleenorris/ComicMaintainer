using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Core.Utilities;

namespace ComicMaintainer.Tests.Utilities;

public class ProviderPublisherTests
{
    [Theory]
    [InlineData("DC Comics", "DC Comics")]
    [InlineData("  Image \t Comics\n", "Image Comics")]
    [InlineData("Dark\u0001 Horse", "Dark Horse")]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void Publisher_IsCleaned(string? value, string? expected)
        => Assert.Equal(expected, ProviderMetadataText.Publisher(value));

    [Fact]
    public void Publisher_LongerThanTheColumn_IsNoName()
    {
        Assert.NotNull(ProviderMetadataText.Publisher(new string('x', ProviderMetadataText.MaxPublisherLength)));
        Assert.Null(ProviderMetadataText.Publisher(new string('x', ProviderMetadataText.MaxPublisherLength + 1)));
    }

    [Fact]
    public void From_TheRecordsPublisher_IsFilled_AndAloneIsSomethingToFill()
    {
        var fill = ProviderComicInfoFields.From(new SeriesMetadataCacheRecord { Publisher = "DC Comics" });

        Assert.Equal("DC Comics", fill.Publisher);
        Assert.False(fill.IsEmpty);
        Assert.True(ProviderComicInfoFields.From(new SeriesMetadataCacheRecord()).IsEmpty);
    }

    [Fact]
    public void WithoutLocked_LeavesOutALockedSummaryAndPublisher()
    {
        var fill = new ProviderComicInfoFields(Summary: "S", Genre: "G", Publisher: "P");

        Assert.Equal(fill, ComicProcessorService.WithoutLocked(fill, ComicMetadataFieldFlags.None));
        Assert.Equal(fill with { Publisher = null }, ComicProcessorService.WithoutLocked(fill, ComicMetadataFieldFlags.Publisher));
        Assert.Equal(fill with { Summary = null, Publisher = null },
            ComicProcessorService.WithoutLocked(fill, ComicMetadataFieldFlags.Summary | ComicMetadataFieldFlags.Publisher));
    }
}
