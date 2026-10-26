using System.Security.Claims;
using System.Text.Json;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.WebApi.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;

namespace ComicMaintainer.Tests.Controllers;

/// <summary>
/// Restore paths of <see cref="BackupsController"/> beyond
/// <see cref="BackupsControllerTests"/>: conflicts, oversized uploads, callers
/// without an identity, and the upload stream's lifetime.
/// </summary>
public class BackupsControllerCoverageTests
{
    private const string ValidName = "comicmaintainer-20261020-040000.db";

    private readonly Mock<IDatabaseBackupService> _backups = new();
    private readonly Mock<ILogger<BackupsController>> _logger = new();
    private readonly BackupsController _controller;

    public BackupsControllerCoverageTests()
    {
        _controller = new BackupsController(_backups.Object, _logger.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    [Fact]
    public async Task Restore_WhileAnotherRestoreIsPendingIsConflict()
    {
        _backups.Setup(b => b.StageRestoreAsync(ValidName, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("A restore is already pending."));

        var result = await _controller.Restore(ValidName, CancellationToken.None);

        Assert.Equal("A restore is already pending.", ErrorOf<ConflictObjectResult>(result.Result));
        VerifyRestoreLogged(Times.Never());
    }

    [Fact]
    public async Task Restore_ByACallerWithoutAnIdentityIsStillAudited()
    {
        _controller.ControllerContext.HttpContext.User = new ClaimsPrincipal();
        _backups.Setup(b => b.StageRestoreAsync(ValidName, It.IsAny<CancellationToken>())).ReturnsAsync(RestoreResult(ValidName));

        var result = await _controller.Restore(ValidName, CancellationToken.None);

        var json = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(ValidName, json.GetProperty("source").GetString());
        VerifyRestoreLogged(Times.Once());
    }

    [Fact]
    public async Task UploadRestore_LargerThanTheLimitIsRejectedWithoutReadingIt()
    {
        var file = new Mock<IFormFile>();
        file.SetupGet(f => f.Length).Returns(BackupsController.MaxUploadBytes + 1);

        var result = await _controller.UploadRestore(file.Object, CancellationToken.None);

        Assert.Equal("File too large", ErrorOf<BadRequestObjectResult>(result.Result));
        file.Verify(f => f.OpenReadStream(), Times.Never);
        _backups.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UploadRestore_AtExactlyTheLimitIsAccepted()
    {
        var content = new TrackingStream();
        var file = UploadOf(content, BackupsController.MaxUploadBytes);
        _backups.Setup(b => b.StageRestoreFromStreamAsync(content, It.IsAny<CancellationToken>())).ReturnsAsync(RestoreResult("upload"));

        var result = await _controller.UploadRestore(file, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task UploadRestore_WhileAnotherRestoreIsPendingIsConflictAndReleasesTheUpload()
    {
        var content = new TrackingStream();
        _backups.Setup(b => b.StageRestoreFromStreamAsync(content, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("A restore is already pending."));

        var result = await _controller.UploadRestore(UploadOf(content, 16), CancellationToken.None);

        Assert.Equal("A restore is already pending.", ErrorOf<ConflictObjectResult>(result.Result));
        Assert.True(content.DisposedAsync);
    }

    [Fact]
    public async Task UploadRestore_ByACallerWithoutAnIdentityStagesAndReleasesTheUpload()
    {
        _controller.ControllerContext.HttpContext.User = new ClaimsPrincipal();
        var content = new TrackingStream();
        _backups.Setup(b => b.StageRestoreFromStreamAsync(content, It.IsAny<CancellationToken>())).ReturnsAsync(RestoreResult("upload"));

        var result = await _controller.UploadRestore(UploadOf(content, 16), CancellationToken.None);

        var json = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal("upload", json.GetProperty("source").GetString());
        Assert.True(content.DisposedAsync);
        VerifyRestoreLogged(Times.Once());
    }

    private static IFormFile UploadOf(Stream content, long length)
    {
        var file = new Mock<IFormFile>();
        file.SetupGet(f => f.Length).Returns(length);
        file.Setup(f => f.OpenReadStream()).Returns(content);
        return file.Object;
    }

    private static DatabaseRestoreResult RestoreResult(string source) =>
        new(source, new DatabaseBackupInfo("comicmaintainer-20261020-040500-prerestore.db", 10, DateTime.UtcNow));

    private void VerifyRestoreLogged(Times times) =>
        _logger.Verify(l => l.Log(
            LogLevel.Warning,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains("staged by")),
            It.IsAny<Exception?>(),
            (Func<It.IsAnyType, Exception?, string>)It.IsAny<object>()), times);

    private static string? ErrorOf<TResult>(IActionResult? result)
        where TResult : ObjectResult =>
        JsonSerializer.SerializeToElement(Assert.IsType<TResult>(result).Value).GetProperty("error").GetString();

    /// <summary>
    /// Upload body that records being released and, like a real request body,
    /// completes its asynchronous disposal off the calling stack.
    /// </summary>
    private sealed class TrackingStream : MemoryStream
    {
        public bool DisposedAsync { get; private set; }

        public override async ValueTask DisposeAsync()
        {
            await Task.Yield();
            DisposedAsync = true;
            await base.DisposeAsync();
        }
    }
}
