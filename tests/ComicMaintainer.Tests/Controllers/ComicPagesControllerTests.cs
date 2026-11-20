using ComicMaintainer.Core.Services;
using ComicMaintainer.WebApi.Controllers;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace ComicMaintainer.Tests.Controllers;

public sealed class ComicPagesControllerTests
{
    private readonly Mock<IComicPageRemover> _remover = new();

    private static string ErrorOf(IActionResult result)
    {
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        return (string)badRequest.Value!.GetType().GetProperty("error")!.GetValue(badRequest.Value)!;
    }

    [Fact]
    public async Task Remove_PassesThePagesOn_AndAnswersWhatWasRemoved()
    {
        _remover.Setup(r => r.RemovePagesAsync("/c/a.cbz", It.Is<IReadOnlyCollection<string>>(p => p.SequenceEqual(new[] { "p01.jpg" })), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PageRemovalResult(true, 1, 23, null));

        var result = await new ComicPagesController(_remover.Object).Remove(
            new ComicPagesController.RemovePagesRequest { FilePath = "/c/a.cbz", Pages = ["p01.jpg"] }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal((1, 23), ((int)ok.Value!.GetType().GetProperty("removed")!.GetValue(ok.Value)!, (int)ok.Value.GetType().GetProperty("pages_left")!.GetValue(ok.Value)!));
    }

    [Fact]
    public async Task Remove_ThatTheRemoverRefuses_IsABadRequestWithItsReason()
    {
        _remover.Setup(r => r.RemovePagesAsync(It.IsAny<string>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PageRemovalResult(false, 0, 0, "A comic must keep at least one page"));

        var result = await new ComicPagesController(_remover.Object).Remove(
            new ComicPagesController.RemovePagesRequest { FilePath = "/c/a.cbz", Pages = ["p01.jpg"] }, CancellationToken.None);

        Assert.Equal("A comic must keep at least one page", ErrorOf(result.Result!));
    }

    [Fact]
    public async Task Remove_WithoutAFileOrPages_OrTooManyPages_IsRefusedUnasked()
    {
        var controller = new ComicPagesController(_remover.Object);

        Assert.Equal("filePath and pages are required", ErrorOf((await controller.Remove(null, CancellationToken.None)).Result!));
        Assert.Equal("filePath and pages are required", ErrorOf((await controller.Remove(new() { FilePath = " ", Pages = ["p"] }, CancellationToken.None)).Result!));
        Assert.Equal("filePath and pages are required", ErrorOf((await controller.Remove(new() { FilePath = "/c/a.cbz", Pages = [] }, CancellationToken.None)).Result!));
        var many = Enumerable.Range(0, ComicPagesController.MaxPagesPerRequest + 1).Select(i => $"p{i}.jpg").ToList();
        Assert.Equal("At most 2000 pages can be removed at once", ErrorOf((await controller.Remove(new() { FilePath = "/c/a.cbz", Pages = many }, CancellationToken.None)).Result!));
        _remover.VerifyNoOtherCalls();
    }
}
