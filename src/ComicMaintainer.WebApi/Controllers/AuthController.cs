using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.WebApi.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.WebApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly IAuthService _authService;
    private readonly ILogger<AuthController> _logger;
    private readonly AutheliaSettings _autheliaSettings;
    private readonly IAuthorizationService _authorizationService;

    public AuthController(
        IAuthService authService, 
        ILogger<AuthController> logger,
        IOptions<AutheliaSettings> autheliaSettings,
        IAuthorizationService authorizationService)
    {
        _authService = authService;
        _logger = logger;
        _autheliaSettings = autheliaSettings.Value;
        _authorizationService = authorizationService;
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

    [HttpGet("setup-required")]
    public async Task<ActionResult> IsSetupRequired()
    {
        var setupRequired = await _authService.IsSetupRequiredAsync();
        return Ok(new { setupRequired });
    }

    [HttpPost("setup")]
    public async Task<ActionResult> SetupAdmin([FromBody] SetupRequest request)
    {
        // Validate input
        if (string.IsNullOrWhiteSpace(request.Username) || request.Username.Length < 3)
        {
            return BadRequest(new { error = "Username must be at least 3 characters" });
        }

        if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < 8)
        {
            return BadRequest(new { error = "Password must be at least 8 characters" });
        }

        var (success, error) = await _authService.SetupAdminAsync(
            request.Username, 
            request.Password, 
            request.Email);
        
        if (!success)
        {
            return BadRequest(new { error });
        }

        return Ok(new { message = "Admin user created successfully" });
    }

    [HttpGet("status")]
    public ActionResult GetAuthStatus()
    {
        // Return authentication configuration and user status
        var isAuthenticated = User.Identity?.IsAuthenticated ?? false;
        var username = User.Identity?.Name;
        var authMethod = User.FindFirst("auth_method")?.Value;

        return Ok(new 
        { 
            autheliaEnabled = _autheliaSettings.Enabled,
            isAuthenticated = isAuthenticated,
            username = username,
            authMethod = authMethod ?? "jwt",
            requiresLogin = !_autheliaSettings.Enabled || !isAuthenticated
        });
    }

    /// <summary>
    /// Get a JWT token for SSE connections when authenticated via Authelia
    /// This allows Authelia-authenticated users to establish SSE connections
    /// which cannot send custom headers
    /// </summary>
    [Authorize]
    [HttpGet("sse-token")]
    public async Task<ActionResult> GetSseToken()
    {
        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        var username = User.Identity?.Name;
        
        if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(username))
        {
            return Unauthorized();
        }

        // Generate a JWT token for this user
        var (success, token, error) = await _authService.GenerateTokenForUserAsync(userId);
        
        if (!success)
        {
            _logger.LogError("Failed to generate SSE token for user {Username}: {Error}", username, error);
            return StatusCode(500, new { error = "Failed to generate token" });
        }

        return Ok(new { token });
    }

    [Authorize]
    [HttpGet("user")]
    public async Task<ActionResult> GetCurrentUser()
    {
        // Return current user information
        var username = User.Identity?.Name;
        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        var email = User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value;
        var roles = User.FindAll(System.Security.Claims.ClaimTypes.Role)
            .Select(c => c.Value)
            .ToList();
        var authMethod = User.FindFirst("auth_method")?.Value ?? "jwt";

        // Evaluate the authorization policies rather than re-deriving the role
        // rules here, so the UI can never disagree with what the API enforces.
        var canModifyLibrary = await _authorizationService.AuthorizeAsync(User, AuthorizationPolicies.CanModifyLibrary);
        var canAdminister = await _authorizationService.AuthorizeAsync(User, AuthorizationPolicies.CanAdminister);

        return Ok(new 
        { 
            username = username,
            userId = userId,
            email = email,
            roles = roles,
            authMethod = authMethod,
            canModifyLibrary = canModifyLibrary.Succeeded,
            canAdminister = canAdminister.Succeeded
        });
    }
}

public record LoginRequest(string Username, string Password);
public record RegisterRequest(string Username, string Password, string Email, string? FullName);
public record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public record SetupRequest(string Username, string Password, string? Email);
