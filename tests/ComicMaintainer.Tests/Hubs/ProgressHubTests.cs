using ComicMaintainer.WebApi.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Moq;

namespace ComicMaintainer.Tests.Hubs;

public class ProgressHubTests
{
    private readonly Mock<ILogger<ProgressHub>> _mockLogger;
    private readonly ProgressHub _hub;
    private readonly Mock<HubCallerContext> _mockContext;
    private readonly Mock<IGroupManager> _mockGroups;

    public ProgressHubTests()
    {
        _mockLogger = new Mock<ILogger<ProgressHub>>();
        _hub = new ProgressHub(_mockLogger.Object);
        
        _mockContext = new Mock<HubCallerContext>();
        _mockContext.Setup(c => c.ConnectionId).Returns("test-connection-id");
        
        _mockGroups = new Mock<IGroupManager>();
        
        // Set the Context property via reflection since it's protected
        var contextProperty = typeof(Hub).GetProperty("Context");
        contextProperty!.SetValue(_hub, _mockContext.Object);
        
        var groupsProperty = typeof(Hub).GetProperty("Groups");
        groupsProperty!.SetValue(_hub, _mockGroups.Object);
    }

    [Fact]
    public async Task OnConnectedAsync_LogsConnection()
    {
        // Act
        await _hub.OnConnectedAsync();

        // Assert
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, t) => o.ToString()!.Contains("Client connected")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task OnDisconnectedAsync_LogsDisconnection()
    {
        // Act
        await _hub.OnDisconnectedAsync(null);

        // Assert
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, t) => o.ToString()!.Contains("Client disconnected")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task SubscribeToJob_AddsConnectionToGroup()
    {
        // Arrange
        var jobId = Guid.NewGuid().ToString();

        // Act
        await _hub.SubscribeToJob(jobId);

        // Assert
        _mockGroups.Verify(
            g => g.AddToGroupAsync(
                "test-connection-id",
                $"job-{jobId}",
                default),
            Times.Once);
    }

    [Fact]
    public async Task SubscribeToJob_LogsSubscription()
    {
        // Arrange
        var jobId = Guid.NewGuid().ToString();

        // Act
        await _hub.SubscribeToJob(jobId);

        // Assert
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, t) => o.ToString()!.Contains("subscribed to job")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task UnsubscribeFromJob_RemovesConnectionFromGroup()
    {
        // Arrange
        var jobId = Guid.NewGuid().ToString();

        // Act
        await _hub.UnsubscribeFromJob(jobId);

        // Assert
        _mockGroups.Verify(
            g => g.RemoveFromGroupAsync(
                "test-connection-id",
                $"job-{jobId}",
                default),
            Times.Once);
    }

    [Fact]
    public async Task UnsubscribeFromJob_LogsUnsubscription()
    {
        // Arrange
        var jobId = Guid.NewGuid().ToString();

        // Act
        await _hub.UnsubscribeFromJob(jobId);

        // Assert
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, t) => o.ToString()!.Contains("unsubscribed from job")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }
}
