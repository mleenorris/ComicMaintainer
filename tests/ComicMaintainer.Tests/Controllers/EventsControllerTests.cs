using ComicMaintainer.WebApi.Controllers;
using ComicMaintainer.WebApi.Services;
using ComicMaintainer.Core.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using System.IO;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace ComicMaintainer.Tests.Controllers;

public class EventsControllerTests
{
    private readonly Mock<ILogger<EventsController>> _loggerMock;
    private readonly EventBroadcasterService _eventBroadcaster;
    private readonly EventsController _controller;
    private readonly JwtSettings _jwtSettings;
    private readonly string _validToken;

    public EventsControllerTests()
    {
        _loggerMock = new Mock<ILogger<EventsController>>();
        
        // Setup JWT settings
        _jwtSettings = new JwtSettings
        {
            Secret = "TestSecretKeyForJwtTokenValidation123456789!",
            Issuer = "ComicMaintainer",
            Audience = "ComicMaintainerAPI",
            ExpirationMinutes = 1440
        };
        var jwtSettingsMock = new Mock<IOptions<JwtSettings>>();
        jwtSettingsMock.Setup(x => x.Value).Returns(_jwtSettings);
        
        // Create concrete EventBroadcasterService for testing
        var hubContextMock = new Mock<Microsoft.AspNetCore.SignalR.IHubContext<ComicMaintainer.WebApi.Hubs.ProgressHub>>();
        var broadcasterLoggerMock = new Mock<ILogger<EventBroadcasterService>>();
        _eventBroadcaster = new EventBroadcasterService(broadcasterLoggerMock.Object, hubContextMock.Object);
        
        _controller = new EventsController(_loggerMock.Object, _eventBroadcaster, jwtSettingsMock.Object);
        
        // Setup HTTP context
        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = new MemoryStream();
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };
        
        // Generate a valid token for testing
        _validToken = GenerateJwtToken();
    }

    private string GenerateJwtToken()
    {
        var tokenHandler = new JwtSecurityTokenHandler();
        var key = Encoding.UTF8.GetBytes(_jwtSettings.Secret);
        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.Name, "testuser"),
                new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())
            }),
            Expires = DateTime.UtcNow.AddMinutes(_jwtSettings.ExpirationMinutes),
            Issuer = _jwtSettings.Issuer,
            Audience = _jwtSettings.Audience,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(key),
                SecurityAlgorithms.HmacSha256Signature)
        };
        var token = tokenHandler.CreateToken(tokenDescriptor);
        return tokenHandler.WriteToken(token);
    }

    [Fact]
    public async Task Stream_WithValidToken_SetsCorrectHeaders()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        _controller.HttpContext.RequestAborted = cts.Token;
        
        // Cancel immediately to exit the stream loop
        cts.Cancel();

        // Act
        try
        {
            await _controller.Stream(_validToken);
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
    public async Task Stream_WithoutToken_Returns401()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        _controller.HttpContext.RequestAborted = cts.Token;

        // Act
        await _controller.Stream(null);

        // Assert
        Assert.Equal(401, _controller.Response.StatusCode);
    }

    [Fact]
    public async Task Stream_WithInvalidToken_Returns401()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        _controller.HttpContext.RequestAborted = cts.Token;
        var invalidToken = "invalid.jwt.token";

        // Act
        await _controller.Stream(invalidToken);

        // Assert
        Assert.Equal(401, _controller.Response.StatusCode);
    }

    [Fact]
    public async Task Stream_WithValidToken_HandlesClientDisconnect()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        _controller.HttpContext.RequestAborted = cts.Token;
        cts.Cancel(); // Simulate client disconnect

        // Act & Assert - should not throw
        try
        {
            await _controller.Stream(_validToken);
        }
        catch (OperationCanceledException)
        {
            // Expected behavior
        }
    }
}
