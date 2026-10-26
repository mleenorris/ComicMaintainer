using System.Text;
using ComicMaintainer.WebApi.Controllers;
using ComicMaintainer.WebApi.Hubs;
using ComicMaintainer.WebApi.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Moq;

namespace ComicMaintainer.Tests.Controllers;

/// <summary>
/// The lifetime of a live SSE connection, which <see cref="EventsControllerTests"/>
/// never opens: a connected client gets heartbeats and broadcasts until it
/// disconnects, and is unregistered afterwards.
/// </summary>
public class EventsControllerCoverageTests
{
    private readonly Mock<ILogger<EventsController>> _logger = new();
    private readonly EventBroadcasterService _broadcaster = new(
        Mock.Of<ILogger<EventBroadcasterService>>(),
        Mock.Of<IHubContext<ProgressHub>>());

    [Fact]
    public async Task Stream_SendsHeartbeatsAndBroadcastsUntilTheClientDisconnects()
    {
        using var disconnect = new CancellationTokenSource();
        var body = new FlushSignallingStream();
        var httpContext = new DefaultHttpContext { RequestAborted = disconnect.Token };
        httpContext.Response.Body = body;
        var controller = new EventsController(_logger.Object, _broadcaster)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };

        var streaming = controller.Stream();

        // The first heartbeat goes out immediately; the next one is 30s away.
        await body.FirstFlush.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(": heartbeat\n\n", body.Text);
        Assert.False(streaming.IsCompleted);

        // While connected, the client receives broadcasts.
        await _broadcaster.BroadcastFileListUpdateAsync();
        Assert.Equal(": heartbeat\n\ndata: {\"type\":\"file_list_updated\",\"data\":{}}\n\n", body.Text);

        // Disconnecting ends the stream normally rather than faulting it...
        disconnect.Cancel();
        await streaming.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(streaming.IsCompletedSuccessfully);
        _logger.Verify(l => l.Log(
            LogLevel.Debug,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((state, _) => state.ToString()!.StartsWith("SSE client disconnected")),
            It.IsAny<Exception?>(),
            (Func<It.IsAnyType, Exception?, string>)It.IsAny<object>()), Times.Once);

        // ...and the client no longer receives anything.
        var textAfterDisconnect = body.Text;
        await _broadcaster.BroadcastFileListUpdateAsync();
        Assert.Equal(textAfterDisconnect, body.Text);
    }

    /// <summary>Response body that signals when something was first flushed to it.</summary>
    private sealed class FlushSignallingStream : MemoryStream
    {
        private readonly TaskCompletionSource _firstFlush = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task FirstFlush => _firstFlush.Task;

        public string Text => Encoding.UTF8.GetString(ToArray());

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            await base.FlushAsync(cancellationToken);
            _firstFlush.TrySetResult();
        }
    }
}
