using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Timeouts;
using ComicMaintainer.WebApi.Services;

namespace ComicMaintainer.WebApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
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
    // The stream is open for as long as the page is; it is never a timeout.
    [DisableRequestTimeout]
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
            // A failed or timed-out send aborts the connection so the browser reconnects.
            _eventBroadcaster.RegisterSseClient(clientId, writer, HttpContext.Abort);
            _logger.LogInformation("SSE client connected: {ClientId}", clientId);
            
            // Keep the connection open
            while (!HttpContext.RequestAborted.IsCancellationRequested)
            {
                // Send a heartbeat comment every 30 seconds to keep the connection alive
                // SSE comments must be ": " (colon followed by space) per spec
                // Sent through the broadcaster so it never overlaps an event on the same writer.
                if (!await _eventBroadcaster.SendToSseClientAsync(clientId, ": heartbeat\n\n"))
                {
                    break;
                }
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
