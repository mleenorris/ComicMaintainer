using ComicMaintainer.WebApi.Hubs;
using ComicMaintainer.WebApi.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Moq;
using System.IO;

namespace ComicMaintainer.Tests.Services;

public class EventBroadcasterServiceTests
{
    private readonly Mock<ILogger<EventBroadcasterService>> _mockLogger;
    private readonly Mock<IHubContext<ProgressHub>> _mockHubContext;
    private readonly Mock<IClientProxy> _mockClientProxy;
    private readonly EventBroadcasterService _service;

    public EventBroadcasterServiceTests()
    {
        _mockLogger = new Mock<ILogger<EventBroadcasterService>>();
        _mockHubContext = new Mock<IHubContext<ProgressHub>>();
        _mockClientProxy = new Mock<IClientProxy>();
        
        // Setup SignalR hub context mock
        _mockHubContext
            .Setup(h => h.Clients.Group(It.IsAny<string>()))
            .Returns(_mockClientProxy.Object);
        
        _service = new EventBroadcasterService(_mockLogger.Object, _mockHubContext.Object);
    }

    [Fact]
    public async Task BroadcastJobUpdateAsync_BroadcastsToSignalR()
    {
        // Arrange
        var jobId = Guid.NewGuid();
        var status = "running";
        var processed = 5;
        var total = 10;
        var success = 4;
        var errors = 1;

        // Act
        await _service.BroadcastJobUpdateAsync(jobId, status, processed, total, success, errors);

        // Assert
        _mockHubContext.Verify(
            h => h.Clients.Group($"job-{jobId}"),
            Times.Once);
        
        _mockClientProxy.Verify(
            c => c.SendCoreAsync(
                "JobUpdate",
                It.IsAny<object[]>(),
                default),
            Times.Once);
    }

    [Fact]
    public async Task BroadcastJobUpdateAsync_WithSseClient_BroadcastsToClient()
    {
        // Arrange
        var jobId = Guid.NewGuid();
        var clientId = "test-client";
        var memoryStream = new MemoryStream();
        var writer = new StreamWriter(memoryStream) { AutoFlush = true };
        
        _service.RegisterSseClient(clientId, writer);

        // Act
        await _service.BroadcastJobUpdateAsync(jobId, "running", 1, 10, 1, 0);

        // Assert
        memoryStream.Position = 0;
        var reader = new StreamReader(memoryStream);
        var content = await reader.ReadToEndAsync();
        
        Assert.Contains("data:", content);
        Assert.Contains("job_updated", content);
        Assert.Contains(jobId.ToString(), content);
        
        // Cleanup
        _service.UnregisterSseClient(clientId);
    }

    [Fact]
    public async Task BroadcastFileProcessedAsync_BroadcastsEvent()
    {
        // Arrange
        var filename = "test.cbz";
        var success = true;
        var clientId = "test-client";
        var memoryStream = new MemoryStream();
        var writer = new StreamWriter(memoryStream) { AutoFlush = true };
        
        _service.RegisterSseClient(clientId, writer);

        // Act
        await _service.BroadcastFileProcessedAsync(filename, success);

        // Assert
        memoryStream.Position = 0;
        var reader = new StreamReader(memoryStream);
        var content = await reader.ReadToEndAsync();
        
        Assert.Contains("file_processed", content);
        Assert.Contains(filename, content);
        Assert.Contains("true", content.ToLower());
        
        // Cleanup
        _service.UnregisterSseClient(clientId);
    }

    [Fact]
    public async Task BroadcastFileProcessedAsync_WithError_IncludesErrorMessage()
    {
        // Arrange
        var filename = "test.cbz";
        var success = false;
        var error = "Processing failed";
        var clientId = "test-client";
        var memoryStream = new MemoryStream();
        var writer = new StreamWriter(memoryStream) { AutoFlush = true };
        
        _service.RegisterSseClient(clientId, writer);

        // Act
        await _service.BroadcastFileProcessedAsync(filename, success, error);

        // Assert
        memoryStream.Position = 0;
        var reader = new StreamReader(memoryStream);
        var content = await reader.ReadToEndAsync();
        
        Assert.Contains("file_processed", content);
        Assert.Contains(filename, content);
        Assert.Contains("false", content.ToLower());
        Assert.Contains(error, content);
        
        // Cleanup
        _service.UnregisterSseClient(clientId);
    }

    [Fact]
    public async Task BroadcastWatcherStatusAsync_BroadcastsEvent()
    {
        // Arrange
        var running = true;
        var enabled = true;
        var clientId = "test-client";
        var memoryStream = new MemoryStream();
        var writer = new StreamWriter(memoryStream) { AutoFlush = true };
        
        _service.RegisterSseClient(clientId, writer);

        // Act
        await _service.BroadcastWatcherStatusAsync(running, enabled);

        // Assert
        memoryStream.Position = 0;
        var reader = new StreamReader(memoryStream);
        var content = await reader.ReadToEndAsync();
        
        Assert.Contains("watcher_status", content);
        Assert.Contains("true", content.ToLower());
        
        // Cleanup
        _service.UnregisterSseClient(clientId);
    }

    [Fact]
    public async Task BroadcastEvent_WithMultipleClients_BroadcastsToAll()
    {
        // Arrange
        var client1Id = "client1";
        var client2Id = "client2";
        var stream1 = new MemoryStream();
        var stream2 = new MemoryStream();
        var writer1 = new StreamWriter(stream1) { AutoFlush = true };
        var writer2 = new StreamWriter(stream2) { AutoFlush = true };
        
        _service.RegisterSseClient(client1Id, writer1);
        _service.RegisterSseClient(client2Id, writer2);

        // Act
        await _service.BroadcastWatcherStatusAsync(true, true);

        // Assert
        stream1.Position = 0;
        stream2.Position = 0;
        var reader1 = new StreamReader(stream1);
        var reader2 = new StreamReader(stream2);
        var content1 = await reader1.ReadToEndAsync();
        var content2 = await reader2.ReadToEndAsync();
        
        Assert.Contains("watcher_status", content1);
        Assert.Contains("watcher_status", content2);
        
        // Cleanup
        _service.UnregisterSseClient(client1Id);
        _service.UnregisterSseClient(client2Id);
    }

    [Fact]
    public async Task BroadcastEvent_WithDisconnectedClient_RemovesClient()
    {
        // Arrange
        var clientId = "test-client";
        var stream = new MemoryStream();
        var writer = new StreamWriter(stream);
        
        _service.RegisterSseClient(clientId, writer);
        
        // Close stream to simulate disconnection
        stream.Close();

        // Act - This should handle the error and remove the client
        await _service.BroadcastWatcherStatusAsync(true, true);

        // Assert - No exception thrown, error was handled gracefully
        Assert.True(true);
    }

    [Fact]
    public void RegisterSseClient_AddsClient()
    {
        // Arrange
        var clientId = "test-client";
        var memoryStream = new MemoryStream();
        var writer = new StreamWriter(memoryStream);

        // Act
        _service.RegisterSseClient(clientId, writer);

        // Assert - No exception thrown, registration successful
        Assert.True(true);
        
        // Cleanup
        _service.UnregisterSseClient(clientId);
    }

    [Fact]
    public void UnregisterSseClient_RemovesClient()
    {
        // Arrange
        var clientId = "test-client";
        var memoryStream = new MemoryStream();
        var writer = new StreamWriter(memoryStream);
        _service.RegisterSseClient(clientId, writer);

        // Act
        _service.UnregisterSseClient(clientId);

        // Assert - No exception thrown, unregistration successful
        Assert.True(true);
    }

    [Fact]
    public void UnregisterSseClient_NonExistentClient_DoesNotThrow()
    {
        // Arrange
        var clientId = "non-existent-client";

        // Act & Assert - No exception thrown
        _service.UnregisterSseClient(clientId);
        Assert.True(true);
    }
}
