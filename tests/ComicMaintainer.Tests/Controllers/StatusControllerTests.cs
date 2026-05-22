using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.WebApi.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;

namespace ComicMaintainer.Tests.Controllers;

public class StatusControllerTests
{
    private readonly Mock<IFileStoreService> _fileStoreMock = new();
    private readonly Mock<ISeriesLibraryService> _seriesMock = new();
    private readonly Mock<ILogger<StatusController>> _loggerMock = new();
    private readonly StatusController _controller;

    public StatusControllerTests()
    {
        _controller = new StatusController(_fileStoreMock.Object, _seriesMock.Object, _loggerMock.Object);
    }

    [Fact]
    public async Task ClearSelected_NoFiles_ReturnsBadRequest()
    {
        var result = await _controller.ClearSelected(new StatusController.ClearFilesRequest { Files = new List<string>() });
        Assert.IsType<BadRequestObjectResult>(result.Result);
        _fileStoreMock.Verify(x => x.ClearProcessedStatusAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ClearSelected_WithFiles_DelegatesToFileStoreAndReturnsCount()
    {
        var files = new List<string> { "/a.cbz", "/b.cbz" };
        _fileStoreMock
            .Setup(x => x.ClearProcessedStatusAsync(files, It.IsAny<CancellationToken>()))
            .ReturnsAsync(2);

        var result = await _controller.ClearSelected(new StatusController.ClearFilesRequest { Files = files });

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var cleared = ok.Value!.GetType().GetProperty("cleared")?.GetValue(ok.Value);
        Assert.Equal(2, cleared);
        _fileStoreMock.Verify(x => x.ClearProcessedStatusAsync(files, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ClearSeries_NoSeriesId_ReturnsBadRequest()
    {
        var result = await _controller.ClearSeries("");
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task ClearSeries_UnknownSeries_ReturnsNotFound()
    {
        _seriesMock
            .Setup(x => x.GetSeriesIssuesAsync("abc", null, 1, -1, It.IsAny<CancellationToken>()))
            .ReturnsAsync((SeriesIssuesResult?)null);

        var result = await _controller.ClearSeries("abc");
        Assert.IsType<NotFoundObjectResult>(result.Result);
        _fileStoreMock.Verify(x => x.ClearProcessedStatusAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ClearSeries_KnownSeries_ClearsEveryIssueFile()
    {
        var issues = new SeriesIssuesResult
        {
            Id = "s1",
            Title = "My Series",
            Issues = new List<SeriesIssueDto>
            {
                new() { FilePath = "/lib/My Series/01.cbz" },
                new() { FilePath = "/lib/My Series/02.cbz" },
                new() { FilePath = "" }, // should be filtered out
            }
        };
        _seriesMock
            .Setup(x => x.GetSeriesIssuesAsync("s1", null, 1, -1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(issues);

        List<string>? captured = null;
        _fileStoreMock
            .Setup(x => x.ClearProcessedStatusAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<string>, CancellationToken>((f, _) => captured = f.ToList())
            .ReturnsAsync(2);

        var result = await _controller.ClearSeries("s1");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var cleared = ok.Value!.GetType().GetProperty("cleared")?.GetValue(ok.Value);
        Assert.Equal(2, cleared);
        Assert.NotNull(captured);
        Assert.Equal(2, captured!.Count);
        Assert.Contains("/lib/My Series/01.cbz", captured);
        Assert.Contains("/lib/My Series/02.cbz", captured);
    }

    [Fact]
    public async Task ClearFolder_NoFolder_ReturnsBadRequest()
    {
        var result = await _controller.ClearFolder(new StatusController.ClearFolderRequest { Folder = "" });
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task ClearFolder_MatchesFilesUnderFolder()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), $"statustests_{Guid.NewGuid()}");
        var insideDir = Path.Combine(rootDir, "Series A");
        var insideSubDir = Path.Combine(rootDir, "Series A", "Vol 1");
        var outsideDir = Path.Combine(Path.GetTempPath(), $"other_{Guid.NewGuid()}");

        var files = new List<ComicFile>
        {
            new() { FilePath = Path.Combine(insideDir, "01.cbz"), Directory = insideDir },
            new() { FilePath = Path.Combine(insideSubDir, "02.cbz"), Directory = insideSubDir },
            new() { FilePath = Path.Combine(outsideDir, "x.cbz"), Directory = outsideDir },
        };
        _fileStoreMock
            .Setup(x => x.GetAllFilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(files);

        List<string>? captured = null;
        _fileStoreMock
            .Setup(x => x.ClearProcessedStatusAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<string>, CancellationToken>((f, _) => captured = f.ToList())
            .ReturnsAsync(2);

        var result = await _controller.ClearFolder(new StatusController.ClearFolderRequest { Folder = rootDir });

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(captured);
        Assert.Equal(2, captured!.Count);
        Assert.Contains(Path.Combine(insideDir, "01.cbz"), captured);
        Assert.Contains(Path.Combine(insideSubDir, "02.cbz"), captured);
        Assert.DoesNotContain(Path.Combine(outsideDir, "x.cbz"), captured);
    }
}
