using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using Microsoft.Extensions.Logging;
using Moq;

namespace ComicMaintainer.Tests.Services;

public class CompositeExternalSeriesMetadataServiceTests
{
    [Fact]
    public async Task LookupSeriesAsync_ReturnsFirstNonNullResult()
    {
        var firstProvider = new Mock<IExternalSeriesMetadataService>();
        firstProvider
            .Setup(provider => provider.LookupSeriesAsync("Batman", default))
            .ReturnsAsync(new ExternalSeriesMetadata { CanonicalTitle = "Batman", Source = "FirstProvider" });

        var secondProvider = new Mock<IExternalSeriesMetadataService>();
        secondProvider
            .Setup(provider => provider.LookupSeriesAsync("Batman", default))
            .ReturnsAsync(new ExternalSeriesMetadata { CanonicalTitle = "Batman", Source = "SecondProvider" });

        var service = new CompositeExternalSeriesMetadataService(
            [firstProvider.Object, secondProvider.Object],
            Mock.Of<ILogger<CompositeExternalSeriesMetadataService>>());

        var result = await service.LookupSeriesAsync("Batman");

        Assert.NotNull(result);
        Assert.Equal("FirstProvider", result!.Source);
        secondProvider.Verify(provider => provider.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task LookupSeriesAsync_FallsBackToNextProvider_WhenFirstReturnsNull()
    {
        var firstProvider = new Mock<IExternalSeriesMetadataService>();
        firstProvider
            .Setup(provider => provider.LookupSeriesAsync("Solo Leveling", default))
            .ReturnsAsync((ExternalSeriesMetadata?)null);

        var secondProvider = new Mock<IExternalSeriesMetadataService>();
        secondProvider
            .Setup(provider => provider.LookupSeriesAsync("Solo Leveling", default))
            .ReturnsAsync(new ExternalSeriesMetadata { CanonicalTitle = "Solo Leveling", Source = "SecondProvider" });

        var service = new CompositeExternalSeriesMetadataService(
            [firstProvider.Object, secondProvider.Object],
            Mock.Of<ILogger<CompositeExternalSeriesMetadataService>>());

        var result = await service.LookupSeriesAsync("Solo Leveling");

        Assert.NotNull(result);
        Assert.Equal("SecondProvider", result!.Source);
    }

    [Fact]
    public async Task LookupSeriesAsync_ReturnsNull_WhenAllProvidersReturnNull()
    {
        var firstProvider = new Mock<IExternalSeriesMetadataService>();
        firstProvider
            .Setup(provider => provider.LookupSeriesAsync(It.IsAny<string>(), default))
            .ReturnsAsync((ExternalSeriesMetadata?)null);

        var secondProvider = new Mock<IExternalSeriesMetadataService>();
        secondProvider
            .Setup(provider => provider.LookupSeriesAsync(It.IsAny<string>(), default))
            .ReturnsAsync((ExternalSeriesMetadata?)null);

        var service = new CompositeExternalSeriesMetadataService(
            [firstProvider.Object, secondProvider.Object],
            Mock.Of<ILogger<CompositeExternalSeriesMetadataService>>());

        var result = await service.LookupSeriesAsync("Unknown Series");

        Assert.Null(result);
    }

    [Fact]
    public async Task LookupSeriesAsync_ContinuesToNextProvider_WhenFirstThrows()
    {
        var firstProvider = new Mock<IExternalSeriesMetadataService>();
        firstProvider
            .Setup(provider => provider.LookupSeriesAsync(It.IsAny<string>(), default))
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        var secondProvider = new Mock<IExternalSeriesMetadataService>();
        secondProvider
            .Setup(provider => provider.LookupSeriesAsync("Berserk", default))
            .ReturnsAsync(new ExternalSeriesMetadata { CanonicalTitle = "Berserk", Source = "SecondProvider" });

        var service = new CompositeExternalSeriesMetadataService(
            [firstProvider.Object, secondProvider.Object],
            Mock.Of<ILogger<CompositeExternalSeriesMetadataService>>());

        var result = await service.LookupSeriesAsync("Berserk");

        Assert.NotNull(result);
        Assert.Equal("Berserk", result!.CanonicalTitle);
        Assert.Equal("SecondProvider", result.Source);
    }

    [Fact]
    public async Task LookupSeriesAsync_ReturnsNull_WhenNoProvidersRegistered()
    {
        var service = new CompositeExternalSeriesMetadataService(
            [],
            Mock.Of<ILogger<CompositeExternalSeriesMetadataService>>());

        var result = await service.LookupSeriesAsync("Batman");

        Assert.Null(result);
    }
}
