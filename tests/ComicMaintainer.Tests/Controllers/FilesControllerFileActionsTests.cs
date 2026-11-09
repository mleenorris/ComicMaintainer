using System.Text;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Tests.Helpers;
using ComicMaintainer.Core.Services;
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

    private readonly string _root = Path.Join(AppContext.BaseDirectory, "fc-n2a-tests", Guid.NewGuid().ToString("N"));
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
            _settings.Object,
            TestFolderCombineService.Create(_fileStore.Object, _processor.Object, Mock.Of<IBatchJobRegistry>(), _history.Object, _seriesLibrary.Object, _settings.Object),
            Mock.Of<IEventBroadcaster>(), Mock.Of<ISeriesNameResolver>(), Mock.Of<IScheduledJobRunner>(), new FileOperationLock(), Mock.Of<ComicMaintainer.Core.Notifications.INotificationPublisher>());
    }

    private string InRoot(params string[] parts) => Path.Join(new[] { _root }.Concat(parts).ToArray());

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

    private void VerifyNoErrorLogged() =>
        _logger.Verify(
            l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);

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
    public async Task MarkFileRead_StoreThrows_Escapes()
    {
        var path = InRoot("Saga 001.cbz");
        _fileStore.Setup(f => f.MarkFileReadAsync(path, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db locked"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _controller.MarkFileRead(Encode(path), new FilesController.ReadStatusRequest { Read = true }));
    }

    [Fact]
    public async Task MarkFileRead_Cancelled_Returns499WithoutLoggingAnError()
    {
        // The reader closing mid-request is not a server error.
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var path = InRoot("Saga 001.cbz");
        _fileStore.Setup(f => f.MarkFileReadAsync(path, true, cancelled.Token))
            .ThrowsAsync(new OperationCanceledException(cancelled.Token));

        var result = await _controller.MarkFileRead(Encode(path), new FilesController.ReadStatusRequest { Read = true }, cancelled.Token);

        Assert.Equal(499, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
        VerifyNoErrorLogged();
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
    public async Task MarkFilesReadBatch_StoreThrows_Escapes()
    {
        _fileStore.Setup(f => f.MarkFilesReadAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db locked"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _controller.MarkFilesReadBatch(new FilesController.ReadBatchRequest
        {
            Files = new List<string> { InRoot("Saga 001.cbz") },
            Read = true
        }));
    }

    [Fact]
    public async Task MarkFilesReadBatch_Cancelled_Returns499WithoutLoggingAnError()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        _fileStore.Setup(f => f.MarkFilesReadAsync(It.IsAny<IEnumerable<string>>(), true, cancelled.Token))
            .ThrowsAsync(new OperationCanceledException(cancelled.Token));

        var result = await _controller.MarkFilesReadBatch(
            new FilesController.ReadBatchRequest { Files = new List<string> { InRoot("Saga 001.cbz") }, Read = true },
            cancelled.Token);

        Assert.Equal(499, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
        VerifyNoErrorLogged();
    }

    // --------------------------------------------------------------- MarkScopeRead

    [Theory]
    [InlineData(null, true)]
    [InlineData("library", false)]
    [InlineData(" Library ", true)]
    public async Task MarkScopeRead_Library_MarksTheWholeLibraryServerSide(string? scope, bool read)
    {
        _fileStore.Setup(f => f.MarkScopeReadAsync(It.IsAny<FileReadScope>(), read, It.IsAny<CancellationToken>())).ReturnsAsync(42);

        var result = await _controller.MarkScopeRead(new FilesController.ReadScopeRequest { Scope = scope, Read = read });

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(42, Prop(ok.Value, "updated"));
        _fileStore.Verify(f => f.MarkScopeReadAsync(FileReadScope.Library, read, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MarkScopeRead_Folder_PassesTheFolderKey()
    {
        var result = await _controller.MarkScopeRead(new FilesController.ReadScopeRequest { Scope = "folder", Id = "Saga", Read = true });

        Assert.IsType<OkObjectResult>(result);
        _fileStore.Verify(f => f.MarkScopeReadAsync(
            It.Is<FileReadScope>(s => s.Kind == FileReadScopeKind.Folder && s.FolderKey == "Saga"),
            true,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MarkScopeRead_Series_MarksEveryIssueOfTheSeries()
    {
        var first = InRoot("Saga", "Saga 001.cbz");
        var second = InRoot("Saga", "Saga 002.cbz");
        _seriesLibrary.Setup(s => s.GetSeriesIssuesAsync("saga", null, 1, -1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesIssuesResult
            {
                Id = "saga",
                Issues = new List<SeriesIssueDto> { new() { FilePath = first }, new() { FilePath = second } }
            });

        var result = await _controller.MarkScopeRead(new FilesController.ReadScopeRequest { Scope = "series", Id = "saga", Read = false });

        Assert.IsType<OkObjectResult>(result);
        _fileStore.Verify(f => f.MarkScopeReadAsync(
            It.Is<FileReadScope>(s => s.Kind == FileReadScopeKind.Files && s.FilePaths!.SequenceEqual(new[] { first, second })),
            false,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("series", null, 400)]
    [InlineData("series", "missing", 404)]
    [InlineData("everything", null, 400)]
    public async Task MarkScopeRead_BadScope_ChangesNothing(string scope, string? id, int status)
    {
        var result = await _controller.MarkScopeRead(new FilesController.ReadScopeRequest { Scope = scope, Id = id, Read = true });

        Assert.Equal(status, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
        _fileStore.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task MarkScopeRead_Cancelled_Returns499()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        _fileStore.Setup(f => f.MarkScopeReadAsync(It.IsAny<FileReadScope>(), It.IsAny<bool>(), cancelled.Token))
            .ThrowsAsync(new OperationCanceledException(cancelled.Token));

        var result = await _controller.MarkScopeRead(new FilesController.ReadScopeRequest { Read = true }, cancelled.Token);

        Assert.Equal(499, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
    }

    [Fact]
    public async Task MarkScopeRead_StoreThrows_Escapes()
    {
        _fileStore.Setup(f => f.MarkScopeReadAsync(It.IsAny<FileReadScope>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db locked"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _controller.MarkScopeRead(new FilesController.ReadScopeRequest { Read = true }));
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
    public async Task ProcessFile_ProcessorThrows_Escapes()
    {
        var path = InRoot("Saga 001.cbz");
        _processor.Setup(p => p.ProcessFileAsync(path, It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("corrupt"));

        await Assert.ThrowsAsync<IOException>(() => _controller.ProcessFile(path));
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

    private static IEnumerable<string> Failed(object? value) =>
        Assert.IsAssignableFrom<IEnumerable<string>>(Prop(value, "failed"));

    /// <summary>Makes ApplyMetadataEditAsync succeed or fail for <paramref name="path"/> (any path when null).</summary>
    private void SetupEdit(string? path, ComicMetadata? metadata, bool succeeds)
    {
        _processor.Setup(p => p.ApplyMetadataEditAsync(
                It.Is<string>(file => path == null || file == path),
                It.Is<ComicMetadata>(m => metadata == null || m == metadata),
                It.IsAny<ComicMetadataFieldFlags>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string file, ComicMetadata _, ComicMetadataFieldFlags _, CancellationToken _) =>
                new MetadataWriteResult(succeeds ? MetadataWriteOutcome.Written : MetadataWriteOutcome.Failed, file));
    }

    [Fact]
    public async Task UpdateTags_SkipsBlankEntriesAndUpdatesTheRest()
    {
        var first = InRoot("Saga 001.cbz");
        var second = InRoot("Saga 002.cbz");
        var metadata = new ComicMetadata { Series = "Saga" };
        SetupEdit(null, metadata, succeeds: true);
        var request = new FilesController.UpdateTagsRequest
        {
            Files = new List<string> { first, "", " ", "\t", null!, second },
            Metadata = metadata
        };

        var result = await _controller.UpdateTags(request);

        Assert.IsType<OkResult>(result);
        // Without a field list only the fields that carry a value are applied.
        _processor.Verify(p => p.ApplyMetadataEditAsync(first, metadata, ComicMetadataFieldFlags.Series, It.IsAny<CancellationToken>()), Times.Once);
        _processor.Verify(p => p.ApplyMetadataEditAsync(second, metadata, ComicMetadataFieldFlags.Series, It.IsAny<CancellationToken>()), Times.Once);
        _processor.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UpdateTags_WithAFieldList_AppliesExactlyThoseFields()
    {
        var file = InRoot("Saga 001.cbz");
        var metadata = new ComicMetadata { Series = "Saga" };
        SetupEdit(file, metadata, succeeds: true);

        var result = await _controller.UpdateTags(new FilesController.UpdateTagsRequest
        {
            Files = new List<string> { file },
            Metadata = metadata,
            Fields = new List<string> { "publisher", "Writer" }
        });

        Assert.IsType<OkResult>(result);
        _processor.Verify(p => p.ApplyMetadataEditAsync(
            file, metadata, ComicMetadataFieldFlags.Publisher | ComicMetadataFieldFlags.Authors, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("month")]
    [InlineData("")]
    public async Task UpdateTags_UnknownField_IsRejectedAndUpdatesNothing(string field)
    {
        var result = await _controller.UpdateTags(new FilesController.UpdateTagsRequest
        {
            Files = new List<string> { InRoot("Saga 001.cbz") },
            Metadata = new ComicMetadata { Series = "Saga" },
            Fields = new List<string> { "series", field }
        });

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal($"Unknown metadata field: {field}", Prop(badRequest.Value, "error"));
        _processor.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UpdateTags_NothingToApply_IsRejectedAndUpdatesNothing()
    {
        var result = await _controller.UpdateTags(new FilesController.UpdateTagsRequest
        {
            Files = new List<string> { InRoot("Saga 001.cbz") },
            Metadata = new ComicMetadata()
        });

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("No metadata fields specified", Prop(badRequest.Value, "error"));
        _processor.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UpdateTags_NoFilesToUpdate_ReturnsOk()
    {
        var result = await _controller.UpdateTags(new FilesController.UpdateTagsRequest
        {
            Files = new List<string> { "", null! }
        });

        Assert.IsType<OkResult>(result);
        _processor.VerifyNoOtherCalls();
    }

    // The processor answers false for a file it could not write: missing, not a
    // comic archive, or unreadable.
    [Fact]
    public async Task UpdateTags_NoFileCouldBeUpdated_ReturnsBadRequestListingTheFiles()
    {
        var missing = InRoot("Missing 001.cbz");
        var notAnArchive = InRoot("Notes.txt");
        SetupEdit(null, null, succeeds: false);

        var result = await _controller.UpdateTags(new FilesController.UpdateTagsRequest
        {
            Files = new List<string> { missing, "", notAnArchive },
            Metadata = new ComicMetadata { Series = "X" }
        });

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("Failed to update tags", Prop(badRequest.Value, "error"));
        Assert.Equal(new[] { missing, notAnArchive }, Failed(badRequest.Value));
    }

    [Fact]
    public async Task UpdateTags_SomeFilesCouldNotBeUpdated_Returns207ListingThoseAndUpdatesTheRest()
    {
        var missing = InRoot("Missing 001.cbz");
        var updated = InRoot("Saga 002.cbz");
        var metadata = new ComicMetadata { Series = "Saga" };
        SetupEdit(missing, metadata, succeeds: false);
        SetupEdit(updated, metadata, succeeds: true);

        var result = await _controller.UpdateTags(new FilesController.UpdateTagsRequest
        {
            Files = new List<string> { missing, updated },
            Metadata = metadata
        });

        var multiStatus = Assert.IsType<ObjectResult>(result);
        Assert.Equal(207, multiStatus.StatusCode);
        Assert.Equal(new[] { missing }, Failed(multiStatus.Value));
        _processor.Verify(p => p.ApplyMetadataEditAsync(updated, metadata, It.IsAny<ComicMetadataFieldFlags>(), It.IsAny<CancellationToken>()), Times.Once);
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
    public async Task UpdateTags_ProcessorThrows_Escapes()
    {
        _processor.Setup(p => p.ApplyMetadataEditAsync(
                It.IsAny<string>(), It.IsAny<ComicMetadata>(), It.IsAny<ComicMetadataFieldFlags>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("read-only"));

        await Assert.ThrowsAsync<IOException>(() => _controller.UpdateTags(new FilesController.UpdateTagsRequest
        {
            Files = new List<string> { InRoot("Saga 001.cbz") },
            Metadata = new ComicMetadata { Series = "Saga" }
        }));
    }

    // A body without "files" binds to an empty list. The {"file": ..., "tags": ...}
    // body docs/API.md used to show answered 200 having updated nothing.
    [Fact]
    public async Task UpdateTags_EmptyFileList_ReturnsBadRequestAndUpdatesNothing()
    {
        var result = await _controller.UpdateTags(new FilesController.UpdateTagsRequest
        {
            Metadata = new ComicMetadata { Series = "Saga" }
        });

        AssertStatus(result, 400, "No files provided");
        _processor.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UpdateTags_NullFileList_ReturnsBadRequestAndUpdatesNothing()
    {
        var result = await _controller.UpdateTags(new FilesController.UpdateTagsRequest
        {
            Files = null!,
            Metadata = new ComicMetadata { Series = "Saga" }
        });

        AssertStatus(result, 400, "No files provided");
        _processor.VerifyNoOtherCalls();
    }

    // ---------------------------------------------------------------- ScanUnmarked

    [Fact]
    public async Task ScanUnmarked_StoreThrows_Escapes()
    {
        _fileStore.Setup(f => f.GetAllFilesAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("not loaded"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _controller.ScanUnmarked());
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
    public async Task ProcessFileByEncodedPath_ProcessorThrows_Escapes()
    {
        var path = InRoot("Saga 001.cbz");
        _processor.Setup(p => p.ProcessFileAsync(path, true, It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("corrupt"));

        await Assert.ThrowsAsync<IOException>(() => _controller.ProcessFileByEncodedPath(Encode(path), forceReprocess: true));
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
    [Obsolete("Covers the deprecated /api/rename-file alias until it is removed in v3.0.")]
    public async Task RenameSingleFile_PathOutsideWatchedDirectory_ReturnsBadRequest(string path)
    {
        var result = await _controller.RenameSingleFile(Expand(path));

        AssertStatus(result, 400, "File path is outside the allowed directory");
        VerifyWarned("Attempt to rename file outside watched directory");
        _processor.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RenameFileByEncodedPath_ProcessorThrows_Escapes()
    {
        var path = InRoot("Saga 001.cbz");
        _processor.Setup(p => p.RenameFilesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("queue full"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _controller.RenameFileByEncodedPath(Encode(path)));
    }

    // --------------------------------------------------------- CleanupStaleEntries

    [Fact]
    public async Task CleanupStaleEntries_ReportsRemovedCount()
    {
        using var cts = new CancellationTokenSource();
        _fileStore
            .Setup(f => f.CleanupStaleEntriesAsync(new StaleCleanupOptions(IsManual: true, Confirmed: false), cts.Token))
            .ReturnsAsync(new StaleCleanupResult
            {
                Status = StaleCleanupStatus.Completed,
                RemovedCount = 3,
                TrackedCount = 40,
                Message = "Removed 3 stale database entries.",
            });

        var result = await _controller.CleanupStaleEntries(cancellationToken: cts.Token);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(true, Prop(ok.Value, "success"));
        Assert.Equal(3, Prop(ok.Value, "removedCount"));
        Assert.Equal(40, Prop(ok.Value, "trackedCount"));
        Assert.Equal("Removed 3 stale database entries.", Prop(ok.Value, "message"));
        Assert.Equal(false, Prop(ok.Value, "requiresConfirmation"));
    }

    [Fact]
    public async Task CleanupStaleEntries_OverTheRemovalShare_AsksForConfirmation()
    {
        _fileStore
            .Setup(f => f.CleanupStaleEntriesAsync(It.IsAny<StaleCleanupOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StaleCleanupResult
            {
                Status = StaleCleanupStatus.ThresholdExceeded,
                TrackedCount = 100,
                RemovableCount = 60,
                MaxRemovalPercent = 10,
                Message = "60 of 100 tracked files are missing.",
            });

        var result = await _controller.CleanupStaleEntries();

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(false, Prop(conflict.Value, "success"));
        Assert.Equal(true, Prop(conflict.Value, "requiresConfirmation"));
        Assert.Equal("ThresholdExceeded", Prop(conflict.Value, "status"));
        Assert.Equal(60, Prop(conflict.Value, "removableCount"));
        Assert.Equal(0, Prop(conflict.Value, "removedCount"));
        Assert.Equal("60 of 100 tracked files are missing.", Prop(conflict.Value, "error"));
    }

    [Fact]
    public async Task CleanupStaleEntries_Confirmed_PassesTheConfirmationToTheStore()
    {
        _fileStore
            .Setup(f => f.CleanupStaleEntriesAsync(It.IsAny<StaleCleanupOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StaleCleanupResult { Status = StaleCleanupStatus.Completed, RemovedCount = 60 });

        var result = await _controller.CleanupStaleEntries(confirm: true);

        Assert.IsType<OkObjectResult>(result);
        _fileStore.Verify(f => f.CleanupStaleEntriesAsync(new StaleCleanupOptions(IsManual: true, Confirmed: true), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CleanupStaleEntries_LibraryUnavailable_IsRefusedWithoutAConfirmationOffer()
    {
        _fileStore
            .Setup(f => f.CleanupStaleEntriesAsync(It.IsAny<StaleCleanupOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StaleCleanupResult { Status = StaleCleanupStatus.WatchedDirectoryUnavailable, Message = "unmounted" });

        var result = await _controller.CleanupStaleEntries(confirm: true);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(false, Prop(conflict.Value, "requiresConfirmation"));
        Assert.Equal("WatchedDirectoryUnavailable", Prop(conflict.Value, "status"));
    }

    [Fact]
    public async Task CleanupStaleEntries_Cancelled_Returns499()
    {
        _fileStore
            .Setup(f => f.CleanupStaleEntriesAsync(It.IsAny<StaleCleanupOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StaleCleanupResult { Status = StaleCleanupStatus.Cancelled, Message = "The cleanup was cancelled. No rows were removed." });

        var result = await _controller.CleanupStaleEntries();

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(499, objectResult.StatusCode);
        Assert.Equal("The cleanup was cancelled. No rows were removed.", Prop(objectResult.Value, "error"));
    }

    [Fact]
    public async Task CleanupStaleEntries_StoreFailed_Returns500()
    {
        _fileStore
            .Setup(f => f.CleanupStaleEntriesAsync(It.IsAny<StaleCleanupOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StaleCleanupResult { Status = StaleCleanupStatus.Failed, Message = "The cleanup failed. No rows were removed." });

        var result = await _controller.CleanupStaleEntries();

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(500, objectResult.StatusCode);
        Assert.Equal("The cleanup failed. No rows were removed.", Prop(objectResult.Value, "error"));
    }
}
