using ComicMaintainer.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ComicMaintainer.WebApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly IAuthService _authService;
    private readonly ILogger<AuthController> _logger;

    public AuthController(IAuthService authService, ILogger<AuthController> logger)
    {
        _authService = authService;
        _logger = logger;
    }

    [HttpPost("login")]
    public async Task<ActionResult> Login([FromBody] LoginRequest request)
    {
        var (success, token, error) = await _authService.LoginAsync(request.Username, request.Password);
        
        if (!success)
        {
            return Unauthorized(new { error });
        }

        return Ok(new { token });
    }

    [HttpPost("register")]
    public async Task<ActionResult> Register([FromBody] RegisterRequest request)
    {
        var (success, error) = await _authService.RegisterAsync(
            request.Username, 
            request.Password, 
            request.Email, 
            request.FullName);
        
        if (!success)
        {
            return BadRequest(new { error });
        }

        return Ok(new { message = "User registered successfully" });
    }

    [Authorize]
    [HttpPost("change-password")]
    public async Task<ActionResult> ChangePassword([FromBody] ChangePasswordRequest request)
    {
        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        var (success, error) = await _authService.ChangePasswordAsync(userId, request.CurrentPassword, request.NewPassword);
        
        if (!success)
        {
            return BadRequest(new { error });
        }

        return Ok(new { message = "Password changed successfully" });
    }

    [Authorize]
    [HttpPost("api-key")]
    public async Task<ActionResult> GenerateApiKey()
    {
        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        var (success, apiKey, error) = await _authService.GenerateApiKeyAsync(userId);
        
        if (!success)
        {
            return BadRequest(new { error });
        }

        return Ok(new { apiKey });
    }
}

public record LoginRequest(string Username, string Password);
public record RegisterRequest(string Username, string Password, string Email, string? FullName);
public record ChangePasswordRequest(string CurrentPassword, string NewPassword);
