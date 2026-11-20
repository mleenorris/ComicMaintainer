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
}
