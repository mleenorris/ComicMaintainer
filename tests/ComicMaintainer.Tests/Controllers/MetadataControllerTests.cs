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

    [Fact]
    public async Task RefreshOne_QueueMode_StartsBackgroundJob()
    {
        var jobId = Guid.NewGuid();
        _refreshJobs.Setup(j => j.StartAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(jobId);

        var result = await _controller.RefreshOne("Batman", queue: true, CancellationToken.None);

        var accepted = Assert.IsType<AcceptedResult>(result.Result);
        var idProp = accepted.Value!.GetType().GetProperty("jobId");
        Assert.Equal(jobId, idProp!.GetValue(accepted.Value));
        _cache.Verify(c => c.RefreshAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RefreshFolder_ResolvesSeriesIdToTitlesAndQueuesJob()
    {
        _library.Setup(l => l.GetTitlesForSeriesIdAsync("batman", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "Batman", "Dark Knight" });
        var jobId = Guid.NewGuid();
        _refreshJobs.Setup(j => j.StartAsync(It.Is<IEnumerable<string>>(t => t.Contains("Batman") && t.Contains("Dark Knight")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(jobId);

        var result = await _controller.RefreshFolder(
            new MetadataController.RefreshFolderRequest { SeriesId = "batman" },
            CancellationToken.None);

        var accepted = Assert.IsType<AcceptedResult>(result.Result);
        var totalProp = accepted.Value!.GetType().GetProperty("totalSeries");
        Assert.Equal(2, totalProp!.GetValue(accepted.Value));
    }

    [Fact]
    public async Task RefreshFolder_ReturnsNotFound_WhenNothingResolves()
    {
        _library.Setup(l => l.GetTitlesForSeriesIdAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());

        var result = await _controller.RefreshFolder(
            new MetadataController.RefreshFolderRequest { SeriesId = "missing" },
            CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task RefreshFolder_RejectsEmptyRequest()
    {
        var result = await _controller.RefreshFolder(new MetadataController.RefreshFolderRequest(), CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetProviders_ReturnsHealthSnapshot()
    {
        _external.Setup(e => e.CheckHealthAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderHealth
            {
                Name = "ComicVine",
                Enabled = true,
                Configured = true,
                Reachable = true,
                StatusMessage = "Reachable"
            });

        var result = await _controller.GetProviders(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var providersProp = ok.Value!.GetType().GetProperty("providers")!.GetValue(ok.Value);
        var providers = Assert.IsAssignableFrom<IEnumerable<ProviderHealth>>(providersProp!);
        var single = Assert.Single(providers);
        Assert.Equal("ComicVine", single.Name);
        Assert.True(single.Reachable);
    }
}
