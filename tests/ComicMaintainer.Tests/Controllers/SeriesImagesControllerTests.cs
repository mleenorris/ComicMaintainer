using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.WebApi.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;

namespace ComicMaintainer.Tests.Controllers;

public class SeriesImagesControllerTests
{
    private readonly Mock<ISeriesMetadataCacheService> _cache = new();
    private readonly Mock<ISeriesLibraryService> _library = new();
    private readonly Mock<ISeriesImageStore> _imageStore = new();
    private readonly Mock<IExternalSeriesMetadataService> _externalMetadata = new();
    private readonly SeriesImagesController _controller;

    public SeriesImagesControllerTests()
    {
        _controller = new SeriesImagesController(
            _cache.Object,
            _library.Object,
            _imageStore.Object,
            _externalMetadata.Object,
            new Mock<ILogger<SeriesImagesController>>().Object);
    }

    [Fact]
    public async Task ApplyCurrentToSelected_RejectsEmptyRequest()
    {
        var result = await _controller.ApplyCurrentToSelected(new SeriesImagesController.ApplyCurrentSelectedRequest(), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task ApplyCurrentToSelected_ReturnsUpdatedAndSkippedCounts()
    {
        _cache.Setup(c => c.ReapplyImageArtifactsAsync("Batman", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _cache.Setup(c => c.ReapplyImageArtifactsAsync("Superman", It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var result = await _controller.ApplyCurrentToSelected(
            new SeriesImagesController.ApplyCurrentSelectedRequest
            {
                Series = new List<string> { "Batman", "Superman" }
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(2, ok.Value!.GetType().GetProperty("totalSeries")!.GetValue(ok.Value));
        Assert.Equal(1, ok.Value.GetType().GetProperty("updatedSeries")!.GetValue(ok.Value));
        Assert.Equal(1, ok.Value.GetType().GetProperty("skippedSeries")!.GetValue(ok.Value));
    }

    [Fact]
    public async Task ApplyCurrentToAll_UsesLibrarySeriesTitles()
    {
        _library.Setup(l => l.GetSeriesAsync(null, null, 1, -1, "name", "asc", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesLibraryResult
            {
                Series = new List<SeriesLibraryDto>
                {
                    new() { Title = "Batman", CanonicalTitle = "Batman" },
                    new() { Title = "Superman", CanonicalTitle = "Superman" }
                }
            });
        _cache.Setup(c => c.ReapplyImageArtifactsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await _controller.ApplyCurrentToAll(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(2, ok.Value!.GetType().GetProperty("totalSeries")!.GetValue(ok.Value));
        Assert.Equal(2, ok.Value.GetType().GetProperty("updatedSeries")!.GetValue(ok.Value));
        _cache.Verify(c => c.ReapplyImageArtifactsAsync("Batman", It.IsAny<CancellationToken>()), Times.Once);
        _cache.Verify(c => c.ReapplyImageArtifactsAsync("Superman", It.IsAny<CancellationToken>()), Times.Once);
    }
}
