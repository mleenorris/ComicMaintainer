using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Timeouts;
using ComicMaintainer.Core.Services.Auth;
using ComicMaintainer.WebApi.Authentication;
using ComicMaintainer.WebApi.Services;

namespace ComicMaintainer.WebApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class EventsController : ControllerBase
{
    /// <summary>
    /// How often the stream sends a <see cref="HeartbeatMessage"/>. The browser treats a stream that stays silent
    /// for much longer as abandoned (a proxy or the network dropped it without closing it) and replaces it.
    /// </summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The heartbeat: a named event (not a comment, which EventSource hides from the page) so the browser can tell
    /// a live stream from an abandoned one.
    /// </summary>
    public const string HeartbeatMessage = "event: heartbeat\ndata: {}\n\n";

    private readonly ILogger<EventsController> _logger;
    private readonly EventBroadcasterService _eventBroadcaster;
    private readonly IAuthSessionValidator _sessions;

    public EventsController(
        ILogger<EventsController> logger,
        EventBroadcasterService eventBroadcaster,
        IAuthSessionValidator sessions)
    {
        _logger = logger;
        _eventBroadcaster = eventBroadcaster;
        _sessions = sessions;
    }

    /// <summary>
    /// The server-sent event stream. Besides the usual schemes it accepts a stream token in the
    /// <c>access_token</c> query parameter (<see cref="StreamTokenAuthentication"/>), because EventSource cannot
    /// send an Authorization header; no other endpoint accepts a token in its URL. A stream opened with a token
    /// ends at the first heartbeat after its session does (sign-out, password or role change, disabled account).
    /// </summary>
    [HttpGet("stream")]
    [Authorize(AuthenticationSchemes = StreamTokenAuthentication.SchemeName)]
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
                if (!await IsSessionStillValidAsync())
                {
                    _logger.LogInformation("Closing SSE client {ClientId}: its session has ended", clientId);
                    break;
                }

                // Sent through the broadcaster so it never overlaps an event on the same writer.
                if (!await _eventBroadcaster.SendToSseClientAsync(clientId, HeartbeatMessage))
                {
                    break;
                }
                await Task.Delay(HeartbeatInterval, HttpContext.RequestAborted);
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

    // Token-authenticated streams (session or stream token) carry the security-stamp fingerprint and id that
    // IAuthSessionValidator checks; Authelia streams are vouched for by the proxy when they open and carry neither.
    private Task<bool> IsSessionStillValidAsync()
    {
        var stamp = User.FindFirstValue(AuthTokens.SecurityStampClaim);
        if (stamp is null)
        {
            return Task.FromResult(true);
        }

        return _sessions.IsSessionValidAsync(
            User.FindFirstValue(ClaimTypes.NameIdentifier),
            stamp,
            User.FindFirstValue(JwtRegisteredClaimNames.Jti),
            HttpContext.RequestAborted);
    }
}
