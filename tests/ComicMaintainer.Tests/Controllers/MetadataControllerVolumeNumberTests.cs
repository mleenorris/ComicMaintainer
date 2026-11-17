using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using ComicMaintainer.WebApi.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json.Linq;

namespace ComicMaintainer.Tests.Controllers;

/// <summary>GET /api/metadata/volume-number: the number ComicVine's volumes suggest for a start year.</summary>
public class MetadataControllerVolumeNumberTests
{
    private readonly Mock<IComicVineVolumeSearch> _search = new();

    private MetadataController CreateController(IComicVineVolumeSearch search) =>
        new(
            Mock.Of<ISeriesLibraryService>(),
            Mock.Of<ISeriesMetadataCacheService>(),
            Mock.Of<ISeriesMetadataRefreshJobService>(),
            Mock.Of<IExternalSeriesMetadataService>(),
            NullLogger<MetadataController>.Instance,
            Mock.Of<IComicProcessorService>(),
            Mock.Of<IFileStoreService>(),
            Mock.Of<ISeriesLanguagePreferenceRetagService>(),
            search,
            Mock.Of<IComicVineIssueDetailsService>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

    private static ExternalSeriesMetadata Volume(string name, int year, int issues, string publisher = "DC Comics")
        => new() { CanonicalTitle = name, StartYear = year, TotalChapters = issues, Publisher = publisher, SiteUrl = $"https://comicvine.example/{year}" };

    private static JObject Json(object? value) => JObject.FromObject(value!);

    [Fact]
    public async Task VolumeNumber_ReturnsTheNumberAndTheVolumesCounted()
    {
        _search.Setup(s => s.SearchVolumesAsync("Nightwing", It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            Volume("Nightwing", 2016, 120),
            Volume("Nightwing", 1995, 4),
            Volume("Nightwing", 1996, 153),
            Volume("Nightwing", 2011, 30),
        ]);

        var result = await CreateController(_search.Object).VolumeNumber("Nightwing", 2016);

        var body = Json(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(4, (int?)body["number"]);
        Assert.Equal("DC Comics", (string?)body["publisher"]);
        var volumes = body["volumes"]!.ToArray();
        Assert.Equal(new int?[] { 1995, 1996, 2011, 2016 }, volumes.Select(v => (int?)v["start_year"]));
        Assert.Equal(new[] { false, false, false, true }, volumes.Select(v => (bool)v["matched"]!));
        Assert.Equal(new[] { true, true, true, true }, volumes.Select(v => (bool)v["counted"]!));
        Assert.Equal("Nightwing", (string?)volumes[0]["name"]);
        Assert.Equal(4, (int?)volumes[0]["issues"]);
        Assert.Equal("https://comicvine.example/1995", (string?)volumes[0]["site_url"]);
    }

    [Fact]
    public async Task VolumeNumber_ListsAOneShotUncounted()
    {
        _search.Setup(s => s.SearchVolumesAsync("Robin", It.IsAny<CancellationToken>())).ReturnsAsync(
            [Volume("Robin", 2021, 18), Volume("Robin", 1993, 185), Volume("Robin", 2000, 1)]);

        var result = await CreateController(_search.Object).VolumeNumber("Robin", 2021);

        var body = Json(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(2, (int?)body["number"]);
        var volumes = body["volumes"]!.ToArray();
        Assert.Equal(new int?[] { 1993, 2000, 2021 }, volumes.Select(v => (int?)v["start_year"]));
        Assert.Equal(new[] { true, false, true }, volumes.Select(v => (bool)v["counted"]!));
    }

    [Fact]
    public async Task VolumeNumber_NoVolumeStartedThatYear_ReturnsNoNumber()
    {
        _search.Setup(s => s.SearchVolumesAsync("Saga", It.IsAny<CancellationToken>())).ReturnsAsync([Volume("Saga", 2012, 66)]);

        var result = await CreateController(_search.Object).VolumeNumber("Saga", 2020);

        var body = Json(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(JTokenType.Null, body["number"]!.Type);
        Assert.Empty(body["volumes"]!);
    }

    [Fact]
    public async Task VolumeNumber_ComicVineNotConfigured_ReturnsNoNumber()
    {
        // SearchVolumesAsync answers no volumes without an API key.
        _search.Setup(s => s.SearchVolumesAsync("Saga", It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var result = await CreateController(_search.Object).VolumeNumber("Saga", 2012);

        Assert.Equal(JTokenType.Null, Json(Assert.IsType<OkObjectResult>(result.Result).Value)["number"]!.Type);
    }

    [Theory]
    [InlineData(null, 2016)]
    [InlineData(" ", 2016)]
    [InlineData("Batman", 0)]
    [InlineData("Batman", 2200)]
    public async Task VolumeNumber_MissingTitleOrImplausibleYear_IsABadRequest(string? title, int year)
    {
        var result = await CreateController(_search.Object).VolumeNumber(title, year);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        _search.Verify(s => s.SearchVolumesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task VolumeNumber_ComicVineUnavailable_Returns503()
    {
        _search.Setup(s => s.SearchVolumesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ExternalMetadataUnavailableException("ComicVine", ExternalMetadataFailureKind.RateLimited, "ComicVine budget used up"));

        var result = await CreateController(_search.Object).VolumeNumber("Batman", 2016);

        var obj = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, obj.StatusCode);
        Assert.Equal(new[] { "ComicVine" }, Json(obj.Value)["providers"]!.Select(p => (string?)p));
    }
}
