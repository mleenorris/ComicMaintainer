using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.WebApi.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;

namespace ComicMaintainer.Tests.Controllers;

public class MetadataControllerTests
{
    private readonly Mock<ISeriesLibraryService> _library = new();
    private readonly Mock<ISeriesMetadataCacheService> _cache = new();
    private readonly Mock<ISeriesMetadataRefreshJobService> _refreshJobs = new();
    private readonly Mock<IExternalSeriesMetadataService> _external = new();
    private readonly MetadataController _controller;

    public MetadataControllerTests()
    {
        _cache.Setup(c => c.NormalizeKey(It.IsAny<string>())).Returns<string>(s => (s ?? string.Empty).ToLowerInvariant());
        _controller = new MetadataController(
            _library.Object,
            _cache.Object,
            _refreshJobs.Object,
            _external.Object,
            new Mock<ILogger<MetadataController>>().Object);
    }

    [Fact]
    public async Task RefreshAll_QueuesJobForEveryLibrarySeries()
    {
        _library.Setup(l => l.GetSeriesAsync(null, null, 1, -1, "name", "asc", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesLibraryResult
            {
                Series = new List<SeriesLibraryDto>
                {
                    new() { CanonicalTitle = "Batman" },
                    new() { CanonicalTitle = "Superman" }
                }
            });

        var jobId = Guid.NewGuid();
        _refreshJobs.Setup(j => j.StartAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(jobId);

        var result = await _controller.RefreshAll(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var idProp = ok.Value!.GetType().GetProperty("jobId");
        var totalProp = ok.Value.GetType().GetProperty("totalSeries");
        Assert.Equal(jobId, idProp!.GetValue(ok.Value));
        Assert.Equal(2, totalProp!.GetValue(ok.Value));
    }

    [Fact]
    public async Task RefreshSelected_RejectsEmptyBody()
    {
        var result = await _controller.RefreshSelected(new MetadataController.RefreshSelectedRequest(), CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task RefreshOne_ReturnsCacheRecord()
    {
        var record = new SeriesMetadataCacheRecord { NormalizedKey = "batman", CanonicalTitle = "Batman", LookupStatus = "success" };
        _cache.Setup(c => c.RefreshAsync("Batman", It.IsAny<CancellationToken>())).ReturnsAsync(record);

        var result = await _controller.RefreshOne("Batman", queue: false, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Same(record, ok.Value);
    }

    [Fact]
    public async Task Search_ReturnsCandidatesFromProvider()
    {
        _external.Setup(e => e.SearchSeriesAsync("Bat", 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new ExternalSeriesMetadata { CanonicalTitle = "Batman", Aliases = new List<string> { "Dark Knight" }, Source = "ComicVine" },
                new ExternalSeriesMetadata { CanonicalTitle = "Batgirl", Source = "ComicVine" }
            });

        var result = await _controller.Search("Bat");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var resultsProp = ok.Value!.GetType().GetProperty("results")!.GetValue(ok.Value);
        var enumerable = Assert.IsAssignableFrom<System.Collections.IEnumerable>(resultsProp!);
        var list = enumerable.Cast<object>().ToList();
        Assert.Equal(2, list.Count);
    }

    [Fact]
    public async Task Search_RejectsEmptyQuery()
    {
        var result = await _controller.Search("");
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task SetAliases_PersistsViaCacheService()
    {
        var record = new SeriesMetadataCacheRecord { NormalizedKey = "batman", CanonicalTitle = "Batman", UserAliases = new List<string> { "Dark Knight" } };
        _cache.Setup(c => c.SetUserAliasesAsync("Batman", It.IsAny<IEnumerable<string>>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(record);

        var result = await _controller.SetAliases(
            "Batman",
            new MetadataController.SetAliasesRequest { Aliases = new List<string> { "Dark Knight" } },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Same(record, ok.Value);
    }

    [Fact]
    public async Task RemoveAlias_DelegatesToCacheService()
    {
        var record = new SeriesMetadataCacheRecord { NormalizedKey = "batman", CanonicalTitle = "Batman" };
        _cache.Setup(c => c.RemoveUserAliasAsync("batman", "Dark Knight", It.IsAny<CancellationToken>()))
            .ReturnsAsync(record);

        var result = await _controller.RemoveAlias("Batman", "Dark Knight", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Same(record, ok.Value);
    }
}
