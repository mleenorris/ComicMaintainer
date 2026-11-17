using System.Text.Json;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using ComicMaintainer.Tests.Helpers;
using ComicMaintainer.WebApi.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ComicMaintainer.Tests.Controllers;

/// <summary>
/// <c>GET /api/duplicates/compare</c> describes a duplicate and the file that has its name side
/// by side; <c>POST /api/duplicates/swap</c> hands the swap to the processor and maps its outcome.
/// </summary>
public sealed class DuplicatesControllerTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), $"duplicates_controller_{Guid.NewGuid():N}");
    private readonly Mock<IComicProcessorService> _processor = new();
    private readonly Mock<IFileStoreService> _fileStore = new();
    private readonly Mock<IComicReaderService> _reader = new();
    private readonly DuplicatesController _controller;
    private readonly string _duplicate;
    private readonly string _holder;

    public DuplicatesControllerTests()
    {
        Directory.CreateDirectory(Path.Join(_root, "Saga"));
        _duplicate = Path.Join(_root, "Saga", "Saga 1.cbz");
        _holder = Path.Join(_root, "Saga", "Saga 001.cbz");
        File.WriteAllBytes(_duplicate, new byte[100]);
        File.WriteAllBytes(_holder, new byte[250]);

        _controller = new DuplicatesController(
            _processor.Object,
            _fileStore.Object,
            _reader.Object,
            new TestOptionsMonitor<AppSettings>(new AppSettings { WatchedDirectory = _root, DuplicateDirectory = Path.Join(_root, "duplicates") }),
            NullLogger<DuplicatesController>.Instance);
    }

    public void Dispose() => TestIO.TryDeleteDirectory(_root);

    [Fact]
    public async Task Compare_DescribesBothFilesAndTheNameEachWouldGet()
    {
        _processor.Setup(p => p.FindDuplicateClashAsync(_duplicate, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DuplicateClash(DuplicateClashStatus.Clashes, _holder, _holder));
        _processor.Setup(p => p.FindDuplicateClashAsync(_holder, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DuplicateClash(DuplicateClashStatus.Free, Path.Join(_root, "Saga", "Saga Annual 001.cbz")));
        _processor.Setup(p => p.GetMetadataAsync(_duplicate, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComicMetadata { Series = "Saga", Issue = "1", Volume = "1", VolumeYear = 2012, Format = null, Title = "Chapter 1", Year = 2012 });
        _processor.Setup(p => p.GetMetadataAsync(_holder, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComicMetadata { Series = "Saga Annual", Issue = "1", Format = "Annual" });
        _fileStore.Setup(s => s.GetFileAsync(_duplicate, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComicFile { FilePath = _duplicate, IsDuplicate = true });
        _reader.Setup(r => r.GetPageCountAsync(_duplicate)).ReturnsAsync(22);
        _reader.Setup(r => r.GetPageCountAsync(_holder)).ReturnsAsync(0);

        var root = Json(await _controller.Compare(_duplicate, CancellationToken.None));

        Assert.Equal("clashes", root.GetProperty("status").GetString());
        var file = root.GetProperty("file");
        Assert.Equal(_duplicate, file.GetProperty("file_path").GetString());
        Assert.Equal("Saga 1.cbz", file.GetProperty("name").GetString());
        Assert.Equal(100, file.GetProperty("size").GetInt64());
        Assert.True(file.GetProperty("modified").GetInt64() > 0);
        Assert.Equal(22, file.GetProperty("page_count").GetInt32());
        Assert.True(file.GetProperty("duplicate").GetBoolean());
        Assert.Equal("Saga 001.cbz", file.GetProperty("processed_name").GetString());
        var tags = file.GetProperty("tags");
        Assert.Equal("Saga", tags.GetProperty("series").GetString());
        Assert.Equal("1", tags.GetProperty("number").GetString());
        Assert.Equal("1", tags.GetProperty("volume").GetString());
        Assert.Equal(2012, tags.GetProperty("volume_year").GetInt32());
        Assert.Equal(JsonValueKind.Null, tags.GetProperty("format").ValueKind);
        Assert.Equal("Chapter 1", tags.GetProperty("title").GetString());
        Assert.Equal(2012, tags.GetProperty("year").GetInt32());

        var other = root.GetProperty("other");
        Assert.Equal(_holder, other.GetProperty("file_path").GetString());
        Assert.Equal(250, other.GetProperty("size").GetInt64());
        Assert.False(other.GetProperty("duplicate").GetBoolean());
        Assert.Equal(JsonValueKind.Null, other.GetProperty("page_count").ValueKind);
        Assert.Equal("Saga Annual 001.cbz", other.GetProperty("processed_name").GetString());
        Assert.Equal("Annual", other.GetProperty("tags").GetProperty("format").GetString());
    }

    [Fact]
    public async Task Compare_NameNoLongerTaken_HasNoOtherFile()
    {
        _processor.Setup(p => p.FindDuplicateClashAsync(_duplicate, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DuplicateClash(DuplicateClashStatus.Free, _holder));
        _reader.Setup(r => r.GetPageCountAsync(_duplicate)).ThrowsAsync(new ArchiveLimitExceededException("too many entries"));

        var root = Json(await _controller.Compare(_duplicate, CancellationToken.None));

        Assert.Equal("free", root.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("other").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("file").GetProperty("tags").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("file").GetProperty("page_count").ValueKind);
    }

    [Fact]
    public async Task Compare_NameCannotBeWorkedOut_SaysWhy()
    {
        _processor.Setup(p => p.FindDuplicateClashAsync(_duplicate, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DuplicateClash(DuplicateClashStatus.Unknown, null, Reason: "No series"));

        var root = Json(await _controller.Compare(_duplicate, CancellationToken.None));

        Assert.Equal("unknown", root.GetProperty("status").GetString());
        Assert.Equal("No series", root.GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("file").GetProperty("processed_name").ValueKind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public async Task Compare_WithoutAPath_IsRejected(string? path)
    {
        Assert.IsType<BadRequestObjectResult>((await _controller.Compare(path, CancellationToken.None)).Result);
    }

    [Fact]
    public async Task Compare_PathOutsideTheLibrary_IsRejected()
    {
        var outside = Path.Join(Path.GetTempPath(), "elsewhere.cbz");

        Assert.IsType<BadRequestObjectResult>((await _controller.Compare(outside, CancellationToken.None)).Result);
        _processor.Verify(p => p.FindDuplicateClashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Compare_MissingFile_IsNotFound()
    {
        var missing = Path.Join(_root, "Saga", "gone.cbz");

        Assert.IsType<NotFoundObjectResult>((await _controller.Compare(missing, CancellationToken.None)).Result);
    }

    [Fact]
    public async Task Swap_Succeeded_ReturnsWhereEachFileIsNow()
    {
        var aside = Path.Join(_root, "Saga", "Saga 001 (duplicate).cbz");
        _processor.Setup(p => p.SwapDuplicateAsync(_duplicate, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DuplicateSwapResult(true, _holder, aside));

        var result = await _controller.Swap(new DuplicatesController.DuplicateSwapRequest { FilePath = _duplicate }, CancellationToken.None);

        var root = Json(result);
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal(_holder, root.GetProperty("file_path").GetString());
        Assert.Equal(aside, root.GetProperty("duplicate_path").GetString());
    }

    [Fact]
    public async Task Swap_Conflict_Is409()
    {
        _processor.Setup(p => p.SwapDuplicateAsync(_duplicate, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DuplicateSwapResult(false, Error: "changed", Conflict: true));

        var result = await _controller.Swap(new DuplicatesController.DuplicateSwapRequest { FilePath = _duplicate }, CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result.Result);
    }

    [Fact]
    public async Task Swap_Failed_Is400()
    {
        _processor.Setup(p => p.SwapDuplicateAsync(_duplicate, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DuplicateSwapResult(false, Error: "could not process"));

        var result = await _controller.Swap(new DuplicatesController.DuplicateSwapRequest { FilePath = _duplicate }, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("could not process", JsonSerializer.Serialize(badRequest.Value));
    }

    [Fact]
    public async Task Swap_PathOutsideTheLibrary_IsRejected()
    {
        var result = await _controller.Swap(
            new DuplicatesController.DuplicateSwapRequest { FilePath = Path.Join(Path.GetTempPath(), "elsewhere.cbz") },
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        _processor.Verify(p => p.SwapDuplicateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static JsonElement Json(ActionResult<object> result) =>
        JsonDocument.Parse(JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(result.Result).Value)).RootElement;
}
