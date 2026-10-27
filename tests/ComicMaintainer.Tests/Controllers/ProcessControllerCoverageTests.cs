using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.WebApi.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;

namespace ComicMaintainer.Tests.Controllers;

/// <summary>
/// Covers the input validation and failure handling of <see cref="ProcessController"/>:
/// empty selections are rejected before a job is queued, and service failures surface as a
/// generic 500 (without the exception details) while being logged.
/// </summary>
public class ProcessControllerCoverageTests
{
    private readonly Mock<IComicProcessorService> _processor = new();
    private readonly Mock<IFileStoreService> _fileStore = new();
    private readonly Mock<ILogger<ProcessController>> _logger = new();
    private readonly ProcessController _controller;

    public ProcessControllerCoverageTests()
    {
        _controller = new ProcessController(_processor.Object, _fileStore.Object, _logger.Object);
    }

    public static TheoryData<ProcessController.ProcessRequest?> EmptySelections => new()
    {
        null,
        new ProcessController.ProcessRequest { Files = null! },
        new ProcessController.ProcessRequest { Files = new List<string>() },
    };

    [Theory]
    [MemberData(nameof(EmptySelections))]
    public async Task RenameSelected_WithoutFiles_ReturnsBadRequestAndQueuesNothing(ProcessController.ProcessRequest? request)
    {
        var result = await _controller.RenameSelected(request!);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal("No files specified", badRequest.Value);
        _processor.VerifyNoOtherCalls();
    }

    [Theory]
    [MemberData(nameof(EmptySelections))]
    public async Task NormalizeSelected_WithoutFiles_ReturnsBadRequestAndQueuesNothing(ProcessController.ProcessRequest? request)
    {
        var result = await _controller.NormalizeSelected(request!);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal("No files specified", badRequest.Value);
        _processor.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ProcessAll_WhenFileStoreFails_Returns500AndLogsError()
    {
        var failure = new InvalidOperationException("database is locked");
        _fileStore.Setup(f => f.GetAllFilesAsync(It.IsAny<CancellationToken>())).ThrowsAsync(failure);

        var result = await _controller.ProcessAll();

        AssertServerError(result.Result, "Error processing files");
        _processor.VerifyNoOtherCalls();
        VerifyErrorLogged(failure);
    }

    [Fact]
    public async Task ProcessAll_QueuesOnlyUnprocessedNonDuplicateFiles()
    {
        _fileStore.Setup(f => f.GetAllFilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<ComicFile>
        {
            new() { FilePath = "/c/new.cbz" },
            new() { FilePath = "/c/done.cbz", IsProcessed = true },
            new() { FilePath = "/c/dupe.cbz", IsDuplicate = true },
        });
        IEnumerable<string>? queued = null;
        _processor
            .Setup(p => p.ProcessFilesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<string>, CancellationToken>((files, _) => queued = files.ToList())
            .ReturnsAsync(Guid.NewGuid());

        var result = await _controller.ProcessAll();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(1, ok.Value!.GetType().GetProperty("totalFiles")!.GetValue(ok.Value));
        Assert.Equal(new[] { "/c/new.cbz" }, queued);
    }

    [Fact]
    public async Task ProcessSelected_WhenProcessorFails_Returns500AndLogsError()
    {
        var failure = new InvalidOperationException("queue full");
        _processor
            .Setup(p => p.ProcessFilesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);

        var result = await _controller.ProcessSelected(new ProcessController.ProcessRequest { Files = new() { "/c/a.cbz" } });

        AssertServerError(result.Result, "Error processing files");
        VerifyErrorLogged(failure);
    }

    [Fact]
    public async Task RenameAll_WhenFileStoreFails_Returns500AndLogsError()
    {
        var failure = new IOException("disk unavailable");
        _fileStore.Setup(f => f.GetAllFilesAsync(It.IsAny<CancellationToken>())).ThrowsAsync(failure);

        var result = await _controller.RenameAll(forceReprocess: true);

        AssertServerError(result.Result, "Error renaming files");
        _processor.VerifyNoOtherCalls();
        VerifyErrorLogged(failure);
    }

    [Fact]
    public async Task RenameSelected_WhenProcessorFails_Returns500AndLogsError()
    {
        var failure = new InvalidOperationException("queue full");
        _processor
            .Setup(p => p.RenameFilesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);

        var result = await _controller.RenameSelected(new ProcessController.ProcessRequest { Files = new() { "/c/a.cbz" } });

        AssertServerError(result.Result, "Error renaming files");
        VerifyErrorLogged(failure);
    }

    [Fact]
    public async Task NormalizeAll_WhenFileStoreFails_Returns500AndLogsError()
    {
        var failure = new IOException("disk unavailable");
        _fileStore.Setup(f => f.GetAllFilesAsync(It.IsAny<CancellationToken>())).ThrowsAsync(failure);

        var result = await _controller.NormalizeAll(forceReprocess: false);

        AssertServerError(result.Result, "Error normalizing files");
        _processor.VerifyNoOtherCalls();
        VerifyErrorLogged(failure);
    }

    [Fact]
    public async Task NormalizeSelected_WhenProcessorFails_Returns500AndLogsError()
    {
        var failure = new InvalidOperationException("queue full");
        _processor
            .Setup(p => p.NormalizeFilesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);

        var result = await _controller.NormalizeSelected(
            new ProcessController.ProcessRequest { Files = new() { "/c/a.cbz", "/c/b.cbz" } },
            forceReprocess: true);

        AssertServerError(result.Result, "Error normalizing files");
        VerifyErrorLogged(failure);
    }

    private static void AssertServerError(ActionResult? result, string expectedMessage)
    {
        var error = Assert.IsType<ObjectResult>(result);
        Assert.Equal(500, error.StatusCode);
        // Only a generic message is returned; exception details stay in the log.
        Assert.Equal(expectedMessage, error.Value);
    }

    private void VerifyErrorLogged(Exception expected)
    {
        _logger.Verify(
            l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                expected,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }
}
