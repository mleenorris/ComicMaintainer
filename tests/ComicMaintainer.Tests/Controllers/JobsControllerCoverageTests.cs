using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.WebApi.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json.Linq;

namespace ComicMaintainer.Tests.Controllers;

public class JobsControllerCoverageTests
{
    private readonly Mock<IComicProcessorService> _processor = new();
    private readonly Mock<IFileStoreService> _fileStore = new();
    private readonly JobsController _controller;

    public JobsControllerCoverageTests()
    {
        _controller = new JobsController(_processor.Object, _fileStore.Object, NullLogger<JobsController>.Instance);
    }

    private static List<ComicFile> Files(params string[] paths) =>
        paths.Select(p => new ComicFile { FilePath = p }).ToList();

    private void SetupAllFiles(params string[] paths) =>
        _fileStore.Setup(f => f.GetAllFilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Files(paths));

    private void SetupUnprocessedFiles(params string[] paths) =>
        _fileStore.Setup(f => f.GetFilteredFilesAsync("unprocessed", It.IsAny<CancellationToken>())).ReturnsAsync(Files(paths));

    private static JObject OkJson(ActionResult<object> result) =>
        JObject.FromObject(Assert.IsType<OkObjectResult>(result.Result).Value!);

    private static (string JobId, int TotalItems) JobResponse(ActionResult<object> result)
    {
        var json = OkJson(result);
        return (json["job_id"]!.ToString(), json["total_items"]!.Value<int>());
    }

    private static string ErrorOf(IActionResult? result, int expectedStatus)
    {
        var obj = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(expectedStatus, obj.StatusCode);
        return JObject.FromObject(obj.Value!)["error"]!.ToString();
    }

    private static string ErrorOf(ActionResult<object> result, int expectedStatus) => ErrorOf((IActionResult?)result.Result, expectedStatus);

    // ---- GetJob -------------------------------------------------------------------------

    [Fact]
    public void GetJob_ReportsPerFileOutcomeFromCountersAndErrors()
    {
        var jobId = Guid.NewGuid();
        var start = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var job = new ProcessingJob
        {
            JobId = jobId,
            Status = JobStatus.Running,
            Files = new List<string> { "/c/a.cbz", "/c/b.cbz", "/c/c.cbz" },
            TotalFiles = 3,
            ProcessedFiles = 1,
            FailedFiles = 1,
            CurrentFile = "/c/c.cbz",
            StartTime = start,
            Errors = new Dictionary<string, string> { ["/c/b.cbz"] = "corrupt archive" }
        };
        _processor.Setup(p => p.GetJob(jobId)).Returns(job);

        var json = OkJson(_controller.GetJob(jobId));

        Assert.Equal(jobId.ToString(), json["job_id"]!.ToString());
        Assert.Equal("running", json["status"]!.ToString());
        Assert.Equal(3, json["total_items"]!.Value<int>());
        Assert.Equal(1, json["processed_items"]!.Value<int>());
        Assert.Equal(1, json["failed_items"]!.Value<int>());
        Assert.Equal("/c/c.cbz", json["current_file"]!.ToString());
        Assert.Equal(start, json["start_time"]!.Value<DateTime>());
        Assert.Equal(JTokenType.Null, json["end_time"]!.Type);

        var results = Assert.IsType<JArray>(json["results"]);
        Assert.Equal(3, results.Count);

        // First file: within the processed window and no error -> success.
        Assert.Equal("/c/a.cbz", results[0]["file"]!.ToString());
        Assert.True(results[0]["success"]!.Value<bool>());
        Assert.Equal(JTokenType.Null, results[0]["error"]!.Type);

        // Second file: within the processed window but failed -> error surfaced.
        Assert.Equal("/c/b.cbz", results[1]["file"]!.ToString());
        Assert.False(results[1]["success"]!.Value<bool>());
        Assert.Equal("corrupt archive", results[1]["error"]!.ToString());

        // Third file: not reached yet -> neither success nor error.
        Assert.Equal("/c/c.cbz", results[2]["file"]!.ToString());
        Assert.False(results[2]["success"]!.Value<bool>());
        Assert.Equal(JTokenType.Null, results[2]["error"]!.Type);
    }

    // ---- GetActiveJob -------------------------------------------------------------------

    [Theory]
    [InlineData(JobStatus.Queued, 0, "", "Processing Files...")]
    [InlineData(JobStatus.Queued, 2, "", "Processing...")]
    [InlineData(JobStatus.Running, 1, "", "Processing 7 files...")]
    [InlineData(JobStatus.Completed, 7, "", "Completed 7 files")]
    [InlineData(JobStatus.Failed, 3, "", "Processing Failed")]
    [InlineData(JobStatus.Cancelled, 3, "", "Processing Cancelled")]
    [InlineData(JobStatus.Interrupted, 3, "   ", "Interrupted by restart")]
    [InlineData(JobStatus.Interrupted, 3, "Rename all files", "Rename all files interrupted by restart")]
    public void GetActiveJob_DescribesJobTitleFromStatus(JobStatus status, int processed, string operation, string expectedTitle)
    {
        var job = new ProcessingJob
        {
            JobId = Guid.NewGuid(),
            Status = status,
            OperationName = operation,
            TotalFiles = 7,
            ProcessedFiles = processed,
            FailedFiles = 1,
            CurrentFile = "/c/x.cbz"
        };
        _processor.Setup(p => p.GetActiveJob()).Returns(job);

        var json = OkJson(_controller.GetActiveJob());

        Assert.Equal(expectedTitle, json["job_title"]!.ToString());
        Assert.Equal(job.JobId.ToString(), json["job_id"]!.ToString());
        Assert.Equal(status.ToString().ToLower(), json["status"]!.ToString());
        Assert.Equal(7, json["total_items"]!.Value<int>());
        Assert.Equal(processed, json["processed_items"]!.Value<int>());
        Assert.Equal(1, json["failed_items"]!.Value<int>());
        Assert.Equal("/c/x.cbz", json["current_file"]!.ToString());
        _processor.Verify(p => p.GetAllJobs(), Times.Never);
    }

    [Fact]
    public void GetActiveJob_NullJobHistory_ReportsInactive()
    {
        _processor.Setup(p => p.GetActiveJob()).Returns((ProcessingJob?)null);
        _processor.Setup(p => p.GetAllJobs()).Returns((IEnumerable<ProcessingJob>)null!);

        var json = OkJson(_controller.GetActiveJob());

        Assert.False(json["active"]!.Value<bool>());
    }

    [Fact]
    public void GetActiveJob_OnlyFinishedJobsInHistory_ReportsInactive()
    {
        _processor.Setup(p => p.GetActiveJob()).Returns((ProcessingJob?)null);
        _processor.Setup(p => p.GetAllJobs()).Returns(new List<ProcessingJob>
        {
            new() { JobId = Guid.NewGuid(), Status = JobStatus.Completed },
            new() { JobId = Guid.NewGuid(), Status = JobStatus.Cancelled }
        });

        var json = OkJson(_controller.GetActiveJob());

        Assert.False(json["active"]!.Value<bool>());
    }

    [Fact]
    public void GetActiveJob_WhenProcessorThrows_Returns500()
    {
        _processor.Setup(p => p.GetActiveJob()).Throws(new InvalidOperationException("boom"));

        var result = _controller.GetActiveJob();

        var obj = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(500, obj.StatusCode);
        Assert.Equal("Error retrieving active job", obj.Value);
    }

    // ---- *-all endpoints ----------------------------------------------------------------

    [Fact]
    public async Task ProcessAll_WhenFileStoreThrows_Returns500()
    {
        _fileStore.Setup(f => f.GetAllFilesAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("db locked"));

        var result = await _controller.ProcessAll();

        Assert.Equal("Error starting job", ErrorOf(result, 500));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RenameAll_QueuesRenameJobForEveryFile(bool force)
    {
        SetupAllFiles("/c/a.cbz", "/c/b.cbz");
        var jobId = Guid.NewGuid();
        _processor.Setup(p => p.RenameFilesAsync(It.IsAny<IEnumerable<string>>(), force, It.IsAny<CancellationToken>()))
            .ReturnsAsync(jobId);

        var (id, total) = JobResponse(await _controller.RenameAll(force));

        Assert.Equal(jobId.ToString(), id);
        Assert.Equal(2, total);
        _processor.Verify(p => p.RenameFilesAsync(
            It.Is<IEnumerable<string>>(paths => paths.SequenceEqual(new[] { "/c/a.cbz", "/c/b.cbz" })),
            force,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RenameAll_NoFiles_ReturnsEmptyJobWithoutQueuing()
    {
        SetupAllFiles();

        var (id, total) = JobResponse(await _controller.RenameAll());

        Assert.Equal(Guid.Empty.ToString(), id);
        Assert.Equal(0, total);
        _processor.Verify(p => p.RenameFilesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RenameAll_WhenProcessorThrows_Returns500()
    {
        SetupAllFiles("/c/a.cbz");
        _processor.Setup(p => p.RenameFilesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("busy"));

        Assert.Equal("Error starting job", ErrorOf(await _controller.RenameAll(), 500));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NormalizeAll_QueuesNormalizeJobForEveryFile(bool force)
    {
        SetupAllFiles("/c/a.cbz", "/c/b.cbz", "/c/c.cbz");
        var jobId = Guid.NewGuid();
        _processor.Setup(p => p.NormalizeFilesAsync(It.IsAny<IEnumerable<string>>(), force, It.IsAny<CancellationToken>()))
            .ReturnsAsync(jobId);

        var (id, total) = JobResponse(await _controller.NormalizeAll(force));

        Assert.Equal(jobId.ToString(), id);
        Assert.Equal(3, total);
        _processor.Verify(p => p.NormalizeFilesAsync(
            It.Is<IEnumerable<string>>(paths => paths.SequenceEqual(new[] { "/c/a.cbz", "/c/b.cbz", "/c/c.cbz" })),
            force,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NormalizeAll_NoFiles_ReturnsEmptyJobWithoutQueuing()
    {
        SetupAllFiles();

        var (id, total) = JobResponse(await _controller.NormalizeAll());

        Assert.Equal(Guid.Empty.ToString(), id);
        Assert.Equal(0, total);
        _processor.Verify(p => p.NormalizeFilesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task NormalizeAll_WhenFileStoreThrows_Returns500()
    {
        _fileStore.Setup(f => f.GetAllFilesAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("db locked"));

        Assert.Equal("Error starting job", ErrorOf(await _controller.NormalizeAll(), 500));
    }

    [Fact]
    public async Task RemoveMetadataAll_QueuesJobForEveryFile()
    {
        SetupAllFiles("/c/a.cbz", "/c/b.cbz");
        var jobId = Guid.NewGuid();
        _processor.Setup(p => p.RemoveMetadataFromFilesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(jobId);

        var (id, total) = JobResponse(await _controller.RemoveMetadataAll());

        Assert.Equal(jobId.ToString(), id);
        Assert.Equal(2, total);
        _processor.Verify(p => p.RemoveMetadataFromFilesAsync(
            It.Is<IEnumerable<string>>(paths => paths.SequenceEqual(new[] { "/c/a.cbz", "/c/b.cbz" })),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RemoveMetadataAll_NoFiles_ReturnsEmptyJobWithoutQueuing()
    {
        SetupAllFiles();

        var (id, total) = JobResponse(await _controller.RemoveMetadataAll());

        Assert.Equal(Guid.Empty.ToString(), id);
        Assert.Equal(0, total);
        _processor.Verify(p => p.RemoveMetadataFromFilesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RemoveMetadataAll_WhenProcessorThrows_Returns500()
    {
        SetupAllFiles("/c/a.cbz");
        _processor.Setup(p => p.RemoveMetadataFromFilesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("busy"));

        Assert.Equal("Error starting job", ErrorOf(await _controller.RemoveMetadataAll(), 500));
    }

    // ---- *-unmarked endpoints -----------------------------------------------------------

    [Fact]
    public async Task ProcessUnmarked_QueuesProcessJobForUnprocessedFiles()
    {
        SetupUnprocessedFiles("/c/new1.cbz", "/c/new2.cbz");
        var jobId = Guid.NewGuid();
        _processor.Setup(p => p.ProcessFilesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(jobId);

        var (id, total) = JobResponse(await _controller.ProcessUnmarked());

        Assert.Equal(jobId.ToString(), id);
        Assert.Equal(2, total);
        _processor.Verify(p => p.ProcessFilesAsync(
            It.Is<IEnumerable<string>>(paths => paths.SequenceEqual(new[] { "/c/new1.cbz", "/c/new2.cbz" })),
            It.IsAny<CancellationToken>()), Times.Once);
        _fileStore.Verify(f => f.GetFilteredFilesAsync("unprocessed", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessUnmarked_NoUnprocessedFiles_ReturnsEmptyJobWithoutQueuing()
    {
        SetupUnprocessedFiles();

        var (id, total) = JobResponse(await _controller.ProcessUnmarked());

        Assert.Equal(Guid.Empty.ToString(), id);
        Assert.Equal(0, total);
        _processor.Verify(p => p.ProcessFilesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessUnmarked_WhenFileStoreThrows_Returns500()
    {
        _fileStore.Setup(f => f.GetFilteredFilesAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("db locked"));

        Assert.Equal("Error starting job", ErrorOf(await _controller.ProcessUnmarked(), 500));
    }

    [Fact]
    public async Task RenameUnmarked_WhenProcessorThrows_Returns500()
    {
        SetupUnprocessedFiles("/c/a.cbz");
        _processor.Setup(p => p.RenameFilesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("busy"));

        Assert.Equal("Error starting job", ErrorOf(await _controller.RenameUnmarked(), 500));
    }

    [Fact]
    public async Task NormalizeUnmarked_WhenFileStoreThrows_Returns500()
    {
        _fileStore.Setup(f => f.GetFilteredFilesAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("db locked"));

        Assert.Equal("Error starting job", ErrorOf(await _controller.NormalizeUnmarked(), 500));
    }

    // ---- *-selected endpoints -----------------------------------------------------------

    public static TheoryData<List<string>?> MissingFiles => new() { null, new List<string>() };

    [Theory]
    [MemberData(nameof(MissingFiles))]
    public async Task ProcessSelected_WithoutFiles_ReturnsBadRequest(List<string>? files)
    {
        var result = await _controller.ProcessSelected(new JobsController.ProcessSelectedRequest { Files = files! });

        Assert.Equal("No files specified", ErrorOf(result, 400));
        _processor.Verify(p => p.ProcessFilesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessSelected_WhenProcessorThrows_Returns500()
    {
        _processor.Setup(p => p.ProcessFilesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("busy"));

        var result = await _controller.ProcessSelected(new JobsController.ProcessSelectedRequest { Files = new() { "/c/a.cbz" } });

        Assert.Equal("Error starting job", ErrorOf(result, 500));
    }

    [Theory]
    [MemberData(nameof(MissingFiles))]
    public async Task RenameSelected_WithoutFiles_ReturnsBadRequest(List<string>? files)
    {
        var result = await _controller.RenameSelected(new JobsController.ProcessSelectedRequest { Files = files! });

        Assert.Equal("No files specified", ErrorOf(result, 400));
        _processor.Verify(p => p.RenameFilesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RenameSelected_QueuesRenameJobForRequestedFiles(bool force)
    {
        var files = new List<string> { "/c/a.cbz", "/c/b.cbz" };
        var jobId = Guid.NewGuid();
        _processor.Setup(p => p.RenameFilesAsync(files, force, It.IsAny<CancellationToken>())).ReturnsAsync(jobId);

        var (id, total) = JobResponse(await _controller.RenameSelected(
            new JobsController.ProcessSelectedRequest { Files = files, ForceReprocess = force }));

        Assert.Equal(jobId.ToString(), id);
        Assert.Equal(2, total);
        _processor.Verify(p => p.RenameFilesAsync(files, force, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RenameSelected_WhenProcessorThrows_Returns500()
    {
        _processor.Setup(p => p.RenameFilesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("busy"));

        var result = await _controller.RenameSelected(new JobsController.ProcessSelectedRequest { Files = new() { "/c/a.cbz" } });

        Assert.Equal("Error starting job", ErrorOf(result, 500));
    }

    [Theory]
    [MemberData(nameof(MissingFiles))]
    public async Task NormalizeSelected_WithoutFiles_ReturnsBadRequest(List<string>? files)
    {
        var result = await _controller.NormalizeSelected(new JobsController.ProcessSelectedRequest { Files = files! });

        Assert.Equal("No files specified", ErrorOf(result, 400));
        _processor.Verify(p => p.NormalizeFilesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NormalizeSelected_QueuesNormalizeJobForRequestedFiles(bool force)
    {
        var files = new List<string> { "/c/a.cbz" };
        var jobId = Guid.NewGuid();
        _processor.Setup(p => p.NormalizeFilesAsync(files, force, It.IsAny<CancellationToken>())).ReturnsAsync(jobId);

        var (id, total) = JobResponse(await _controller.NormalizeSelected(
            new JobsController.ProcessSelectedRequest { Files = files, ForceReprocess = force }));

        Assert.Equal(jobId.ToString(), id);
        Assert.Equal(1, total);
        _processor.Verify(p => p.NormalizeFilesAsync(files, force, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NormalizeSelected_WhenProcessorThrows_Returns500()
    {
        _processor.Setup(p => p.NormalizeFilesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("busy"));

        var result = await _controller.NormalizeSelected(new JobsController.ProcessSelectedRequest { Files = new() { "/c/a.cbz" } });

        Assert.Equal("Error starting job", ErrorOf(result, 500));
    }

    [Fact]
    public async Task UpdateMetadataSelected_NullFiles_ReturnsBadRequest()
    {
        var result = await _controller.UpdateMetadataSelected(
            new JobsController.UpdateMetadataSelectedRequest { Files = null!, Metadata = new ComicMetadata() });

        Assert.Equal("No files specified", ErrorOf(result, 400));
    }

    [Theory]
    [MemberData(nameof(MissingFiles))]
    public async Task RemoveMetadataSelected_WithoutFiles_ReturnsBadRequest(List<string>? files)
    {
        var result = await _controller.RemoveMetadataSelected(new JobsController.ProcessSelectedRequest { Files = files! });

        Assert.Equal("No files specified", ErrorOf(result, 400));
        _processor.Verify(p => p.RemoveMetadataFromFilesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RemoveMetadataSelected_QueuesJobForRequestedFiles()
    {
        var files = new List<string> { "/c/a.cbz", "/c/b.cbz", "/c/c.cbz" };
        var jobId = Guid.NewGuid();
        _processor.Setup(p => p.RemoveMetadataFromFilesAsync(files, It.IsAny<CancellationToken>())).ReturnsAsync(jobId);

        var (id, total) = JobResponse(await _controller.RemoveMetadataSelected(
            new JobsController.ProcessSelectedRequest { Files = files }));

        Assert.Equal(jobId.ToString(), id);
        Assert.Equal(3, total);
        _processor.Verify(p => p.RemoveMetadataFromFilesAsync(files, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RemoveMetadataSelected_WhenProcessorThrows_Returns500()
    {
        _processor.Setup(p => p.RemoveMetadataFromFilesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("busy"));

        var result = await _controller.RemoveMetadataSelected(new JobsController.ProcessSelectedRequest { Files = new() { "/c/a.cbz" } });

        Assert.Equal("Error starting job", ErrorOf(result, 500));
    }

    [Fact]
    public async Task DeleteSelected_NullRequest_ReturnsBadRequest()
    {
        var result = await _controller.DeleteSelected(null!);

        Assert.Equal("No files specified", ErrorOf(result, 400));
        _processor.Verify(p => p.DeleteFilesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteSelected_NullFiles_ReturnsBadRequest()
    {
        var result = await _controller.DeleteSelected(new JobsController.ProcessSelectedRequest { Files = null! });

        Assert.Equal("No files specified", ErrorOf(result, 400));
        _processor.Verify(p => p.DeleteFilesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---- CancelJob ----------------------------------------------------------------------

    [Fact]
    public void CancelJob_UnknownOrFinishedJob_ReturnsNotFound()
    {
        var jobId = Guid.NewGuid();
        _processor.Setup(p => p.CancelJob(jobId)).Returns(false);

        var result = _controller.CancelJob(jobId);

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        var json = JObject.FromObject(notFound.Value!);
        Assert.False(json["success"]!.Value<bool>());
        Assert.Equal("Job not found or already completed", json["error"]!.ToString());
        _processor.Verify(p => p.CancelJob(jobId), Times.Once);
    }

    [Fact]
    public void CancelJob_WhenProcessorThrows_Returns500()
    {
        var jobId = Guid.NewGuid();
        _processor.Setup(p => p.CancelJob(jobId)).Throws(new InvalidOperationException("boom"));

        var result = _controller.CancelJob(jobId);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(500, obj.StatusCode);
        var json = JObject.FromObject(obj.Value!);
        Assert.False(json["success"]!.Value<bool>());
        Assert.Equal("Error cancelling job", json["error"]!.ToString());
    }
}
