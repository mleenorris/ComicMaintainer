using System.Text.Json;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.WebApi.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;

namespace ComicMaintainer.Tests.Controllers;

/// <summary>
/// Request validation of <see cref="StatusController"/> beyond
/// <see cref="StatusControllerTests"/>: missing bodies and unparseable folders
/// must be rejected before any processed flag is touched.
/// </summary>
public class StatusControllerCoverageTests
{
    private readonly Mock<IFileStoreService> _fileStore = new();
    private readonly Mock<ILogger<StatusController>> _logger = new();
    private readonly StatusController _controller;

    public StatusControllerCoverageTests()
    {
        _controller = new StatusController(_fileStore.Object, Mock.Of<ISeriesLibraryService>(), _logger.Object);
    }

    [Fact]
    public async Task ClearSelected_WithoutBodyIsBadRequest()
    {
        var result = await _controller.ClearSelected(null!);

        Assert.Equal("No files specified", ErrorOf(result));
        _fileStore.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ClearSelected_WithNullFileListIsBadRequest()
    {
        var result = await _controller.ClearSelected(new StatusController.ClearFilesRequest { Files = null! });

        Assert.Equal("No files specified", ErrorOf(result));
        _fileStore.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ClearFolder_UnparseableFolderIsBadRequestAndClearsNothing()
    {
        var result = await _controller.ClearFolder(new StatusController.ClearFolderRequest { Folder = "/comics/bad\0name" });

        Assert.Equal("Invalid folder path", ErrorOf(result));
        _fileStore.VerifyNoOtherCalls();
        _logger.Verify(l => l.Log(
            LogLevel.Warning,
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            It.IsNotNull<ArgumentException>(),
            (Func<It.IsAnyType, Exception?, string>)It.IsAny<object>()), Times.Once);
    }

    private static string? ErrorOf(ActionResult<object> result) =>
        JsonSerializer.SerializeToElement(Assert.IsType<BadRequestObjectResult>(result.Result).Value).GetProperty("error").GetString();
}
