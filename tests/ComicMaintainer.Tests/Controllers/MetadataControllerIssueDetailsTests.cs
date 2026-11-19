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

/// <summary>Filling a series' issues from ComicVine, and the story arcs they are part of.</summary>
public class MetadataControllerIssueDetailsTests
{
    private readonly Mock<ISeriesLibraryService> _library = new();
    private readonly Mock<IComicVineIssueDetailsService> _details = new();

    public MetadataControllerIssueDetailsTests()
    {
        _library.Setup(l => l.GetSeriesIssuesForIdsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<string> ids, CancellationToken _) => ids.Contains("batman")
                ? new Dictionary<string, SeriesIssuesResult>
                {
                    ["batman"] = new() { Issues = [new SeriesIssueDto { FilePath = "/c/Batman 001.cbz" }, new SeriesIssueDto { FilePath = "" }] }
                }
                : new Dictionary<string, SeriesIssuesResult>());
    }

    private MetadataController CreateController() =>
        new(
            _library.Object,
            Mock.Of<ISeriesMetadataCacheService>(),
            Mock.Of<ISeriesMetadataRefreshJobService>(),
            Mock.Of<IExternalSeriesMetadataService>(),
            NullLogger<MetadataController>.Instance,
            Mock.Of<IComicProcessorService>(),
            Mock.Of<IFileStoreService>(),
            Mock.Of<ISeriesLanguagePreferenceRetagService>(),
            Mock.Of<IComicVineVolumeSearch>(),
            _details.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

    private static JObject Json(object? value) => JObject.FromObject(value!);

    [Fact]
    public async Task FetchIssueDetails_StartsAJobForTheSeriesFiles()
    {
        var jobId = Guid.NewGuid();
        _details.Setup(d => d.StartJob(It.Is<IReadOnlyCollection<string>>(paths => paths.SequenceEqual(new[] { "/c/Batman 001.cbz" })))).Returns(jobId);

        var body = Json(Assert.IsType<OkObjectResult>((await CreateController().FetchIssueDetails("batman", CancellationToken.None)).Result).Value);

        Assert.Equal(jobId, (Guid)body["job_id"]!);
        Assert.Equal(1, (int)body["total_items"]!);
        Assert.IsType<NotFoundObjectResult>((await CreateController().FetchIssueDetails("nope", CancellationToken.None)).Result);
    }

    [Fact]
    public async Task SeriesStoryArcs_ListsTheArcsAndTheirFiles()
    {
        _details.Setup(d => d.GetStoryArcsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new SeriesStoryArc(7, "I Am Gotham", ["/c/Batman 001.cbz"])]);

        var body = Json(Assert.IsType<OkObjectResult>((await CreateController().SeriesStoryArcs("batman", CancellationToken.None)).Result).Value);

        Assert.Equal("""[{"id":7,"name":"I Am Gotham","file_paths":["/c/Batman 001.cbz"]}]""", body["arcs"]!.ToString(Newtonsoft.Json.Formatting.None));
        Assert.IsType<NotFoundObjectResult>((await CreateController().SeriesStoryArcs("nope", CancellationToken.None)).Result);
    }

    [Fact]
    public async Task SeriesComicVineLinks_ListsEachIssuesPage()
    {
        _details.Setup(d => d.GetIssueLinksAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new IssueComicVineLink("/c/a.cbz", 101, "1", "One", "https://comicvine.gamespot.com/x/4000-101/", 7, "Batman", "https://comicvine.gamespot.com/volume/4050-7/", true)]);

        var body = Json(Assert.IsType<OkObjectResult>((await CreateController().SeriesComicVineLinks("batman", CancellationToken.None)).Result).Value);

        Assert.Equal(
            """[{"file_path":"/c/a.cbz","issue_id":101,"issue_number":"1","issue_name":"One","issue_url":"https://comicvine.gamespot.com/x/4000-101/","volume_id":7,"volume_name":"Batman","volume_url":"https://comicvine.gamespot.com/volume/4050-7/","filled":true}]""",
            body["issues"]!.ToString(Newtonsoft.Json.Formatting.None));
        Assert.IsType<NotFoundObjectResult>((await CreateController().SeriesComicVineLinks("nope", CancellationToken.None)).Result);
    }

    [Fact]
    public async Task SeriesVolumes_ListsEachVolumeWithItsLink()
    {
        _details.Setup(d => d.GetVolumeLinksAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new SeriesVolumeLink(2, 2016, 19, 88888, "All-New X-Men", "Marvel", "https://comicvine.gamespot.com/volume/4050-88888/"), new SeriesVolumeLink(0, 0, 3, null, null, null, null)]);

        var body = Json(Assert.IsType<OkObjectResult>((await CreateController().SeriesVolumes("batman", CancellationToken.None)).Result).Value);

        Assert.Equal(
            """[{"volume_number":2,"start_year":2016,"issues":19,"comicvine_volume_id":88888,"name":"All-New X-Men","publisher":"Marvel","site_url":"https://comicvine.gamespot.com/volume/4050-88888/"},{"volume_number":null,"start_year":null,"issues":3,"comicvine_volume_id":null,"name":null,"publisher":null,"site_url":null}]""",
            body["volumes"]!.ToString(Newtonsoft.Json.Formatting.None));
        Assert.IsType<NotFoundObjectResult>((await CreateController().SeriesVolumes("nope", CancellationToken.None)).Result);
    }

    [Fact]
    public async Task StoryArc_ReturnsItsIssuesInOrder()
    {
        _details.Setup(d => d.GetStoryArcAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(new StoryArcReadingOrder(
            7, "I Am Gotham", "Summary.", "https://comicvine.example/arc",
            [new StoryArcEntry(101, "Part One", "1", "Batman", "/c/Batman 001.cbz"), new StoryArcEntry(102, null, "2", "Batman", null)]));

        var body = Json(Assert.IsType<OkObjectResult>((await CreateController().StoryArc(7, CancellationToken.None)).Result).Value);

        Assert.Equal(("I Am Gotham", "Summary.", "https://comicvine.example/arc"), ((string?)body["name"], (string?)body["summary"], (string?)body["site_url"]));
        Assert.Equal(
            """[{"id":101,"name":"Part One","issue_number":"1","volume":"Batman","file_path":"/c/Batman 001.cbz"},{"id":102,"name":null,"issue_number":"2","volume":"Batman","file_path":null}]""",
            body["issues"]!.ToString(Newtonsoft.Json.Formatting.None));
        Assert.IsType<NotFoundObjectResult>((await CreateController().StoryArc(8, CancellationToken.None)).Result);
    }

    [Fact]
    public async Task StoryArc_ComicVineUnavailable_Returns503()
    {
        _details.Setup(d => d.GetStoryArcAsync(7, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ExternalMetadataUnavailableException("ComicVine", ExternalMetadataFailureKind.RateLimited, "budget used up"));

        var result = Assert.IsType<ObjectResult>((await CreateController().StoryArc(7, CancellationToken.None)).Result);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
    }

    [Theory]
    [InlineData(null, ComicVineIssueDetailsJobHandler.DefaultMaxRequests)]
    [InlineData("", ComicVineIssueDetailsJobHandler.DefaultMaxRequests)]
    [InlineData("""{"maxRequests":40}""", 40)]
    [InlineData("""{"maxRequests":0}""", ComicVineIssueDetailsJobHandler.DefaultMaxRequests)]
    [InlineData("not json", ComicVineIssueDetailsJobHandler.DefaultMaxRequests)]
    public void JobHandler_ReadsItsRequestBudget(string? options, int expected)
    {
        Assert.Equal(expected, ComicVineIssueDetailsJobHandler.ParseMaxRequests(options));
    }

    [Fact]
    public async Task JobHandler_RunsTheLibrary_OrSaysComicVineIsNotConfigured()
    {
        var client = new Mock<IComicVineCatalogClient>();
        var handler = new ComicVineIssueDetailsJobHandler(_details.Object, client.Object);
        _details.Setup(d => d.RunAsync(null, 40, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComicVineDetailsRunSummary(3, 1, 1, 1, 0, 0, 0, 12, false));

        Assert.StartsWith("ComicVine is not configured", await handler.ExecuteAsync(null, CancellationToken.None));
        client.SetupGet(c => c.IsConfigured).Returns(true);
        Assert.Equal(
            "ComicVine issue details: 1 files filled, 1 unchanged, 1 without a ComicVine volume, 0 without a matching issue, 0 skipped, 0 failed; 12 requests.",
            await handler.ExecuteAsync("""{"maxRequests":40}""", CancellationToken.None));
        Assert.Equal((ComicVineIssueDetailsJobHandler.Key, false, 60), (handler.JobKey, handler.Defaults.Enabled, handler.Defaults.IntervalMinutes));
        Assert.False(string.IsNullOrWhiteSpace(handler.DisplayName + handler.Description));
    }
}
