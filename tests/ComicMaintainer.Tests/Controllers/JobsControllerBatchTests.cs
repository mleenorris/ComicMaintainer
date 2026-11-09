using System.Text.Json;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Tests.Helpers;
using ComicMaintainer.WebApi.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ComicMaintainer.Tests.Controllers;

/// <summary>
/// The one scope + operation path behind every <c>POST /api/jobs/{operation}-{scope}</c> route:
/// which combinations exist, which files each selects, and what is sent back. That the URLs
/// themselves still route here is covered in BatchPathConfinementTests.
/// </summary>
public class JobsControllerBatchTests
{
    private readonly Mock<IComicProcessorService> _processor = new();
    private readonly Mock<IFileStoreService> _fileStore = new();
    private readonly JobsController _controller;
    private readonly Guid _jobId = Guid.NewGuid();

    private List<string>? _queued;
    private bool? _queuedForce;
    private CancellationToken? _queuedToken;

    public JobsControllerBatchTests()
    {
        _controller = CreateController(new AppSettings { WatchedDirectory = "/c", DuplicateDirectory = "/duplicates" });

        void Capture(IEnumerable<string> files, bool? force, CancellationToken token)
        {
            _queued = files.ToList();
            _queuedForce = force;
            _queuedToken = token;
        }

        _processor.Setup(p => p.ProcessFilesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<string>, bool, CancellationToken>((f, force, t) => Capture(f, force, t)).ReturnsAsync(_jobId);
        _processor.Setup(p => p.RenameFilesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<string>, bool, CancellationToken>((f, force, t) => Capture(f, force, t)).ReturnsAsync(_jobId);
        _processor.Setup(p => p.NormalizeFilesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<string>, bool, CancellationToken>((f, force, t) => Capture(f, force, t)).ReturnsAsync(_jobId);
        _processor.Setup(p => p.RemoveMetadataFromFilesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<string>, CancellationToken>((f, t) => Capture(f, null, t)).ReturnsAsync(_jobId);
        _processor.Setup(p => p.DeleteFilesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<string>, CancellationToken>((f, t) => Capture(f, null, t)).ReturnsAsync(_jobId);
        _processor.Setup(p => p.UpdateMetadataAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<ComicMetadata>(), It.IsAny<ComicMetadataFieldFlags>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<string>, ComicMetadata, ComicMetadataFieldFlags, CancellationToken>((f, _, _, t) => Capture(f, null, t)).ReturnsAsync(_jobId);
    }

    private JobsController CreateController(AppSettings settings) => new(
        _processor.Object,
        Mock.Of<IBatchJobRegistry>(),
        _fileStore.Object,
        NullLogger<JobsController>.Instance,
        new TestOptionsMonitor<AppSettings>(settings));

    private void SetupAllFiles(params ComicFile[] files) =>
        _fileStore.Setup(f => f.GetAllFilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(files);

    private void SetupUnprocessedFiles(params string[] paths) =>
        _fileStore.Setup(f => f.GetFilteredFilesAsync("unprocessed", It.IsAny<CancellationToken>()))
            .ReturnsAsync(paths.Select(p => new ComicFile { FilePath = p }).ToList());

    private static JobsController.BatchJobResponse Response(ActionResult<JobsController.BatchJobResponse> result) =>
        Assert.IsType<JobsController.BatchJobResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);

    private static string ErrorOf(ActionResult<JobsController.BatchJobResponse> result)
    {
        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        return (string)badRequest.Value!.GetType().GetProperty("error")!.GetValue(badRequest.Value)!;
    }

    private void VerifyNothingQueued() => Assert.Null(_queued);

    // ---- which combinations exist --------------------------------------------------------

    [Theory]
    [InlineData("delete", "all")]
    [InlineData("delete", "unmarked")]
    [InlineData("update-metadata", "all")]
    [InlineData("update-metadata", "unmarked")]
    [InlineData("remove-metadata", "unmarked")]
    [InlineData("bogus", "all")]
    [InlineData("process", "bogus")]
    [InlineData("process", "none")]
    public async Task UnknownOperationAndScopeCombinations_AreNotFound(string operation, string scope)
    {
        var result = await _controller.StartBatchJob(operation, scope, new JobsController.BatchJobRequest { Files = new() { "/c/a.cbz" } });

        Assert.IsType<NotFoundResult>(result.Result);
        VerifyNothingQueued();
    }

    [Fact]
    public async Task RouteWordsMatchInAnyCase_LikeTheRoutesThemselves()
    {
        SetupAllFiles(new ComicFile { FilePath = "/c/a.cbz" });

        var response = Response(await _controller.StartBatchJob("PROCESS", "All", null));

        Assert.Equal(_jobId.ToString(), response.JobId);
    }

    // ---- the all scope ----------------------------------------------------------------------

    [Theory]
    [InlineData("process")]
    [InlineData("rename")]
    [InlineData("normalize")]
    [InlineData("remove-metadata")]
    public async Task AllScope_QueuesEveryTrackedFileExceptDuplicates(string operation)
    {
        SetupAllFiles(
            new ComicFile { FilePath = "/c/a.cbz" },
            new ComicFile { FilePath = "/duplicates/a.cbz", IsDuplicate = true },
            new ComicFile { FilePath = "/c/b.cbz", IsProcessed = true });

        var response = Response(await _controller.StartBatchJob(operation, "all", null));

        Assert.Equal(new[] { "/c/a.cbz", "/c/b.cbz" }, _queued);
        Assert.Equal(_jobId.ToString(), response.JobId);
        Assert.Equal(2, response.TotalItems);
    }

    [Theory]
    [InlineData("process")]
    [InlineData("remove-metadata")]
    public async Task AllScope_NoEligibleFiles_ReturnsAnEmptyJobWithoutQueuing(string operation)
    {
        SetupAllFiles(new ComicFile { FilePath = "/duplicates/a.cbz", IsDuplicate = true });

        var response = Response(await _controller.StartBatchJob(operation, "all", null));

        Assert.Equal(Guid.Empty.ToString(), response.JobId);
        Assert.Equal(0, response.TotalItems);
        VerifyNothingQueued();
    }

    // ---- the unmarked scope -----------------------------------------------------------------

    [Theory]
    [InlineData("process")]
    [InlineData("rename")]
    [InlineData("normalize")]
    public async Task UnmarkedScope_QueuesTheUnprocessedFiles(string operation)
    {
        SetupUnprocessedFiles("/c/a.cbz", "/c/b.cbz");

        var response = Response(await _controller.StartBatchJob(operation, "unmarked", null));

        Assert.Equal(new[] { "/c/a.cbz", "/c/b.cbz" }, _queued);
        Assert.Equal(2, response.TotalItems);
    }

    [Fact]
    public async Task UnmarkedScope_NoUnprocessedFiles_ReturnsAnEmptyJobWithoutQueuing()
    {
        SetupUnprocessedFiles();

        var response = Response(await _controller.StartBatchJob("rename", "unmarked", null));

        Assert.Equal(Guid.Empty.ToString(), response.JobId);
        VerifyNothingQueued();
    }

    // ---- the selected scope ------------------------------------------------------------------

    [Theory]
    [InlineData("process")]
    [InlineData("rename")]
    [InlineData("normalize")]
    [InlineData("remove-metadata")]
    [InlineData("delete")]
    public async Task SelectedScope_TakesTheNamedFilesAsNamed_DuplicatesIncluded(string operation)
    {
        var request = new JobsController.BatchJobRequest { Files = new() { "/c/a.cbz", "/duplicates/a.cbz" } };

        var response = Response(await _controller.StartBatchJob(operation, "selected", request));

        Assert.Equal(request.Files, _queued);
        Assert.Equal(2, response.TotalItems);
        _fileStore.Verify(f => f.GetAllFilesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SelectedScope_DropsBlankEntries()
    {
        var request = new JobsController.BatchJobRequest { Files = new() { "/c/a.cbz", "", "  ", null! } };

        var response = Response(await _controller.StartBatchJob("delete", "selected", request));

        Assert.Equal(new[] { "/c/a.cbz" }, _queued);
        Assert.Equal(1, response.TotalItems);
    }

    public static TheoryData<List<string>?> NoFiles => new()
    {
        null,
        new List<string>(),
        new List<string> { "", "  " },
    };

    [Theory]
    [MemberData(nameof(NoFiles))]
    public async Task SelectedScope_WithoutFiles_IsRejected(List<string>? files)
    {
        var result = await _controller.StartBatchJob("process", "selected", new JobsController.BatchJobRequest { Files = files });

        Assert.Equal("No files specified", ErrorOf(result));
        VerifyNothingQueued();
    }

    [Fact]
    public async Task SelectedScope_WithoutABody_IsRejected()
    {
        var result = await _controller.StartBatchJob("rename", "selected", null);

        Assert.Equal("No files specified", ErrorOf(result));
    }

    [Theory]
    [InlineData("process", "/c/../Config/victim.txt")]
    [InlineData("rename", "/c-private/a.cbz")]
    [InlineData("normalize", "../../etc/passwd")]
    [InlineData("remove-metadata", "/elsewhere/a.cbz")]
    [InlineData("update-metadata", "/elsewhere/a.cbz")]
    [InlineData("delete", "/elsewhere/a.cbz")]
    [InlineData("delete", "/c/series/..")]
    [InlineData("delete", "/c/missing/../a.cbz")]
    [InlineData("delete", "/c/a.cbz/")]
    [InlineData("delete", "/c/a.cbz/.")]
    public async Task SelectedScope_APathItsJobWouldRefuse_RejectsTheWholeBatch(string operation, string path)
    {
        var request = new JobsController.BatchJobRequest
        {
            Files = new() { "/c/a.cbz", path },
            Metadata = new ComicMetadata { Series = "Series" }
        };

        var result = await _controller.StartBatchJob(operation, "selected", request);

        Assert.Equal(JobsController.OutsideAllowedDirectoryError, ErrorOf(result));
        VerifyNothingQueued();
    }

    [Theory]
    [InlineData("process", "/c/series/../a.cbz")]
    [InlineData("rename", "/c/missing/../a.cbz")]
    [InlineData("normalize", "/c/a.cbz/.")]
    [InlineData("remove-metadata", "/c/a.cbz/")]
    [InlineData("update-metadata", "/c/series/../a.cbz")]
    public async Task SelectedScope_APathOnlyTheEntryRuleRefuses_IsQueuedForOtherOperations(string operation, string path)
    {
        var request = new JobsController.BatchJobRequest
        {
            Files = new() { "/c/a.cbz", path },
            Metadata = new ComicMetadata { Series = "Series" }
        };

        var result = await _controller.StartBatchJob(operation, "selected", request);

        Assert.Equal(2, Response(result).TotalItems);
        Assert.Equal(new[] { "/c/a.cbz", path }, _queued);
    }

    // A link outside the library leading into it: the jobs that follow links work on the file
    // it leads to, which is in the library, but deleting would remove the link outside it.
    [SymbolicLinkTheory]
    [InlineData("delete", true)]
    [InlineData("process", false)]
    public async Task SelectedScope_ALinkOutsideTheLibraryToAComicInIt_FollowsEachOperationsRule(string operation, bool rejected)
    {
        var root = Path.Join(Path.GetTempPath(), "jobs-batch-" + Guid.NewGuid().ToString("N"));
        try
        {
            var library = Directory.CreateDirectory(Path.Join(root, "library")).FullName;
            var outside = Directory.CreateDirectory(Path.Join(root, "outside")).FullName;
            var comic = Path.Join(library, "a.cbz");
            File.WriteAllText(comic, "keep me");
            var link = Path.Join(outside, "a.cbz");
            File.CreateSymbolicLink(link, comic);
            var controller = CreateController(new AppSettings { WatchedDirectory = library });

            var result = await controller.StartBatchJob(
                operation, "selected", new JobsController.BatchJobRequest { Files = new() { comic, link } });

            if (rejected)
            {
                Assert.Equal(JobsController.OutsideAllowedDirectoryError, ErrorOf(result));
                VerifyNothingQueued();
            }
            else
            {
                Assert.Equal(new[] { comic, link }, _queued);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [SymbolicLinkTheory]
    [InlineData("process")]
    [InlineData("delete")]
    public async Task SelectedScope_ALinkInTheLibraryLeadingOutOfIt_RejectsTheWholeBatch(string operation)
    {
        var root = Path.Join(Path.GetTempPath(), "jobs-batch-" + Guid.NewGuid().ToString("N"));
        try
        {
            var library = Directory.CreateDirectory(Path.Join(root, "library")).FullName;
            var outside = Directory.CreateDirectory(Path.Join(root, "outside")).FullName;
            var secret = Path.Join(outside, "secret.cbz");
            File.WriteAllText(secret, "keep me");
            var link = Path.Join(library, "escape.cbz");
            File.CreateSymbolicLink(link, secret);
            var controller = CreateController(new AppSettings { WatchedDirectory = library });

            var result = await controller.StartBatchJob(
                operation, "selected", new JobsController.BatchJobRequest { Files = new() { link } });

            Assert.Equal(JobsController.OutsideAllowedDirectoryError, ErrorOf(result));
            VerifyNothingQueued();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ---- options --------------------------------------------------------------------------

    [Theory]
    [InlineData("process", "all", false, false, false)]
    [InlineData("process", "all", true, false, true)]
    [InlineData("rename", "all", true, false, true)]
    [InlineData("normalize", "all", true, false, true)]
    [InlineData("process", "unmarked", true, false, true)]
    [InlineData("rename", "unmarked", false, false, false)]
    [InlineData("normalize", "unmarked", true, false, true)]
    [InlineData("process", "selected", false, true, true)]
    [InlineData("rename", "selected", true, false, true)]
    [InlineData("normalize", "selected", false, false, false)]
    public async Task ForceReprocess_ComesFromTheQueryOrTheBody(string operation, string scope, bool query, bool body, bool expected)
    {
        SetupAllFiles(new ComicFile { FilePath = "/c/a.cbz" });
        SetupUnprocessedFiles("/c/a.cbz");
        var request = new JobsController.BatchJobRequest { Files = new() { "/c/a.cbz" }, ForceReprocess = body };

        await _controller.StartBatchJob(operation, scope, request, query);

        Assert.Equal(expected, _queuedForce);
    }

    [Fact]
    public async Task TheJobOutlivesTheRequest()
    {
        using var abort = new CancellationTokenSource();
        SetupAllFiles(new ComicFile { FilePath = "/c/a.cbz" });

        await _controller.StartBatchJob("process", "all", null, cancellationToken: abort.Token);

        Assert.Equal(CancellationToken.None, _queuedToken);
        _fileStore.Verify(f => f.GetAllFilesAsync(abort.Token), Times.Once);
    }

    // ---- update-metadata ---------------------------------------------------------------------

    [Fact]
    public async Task UpdateMetadata_WithoutAFieldList_AppliesTheFieldsThatCarryAValue()
    {
        var metadata = new ComicMetadata { Series = "Series", Issue = "1" };

        await _controller.StartBatchJob("update-metadata", "selected", new JobsController.BatchJobRequest
        {
            Files = new() { "/c/a.cbz" },
            Metadata = metadata
        });

        _processor.Verify(p => p.UpdateMetadataAsync(
            It.Is<IEnumerable<string>>(files => files.SequenceEqual(new[] { "/c/a.cbz" })),
            metadata,
            ComicMetadataFieldFlags.Series | ComicMetadataFieldFlags.Issue,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateMetadata_WithAFieldList_AppliesExactlyThoseFields()
    {
        var metadata = new ComicMetadata { Series = "Series", Issue = "1" };

        await _controller.StartBatchJob("update-metadata", "selected", new JobsController.BatchJobRequest
        {
            Files = new() { "/c/a.cbz" },
            Metadata = metadata,
            Fields = new() { "series", "title" }
        });

        _processor.Verify(p => p.UpdateMetadataAsync(
            It.IsAny<IEnumerable<string>>(),
            metadata,
            ComicMetadataFieldFlags.Series | ComicMetadataFieldFlags.Title,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData(null)]
    public async Task UpdateMetadata_WithAnUnknownField_IsRejected(string? field)
    {
        var result = await _controller.StartBatchJob("update-metadata", "selected", new JobsController.BatchJobRequest
        {
            Files = new() { "/c/a.cbz" },
            Metadata = new ComicMetadata { Series = "Series" },
            Fields = new() { field! }
        });

        Assert.StartsWith("Unknown metadata field", ErrorOf(result));
        VerifyNothingQueued();
    }

    [Fact]
    public async Task UpdateMetadata_WithNoFieldToApply_IsRejected()
    {
        var result = await _controller.StartBatchJob("update-metadata", "selected", new JobsController.BatchJobRequest
        {
            Files = new() { "/c/a.cbz" },
            Metadata = new ComicMetadata()
        });

        Assert.Equal("No metadata fields specified", ErrorOf(result));
    }

    [Fact]
    public async Task UpdateMetadata_WithoutMetadata_IsRejected()
    {
        var result = await _controller.StartBatchJob(
            "update-metadata", "selected", new JobsController.BatchJobRequest { Files = new() { "/c/a.cbz" } });

        Assert.Equal("No metadata specified", ErrorOf(result));
    }

    [Fact]
    public async Task UpdateMetadata_WithoutFiles_IsRejectedBeforeLookingAtTheMetadata()
    {
        var result = await _controller.StartBatchJob("update-metadata", "selected", new JobsController.BatchJobRequest());

        Assert.Equal("No files specified", ErrorOf(result));
    }

    // ---- errors and the response -----------------------------------------------------------

    [Fact]
    public async Task AFailureWhileSelecting_ReachesTheGlobalExceptionHandler()
    {
        _fileStore.Setup(f => f.GetAllFilesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("internal details"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _controller.StartBatchJob("process", "all", null));
    }

    [Fact]
    public async Task AFailureQueuingTheJob_ReachesTheGlobalExceptionHandler()
    {
        _processor.Setup(p => p.RenameFilesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("internal details"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _controller.StartBatchJob(
            "rename", "selected", new JobsController.BatchJobRequest { Files = new() { "/c/a.cbz" } }));
    }

    [Fact]
    public void BatchJobResponse_CarriesTheNamesEveryClientReads()
    {
        var json = JsonSerializer.SerializeToElement(new JobsController.BatchJobResponse("abc", 3));

        Assert.Equal("abc", json.GetProperty("job_id").GetString());
        Assert.Equal("abc", json.GetProperty("jobId").GetString());
        Assert.Equal(3, json.GetProperty("total_items").GetInt32());
        Assert.Equal(3, json.GetProperty("totalItems").GetInt32());
    }
}
