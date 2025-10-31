using Microsoft.AspNetCore.Mvc;

namespace ComicMaintainer.WebApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class EventsController : ControllerBase
{
    private readonly ILogger<EventsController> _logger;

    public EventsController(ILogger<EventsController> logger)
    {
        _logger = logger;
    }

    [HttpGet("stream")]
    public async Task Stream()
    {
        Response.Headers.Append("Content-Type", "text/event-stream");
        Response.Headers.Append("Cache-Control", "no-cache");
        Response.Headers.Append("Connection", "keep-alive");

        // Keep the connection open
        // In the future, this should integrate with SignalR or provide real server-sent events
        try
        {
            while (!HttpContext.RequestAborted.IsCancellationRequested)
            {
                // Send a heartbeat every 30 seconds to keep the connection alive
                await Response.WriteAsync(":\n\n");
                await Response.Body.FlushAsync();
                await Task.Delay(30000, HttpContext.RequestAborted);
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected, this is normal
            _logger.LogDebug("SSE client disconnected");
        }
    }
}
