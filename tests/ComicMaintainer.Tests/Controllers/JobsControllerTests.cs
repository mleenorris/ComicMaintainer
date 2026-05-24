using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.WebApi.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Newtonsoft.Json.Linq;

namespace ComicMaintainer.Tests.Controllers;

public class JobsControllerTests
{
    private readonly Mock<IComicProcessorService> _mockProcessor;
    private readonly Mock<IFileStoreService> _mockFileStore;
    private readonly Mock<ILogger<JobsController>> _mockLogger;
    private readonly JobsController _controller;

    public JobsControllerTests()
    {
        _mockProcessor = new Mock<IComicProcessorService>();
        _mockFileStore = new Mock<IFileStoreService>();
        _mockLogger = new Mock<ILogger<JobsController>>();
        _controller = new JobsController(_mockProcessor.Object, _mockFileStore.Object, _mockLogger.Object);
    }

    // Helper method to extract job response from OkObjectResult without reflection
    private static (string jobId, int totalItems) GetJobResponse(OkObjectResult result)
    {
        var json = JObject.FromObject(result.Value!);
        return (json["job_id"]!.ToString(), json["total_items"]!.Value<int>());
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
        _mockProcessor.Setup(p => p.GetJob(jobId)).Returns(job);

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
    public void GetJob_NonExistentJob_ReturnsNotFound()
    {
        // Arrange
        var jobId = Guid.NewGuid();
        _mockProcessor.Setup(p => p.GetJob(jobId)).Returns((ProcessingJob?)null);

        // Act
        var result = _controller.GetJob(jobId);

        // Assert
        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public void GetJob_WhenExceptionThrown_ReturnsInternalServerError()
    {
        // Arrange
        var jobId = Guid.NewGuid();
        _mockProcessor.Setup(p => p.GetJob(jobId)).Throws(new InvalidOperationException("Test error"));

        // Act
        var result = _controller.GetJob(jobId);

        // Assert
        var statusCodeResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(500, statusCodeResult.StatusCode);
    }

    [Fact]
    public void GetActiveJob_WhenNoActiveJob_ReturnsInactive()
    {
        // Arrange
        _mockProcessor.Setup(p => p.GetActiveJob()).Returns((ProcessingJob?)null);

        // Act
        var result = _controller.GetActiveJob();

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(okResult.Value);
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
        _mockProcessor.Setup(p => p.GetActiveJob()).Returns(job);

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
    public async Task ProcessSelected_WithValidFiles_ReturnsJobId()
    {
        // Arrange
        var files = new List<string> { "file1.cbz", "file2.cbz" };
        var expectedJobId = Guid.NewGuid();
        var request = new JobsController.ProcessSelectedRequest { Files = files };
        
        _mockProcessor
            .Setup(p => p.ProcessFilesAsync(It.IsAny<IEnumerable<string>>(), false, default))
            .ReturnsAsync(expectedJobId);

        // Act
        var result = await _controller.ProcessSelected(request);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var (jobId, totalItems) = GetJobResponse(okResult);
        Assert.Equal(expectedJobId.ToString(), jobId);
        Assert.Equal(files.Count, totalItems);
    }

    [Fact]
    public async Task ProcessSelected_WithForceReprocess_PassesFlagToProcessor()
    {
        var files = new List<string> { "file1.cbz" };
        var expectedJobId = Guid.NewGuid();
        var request = new JobsController.ProcessSelectedRequest { Files = files, ForceReprocess = true };

        _mockProcessor
            .Setup(p => p.ProcessFilesAsync(It.IsAny<IEnumerable<string>>(), true, default))
            .ReturnsAsync(expectedJobId);

        var result = await _controller.ProcessSelected(request);

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var (jobId, totalItems) = GetJobResponse(okResult);
        Assert.Equal(expectedJobId.ToString(), jobId);
        Assert.Equal(1, totalItems);
        _mockProcessor.Verify(p => p.ProcessFilesAsync(It.Is<IEnumerable<string>>(paths => paths.SequenceEqual(files)), true, default), Times.Once);
    }

    [Fact]
    public async Task ProcessAll_WithUnprocessedFiles_ReturnsJobIdAndTotalItems()
    {
        // Arrange
        var allFiles = new List<ComicFile>
        {
            new() { FilePath = "/path/file1.cbz", IsProcessed = false },
            new() { FilePath = "/path/file2.cbz", IsProcessed = false }
        };
        var expectedJobId = Guid.NewGuid();
        
        _mockFileStore
            .Setup(fs => fs.GetAllFilesAsync(default))
            .ReturnsAsync(allFiles);
        
        _mockProcessor
            .Setup(p => p.ProcessFilesAsync(It.IsAny<IEnumerable<string>>(), false, default))
            .ReturnsAsync(expectedJobId);

        // Act
        var result = await _controller.ProcessAll();

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var (jobId, totalItems) = GetJobResponse(okResult);
        Assert.Equal(expectedJobId.ToString(), jobId);
        Assert.Equal(allFiles.Count, totalItems);
    }
    
    [Fact]
    public async Task ProcessAll_WithNoUnprocessedFiles_ReturnsEmptyJobId()
    {
        // Arrange
        _mockFileStore
            .Setup(fs => fs.GetAllFilesAsync(default))
            .ReturnsAsync(new List<ComicFile>());

        // Act
        var result = await _controller.ProcessAll();

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var (jobId, totalItems) = GetJobResponse(okResult);
        Assert.Equal(Guid.Empty.ToString(), jobId);
        Assert.Equal(0, totalItems);
    }

    [Fact]
    public void CancelJob_ReturnsOkWithSuccessJson()
    {
        // Arrange
        var jobId = Guid.NewGuid();
        _mockProcessor.Setup(p => p.CancelJob(jobId)).Returns(true);

        // Act
        var result = _controller.CancelJob(jobId);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(okResult.Value);
        var json = JObject.FromObject(okResult.Value);
        Assert.True(json["success"]!.Value<bool>());
    }

    [Fact]
    public async Task RenameUnmarked_WithUnprocessedFiles_ReturnsJobIdAndTotalItems()
    {
        // Arrange
        var unprocessedFiles = new List<ComicFile>
        {
            new() { FilePath = "/path/file1.cbz", IsProcessed = false },
            new() { FilePath = "/path/file2.cbz", IsProcessed = false }
        };
        var expectedJobId = Guid.NewGuid();
        
        _mockFileStore
            .Setup(fs => fs.GetFilteredFilesAsync("unprocessed", default))
            .ReturnsAsync(unprocessedFiles);
        
        _mockProcessor
            .Setup(p => p.RenameFilesAsync(It.IsAny<IEnumerable<string>>(), default))
            .ReturnsAsync(expectedJobId);

        // Act
        var result = await _controller.RenameUnmarked();

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var (jobId, totalItems) = GetJobResponse(okResult);
        Assert.Equal(expectedJobId.ToString(), jobId);
        Assert.Equal(unprocessedFiles.Count, totalItems);
    }

    [Fact]
    public async Task RenameUnmarked_WithNoUnprocessedFiles_ReturnsEmptyJobId()
    {
        // Arrange
        _mockFileStore
            .Setup(fs => fs.GetFilteredFilesAsync("unprocessed", default))
            .ReturnsAsync(new List<ComicFile>());

        // Act
        var result = await _controller.RenameUnmarked();

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var (jobId, totalItems) = GetJobResponse(okResult);
        Assert.Equal(Guid.Empty.ToString(), jobId);
        Assert.Equal(0, totalItems);
    }

    [Fact]
    public async Task NormalizeUnmarked_WithUnprocessedFiles_ReturnsJobIdAndTotalItems()
    {
        // Arrange
        var unprocessedFiles = new List<ComicFile>
        {
            new() { FilePath = "/path/file1.cbz", IsProcessed = false },
            new() { FilePath = "/path/file2.cbz", IsProcessed = false }
        };
        var expectedJobId = Guid.NewGuid();
        
        _mockFileStore
            .Setup(fs => fs.GetFilteredFilesAsync("unprocessed", default))
            .ReturnsAsync(unprocessedFiles);
        
        _mockProcessor
            .Setup(p => p.NormalizeFilesAsync(It.IsAny<IEnumerable<string>>(), false, default))
            .ReturnsAsync(expectedJobId);

        // Act
        var result = await _controller.NormalizeUnmarked();

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var (jobId, totalItems) = GetJobResponse(okResult);
        Assert.Equal(expectedJobId.ToString(), jobId);
        Assert.Equal(unprocessedFiles.Count, totalItems);
    }

    [Fact]
    public async Task NormalizeUnmarked_WithNoUnprocessedFiles_ReturnsEmptyJobId()
    {
        // Arrange
        _mockFileStore
            .Setup(fs => fs.GetFilteredFilesAsync("unprocessed", default))
            .ReturnsAsync(new List<ComicFile>());

        // Act
        var result = await _controller.NormalizeUnmarked();

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var (jobId, totalItems) = GetJobResponse(okResult);
        Assert.Equal(Guid.Empty.ToString(), jobId);
        Assert.Equal(0, totalItems);
    }

    [Fact]
    public async Task NormalizeUnmarked_WithForceReprocess_ForwardsFlagToProcessor()
    {
        // Arrange
        var unprocessedFiles = new List<ComicFile>
        {
            new() { FilePath = "/path/file1.cbz", IsProcessed = false },
        };
        var expectedJobId = Guid.NewGuid();

        _mockFileStore
            .Setup(fs => fs.GetFilteredFilesAsync("unprocessed", default))
            .ReturnsAsync(unprocessedFiles);

        _mockProcessor
            .Setup(p => p.NormalizeFilesAsync(It.IsAny<IEnumerable<string>>(), true, default))
            .ReturnsAsync(expectedJobId);

        // Act
        var result = await _controller.NormalizeUnmarked(forceReprocess: true);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var (jobId, _) = GetJobResponse(okResult);
        Assert.Equal(expectedJobId.ToString(), jobId);
        _mockProcessor.Verify(
            p => p.NormalizeFilesAsync(It.IsAny<IEnumerable<string>>(), true, default),
            Times.Once);
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
        _mockProcessor.Setup(p => p.GetAllJobs()).Returns(jobs);

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
        _mockProcessor.Setup(p => p.GetAllJobs()).Returns(new List<ProcessingJob>());

        // Act
        var result = _controller.ListJobs();

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(okResult.Value);
        var json = JObject.FromObject(okResult.Value);
        Assert.Equal(0, json["count"]!.Value<int>());
    }

    [Fact]
    public void ListJobs_WhenExceptionThrown_ReturnsInternalServerError()
    {
        // Arrange
        _mockProcessor.Setup(p => p.GetAllJobs()).Throws(new InvalidOperationException("Test error"));

        // Act
        var result = _controller.ListJobs();

        // Assert
        var statusCodeResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(500, statusCodeResult.StatusCode);
    }

    [Fact]
    public void DeleteJob_ExistingJob_ReturnsNoContent()
    {
        // Arrange
        var jobId = Guid.NewGuid();
        _mockProcessor.Setup(p => p.DeleteJob(jobId)).Returns(true);

        // Act
        var result = _controller.DeleteJob(jobId);

        // Assert
        Assert.IsType<NoContentResult>(result);
        _mockProcessor.Verify(p => p.DeleteJob(jobId), Times.Once);
    }

    [Fact]
    public void DeleteJob_NonExistentJob_ReturnsNotFound()
    {
        // Arrange
        var jobId = Guid.NewGuid();
        _mockProcessor.Setup(p => p.DeleteJob(jobId)).Returns(false);

        // Act
        var result = _controller.DeleteJob(jobId);

        // Assert
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public void DeleteJob_WhenExceptionThrown_ReturnsInternalServerError()
    {
        // Arrange
        var jobId = Guid.NewGuid();
        _mockProcessor.Setup(p => p.DeleteJob(jobId)).Throws(new InvalidOperationException("Test error"));

        // Act
        var result = _controller.DeleteJob(jobId);

        // Assert
        var statusCodeResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(500, statusCodeResult.StatusCode);
    }

    [Fact]
    public async Task UpdateMetadataSelected_WithValidFilesAndMetadata_ReturnsJobIdAndTotalItems()
    {
        // Arrange
        var files = new List<string> { "/path/file1.cbz", "/path/file2.cbz" };
        var metadata = new ComicMetadata { Series = "Test Series", Issue = "1" };
        var expectedJobId = Guid.NewGuid();
        
        _mockProcessor
            .Setup(p => p.UpdateMetadataAsync(files, metadata, default))
            .ReturnsAsync(expectedJobId);

        var request = new JobsController.UpdateMetadataSelectedRequest
        {
            Files = files,
            Metadata = metadata
        };

        // Act
        var result = await _controller.UpdateMetadataSelected(request);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var (jobId, totalItems) = GetJobResponse(okResult);
        Assert.Equal(expectedJobId.ToString(), jobId);
        Assert.Equal(2, totalItems);
    }

    [Fact]
    public async Task UpdateMetadataSelected_WithNoFiles_ReturnsBadRequest()
    {
        // Arrange
        var request = new JobsController.UpdateMetadataSelectedRequest
        {
            Files = new List<string>(),
            Metadata = new ComicMetadata { Series = "Test Series" }
        };

        // Act
        var result = await _controller.UpdateMetadataSelected(request);

        // Assert
        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.NotNull(badRequestResult.Value);
    }

    [Fact]
    public async Task UpdateMetadataSelected_WithNullMetadata_ReturnsBadRequest()
    {
        // Arrange
        var request = new JobsController.UpdateMetadataSelectedRequest
        {
            Files = new List<string> { "/path/file1.cbz" },
            Metadata = null!
        };

        // Act
        var result = await _controller.UpdateMetadataSelected(request);

        // Assert
        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.NotNull(badRequestResult.Value);
    }

    [Fact]
    public async Task UpdateMetadataSelected_WhenExceptionThrown_ReturnsInternalServerError()
    {
        // Arrange
        var files = new List<string> { "/path/file1.cbz" };
        var metadata = new ComicMetadata { Series = "Test Series" };
        
        _mockProcessor
            .Setup(p => p.UpdateMetadataAsync(files, metadata, default))
            .ThrowsAsync(new InvalidOperationException("Test error"));

        var request = new JobsController.UpdateMetadataSelectedRequest
        {
            Files = files,
            Metadata = metadata
        };

        // Act
        var result = await _controller.UpdateMetadataSelected(request);

        // Assert
        var statusCodeResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(500, statusCodeResult.StatusCode);
    }

    [Fact]
    public async Task DeleteSelected_ValidRequest_QueuesJobAndReturnsBothJobIdShapes()
    {
        // Arrange
        var files = new List<string> { "/path/a.cbz", "/path/b.cbz" };
        var jobId = Guid.NewGuid();
        _mockProcessor
            .Setup(p => p.DeleteFilesAsync(files, default))
            .ReturnsAsync(jobId);

        var request = new JobsController.ProcessSelectedRequest { Files = files };

        // Act
        var result = await _controller.DeleteSelected(request);

        // Assert
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var json = JObject.FromObject(ok.Value!);
        // Both legacy snake_case and new camelCase shapes must be present so
        // callers can use a single response contract.
        Assert.Equal(jobId.ToString(), json["job_id"]!.ToString());
        Assert.Equal(jobId.ToString(), json["jobId"]!.ToString());
        Assert.Equal(2, json["total_items"]!.Value<int>());
        Assert.Equal(2, json["totalItems"]!.Value<int>());
    }

    [Fact]
    public async Task DeleteSelected_EmptyRequest_ReturnsBadRequest()
    {
        // Arrange
        var request = new JobsController.ProcessSelectedRequest { Files = new List<string>() };

        // Act
        var result = await _controller.DeleteSelected(request);

        // Assert
        Assert.IsType<BadRequestObjectResult>(result.Result);
        _mockProcessor.Verify(p => p.DeleteFilesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteSelected_WhenExceptionThrown_ReturnsInternalServerError()
    {
        // Arrange
        var files = new List<string> { "/path/a.cbz" };
        _mockProcessor
            .Setup(p => p.DeleteFilesAsync(files, default))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var request = new JobsController.ProcessSelectedRequest { Files = files };

        // Act
        var result = await _controller.DeleteSelected(request);

        // Assert
        var status = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(500, status.StatusCode);
    }
}
