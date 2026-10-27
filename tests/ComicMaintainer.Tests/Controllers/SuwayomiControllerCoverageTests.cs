using System.Text.Json;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.WebApi.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;

namespace ComicMaintainer.Tests.Controllers;

/// <summary>
/// Request validation and error mapping of <see cref="SuwayomiController"/>:
/// Suwayomi failures are user-facing 400s with the service's message.
/// </summary>
public class SuwayomiControllerCoverageTests
{
    private readonly Mock<ISuwayomiService> _suwayomi = new();

    [Fact]
    public async Task GetStatus_ReturnsTheConnectionStatus()
    {
        var status = new SuwayomiConnectionStatus { Enabled = true, Configured = true, Connected = true, ServerName = "Suwayomi" };
        _suwayomi.Setup(s => s.GetStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync(status);

        var result = await CreateController().GetStatus(CancellationToken.None);

        Assert.Same(status, Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    [Fact]
    public async Task SearchSeries_ReturnsTheMatches()
    {
        var match = new SuwayomiSeries { Id = 12, Title = "Solo Leveling", InLibrary = true };
        _suwayomi.Setup(s => s.SearchSeriesAsync("solo", 5, It.IsAny<CancellationToken>())).ReturnsAsync(new[] { match });

        var result = await CreateController().SearchSeries("solo", 5, CancellationToken.None);

        var json = OkJson(result);
        Assert.Equal(12, Assert.Single(json.GetProperty("series").EnumerateArray()).GetProperty("Id").GetInt32());
    }

    [Fact]
    public async Task SearchSeries_SuwayomiFailureIsBadRequest()
    {
        _suwayomi.Setup(s => s.SearchSeriesAsync(It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new SuwayomiException("Suwayomi is not configured"));

        var result = await CreateController().SearchSeries(null, cancellationToken: CancellationToken.None);

        Assert.Equal("Suwayomi is not configured", ErrorOf(result.Result));
        _suwayomi.Verify(s => s.SearchSeriesAsync(null, 20, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetChapters_ReturnsTheSeriesChapters()
    {
        _suwayomi.Setup(s => s.GetChaptersAsync(12, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new SuwayomiChapter { Id = 1, Name = "Chapter 1", ChapterNumber = 1, IsDownloaded = true },
                new SuwayomiChapter { Id = 2, Name = "Chapter 2", ChapterNumber = 2 }
            });

        var json = OkJson(await CreateController().GetChapters(12, CancellationToken.None));

        Assert.Equal(new[] { 1, 2 }, json.GetProperty("chapters").EnumerateArray().Select(c => c.GetProperty("Id").GetInt32()));
    }

    [Fact]
    public async Task GetChapters_SuwayomiFailureIsBadRequest()
    {
        _suwayomi.Setup(s => s.GetChaptersAsync(99, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new SuwayomiException("Series 99 was not found on the Suwayomi server"));

        var result = await CreateController().GetChapters(99, CancellationToken.None);

        Assert.Equal("Series 99 was not found on the Suwayomi server", ErrorOf(result.Result));
    }

    [Fact]
    public async Task DownloadMissing_WithoutBodyIsBadRequest()
    {
        var result = await CreateController().DownloadMissing(null!, CancellationToken.None);

        Assert.Equal("Request body is required", ErrorOf(result.Result));
        _suwayomi.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DownloadMissing_RequiresAtLeastOneIssueNumber()
    {
        var controller = CreateController();

        Assert.Equal("At least one issue number is required", ErrorOf(
            (await controller.DownloadMissing(new SuwayomiController.DownloadMissingRequest { SeriesTitle = "Saga" }, CancellationToken.None)).Result));
        Assert.Equal("At least one issue number is required", ErrorOf(
            (await controller.DownloadMissing(new SuwayomiController.DownloadMissingRequest { SeriesTitle = "Saga", IssueNumbers = new List<int>() }, CancellationToken.None)).Result));
        _suwayomi.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DownloadMissing_ByExplicitSeriesIdNeedsNoTitle()
    {
        var download = new SuwayomiDownloadResult
        {
            Series = new SuwayomiSeries { Id = 4, Title = "Saga" },
            QueuedChapters = new List<SuwayomiChapter> { new() { Id = 40, ChapterNumber = 3 } },
            UnmatchedIssueNumbers = new List<int> { 9 }
        };
        _suwayomi.Setup(s => s.DownloadMissingIssuesAsync(null, It.IsAny<IEnumerable<int>>(), 4, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(download);

        var result = await CreateController().DownloadMissing(
            new SuwayomiController.DownloadMissingRequest { SuwayomiSeriesId = 4, IssueNumbers = new List<int> { 3, 9 } },
            CancellationToken.None);

        Assert.Same(download, Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    [Fact]
    public async Task DownloadMissing_SuwayomiFailureIsBadRequest()
    {
        _suwayomi.Setup(s => s.DownloadMissingIssuesAsync(It.IsAny<string?>(), It.IsAny<IEnumerable<int>>(), It.IsAny<int?>(), It.IsAny<IEnumerable<string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new SuwayomiException("No Suwayomi series matches 'Saga'"));

        var result = await CreateController().DownloadMissing(
            new SuwayomiController.DownloadMissingRequest { SeriesTitle = "Saga", IssueNumbers = new List<int> { 1 } },
            CancellationToken.None);

        Assert.Equal("No Suwayomi series matches 'Saga'", ErrorOf(result.Result));
    }

    private SuwayomiController CreateController() =>
        new(_suwayomi.Object, Mock.Of<ILogger<SuwayomiController>>());

    private static JsonElement OkJson(ActionResult<object> result) =>
        JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result.Result).Value);

    private static string? ErrorOf(IActionResult? result) =>
        JsonSerializer.SerializeToElement(Assert.IsType<BadRequestObjectResult>(result).Value).GetProperty("error").GetString();
}
