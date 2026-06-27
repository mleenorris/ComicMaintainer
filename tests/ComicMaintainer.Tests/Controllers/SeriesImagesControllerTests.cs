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
    private readonly Mock<IComicProcessorService> _processor = new();
    private readonly SeriesImagesController _controller;

    public SeriesImagesControllerTests()
    {
        _controller = new SeriesImagesController(
            _cache.Object,
            _library.Object,
            _imageStore.Object,
            _externalMetadata.Object,
            _processor.Object,
            new Mock<ILogger<SeriesImagesController>>().Object);
    }

    private static object? GetProp(object value, string name) =>
        value.GetType().GetProperty(name)!.GetValue(value);

    private void SetupLibrary(params SeriesLibraryDto[] series)
    {
        _library.Setup(l => l.GetSeriesAsync(
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), -1,
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesLibraryResult { Series = series.ToList() });
    }

    private Guid SetupJobCapture(out Func<List<string>?> getTrackedItems)
    {
        var jobId = Guid.NewGuid();
        List<string>? trackedItems = null;
        _processor.Setup(p => p.RunCustomBatchJobAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<Func<string, CancellationToken, Task<bool>>>(),
                It.IsAny<string>(),
                It.IsAny<Func<CancellationToken, Task>?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IEnumerable<string>, Func<string, CancellationToken, Task<bool>>, string, Func<CancellationToken, Task>?, CancellationToken>(
                (_, items, _, _, _, _) => trackedItems = items.ToList())
            .ReturnsAsync(jobId);
        getTrackedItems = () => trackedItems;
        return jobId;
    }

    [Fact]
    public async Task ApplyCurrentToSelected_RejectsEmptyRequest()
    {
        var result = await _controller.ApplyCurrentToSelected(new SeriesImagesController.ApplyCurrentSelectedRequest(), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task ApplyCurrentToSelected_QueuesJobForSeriesWithCachedImage()
    {
        SetupLibrary(
            new SeriesLibraryDto { Title = "Batman", CanonicalTitle = "Batman", HasExternalImage = true },
            new SeriesLibraryDto { Title = "Superman", CanonicalTitle = "Superman", HasExternalImage = false });
        var jobId = SetupJobCapture(out var getTrackedItems);

        var result = await _controller.ApplyCurrentToSelected(
            new SeriesImagesController.ApplyCurrentSelectedRequest
            {
                Series = new List<string> { "Batman", "Superman" }
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(jobId.ToString(), GetProp(ok.Value!, "job_id"));
        Assert.Equal(1, GetProp(ok.Value!, "total_items"));
        Assert.Equal(2, GetProp(ok.Value!, "totalSeries"));
        Assert.Equal(1, GetProp(ok.Value!, "skippedSeries"));
        Assert.Equal(new[] { "Batman" }, getTrackedItems());
    }

    [Fact]
    public async Task ApplyCurrentToSelected_NoCachedImages_ReturnsEmptyJob()
    {
        SetupLibrary(
            new SeriesLibraryDto { Title = "Batman", CanonicalTitle = "Batman", HasExternalImage = false });

        var result = await _controller.ApplyCurrentToSelected(
            new SeriesImagesController.ApplyCurrentSelectedRequest
            {
                Series = new List<string> { "Batman" }
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(Guid.Empty.ToString(), GetProp(ok.Value!, "job_id"));
        Assert.Equal(0, GetProp(ok.Value!, "total_items"));
        Assert.Equal(1, GetProp(ok.Value!, "skippedSeries"));
        _processor.Verify(p => p.RunCustomBatchJobAsync(
            It.IsAny<string>(), It.IsAny<IEnumerable<string>>(),
            It.IsAny<Func<string, CancellationToken, Task<bool>>>(), It.IsAny<string>(),
            It.IsAny<Func<CancellationToken, Task>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ApplyCurrentToAll_QueuesJobForSeriesWithCachedImages()
    {
        SetupLibrary(
            new SeriesLibraryDto { Title = "Batman", CanonicalTitle = "Batman", HasExternalImage = true },
            new SeriesLibraryDto { Title = "Superman", CanonicalTitle = "Superman", HasExternalImage = true },
            new SeriesLibraryDto { Title = "Flash", CanonicalTitle = "Flash", HasExternalImage = false });
        var jobId = SetupJobCapture(out var getTrackedItems);

        var result = await _controller.ApplyCurrentToAll(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(jobId.ToString(), GetProp(ok.Value!, "job_id"));
        Assert.Equal(2, GetProp(ok.Value!, "total_items"));
        Assert.Equal(3, GetProp(ok.Value!, "totalSeries"));
        Assert.Equal(1, GetProp(ok.Value!, "skippedSeries"));
        Assert.Equal(new[] { "Batman", "Superman" }, getTrackedItems());
    }
}
