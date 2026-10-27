using System.Text;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.WebApi.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Controllers;

/// <summary>
/// Covers the read-status, processing and mutation endpoints of
/// <see cref="FilesController"/> (FilesController.ReadStatus.cs,
/// FilesController.Processing.cs and FilesController.Mutations.cs).
/// </summary>
public sealed class FilesControllerFileActionsTests
{
    private const string OutsidePath = "/etc/passwd";

    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "fc-n2a-tests", Guid.NewGuid().ToString("N"));
    private readonly Mock<IFileStoreService> _fileStore = new();
    private readonly Mock<IComicProcessorService> _processor = new();
    private readonly Mock<IProcessingHistoryService> _history = new();
    private readonly Mock<ISeriesLibraryService> _seriesLibrary = new();
    private readonly Mock<IOptionsMonitor<AppSettings>> _settings = new();
    private readonly Mock<ILogger<FilesController>> _logger = new();
    private readonly FilesController _controller;

    public FilesControllerFileActionsTests()
    {
        _settings.Setup(s => s.CurrentValue).Returns(new AppSettings { WatchedDirectory = _root });
        _controller = new FilesController(
            _fileStore.Object,
            _processor.Object,
            _history.Object,
            _seriesLibrary.Object,
            _logger.Object,
            _settings.Object);
    }

    private string InRoot(params string[] parts) => Path.Combine(new[] { _root }.Concat(parts).ToArray());

    // Path theories use "{root}" for the watched directory, e.g. to build a
    // sibling directory that shares its name prefix.
    private string Expand(string path) => path.Replace("{root}", _root);

    private static string Encode(string path) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(path)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static object? Prop(object? value, string name) => value?.GetType().GetProperty(name)?.GetValue(value);

    private static void AssertStatus(ActionResult? result, int expectedStatus, object? expectedValue)
    {
        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(expectedStatus, objectResult.StatusCode);
        Assert.Equal(expectedValue, objectResult.Value);
    }

    private void VerifyWarned(string fragment) =>
        _logger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v!.ToString()!.Contains(fragment)),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);

    // ---------------------------------------------------------------- MarkFileRead

    [Theory]
    [InlineData("@@@")]
    [InlineData("")]
    public async Task MarkFileRead_UndecodablePath_ReturnsBadRequest(string encoded)
    {
        var result = await _controller.MarkFileRead(encoded, new FilesController.ReadStatusRequest { Read = true });

        AssertStatus(result, 400, "Invalid file path");
        _fileStore.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(OutsidePath)]
    [InlineData("../../etc/passwd")]
    public async Task MarkFileRead_PathOutsideWatchedDirectory_ReturnsBadRequest(string path)
    {
        var result = await _controller.MarkFileRead(Encode(path), new FilesController.ReadStatusRequest { Read = true });

        AssertStatus(result, 400, "File path is outside the allowed directory");
        VerifyWarned("Attempt to mark file read outside watched directory");
        _fileStore.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MarkFileRead_ValidPath_UpdatesReadState(bool read)
    {
        var path = InRoot("Saga", "Saga 001.cbz");
        using var cts = new CancellationTokenSource();

        var result = await _controller.MarkFileRead(Encode(path), new FilesController.ReadStatusRequest { Read = read }, cts.Token);

        Assert.IsType<OkResult>(result);
        _fileStore.Verify(f => f.MarkFileReadAsync(path, read, cts.Token), Times.Once);
    }

    [Fact]
    public async Task MarkFileRead_StoreThrows_Returns500()
    {
        var path = InRoot("Saga 001.cbz");
        _fileStore.Setup(f => f.MarkFileReadAsync(path, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db locked"));

        var result = await _controller.MarkFileRead(Encode(path), new FilesController.ReadStatusRequest { Read = true });

        AssertStatus(result, 500, "Error updating read status");
    }

    // ---------------------------------------------------------- MarkFilesReadBatch

    [Fact]
    public async Task MarkFilesReadBatch_NullFileList_ReturnsBadRequest()
    {
        var result = await _controller.MarkFilesReadBatch(new FilesController.ReadBatchRequest { Files = null!, Read = true });

        AssertStatus(result, 400, "No files provided");
        _fileStore.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task MarkFilesReadBatch_EmptyFileList_ReturnsBadRequest()
    {
        var result = await _controller.MarkFilesReadBatch(new FilesController.ReadBatchRequest { Read = true });

        AssertStatus(result, 400, "No files provided");
        _fileStore.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task MarkFilesReadBatch_AnyPathOutsideWatchedDirectory_RejectsWholeBatch()
    {
        var request = new FilesController.ReadBatchRequest
        {
            Files = new List<string> { InRoot("Saga 001.cbz"), OutsidePath },
            Read = true
        };

        var result = await _controller.MarkFilesReadBatch(request);

        AssertStatus(result, 400, "One or more file paths are outside the allowed directory");
        _fileStore.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task MarkFilesReadBatch_AllPathsSafe_UpdatesAllFiles()
    {
        var files = new List<string> { InRoot("Saga 001.cbz"), InRoot("Saga", "Saga 002.cbz") };
        using var cts = new CancellationTokenSource();

        var result = await _controller.MarkFilesReadBatch(new FilesController.ReadBatchRequest { Files = files, Read = false }, cts.Token);

        Assert.IsType<OkResult>(result);
        _fileStore.Verify(f => f.MarkFilesReadAsync(files, false, cts.Token), Times.Once);
    }

    [Fact]
    public async Task MarkFilesReadBatch_StoreThrows_Returns500()
    {
        _fileStore.Setup(f => f.MarkFilesReadAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db locked"));

        var result = await _controller.MarkFilesReadBatch(new FilesController.ReadBatchRequest
        {
            Files = new List<string> { InRoot("Saga 001.cbz") },
            Read = true
        });

        AssertStatus(result, 500, "Error updating read status");
    }

    // ----------------------------------------------------------------- ProcessFile

    [Fact]
    public async Task ProcessFile_ProcessorReportsFailure_ReturnsBadRequest()
    {
        var path = InRoot("Saga 001.cbz");
        _processor.Setup(p => p.ProcessFileAsync(path, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var result = await _controller.ProcessFile(path);

        AssertStatus(result, 400, "Failed to process file");
    }

    [Fact]
    public async Task ProcessFile_ProcessorThrows_Returns500()
    {
        var path = InRoot("Saga 001.cbz");
        _processor.Setup(p => p.ProcessFileAsync(path, It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("corrupt"));

        var result = await _controller.ProcessFile(path);

        AssertStatus(result, 500, "Error processing file");
    }

    [Fact]
    public async Task ProcessFile_PathOutsideWatchedDirectory_ReturnsBadRequest()
    {
        var result = await _controller.ProcessFile(OutsidePath);

        AssertStatus(result, 400, "File path is outside the allowed directory");
        _processor.VerifyNoOtherCalls();
    }

    // ---------------------------------------------------------------- ProcessBatch

    [Theory]
    [InlineData(OutsidePath)]
    [InlineData("../../etc/passwd")]
    [InlineData("{root}-private/Saga 001.cbz")]
    public async Task ProcessBatch_PathOutsideWatchedDirectory_RejectsTheWholeBatch(string path)
    {
        var result = await _controller.ProcessBatch(new List<string> { InRoot("Saga 001.cbz"), Expand(path) });

        AssertStatus(result.Result, 400, "One or more file paths are outside the allowed directory");
        _processor.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ProcessBatch_LeavesBlankEntriesToTheBatchJob()
    {
        var jobId = Guid.NewGuid();
        var files = new List<string> { InRoot("Saga 001.cbz"), "", "  ", null!, InRoot("Saga", "Saga 002.cbz") };
        _processor.Setup(p => p.ProcessFilesAsync(files, It.IsAny<CancellationToken>())).ReturnsAsync(jobId);

        var result = await _controller.ProcessBatch(files);

        Assert.Equal(jobId, Prop(Assert.IsType<OkObjectResult>(result.Result).Value, "jobId"));
    }

    // ------------------------------------------------------------------ UpdateTags

    [Fact]
    public async Task UpdateTags_SkipsBlankEntriesAndUpdatesTheRest()
    {
        var first = InRoot("Saga 001.cbz");
        var second = InRoot("Saga 002.cbz");
        var metadata = new ComicMetadata { Series = "Saga" };
        var request = new FilesController.UpdateTagsRequest
        {
            Files = new List<string> { first, "", null!, second },
            Metadata = metadata
        };

        var result = await _controller.UpdateTags(request);

        Assert.IsType<OkResult>(result);
        _processor.Verify(p => p.UpdateMetadataAsync(first, metadata, It.IsAny<CancellationToken>()), Times.Once);
        _processor.Verify(p => p.UpdateMetadataAsync(second, metadata, It.IsAny<CancellationToken>()), Times.Once);
        _processor.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(OutsidePath)]
    [InlineData("../../etc/passwd")]
    [InlineData("{root}-private/Saga 001.cbz")]
    public async Task UpdateTags_PathOutsideWatchedDirectory_RejectsTheWholeRequestAndUpdatesNothing(string path)
    {
        var result = await _controller.UpdateTags(new FilesController.UpdateTagsRequest
        {
            Files = new List<string> { InRoot("Saga 001.cbz"), "", Expand(path) },
            Metadata = new ComicMetadata { Series = "Saga" }
        });

        AssertStatus(result, 400, "One or more file paths are outside the allowed directory");
        _processor.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UpdateTags_ProcessorThrows_Returns500()
    {
        _processor.Setup(p => p.UpdateMetadataAsync(It.IsAny<string>(), It.IsAny<ComicMetadata>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("read-only"));

        var result = await _controller.UpdateTags(new FilesController.UpdateTagsRequest
        {
            Files = new List<string> { InRoot("Saga 001.cbz") }
        });

        AssertStatus(result, 500, "Error updating tags");
    }

    // ---------------------------------------------------------------- ScanUnmarked

    [Fact]
    public async Task ScanUnmarked_StoreThrows_Returns500()
    {
        _fileStore.Setup(f => f.GetAllFilesAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("not loaded"));

        var result = await _controller.ScanUnmarked();

        AssertStatus(result, 500, "Error scanning files");
    }

    // ---------------------------------------------------- ProcessFileByEncodedPath

    [Fact]
    public async Task ProcessFileByEncodedPath_UndecodablePath_ReturnsBadRequest()
    {
        var result = await _controller.ProcessFileByEncodedPath("@@@");

        AssertStatus(result, 400, "Invalid file path");
        _processor.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(OutsidePath)]
    [InlineData("../../etc/passwd")]
    [InlineData("{root}-private/Saga 001.cbz")]
    public async Task ProcessFileByEncodedPath_PathOutsideWatchedDirectory_ReturnsBadRequest(string path)
    {
        var result = await _controller.ProcessFileByEncodedPath(Encode(Expand(path)), forceReprocess: true);

        AssertStatus(result, 400, "File path is outside the allowed directory");
        _processor.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ProcessFileByEncodedPath_ProcessingFails_ReturnsBadRequest()
    {
        var path = InRoot("Saga", "Saga 001.cbz");
        _processor.Setup(p => p.ProcessFileAsync(path, false, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var result = await _controller.ProcessFileByEncodedPath(Encode(path));

        AssertStatus(result, 400, "Failed to process file");
        _processor.Verify(p => p.ProcessFileAsync(path, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessFileByEncodedPath_ProcessorThrows_Returns500()
    {
        var path = InRoot("Saga 001.cbz");
        _processor.Setup(p => p.ProcessFileAsync(path, true, It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("corrupt"));

        var result = await _controller.ProcessFileByEncodedPath(Encode(path), forceReprocess: true);

        AssertStatus(result, 500, "Error processing file");
    }

    // ----------------------------------------------------- RenameFileByEncodedPath

    [Fact]
    public async Task RenameFileByEncodedPath_UndecodablePath_ReturnsBadRequest()
    {
        var result = await _controller.RenameFileByEncodedPath("@@@");

        AssertStatus(result, 400, "Invalid file path");
        _processor.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RenameFileByEncodedPath_PathOutsideWatchedDirectory_ReturnsBadRequest()
    {
        var result = await _controller.RenameFileByEncodedPath(Encode(OutsidePath));

        AssertStatus(result, 400, "File path is outside the allowed directory");
        VerifyWarned("Attempt to rename file outside watched directory");
        _processor.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(OutsidePath)]
    [InlineData("../../etc/passwd")]
    [InlineData("{root}-private/Saga 001.cbz")]
    public async Task RenameSingleFile_PathOutsideWatchedDirectory_ReturnsBadRequest(string path)
    {
#pragma warning disable CS0618 // Type or member is obsolete
        var result = await _controller.RenameSingleFile(Expand(path));
#pragma warning restore CS0618 // Type or member is obsolete

        AssertStatus(result, 400, "File path is outside the allowed directory");
        VerifyWarned("Attempt to rename file outside watched directory");
        _processor.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RenameFileByEncodedPath_ProcessorThrows_Returns500()
    {
        var path = InRoot("Saga 001.cbz");
        _processor.Setup(p => p.RenameFilesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("queue full"));

        var result = await _controller.RenameFileByEncodedPath(Encode(path));

        AssertStatus(result, 500, "Error renaming file");
    }

    // --------------------------------------------------------- CleanupStaleEntries

    [Fact]
    public async Task CleanupStaleEntries_ReportsRemovedCount()
    {
        using var cts = new CancellationTokenSource();
        _fileStore.Setup(f => f.CleanupStaleEntriesAsync(cts.Token)).ReturnsAsync(3);

        var result = await _controller.CleanupStaleEntries(cts.Token);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(true, Prop(ok.Value, "success"));
        Assert.Equal(3, Prop(ok.Value, "removedCount"));
        Assert.Equal("Removed 3 stale database entries", Prop(ok.Value, "message"));
    }

    [Fact]
    public async Task CleanupStaleEntries_Cancelled_Returns499()
    {
        _fileStore.Setup(f => f.CleanupStaleEntriesAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new OperationCanceledException());

        var result = await _controller.CleanupStaleEntries();

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(499, objectResult.StatusCode);
        Assert.Equal("Cleanup operation was cancelled", Prop(objectResult.Value, "error"));
    }

    [Fact]
    public async Task CleanupStaleEntries_StoreThrows_Returns500()
    {
        _fileStore.Setup(f => f.CleanupStaleEntriesAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("db locked"));

        var result = await _controller.CleanupStaleEntries();

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(500, objectResult.StatusCode);
        Assert.Equal("Failed to cleanup stale entries", Prop(objectResult.Value, "error"));
    }
}
