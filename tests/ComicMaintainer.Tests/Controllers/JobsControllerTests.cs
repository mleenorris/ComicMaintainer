using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.WebApi.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;

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
        var returnedJob = Assert.IsType<ProcessingJob>(okResult.Value);
        Assert.Equal(jobId, returnedJob.JobId);
        Assert.Equal(JobStatus.Running, returnedJob.Status);
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
        var returnedJob = Assert.IsType<ProcessingJob>(okResult.Value);
        Assert.Equal(job.JobId, returnedJob.JobId);
    }

    [Fact]
    public async Task ProcessSelected_WithValidFiles_ReturnsJobId()
    {
        // Arrange
        var files = new List<string> { "file1.cbz", "file2.cbz" };
        var expectedJobId = Guid.NewGuid();
        var request = new JobsController.ProcessSelectedRequest { Files = files };
        
        _mockProcessor
            .Setup(p => p.ProcessFilesAsync(It.IsAny<IEnumerable<string>>(), default))
            .ReturnsAsync(expectedJobId);

        // Act
        var result = await _controller.ProcessSelected(request);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var value = okResult.Value;
        var jobIdProperty = value?.GetType().GetProperty("job_id");
        Assert.NotNull(jobIdProperty);
        Assert.Equal(expectedJobId.ToString(), jobIdProperty.GetValue(value));
        
        var totalItemsProperty = value?.GetType().GetProperty("total_items");
        Assert.NotNull(totalItemsProperty);
        Assert.Equal(files.Count, totalItemsProperty.GetValue(value));
    }

    [Fact]
    public async Task ProcessAll_WithUnprocessedFiles_ReturnsJobIdAndTotalItems()
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
            .Setup(p => p.ProcessFilesAsync(It.IsAny<IEnumerable<string>>(), default))
            .ReturnsAsync(expectedJobId);

        // Act
        var result = await _controller.ProcessAll();

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var value = okResult.Value;
        
        var jobIdProperty = value?.GetType().GetProperty("job_id");
        Assert.NotNull(jobIdProperty);
        Assert.Equal(expectedJobId.ToString(), jobIdProperty.GetValue(value));
        
        var totalItemsProperty = value?.GetType().GetProperty("total_items");
        Assert.NotNull(totalItemsProperty);
        Assert.Equal(unprocessedFiles.Count, totalItemsProperty.GetValue(value));
    }
    
    [Fact]
    public async Task ProcessAll_WithNoUnprocessedFiles_ReturnsEmptyJobId()
    {
        // Arrange
        _mockFileStore
            .Setup(fs => fs.GetFilteredFilesAsync("unprocessed", default))
            .ReturnsAsync(new List<ComicFile>());

        // Act
        var result = await _controller.ProcessAll();

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var value = okResult.Value;
        
        var jobIdProperty = value?.GetType().GetProperty("job_id");
        Assert.NotNull(jobIdProperty);
        Assert.Equal(Guid.Empty.ToString(), jobIdProperty.GetValue(value));
        
        var totalItemsProperty = value?.GetType().GetProperty("total_items");
        Assert.NotNull(totalItemsProperty);
        Assert.Equal(0, totalItemsProperty.GetValue(value));
    }

    [Fact]
    public void CancelJob_ReturnsOk()
    {
        // Arrange
        var jobId = Guid.NewGuid();

        // Act
        var result = _controller.CancelJob(jobId);

        // Assert
        Assert.IsType<OkResult>(result);
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
        var value = okResult.Value;
        
        var jobIdProperty = value?.GetType().GetProperty("job_id");
        Assert.NotNull(jobIdProperty);
        Assert.Equal(expectedJobId.ToString(), jobIdProperty.GetValue(value));
        
        var totalItemsProperty = value?.GetType().GetProperty("total_items");
        Assert.NotNull(totalItemsProperty);
        Assert.Equal(unprocessedFiles.Count, totalItemsProperty.GetValue(value));
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
        var value = okResult.Value;
        
        var jobIdProperty = value?.GetType().GetProperty("job_id");
        Assert.NotNull(jobIdProperty);
        Assert.Equal(Guid.Empty.ToString(), jobIdProperty.GetValue(value));
        
        var totalItemsProperty = value?.GetType().GetProperty("total_items");
        Assert.NotNull(totalItemsProperty);
        Assert.Equal(0, totalItemsProperty.GetValue(value));
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
            .Setup(p => p.NormalizeFilesAsync(It.IsAny<IEnumerable<string>>(), default))
            .ReturnsAsync(expectedJobId);

        // Act
        var result = await _controller.NormalizeUnmarked();

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var value = okResult.Value;
        
        var jobIdProperty = value?.GetType().GetProperty("job_id");
        Assert.NotNull(jobIdProperty);
        Assert.Equal(expectedJobId.ToString(), jobIdProperty.GetValue(value));
        
        var totalItemsProperty = value?.GetType().GetProperty("total_items");
        Assert.NotNull(totalItemsProperty);
        Assert.Equal(unprocessedFiles.Count, totalItemsProperty.GetValue(value));
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
        var value = okResult.Value;
        
        var jobIdProperty = value?.GetType().GetProperty("job_id");
        Assert.NotNull(jobIdProperty);
        Assert.Equal(Guid.Empty.ToString(), jobIdProperty.GetValue(value));
        
        var totalItemsProperty = value?.GetType().GetProperty("total_items");
        Assert.NotNull(totalItemsProperty);
        Assert.Equal(0, totalItemsProperty.GetValue(value));
    }
}
