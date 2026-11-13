using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Tests.Helpers;
using ComicMaintainer.WebApi.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json.Linq;

namespace ComicMaintainer.Tests.Controllers;

public class JobsControllerCoverageTests
{
    private readonly Mock<IComicProcessorService> _processor = new();
    private readonly Mock<IBatchJobRegistry> _jobRegistry = new();
    private readonly Mock<IFileStoreService> _fileStore = new();
    private readonly JobsController _controller;

    public JobsControllerCoverageTests()
    {
        var settings = new AppSettings { WatchedDirectory = "/c", DuplicateDirectory = "/duplicates" };
        _controller = new JobsController(
            _processor.Object, _jobRegistry.Object, _fileStore.Object, NullLogger<JobsController>.Instance, new TestOptionsMonitor<AppSettings>(settings));
    }

    private static JObject OkJson(ActionResult<object> result) =>
        JObject.FromObject(Assert.IsType<OkObjectResult>(result.Result).Value!);

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
        _jobRegistry.Setup(p => p.GetJob(jobId)).Returns(job);

        var json = OkJson(_controller.GetJob(jobId, include: "results"));

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

    // A whole-library job has an entry per file: megabytes that polling for progress does not need.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("problems")]
    [InlineData("result")]
    public void GetJob_WithoutAskingForResults_LeavesThemOut(string? include)
    {
        var jobId = Guid.NewGuid();
        _jobRegistry.Setup(p => p.GetJob(jobId)).Returns(new ProcessingJob
        {
            JobId = jobId,
            Status = JobStatus.Running,
            Files = new List<string> { "/c/a.cbz", "/c/b.cbz" },
            TotalFiles = 2,
            ProcessedFiles = 1,
        });

        var json = OkJson(_controller.GetJob(jobId, include));

        Assert.Null(json["results"]);
        Assert.Equal(1, json["processed_items"]!.Value<int>());
        Assert.Empty(Assert.IsType<JArray>(json["reported_problems"]));
    }

    [Theory]
    [InlineData("results")]
    [InlineData("RESULTS")]
    [InlineData("problems, results")]
    public void GetJob_AskedForResults_ListsOnePerFile(string include)
    {
        var jobId = Guid.NewGuid();
        _jobRegistry.Setup(p => p.GetJob(jobId)).Returns(new ProcessingJob
        {
            JobId = jobId,
            Status = JobStatus.Running,
            Files = new List<string> { "/c/a.cbz", "/c/b.cbz" },
            TotalFiles = 2,
            ProcessedFiles = 1,
        });

        var json = OkJson(_controller.GetJob(jobId, include));

        Assert.Equal(2, Assert.IsType<JArray>(json["results"]).Count);
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
        _jobRegistry.Setup(p => p.GetActiveJob()).Returns(job.ToSummary());

        var json = OkJson(_controller.GetActiveJob());

        Assert.Equal(expectedTitle, json["job_title"]!.ToString());
        Assert.Equal(job.JobId.ToString(), json["job_id"]!.ToString());
        Assert.Equal(status.ToString().ToLower(), json["status"]!.ToString());
        Assert.Equal(7, json["total_items"]!.Value<int>());
        Assert.Equal(processed, json["processed_items"]!.Value<int>());
        Assert.Equal(1, json["failed_items"]!.Value<int>());
        Assert.Equal("/c/x.cbz", json["current_file"]!.ToString());
        _jobRegistry.Verify(p => p.GetJobSummaries(), Times.Never);
    }

    [Fact]
    public void GetActiveJob_OnlyFinishedJobsInHistory_ReportsInactive()
    {
        _jobRegistry.Setup(p => p.GetActiveJob()).Returns(default(ProcessingJobSummary?));
        _jobRegistry.Setup(p => p.GetJobSummaries()).Returns(new List<ProcessingJobSummary>
        {
            new ProcessingJob { JobId = Guid.NewGuid(), Status = JobStatus.Completed }.ToSummary(),
            new ProcessingJob { JobId = Guid.NewGuid(), Status = JobStatus.Cancelled }.ToSummary()
        });

        var json = OkJson(_controller.GetActiveJob());

        Assert.False(json["active"]!.Value<bool>());
    }

    // ---- CancelJob ----------------------------------------------------------------------

    [Fact]
    public void CancelJob_UnknownOrFinishedJob_ReturnsNotFound()
    {
        var jobId = Guid.NewGuid();
        _jobRegistry.Setup(p => p.CancelJob(jobId)).Returns(false);

        var result = _controller.CancelJob(jobId);

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        var json = JObject.FromObject(notFound.Value!);
        Assert.False(json["success"]!.Value<bool>());
        Assert.Equal("Job not found or already completed", json["error"]!.ToString());
        _jobRegistry.Verify(p => p.CancelJob(jobId), Times.Once);
    }
}
