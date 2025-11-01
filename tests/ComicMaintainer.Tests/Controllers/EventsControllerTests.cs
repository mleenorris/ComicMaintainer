using ComicMaintainer.WebApi.Controllers;
using ComicMaintainer.WebApi.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using System.IO;

namespace ComicMaintainer.Tests.Controllers;

public class EventsControllerTests
{
    private readonly Mock<ILogger<EventsController>> _loggerMock;
    private readonly EventBroadcasterService _eventBroadcaster;
    private readonly EventsController _controller;

    public EventsControllerTests()
    {
        _loggerMock = new Mock<ILogger<EventsController>>();
        
        // Create concrete EventBroadcasterService for testing
        var hubContextMock = new Mock<Microsoft.AspNetCore.SignalR.IHubContext<ComicMaintainer.WebApi.Hubs.ProgressHub>>();
        var broadcasterLoggerMock = new Mock<ILogger<EventBroadcasterService>>();
        _eventBroadcaster = new EventBroadcasterService(broadcasterLoggerMock.Object, hubContextMock.Object);
        
        _controller = new EventsController(_loggerMock.Object, _eventBroadcaster);
        
        // Setup HTTP context
        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = new MemoryStream();
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };
    }

    [Fact]
    public async Task Stream_SetsCorrectHeaders()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        _controller.HttpContext.RequestAborted = cts.Token;
        
        // Cancel immediately to exit the stream loop
        cts.Cancel();

        // Act
        try
        {
            await _controller.Stream();
        }
        catch (OperationCanceledException)
        {
            // Expected when cancelled
        }

        // Assert
        Assert.Equal("text/event-stream", _controller.Response.Headers.ContentType.ToString());
        Assert.Equal("no-cache", _controller.Response.Headers.CacheControl.ToString());
        Assert.Equal("keep-alive", _controller.Response.Headers.Connection.ToString());
    }

    [Fact]
    public async Task Stream_HandlesClientDisconnect()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        _controller.HttpContext.RequestAborted = cts.Token;
        cts.Cancel(); // Simulate client disconnect

        // Act & Assert - should not throw
        try
        {
            await _controller.Stream();
        }
        catch (OperationCanceledException)
        {
            // Expected behavior
        }
    }
}
