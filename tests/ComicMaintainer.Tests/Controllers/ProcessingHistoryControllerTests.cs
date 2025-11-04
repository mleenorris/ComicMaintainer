using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.WebApi.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;

namespace ComicMaintainer.Tests.Controllers;

public class ProcessingHistoryControllerTests
{
    private readonly Mock<IProcessingHistoryService> _historyServiceMock;
    private readonly Mock<ILogger<ProcessingHistoryController>> _loggerMock;
    private readonly ProcessingHistoryController _controller;

    public ProcessingHistoryControllerTests()
    {
        _historyServiceMock = new Mock<IProcessingHistoryService>();
        _loggerMock = new Mock<ILogger<ProcessingHistoryController>>();
        _controller = new ProcessingHistoryController(_historyServiceMock.Object, _loggerMock.Object);
    }

    [Fact]
    public async Task GetProcessingHistory_ReturnsEmptyHistoryWhenNoData()
    {
        // Arrange
        var emptyHistory = Enumerable.Empty<ProcessingHistoryEntry>();
        _historyServiceMock
            .Setup(s => s.GetHistoryAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((emptyHistory, 0));

        // Act
        var result = await _controller.GetProcessingHistory(50, 0);

        // Assert
        var okResult = Assert.IsType<ActionResult<object>>(result);
        var objectResult = Assert.IsType<OkObjectResult>(okResult.Result);
        Assert.NotNull(objectResult.Value);
        
        var value = objectResult.Value;
        var historyProperty = value.GetType().GetProperty("history");
        var totalProperty = value.GetType().GetProperty("total");
        
        Assert.NotNull(historyProperty);
        Assert.NotNull(totalProperty);
        Assert.Equal(0, totalProperty.GetValue(value));
    }

    [Fact]
    public async Task GetProcessingHistory_ReturnsHistoryWithCorrectFormat()
    {
        // Arrange
        var historyEntries = new List<ProcessingHistoryEntry>
        {
            new ProcessingHistoryEntry
            {
                Id = Guid.NewGuid(),
                FilePath = "/path/to/file.cbz",
                Action = "processed",
                Timestamp = DateTime.UtcNow,
                Success = true,
                ErrorMessage = null
            }
        };
        
        _historyServiceMock
            .Setup(s => s.GetHistoryAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((historyEntries, 1));

        // Act
        var result = await _controller.GetProcessingHistory(50, 0);

        // Assert
        var okResult = Assert.IsType<ActionResult<object>>(result);
        var objectResult = Assert.IsType<OkObjectResult>(okResult.Result);
        Assert.NotNull(objectResult.Value);
        
        var value = objectResult.Value;
        var totalProperty = value.GetType().GetProperty("total");
        
        Assert.NotNull(totalProperty);
        Assert.Equal(1, totalProperty.GetValue(value));
    }

    [Fact]
    public async Task GetProcessingHistory_HandlesExceptionAndReturns500()
    {
        // Arrange
        _historyServiceMock
            .Setup(s => s.GetHistoryAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Database error"));

        // Act
        var result = await _controller.GetProcessingHistory(50, 0);

        // Assert
        var statusResult = Assert.IsType<ActionResult<object>>(result);
        var objectResult = Assert.IsType<ObjectResult>(statusResult.Result);
        Assert.Equal(500, objectResult.StatusCode);
    }

    [Fact]
    public async Task GetProcessingHistory_UsesCorrectLimitAndOffset()
    {
        // Arrange
        int expectedLimit = 25;
        int expectedOffset = 50;
        var emptyHistory = Enumerable.Empty<ProcessingHistoryEntry>();
        
        _historyServiceMock
            .Setup(s => s.GetHistoryAsync(expectedLimit, expectedOffset, It.IsAny<CancellationToken>()))
            .ReturnsAsync((emptyHistory, 0));

        // Act
        await _controller.GetProcessingHistory(expectedLimit, expectedOffset);

        // Assert
        _historyServiceMock.Verify(
            s => s.GetHistoryAsync(expectedLimit, expectedOffset, It.IsAny<CancellationToken>()), 
            Times.Once);
    }

    [Fact]
    public async Task GetProcessingHistory_RejectsInvalidLimit()
    {
        // Arrange
        _historyServiceMock
            .Setup(s => s.GetHistoryAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentOutOfRangeException("limit"));

        // Act
        var result = await _controller.GetProcessingHistory(0, 0);

        // Assert
        var statusResult = Assert.IsType<ActionResult<object>>(result);
        var badRequestResult = Assert.IsType<BadRequestObjectResult>(statusResult.Result);
        Assert.Equal(400, badRequestResult.StatusCode);
    }

    [Fact]
    public async Task GetProcessingHistory_RejectsNegativeOffset()
    {
        // Arrange
        _historyServiceMock
            .Setup(s => s.GetHistoryAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentOutOfRangeException("offset"));

        // Act
        var result = await _controller.GetProcessingHistory(50, -1);

        // Assert
        var statusResult = Assert.IsType<ActionResult<object>>(result);
        var badRequestResult = Assert.IsType<BadRequestObjectResult>(statusResult.Result);
        Assert.Equal(400, badRequestResult.StatusCode);
    }

    [Fact]
    public async Task GetProcessingHistory_IncludesSuccessStatusAndErrorMessage()
    {
        // Arrange
        var historyEntries = new List<ProcessingHistoryEntry>
        {
            new ProcessingHistoryEntry
            {
                Id = Guid.NewGuid(),
                FilePath = "/path/to/file.cbz",
                Action = "Rename",
                Timestamp = DateTime.UtcNow,
                Success = false,
                ErrorMessage = "File not found"
            }
        };
        
        _historyServiceMock
            .Setup(s => s.GetHistoryAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((historyEntries, 1));

        // Act
        var result = await _controller.GetProcessingHistory(50, 0);

        // Assert
        var okResult = Assert.IsType<ActionResult<object>>(result);
        var objectResult = Assert.IsType<OkObjectResult>(okResult.Result);
        Assert.NotNull(objectResult.Value);
        
        // Use reflection to access the anonymous type properties
        var value = objectResult.Value;
        var historyProperty = value.GetType().GetProperty("history");
        Assert.NotNull(historyProperty);
        
        var historyValue = historyProperty.GetValue(value) as System.Collections.IEnumerable;
        Assert.NotNull(historyValue);
        
        var historyList = historyValue.Cast<object>().ToList();
        Assert.Single(historyList);
        
        var firstItem = historyList[0];
        var successProperty = firstItem.GetType().GetProperty("success");
        var errorMessageProperty = firstItem.GetType().GetProperty("error_message");
        
        Assert.NotNull(successProperty);
        Assert.NotNull(errorMessageProperty);
        Assert.False((bool)successProperty.GetValue(firstItem)!);
        Assert.Equal("File not found", errorMessageProperty.GetValue(firstItem));
    }

    [Fact]
    public async Task GetProcessingHistory_IncludesBeforeAndAfterMetadata()
    {
        // Arrange
        var historyEntries = new List<ProcessingHistoryEntry>
        {
            new ProcessingHistoryEntry
            {
                Id = Guid.NewGuid(),
                FilePath = "/path/to/file.cbz",
                Action = "Rename",
                Timestamp = DateTime.UtcNow,
                Success = true,
                BeforeFilename = "old_name.cbz",
                AfterFilename = "new_name.cbz",
                BeforeTitle = "Old Title",
                AfterTitle = "New Title",
                BeforeSeries = "Old Series",
                AfterSeries = "New Series"
            }
        };
        
        _historyServiceMock
            .Setup(s => s.GetHistoryAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((historyEntries, 1));

        // Act
        var result = await _controller.GetProcessingHistory(50, 0);

        // Assert
        var okResult = Assert.IsType<ActionResult<object>>(result);
        var objectResult = Assert.IsType<OkObjectResult>(okResult.Result);
        Assert.NotNull(objectResult.Value);
        
        // Use reflection to access the anonymous type properties
        var value = objectResult.Value;
        var historyProperty = value.GetType().GetProperty("history");
        Assert.NotNull(historyProperty);
        
        var historyValue = historyProperty.GetValue(value) as System.Collections.IEnumerable;
        Assert.NotNull(historyValue);
        
        var historyList = historyValue.Cast<object>().ToList();
        Assert.Single(historyList);
        
        var firstItem = historyList[0];
        var beforeFilenameProperty = firstItem.GetType().GetProperty("before_filename");
        var afterFilenameProperty = firstItem.GetType().GetProperty("after_filename");
        var beforeTitleProperty = firstItem.GetType().GetProperty("before_title");
        var afterTitleProperty = firstItem.GetType().GetProperty("after_title");
        
        Assert.NotNull(beforeFilenameProperty);
        Assert.NotNull(afterFilenameProperty);
        Assert.NotNull(beforeTitleProperty);
        Assert.NotNull(afterTitleProperty);
        Assert.Equal("old_name.cbz", beforeFilenameProperty.GetValue(firstItem));
        Assert.Equal("new_name.cbz", afterFilenameProperty.GetValue(firstItem));
        Assert.Equal("Old Title", beforeTitleProperty.GetValue(firstItem));
        Assert.Equal("New Title", afterTitleProperty.GetValue(firstItem));
    }
}
