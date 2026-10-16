using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.WebApi.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;

namespace ComicMaintainer.Tests.Controllers;

public class SuwayomiControllerTests
{
    [Fact]
    public async Task DownloadMissing_PassesTheSeriesOtherTitlesToTheService()
    {
        var suwayomi = new Mock<ISuwayomiService>();
        suwayomi
            .Setup(s => s.DownloadMissingIssuesAsync(
                It.IsAny<string?>(),
                It.IsAny<IEnumerable<int>>(),
                It.IsAny<int?>(),
                It.IsAny<IEnumerable<string>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SuwayomiDownloadResult { Series = new SuwayomiSeries { Id = 4, Title = "Ore dake Level Up na Ken" } });
        var controller = new SuwayomiController(suwayomi.Object, Mock.Of<ILogger<SuwayomiController>>());

        var response = await controller.DownloadMissing(
            new SuwayomiController.DownloadMissingRequest
            {
                SeriesTitle = "Solo Leveling",
                AlternateTitles = new List<string> { "Ore dake Level Up na Ken", "Na Honjaman Level Up" },
                IssueNumbers = new List<int> { 3 }
            },
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(response.Result);
        suwayomi.Verify(s => s.DownloadMissingIssuesAsync(
            "Solo Leveling",
            It.Is<IEnumerable<int>>(numbers => numbers.SequenceEqual(new[] { 3 })),
            null,
            It.Is<IEnumerable<string>?>(titles => titles != null
                && titles.SequenceEqual(new[] { "Ore dake Level Up na Ken", "Na Honjaman Level Up" })),
            It.IsAny<CancellationToken>()));
    }
}
