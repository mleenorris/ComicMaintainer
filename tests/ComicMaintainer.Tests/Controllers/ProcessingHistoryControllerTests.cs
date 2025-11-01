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
}
