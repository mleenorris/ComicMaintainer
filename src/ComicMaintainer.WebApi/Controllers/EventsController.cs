using Microsoft.AspNetCore.Mvc;
using ComicMaintainer.WebApi.Services;

namespace ComicMaintainer.WebApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class EventsController : ControllerBase
{
    private readonly ILogger<EventsController> _logger;
    private readonly EventBroadcasterService _eventBroadcaster;

    public EventsController(
        ILogger<EventsController> logger,
        EventBroadcasterService eventBroadcaster)
    {
        _logger = logger;
        _eventBroadcaster = eventBroadcaster;
    }

    [HttpGet("stream")]
    public async Task Stream()
    {
        Response.Headers.Append("Content-Type", "text/event-stream");
        Response.Headers.Append("Cache-Control", "no-cache");
        Response.Headers.Append("Connection", "keep-alive");

        var clientId = Guid.NewGuid().ToString();
        var writer = new StreamWriter(Response.Body);
        
        try
        {
            // Register this client for event broadcasting
            _eventBroadcaster.RegisterSseClient(clientId, writer);
            _logger.LogInformation("SSE client connected: {ClientId}", clientId);
            
            // Keep the connection open
            while (!HttpContext.RequestAborted.IsCancellationRequested)
            {
                // Send a heartbeat comment every 30 seconds to keep the connection alive
                // SSE comments must be ": " (colon followed by space) per spec
                await writer.WriteAsync(": heartbeat\n\n");
                await writer.FlushAsync();
                await Task.Delay(30000, HttpContext.RequestAborted);
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected, this is normal
            _logger.LogDebug("SSE client disconnected: {ClientId}", clientId);
        }
        finally
        {
            // Unregister this client
            _eventBroadcaster.UnregisterSseClient(clientId);
        }
    }
}
