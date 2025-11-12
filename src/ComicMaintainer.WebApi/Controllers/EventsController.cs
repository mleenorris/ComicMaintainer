using Microsoft.AspNetCore.Mvc;
using ComicMaintainer.WebApi.Services;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Text;
using Microsoft.Extensions.Options;
using ComicMaintainer.Core.Configuration;

namespace ComicMaintainer.WebApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class EventsController : ControllerBase
{
    private readonly ILogger<EventsController> _logger;
    private readonly EventBroadcasterService _eventBroadcaster;
    private readonly JwtSettings _jwtSettings;

    public EventsController(
        ILogger<EventsController> logger,
        EventBroadcasterService eventBroadcaster,
        IOptions<JwtSettings> jwtSettings)
    {
        _logger = logger;
        _eventBroadcaster = eventBroadcaster;
        _jwtSettings = jwtSettings.Value;
    }

    [HttpGet("stream")]
    public async Task Stream([FromQuery] string? token)
    {
        // Validate JWT token from query parameter
        // EventSource doesn't support custom headers, so we must use query parameters
        if (string.IsNullOrEmpty(token))
        {
            _logger.LogWarning("SSE connection attempt without token");
            Response.StatusCode = 401;
            await Response.WriteAsync("Unauthorized: Token required");
            return;
        }

        // Validate the token
        try
        {
            var jwtSecret = Environment.GetEnvironmentVariable("JWT_SECRET") ?? _jwtSettings.Secret;
            var tokenHandler = new JwtSecurityTokenHandler();
            var key = Encoding.UTF8.GetBytes(jwtSecret);

            tokenHandler.ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = _jwtSettings.Issuer,
                ValidAudience = _jwtSettings.Audience,
                IssuerSigningKey = new SymmetricSecurityKey(key),
                ClockSkew = TimeSpan.Zero
            }, out SecurityToken validatedToken);

            _logger.LogDebug("SSE: Token validated successfully");
        }
        catch (SecurityTokenException ex)
        {
            _logger.LogWarning(ex, "SSE: Invalid token provided");
            Response.StatusCode = 401;
            await Response.WriteAsync("Unauthorized: Invalid token");
            return;
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "SSE: Malformed token provided");
            Response.StatusCode = 401;
            await Response.WriteAsync("Unauthorized: Malformed token");
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SSE: Error validating token");
            Response.StatusCode = 401;
            await Response.WriteAsync("Unauthorized");
            return;
        }

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
