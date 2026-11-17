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
/// <c>POST /api/jobs/recheck-duplicates-{all|selected}</c> takes only files flagged as
/// duplicates, and <c>GET /api/jobs/{jobId}/duplicate-recheck</c> sums up what the job found.
/// </summary>
public class JobsControllerDuplicateRecheckTests
{
    private readonly Mock<IComicProcessorService> _processor = new();
    private readonly Mock<IFileStoreService> _fileStore = new();
    private readonly Mock<IBatchJobRegistry> _jobs = new();
    private readonly JobsController _controller;
    private readonly Guid _jobId = Guid.NewGuid();
    private List<string>? _queued;

    public JobsControllerDuplicateRecheckTests()
    {
        _controller = new JobsController(
            _processor.Object,
            _jobs.Object,
            _fileStore.Object,
            NullLogger<JobsController>.Instance,
            new TestOptionsMonitor<AppSettings>(new AppSettings { WatchedDirectory = "/c", DuplicateDirectory = "/duplicates" }));

        _processor.Setup(p => p.RecheckDuplicatesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<string>, CancellationToken>((files, _) => _queued = files.ToList())
            .ReturnsAsync(_jobId);
        _fileStore.Setup(f => f.GetFilteredFilesAsync("duplicates", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "/c/Saga/Saga 1.cbz", "/c/Saga/Saga 2.cbz" }.Select(p => new ComicFile { FilePath = p, IsDuplicate = true }).ToList());
    }

    [Fact]
    public async Task AllScope_QueuesEveryDuplicate()
    {
        var result = await _controller.StartBatchJob("recheck-duplicates", "all", null);

        var response = Assert.IsType<JobsController.BatchJobResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(_jobId.ToString(), response.JobId);
        Assert.Equal(2, response.TotalItems);
        Assert.Equal(new[] { "/c/Saga/Saga 1.cbz", "/c/Saga/Saga 2.cbz" }, _queued);
    }

    [Fact]
    public async Task SelectedScope_LeavesOutFilesThatAreNotFlagged()
    {
        var request = new JobsController.BatchJobRequest { Files = new() { "/c/Saga/Saga 2.cbz", "/c/Saga/Saga 3.cbz", "/c/Saga/Saga 2.cbz" } };

        var result = await _controller.StartBatchJob("recheck-duplicates", "selected", request);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(new[] { "/c/Saga/Saga 2.cbz" }, _queued);
    }

    [Fact]
    public async Task SelectedScope_NoFlaggedFile_StartsNothing()
    {
        var request = new JobsController.BatchJobRequest { Files = new() { "/c/Saga/Saga 3.cbz" } };

        var result = await _controller.StartBatchJob("recheck-duplicates", "selected", request);

        var response = Assert.IsType<JobsController.BatchJobResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(Guid.Empty.ToString(), response.JobId);
        Assert.Null(_queued);
    }

    [Fact]
    public async Task SelectedScope_WithoutFiles_IsRejected()
    {
        var result = await _controller.StartBatchJob("recheck-duplicates", "selected", new JobsController.BatchJobRequest { Files = new() { " " } });

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Null(_queued);
    }

    [Fact]
    public async Task SelectedScope_APathOutsideTheLibrary_RejectsTheWholeBatch()
    {
        var request = new JobsController.BatchJobRequest { Files = new() { "/c/Saga/Saga 1.cbz", "/elsewhere/x.cbz" } };

        var result = await _controller.StartBatchJob("recheck-duplicates", "selected", request);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains(JobsController.OutsideAllowedDirectoryError, JsonSerializer.Serialize(badRequest.Value));
        Assert.Null(_queued);
    }

    [Fact]
    public async Task UnmarkedScope_IsNotOffered()
    {
        var result = await _controller.StartBatchJob("recheck-duplicates", "unmarked", null);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public void Summary_SortsTheFilesIntoClearedStillDuplicateAndFailed()
    {
        _jobs.Setup(j => j.GetJob(_jobId)).Returns(new ProcessingJob
        {
            JobId = _jobId,
            OperationName = DuplicateRecheck.OperationName,
            Status = JobStatus.Completed,
            Files = new() { "/c/a.cbz", "/c/b.cbz", "/c/c.cbz" },
            TotalFiles = 3,
            ProcessedFiles = 1,
            FailedFiles = 2,
            Errors = new()
            {
                ["/c/b.cbz"] = DuplicateRecheck.StillDuplicateError("/c/b proper.cbz"),
                ["/c/c.cbz"] = "Could not work out the processed name: File not found"
            }
        });

        var result = _controller.GetDuplicateRecheckSummary(_jobId);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(result.Result).Value));
        var root = json.RootElement;
        Assert.Equal("completed", root.GetProperty("status").GetString());
        Assert.Equal(3, root.GetProperty("total_items").GetInt32());
        Assert.Equal(1, root.GetProperty("cleared").GetInt32());
        Assert.Equal(1, root.GetProperty("still_duplicate").GetInt32());
        Assert.Equal(1, root.GetProperty("failed").GetInt32());
        var clash = Assert.Single(root.GetProperty("clashes").EnumerateArray());
        Assert.Equal("/c/b.cbz", clash.GetProperty("file").GetString());
        Assert.Equal("/c/b proper.cbz", clash.GetProperty("clashes_with").GetString());
        var failure = Assert.Single(root.GetProperty("failures").EnumerateArray());
        Assert.Equal("/c/c.cbz", failure.GetProperty("file").GetString());
        Assert.Equal("Could not work out the processed name: File not found", failure.GetProperty("error").GetString());
    }

    [Fact]
    public void Summary_OfAnotherKindOfJob_IsNotFound()
    {
        _jobs.Setup(j => j.GetJob(_jobId)).Returns(new ProcessingJob { JobId = _jobId, OperationName = "ProcessFilesAsync" });

        Assert.IsType<NotFoundResult>(_controller.GetDuplicateRecheckSummary(_jobId).Result);
    }

    [Fact]
    public void Summary_OfAnUnknownJob_IsNotFound()
    {
        Assert.IsType<NotFoundResult>(_controller.GetDuplicateRecheckSummary(Guid.NewGuid()).Result);
    }

    [Theory]
    [InlineData("Still a duplicate of /c/x.cbz", "/c/x.cbz")]
    [InlineData("Re-check failed", null)]
    [InlineData(null, null)]
    public void ClashingPathOf_ReadsBackTheErrorItWrote(string? error, string? expected)
    {
        Assert.Equal(expected, DuplicateRecheck.ClashingPathOf(error));
    }
}
