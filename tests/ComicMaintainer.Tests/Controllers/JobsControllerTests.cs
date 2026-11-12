using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Tests.Helpers;
using ComicMaintainer.WebApi.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Newtonsoft.Json.Linq;

namespace ComicMaintainer.Tests.Controllers;

public class JobsControllerTests
{
    private readonly Mock<IComicProcessorService> _mockProcessor;
    private readonly Mock<IBatchJobRegistry> _jobRegistry = new();
    private readonly Mock<IFileStoreService> _mockFileStore;
    private readonly Mock<ILogger<JobsController>> _mockLogger;
    private readonly JobsController _controller;

    public JobsControllerTests()
    {
        _mockProcessor = new Mock<IComicProcessorService>();
        _mockFileStore = new Mock<IFileStoreService>();
        _mockLogger = new Mock<ILogger<JobsController>>();
        // The files these tests delete ("/path/...") live in the library.
        var settings = new TestOptionsMonitor<AppSettings>(new AppSettings { WatchedDirectory = "/path" });
        _controller = new JobsController(_mockProcessor.Object, _jobRegistry.Object, _mockFileStore.Object, _mockLogger.Object, settings);
    }

    [Fact]
    public void GetJob_ExistingJob_ReturnsOkWithJob()
    {
        // Arrange
        var jobId = Guid.NewGuid();
        var job = new ProcessingJob
        {
            JobId = jobId,
            Status = JobStatus.Running,
            TotalFiles = 5,
            ProcessedFiles = 2
        };
        _jobRegistry.Setup(p => p.GetJob(jobId)).Returns(job);

        // Act
        var result = _controller.GetJob(jobId);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(okResult.Value);
        var json = JObject.FromObject(okResult.Value);
        Assert.Equal(jobId.ToString(), json["job_id"]!.ToString());
        Assert.Equal("running", json["status"]!.ToString());
    }

    [Fact]
    public void GetJob_ProblemReportedForSomethingOtherThanAnItem_IsListedApartFromTheResults()
    {
        // A folder combine reports a source folder it had to keep through
        // IBatchJobReport; the job's items are the files it moved.
        var jobId = Guid.NewGuid();
        var job = new ProcessingJob
        {
            JobId = jobId,
            Status = JobStatus.Completed,
            Files = new List<string> { "/library/Old/a.cbz", "/library/Old/b.cbz" },
            TotalFiles = 2,
            ProcessedFiles = 1,
            FailedFiles = 1,
            Errors = new Dictionary<string, string>
            {
                ["/library/Old/b.cbz"] = "Combine folder move failed",
                ["/library/Old"] = "Source folder kept. It still holds files that are not disposable. Remaining: b.cbz."
            }
        };
        _jobRegistry.Setup(p => p.GetJob(jobId)).Returns(job);

        var result = _controller.GetJob(jobId);

        var json = JObject.FromObject(Assert.IsType<OkObjectResult>(result.Result).Value!);
        Assert.Equal(1, json["failed_items"]!.Value<int>());
        // The results stay one entry per item, so whatever counts them still
        // counts the job's items.
        var results = json["results"]!.ToObject<List<JObject>>()!;
        Assert.Equal(new[] { "/library/Old/a.cbz", "/library/Old/b.cbz" }, results.Select(r => r["file"]!.ToString()));
        Assert.True(results[0]["success"]!.Value<bool>());
        Assert.False(results[1]["success"]!.Value<bool>());
        var problem = Assert.Single(json["reported_problems"]!.ToObject<List<JObject>>()!);
        Assert.Equal("/library/Old", problem["subject"]!.ToString());
        Assert.Equal(job.Errors["/library/Old"], problem["message"]!.ToString());
    }

    [Fact]
    public void GetJob_NoProblemsReported_ListsNone()
    {
        var jobId = Guid.NewGuid();
        var job = new ProcessingJob
        {
            JobId = jobId,
            Status = JobStatus.Completed,
            Files = new List<string> { "/library/a.cbz" },
            TotalFiles = 1,
            FailedFiles = 1,
            Errors = new Dictionary<string, string> { ["/library/a.cbz"] = "Failed" }
        };
        _jobRegistry.Setup(p => p.GetJob(jobId)).Returns(job);

        var result = _controller.GetJob(jobId);

        var json = JObject.FromObject(Assert.IsType<OkObjectResult>(result.Result).Value!);
        Assert.Empty(json["reported_problems"]!);
    }

    [Fact]
    public void GetJob_NonExistentJob_ReturnsNotFound()
    {
        // Arrange
        var jobId = Guid.NewGuid();
        _jobRegistry.Setup(p => p.GetJob(jobId)).Returns(default(ProcessingJob?));

        // Act
        var result = _controller.GetJob(jobId);

        // Assert
        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public void GetActiveJob_WhenNoActiveJob_ReturnsInactive()
    {
        // Arrange
        _jobRegistry.Setup(p => p.GetActiveJob()).Returns(default(ProcessingJobSummary?));
        _jobRegistry.Setup(p => p.GetJobSummaries()).Returns(new List<ProcessingJobSummary>());

        // Act
        var result = _controller.GetActiveJob();

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(okResult.Value);
    }

    [Fact]
    public void GetActiveJob_WhenNoRunningJobButInterruptedExists_ReturnsInterruptedJob()
    {
        var interrupted = new ProcessingJob
        {
            JobId = Guid.NewGuid(),
            Status = JobStatus.Interrupted,
            TotalFiles = 10,
            ProcessedFiles = 3,
            FailedFiles = 1
        };

        _jobRegistry.Setup(p => p.GetActiveJob()).Returns(default(ProcessingJobSummary?));
        _jobRegistry.Setup(p => p.GetJobSummaries()).Returns(new List<ProcessingJobSummary> { interrupted.ToSummary() });

        var result = _controller.GetActiveJob();

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var json = JObject.FromObject(okResult.Value!);
        Assert.Equal(interrupted.JobId.ToString(), json["job_id"]!.ToString());
        Assert.Equal("interrupted", json["status"]!.ToString());
    }

    [Fact]
    public void GetActiveJob_WhenActiveJobExists_ReturnsJob()
    {
        // Arrange
        var job = new ProcessingJob
        {
            JobId = Guid.NewGuid(),
            Status = JobStatus.Running,
            TotalFiles = 10
        };
        _jobRegistry.Setup(p => p.GetActiveJob()).Returns(job.ToSummary());

        // Act
        var result = _controller.GetActiveJob();

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(okResult.Value);
        var json = JObject.FromObject(okResult.Value);
        Assert.Equal(job.JobId.ToString(), json["job_id"]!.ToString());
        Assert.Equal("running", json["status"]!.ToString());
    }

    
    [Fact]
    public void CancelJob_ReturnsOkWithSuccessJson()
    {
        // Arrange
        var jobId = Guid.NewGuid();
        _jobRegistry.Setup(p => p.CancelJob(jobId)).Returns(true);

        // Act
        var result = _controller.CancelJob(jobId);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(okResult.Value);
        var json = JObject.FromObject(okResult.Value);
        Assert.True(json["success"]!.Value<bool>());
    }

    // New RESTful endpoint tests

    [Fact]
    public void ListJobs_ReturnsOkWithJobsList()
    {
        // Arrange
        var jobs = new List<ProcessingJob>
        {
            new() { JobId = Guid.NewGuid(), Status = JobStatus.Completed, TotalFiles = 5, ProcessedFiles = 5 },
            new() { JobId = Guid.NewGuid(), Status = JobStatus.Running, TotalFiles = 10, ProcessedFiles = 3 }
        };
        _jobRegistry.Setup(p => p.GetJobSummaries()).Returns(jobs.ConvertAll(j => j.ToSummary()));

        // Act
        var result = _controller.ListJobs();

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(okResult.Value);
        var json = JObject.FromObject(okResult.Value);
        Assert.Equal(2, json["count"]!.Value<int>());
        Assert.NotNull(json["jobs"]);
    }

    [Fact]
    public void ListJobs_WhenNoJobs_ReturnsEmptyList()
    {
        // Arrange
        _jobRegistry.Setup(p => p.GetJobSummaries()).Returns(new List<ProcessingJobSummary>());

        // Act
        var result = _controller.ListJobs();

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(okResult.Value);
        var json = JObject.FromObject(okResult.Value);
        Assert.Equal(0, json["count"]!.Value<int>());
    }

    [Fact]
    public async Task DeleteJob_ExistingJob_ReturnsNoContent()
    {
        // Arrange
        var jobId = Guid.NewGuid();
        _jobRegistry.Setup(p => p.DeleteJobAsync(jobId, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        // Act
        var result = await _controller.DeleteJob(jobId, CancellationToken.None);

        // Assert
        Assert.IsType<NoContentResult>(result);
        _jobRegistry.Verify(p => p.DeleteJobAsync(jobId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteJob_NonExistentJob_ReturnsNotFound()
    {
        // Arrange
        var jobId = Guid.NewGuid();
        _jobRegistry.Setup(p => p.DeleteJobAsync(jobId, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        // Act
        var result = await _controller.DeleteJob(jobId, CancellationToken.None);

        // Assert
        Assert.IsType<NotFoundResult>(result);
    }
}
