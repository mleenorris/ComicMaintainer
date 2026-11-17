using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Tests.Helpers;
using ComicMaintainer.WebApi.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json.Linq;

namespace ComicMaintainer.Tests.Controllers;

/// <summary>
/// The processing preview routes under <c>/api/files/process-preview</c>: one file at once, a
/// scope as a job, and the job's results in pages.
/// </summary>
public sealed class ProcessPreviewControllerTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "preview-ctl-" + Guid.NewGuid().ToString("N"));
    private readonly Mock<IComicProcessorService> _processor = new();
    private readonly Mock<IProcessingPreviewService> _previews = new();
    private readonly Mock<IFileStoreService> _fileStore = new();
    private readonly Mock<ISeriesLibraryService> _seriesLibrary = new();
    private readonly Guid _jobId = Guid.NewGuid();
    private List<string>? _started;

    public ProcessPreviewControllerTests()
    {
        Directory.CreateDirectory(_root);
        _previews.Setup(p => p.StartPreview(It.IsAny<IEnumerable<string>>()))
            .Callback<IEnumerable<string>>(files => _started = files.ToList())
            .Returns(_jobId);
    }

    public void Dispose() => TestIO.TryDeleteDirectory(_root);

    private string InLibrary(string relative) => Path.Join(_root, relative);

    private ProcessPreviewController CreateController() => new(
        _processor.Object,
        _previews.Object,
        _fileStore.Object,
        _seriesLibrary.Object,
        new TestOptionsMonitor<AppSettings>(new AppSettings { WatchedDirectory = _root, DuplicateDirectory = Path.Join(_root, "duplicates") }),
        NullLogger<ProcessPreviewController>.Instance)
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
    };

    private static JObject Json(object? value) => JObject.FromObject(value!);

    private static string? Error(IActionResult result) =>
        (string?)Json(Assert.IsAssignableFrom<ObjectResult>(result).Value)["error"];

    [Fact]
    public async Task PreviewFile_AnswersWithTheFilesPreview()
    {
        var path = InLibrary("Batman/batman 1.cbz");
        _processor.Setup(p => p.PreviewProcessingAsync(path, It.IsAny<CancellationToken>())).ReturnsAsync(new ProcessingPreviewItem
        {
            FilePath = path,
            Series = "Batman",
            NewFilePath = InLibrary("Batman/Batman - Chapter 0001.cbz"),
            TagChanges = [new ComicInfoFieldChange("Series", "batman", "Batman")]
        });

        var result = await CreateController().PreviewFile(path, CancellationToken.None);

        var body = Json(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(path, (string?)body["filePath"]);
        Assert.Equal("batman 1.cbz", (string?)body["fileName"]);
        Assert.Equal("Batman", (string?)body["series"]);
        Assert.Equal("Batman - Chapter 0001.cbz", (string?)body["newFileName"]);
        Assert.True((bool)body["renamed"]!);
        Assert.True((bool)body["changed"]!);
        Assert.Null((string?)body["clash"]);
        var change = body["tagChanges"]!.Single();
        Assert.Equal("Series", (string?)change["field"]);
        Assert.Equal("batman", (string?)change["before"]);
        Assert.Equal("Batman", (string?)change["after"]);
    }

    [Fact]
    public async Task PreviewFile_RefusesAMissingOrOutsidePath()
    {
        var controller = CreateController();

        Assert.Equal("File path is required", Error((await controller.PreviewFile(" ", CancellationToken.None)).Result!));
        var outside = Path.Join(Path.GetTempPath(), "elsewhere", "x.cbz");
        Assert.Equal("File path is outside the allowed directory", Error((await controller.PreviewFile(outside, CancellationToken.None)).Result!));
        _processor.Verify(p => p.PreviewProcessingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StartPreview_AllScope_TakesEveryFileButDuplicates()
    {
        _fileStore.Setup(f => f.GetAllFilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new ComicFile { FilePath = InLibrary("a.cbz") },
            new ComicFile { FilePath = InLibrary("b.cbz"), IsDuplicate = true },
            new ComicFile { FilePath = InLibrary("c.cbz") }
        ]);

        var result = await CreateController().StartPreview(new ProcessPreviewController.PreviewRequest { Scope = " ALL " }, CancellationToken.None);

        var response = Assert.IsType<JobsController.BatchJobResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(_jobId.ToString(), response.JobId);
        Assert.Equal(2, response.TotalItems);
        Assert.Equal(new[] { InLibrary("a.cbz"), InLibrary("c.cbz") }, _started);
    }

    [Fact]
    public async Task StartPreview_SelectedScope_TakesTheNamedFiles()
    {
        var a = InLibrary("a.cbz");
        var result = await CreateController().StartPreview(
            new ProcessPreviewController.PreviewRequest { Scope = "selected", Files = [a, "", a, InLibrary("b.cbz")] },
            CancellationToken.None);

        Assert.Equal(2, Assert.IsType<JobsController.BatchJobResponse>(Assert.IsType<OkObjectResult>(result.Result).Value).TotalItems);
        Assert.Equal(new[] { a, InLibrary("b.cbz") }, _started);
    }

    [Fact]
    public async Task StartPreview_SelectedScope_RefusesNoFilesOrAFileOutside()
    {
        var controller = CreateController();

        Assert.Equal("No files specified", Error((await controller.StartPreview(
            new ProcessPreviewController.PreviewRequest { Scope = "selected", Files = [" "] }, CancellationToken.None)).Result!));
        Assert.Equal("No files specified", Error((await controller.StartPreview(
            new ProcessPreviewController.PreviewRequest { Scope = "selected" }, CancellationToken.None)).Result!));
        Assert.Equal(JobsController.OutsideAllowedDirectoryError, Error((await controller.StartPreview(
            new ProcessPreviewController.PreviewRequest { Scope = "selected", Files = [InLibrary("a.cbz"), Path.Join(Path.GetTempPath(), "x.cbz")] },
            CancellationToken.None)).Result!));
        Assert.Null(_started);
    }

    [Fact]
    public async Task StartPreview_SeriesScope_TakesItsIssuesButDuplicates()
    {
        _seriesLibrary.Setup(s => s.GetSeriesIssuesAsync("batman", null, 1, -1, It.IsAny<CancellationToken>())).ReturnsAsync(new SeriesIssuesResult
        {
            Id = "batman",
            Title = "Batman",
            Issues =
            [
                new SeriesIssueDto { FilePath = InLibrary("Batman/1.cbz") },
                new SeriesIssueDto { FilePath = InLibrary("Batman/2.cbz"), Duplicate = true },
                new SeriesIssueDto { FilePath = "" }
            ]
        });

        var result = await CreateController().StartPreview(
            new ProcessPreviewController.PreviewRequest { Scope = "series", SeriesId = "batman" }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(new[] { InLibrary("Batman/1.cbz") }, _started);
    }

    [Fact]
    public async Task StartPreview_SeriesScope_NeedsAKnownSeries()
    {
        var controller = CreateController();

        Assert.Equal("seriesId is required", Error((await controller.StartPreview(
            new ProcessPreviewController.PreviewRequest { Scope = "series" }, CancellationToken.None)).Result!));
        var missing = await controller.StartPreview(
            new ProcessPreviewController.PreviewRequest { Scope = "series", SeriesId = "nope" }, CancellationToken.None);
        Assert.IsType<NotFoundObjectResult>(missing.Result);
        Assert.Equal("Series not found", Error(missing.Result!));
    }

    [Fact]
    public async Task StartPreview_RefusesAnUnknownScope_AndStartsNothingForNoFiles()
    {
        var controller = CreateController();

        Assert.Equal("scope must be all, selected or series", Error((await controller.StartPreview(null, CancellationToken.None)).Result!));
        Assert.Equal("scope must be all, selected or series", Error((await controller.StartPreview(
            new ProcessPreviewController.PreviewRequest { Scope = "unmarked" }, CancellationToken.None)).Result!));

        _fileStore.Setup(f => f.GetAllFilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var empty = await controller.StartPreview(new ProcessPreviewController.PreviewRequest { Scope = "all" }, CancellationToken.None);
        var response = Assert.IsType<JobsController.BatchJobResponse>(Assert.IsType<OkObjectResult>(empty.Result).Value);
        Assert.Equal(Guid.Empty.ToString(), response.JobId);
        Assert.Equal(0, response.TotalItems);
        Assert.Null(_started);
    }

    [Fact]
    public void GetResults_SendsTheSummaryAndThePage()
    {
        var item = new ProcessingPreviewItem
        {
            FilePath = InLibrary("S/two.cbz"),
            Series = "S",
            NewFilePath = InLibrary("S/S 1.cbz"),
            Clash = ProcessingPreviewClash.SameTargetInPreview,
            ClashWith = InLibrary("S/one.cbz"),
            Warning = "w",
            Error = null
        };
        var existing = item with { Clash = ProcessingPreviewClash.ExistingFile, ClashWith = InLibrary("S/S 1.cbz") };
        _previews.Setup(p => p.GetResults(_jobId, ProcessingPreviewFilter.Clashes, 5, 200)).Returns(new ProcessingPreviewResults(
            _jobId, "running", 40, false,
            new ProcessingPreviewSummary(10, 4, 1, 2, 2, 6, 1),
            ProcessingPreviewFilter.Clashes, 5, 200, 7, [item, existing]));

        var result = CreateController().GetResults(_jobId, "Clashes", 5, 5000);

        var body = Json(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(_jobId.ToString(), (string?)body["jobId"]);
        Assert.Equal("running", (string?)body["status"]);
        Assert.False((bool)body["finished"]!);
        Assert.Equal(40, (int)body["totalFiles"]!);
        Assert.Equal("clashes", (string?)body["filter"]);
        Assert.Equal(5, (int)body["offset"]!);
        Assert.Equal(200, (int)body["limit"]!);
        Assert.Equal(7, (int)body["total"]!);
        var summary = body["summary"]!;
        Assert.Equal(
            new[] { 10, 4, 1, 2, 2, 6, 1 },
            new[] { "checked", "changed", "renamed", "tagsChanged", "clashes", "unchanged", "errors" }.Select(name => (int)summary[name]!));
        var items = body["items"]!.ToArray();
        Assert.Equal("preview", (string?)items[0]["clash"]);
        Assert.Equal(InLibrary("S/one.cbz"), (string?)items[0]["clashWith"]);
        Assert.Equal("w", (string?)items[0]["warning"]);
        Assert.False((bool)items[0]["renamed"]!);
        Assert.Equal("existing", (string?)items[1]["clash"]);
    }

    [Fact]
    public void GetResults_DefaultsToChanges_AndRefusesAnUnknownFilterOrPreview()
    {
        var controller = CreateController();
        _previews.Setup(p => p.GetResults(_jobId, ProcessingPreviewFilter.Changes, 0, 200)).Returns(new ProcessingPreviewResults(
            _jobId, "completed", 0, true, new ProcessingPreviewSummary(0, 0, 0, 0, 0, 0, 0),
            ProcessingPreviewFilter.Changes, 0, 200, 0, []));

        Assert.Equal("changes", (string?)Json(Assert.IsType<OkObjectResult>(controller.GetResults(_jobId).Result).Value)["filter"]);
        Assert.Equal("Unknown filter: 3", Error(controller.GetResults(_jobId, "3").Result!));
        var missing = controller.GetResults(Guid.NewGuid(), "all", 0, 0);
        Assert.IsType<NotFoundObjectResult>(missing.Result);
        Assert.StartsWith("Preview not found", Error(missing.Result!));
    }
}
