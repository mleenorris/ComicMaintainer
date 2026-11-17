using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using ComicMaintainer.WebApi.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Newtonsoft.Json.Linq;

namespace ComicMaintainer.Tests.Controllers;

/// <summary>GET /api/covers/candidates and POST /api/covers/replace.</summary>
public class CoversControllerTests
{
    private readonly Mock<IComicVineIssueDetailsService> _details = new();
    private readonly Mock<IIssueCoverService> _covers = new();

    private CoversController CreateController() => new(_details.Object, _covers.Object)
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
    };

    private static JObject Json(object? value) => JObject.FromObject(value!);

    [Fact]
    public async Task Candidates_ListsTheCovers_WithTheMatchedOneMarked()
    {
        _details.Setup(d => d.GetCoverCandidatesAsync("/c/a.cbz", It.IsAny<CancellationToken>())).ReturnsAsync(new CoverCandidates(
            91273, "Batman", 102, null,
            [new CoverCandidate(101, "1", "One", "2016-08-01", "https://cv.example/101.jpg", "https://cv.example/101-m.jpg", null),
             new CoverCandidate(102, "2", null, null, "https://cv.example/102.jpg", null, "https://cv.example/102")]));

        var body = Json(Assert.IsType<OkObjectResult>((await CreateController().Candidates("/c/a.cbz", CancellationToken.None)).Result).Value);

        Assert.Equal((91273, "Batman", 102), ((int)body["volume_id"]!, (string?)body["volume_name"], (int)body["matched_issue_id"]!));
        Assert.Equal(new[] { false, true }, body["covers"]!.Select(c => (bool)c["matched"]!));
        Assert.Equal("https://cv.example/101-m.jpg", (string?)body["covers"]![0]!["thumb_url"]);
        Assert.Equal("2016-08-01", (string?)body["covers"]![0]!["cover_date"]);
    }

    [Fact]
    public async Task Candidates_NeedsAFileInTheLibrary()
    {
        Assert.IsType<BadRequestObjectResult>((await CreateController().Candidates(" ", CancellationToken.None)).Result);
        Assert.IsType<NotFoundObjectResult>((await CreateController().Candidates("/c/nope.cbz", CancellationToken.None)).Result);
    }

    [Fact]
    public async Task Replace_WritesTheChosenCover_OrSaysWhyNot()
    {
        _covers.Setup(c => c.ReplaceWithComicVineCoverAsync("/c/a.cbz", 101, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueCoverReplaceResult(true, true, null));
        _covers.Setup(c => c.ReplaceWithComicVineCoverAsync("/c/b.cbr", 101, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueCoverReplaceResult(false, false, "Only a CBZ's cover can be replaced; convert this file to CBZ first"));

        var replaced = await CreateController().Replace(new CoversController.ReplaceCoverRequest { FilePath = "/c/a.cbz", ComicVineIssueId = 101 }, CancellationToken.None);
        var refused = await CreateController().Replace(new CoversController.ReplaceCoverRequest { FilePath = "/c/b.cbr", ComicVineIssueId = 101 }, CancellationToken.None);

        Assert.True((bool)Json(Assert.IsType<OkObjectResult>(replaced.Result).Value)["replaced"]!);
        Assert.Equal("Only a CBZ's cover can be replaced; convert this file to CBZ first", (string?)Json(Assert.IsType<BadRequestObjectResult>(refused.Result).Value)["error"]);
        Assert.IsType<BadRequestObjectResult>((await CreateController().Replace(new CoversController.ReplaceCoverRequest { FilePath = "/c/a.cbz" }, CancellationToken.None)).Result);
        Assert.IsType<BadRequestObjectResult>((await CreateController().Replace(null!, CancellationToken.None)).Result);
    }

    [Fact]
    public async Task CandidateImage_ServesTheCoverTheServerFetched_ForTheBrowserToKeep()
    {
        _covers.Setup(c => c.GetCandidateImageAsync(101, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueCoverImage([1, 2, 3], "image/jpeg", null));
        var controller = CreateController();

        var file = Assert.IsType<FileContentResult>(await controller.CandidateImage(101, "THUMB", CancellationToken.None));

        Assert.Equal([1, 2, 3], file.FileContents);
        Assert.Equal("image/jpeg", file.ContentType);
        Assert.Equal("private, max-age=86400", controller.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task CandidateImage_NoStoredCover_Is404_AndAFailedDownload_Is502()
    {
        _covers.Setup(c => c.GetCandidateImageAsync(101, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueCoverImage(null, null, "The cover could not be downloaded: 403"));

        Assert.IsType<NotFoundObjectResult>(await CreateController().CandidateImage(999, null, CancellationToken.None));
        var failed = Assert.IsType<ObjectResult>(await CreateController().CandidateImage(101, null, CancellationToken.None));
        Assert.Equal(StatusCodes.Status502BadGateway, failed.StatusCode);
        Assert.Equal("The cover could not be downloaded: 403", (string?)Json(failed.Value)["error"]);
    }
}
