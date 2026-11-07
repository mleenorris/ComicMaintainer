using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Core.Services.Auth;
using ComicMaintainer.Core.Utilities;
using ComicMaintainer.WebApi.Authentication;
using ComicMaintainer.WebApi.Authorization;
using ComicMaintainer.WebApi.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
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
    private readonly IOptionsMonitor<AppSettings> _appSettings;
    private readonly ClientAddressResolver _clientAddresses;

    public AuthController(
        IAuthService authService, 
        ILogger<AuthController> logger,
        IOptions<AutheliaSettings> autheliaSettings,
        IAuthorizationService authorizationService,
        IOptionsMonitor<AppSettings> appSettings,
        ClientAddressResolver clientAddresses)
    {
        _clientAddresses = clientAddresses;
        _authService = authService;
        _logger = logger;
        _autheliaSettings = autheliaSettings.Value;
        _authorizationService = authorizationService;
        _appSettings = appSettings;
    }

    /// <summary>
    /// Signs in with a username and password. Failures all answer 401 with the same error, are logged to the auth
    /// log, and lock the account out after repeated wrong passwords; each client address gets a few attempts per
    /// window (<see cref="AuthenticationRateLimit"/>).
    /// </summary>
    [HttpPost("login")]
    [EnableRateLimiting(AuthenticationRateLimit.PolicyName)]
    public async Task<ActionResult> Login([FromBody] LoginRequest request)
    {
        var (success, token, error) = await _authService.LoginAsync(request.Username, request.Password, _clientAddresses.Describe(HttpContext));
        
        if (!success)
        {
            return Unauthorized(new { error });
        }

        return Ok(new { token });
    }

    /// <summary>
    /// Creates a new account.
    /// </summary>
    /// <remarks>
    /// Self-service registration is disabled unless <c>AllowRegistration</c> is
    /// enabled in settings, so a self-hosted instance is not open to anyone who
    /// can reach the port. Administrators can always create accounts here.
    /// </remarks>
    [AllowAnonymous]
    [HttpPost("register")]
    [EnableRateLimiting(AuthenticationRateLimit.PolicyName)]
    public async Task<ActionResult> Register([FromBody] RegisterRequest request)
    {
        var isAdmin = (await _authorizationService.AuthorizeAsync(User, AuthorizationPolicies.CanAdminister)).Succeeded;

        if (!isAdmin && !_appSettings.CurrentValue.AllowRegistration)
        {
            _logger.LogWarning("Rejected registration attempt: self-service registration is disabled.");

            // 401 when unauthenticated so clients know to sign in first; 403
            // when a signed-in non-admin tries to create another account.
            return User.Identity?.IsAuthenticated == true
                ? StatusCode(StatusCodes.Status403Forbidden, new { error = "Registration is disabled. Ask an administrator to create your account." })
                : Unauthorized(new { error = "Registration is disabled. Ask an administrator to create your account." });
        }

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

    /// <summary>
    /// Changes the password. This signs every session of the account out; the response carries a new session
    /// token (<c>token</c>) for the caller.
    /// </summary>
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

        // The change signed every session out, this one included; hand the caller a new session token.
        var (_, token, _) = await _authService.GenerateTokenForUserAsync(userId);
        return Ok(new { message = "Password changed successfully", token = string.IsNullOrEmpty(token) ? null : token });
    }

    /// <summary>
    /// Creates an API key for the caller's account, replacing (and so revoking) its previous one. Send it in the
    /// <c>X-API-Key</c> header; it acts as this account with its current roles. It is shown only in this response.
    /// A request authenticated with an API key cannot create one, so a leaked key cannot replace itself.
    /// </summary>
    [Authorize]
    [HttpPost("api-key")]
    public async Task<ActionResult> GenerateApiKey()
    {
        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        if (User.FindFirst("auth_method")?.Value == ApiKeys.AuthMethod)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Sign in to create an API key; an API key cannot create one." });
        }

        var (success, apiKey, error) = await _authService.GenerateApiKeyAsync(userId);

        if (!success)
        {
            return BadRequest(new { error });
        }

        return Ok(new { apiKey, header = ApiKeys.HeaderName });
    }

    /// <summary>Whether the caller's account has an API key, and when it was created and last used.</summary>
    [Authorize]
    [HttpGet("api-key")]
    public async Task<ActionResult> GetApiKeyStatus()
    {
        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        var status = await _authService.GetApiKeyStatusAsync(userId);
        return Ok(new
        {
            hasApiKey = status.HasApiKey,
            createdAt = status.CreatedAt,
            lastUsedAt = status.LastUsedAt,
            header = ApiKeys.HeaderName
        });
    }

    /// <summary>Revokes the caller's API key: requests that send it are refused from now on.</summary>
    [Authorize]
    [HttpDelete("api-key")]
    public async Task<ActionResult> RevokeApiKey()
    {
        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        return await _authService.RevokeApiKeyAsync(userId)
            ? NoContent()
            : NotFound(new { error = "This account has no API key." });
    }

    [HttpGet("setup-required")]
    public async Task<ActionResult> IsSetupRequired()
    {
        var setupRequired = await _authService.IsSetupRequiredAsync();
        return Ok(new { setupRequired });
    }

    /// <summary>
    /// First-run setup: creates the first administrator of a new instance. It is only available while no account
    /// exists at all and Authelia is not enabled, and it requires the one-time setup code that the server writes to
    /// its log at startup (or <c>SETUP_TOKEN</c>), so that whoever reaches a fresh, exposed instance first cannot claim
    /// it. Answers 403 when setup is not available or the code is wrong.
    /// </summary>
    [HttpPost("setup")]
    [EnableRateLimiting(AuthenticationRateLimit.PolicyName)]
    public async Task<ActionResult> SetupAdmin([FromBody] SetupRequest request)
    {
        if (!await _authService.IsSetupRequiredAsync())
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = AuthService.SetupNotAvailableError });
        }

        // Validate input
        if (string.IsNullOrWhiteSpace(request.Username) || request.Username.Length < 3)
        {
            return BadRequest(new { error = "Username must be at least 3 characters" });
        }

        if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < 8)
        {
            return BadRequest(new { error = "Password must be at least 8 characters" });
        }

        var (outcome, error) = await _authService.SetupAdminAsync(
            request.Username, 
            request.Password, 
            request.Email,
            request.SetupToken,
            _clientAddresses.Describe(HttpContext));

        return outcome switch
        {
            SetupOutcome.Created => Ok(new { message = "Admin user created successfully" }),
            SetupOutcome.Rejected => BadRequest(new { error }),
            _ => StatusCode(StatusCodes.Status403Forbidden, new { error }),
        };
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
    /// A stream token for opening the event stream (<c>GET /api/events/stream?access_token=…</c>), which
    /// EventSource cannot send an Authorization header to. It is valid for a minute, once, and for nothing else
    /// (see <see cref="StreamTokenAuthentication"/>), so fetch a new one for every (re)connection.
    /// </summary>
    [Authorize]
    [HttpPost("stream-token")]
    public async Task<ActionResult> GetStreamToken()
    {
        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        var (success, token, error) = await _authService.GenerateStreamTokenAsync(userId);
        if (!success)
        {
            _logger.LogWarning("Could not issue a stream token for {UserId}: {Error}", LoggingHelper.SanitizeForLog(userId), error);
            return Unauthorized();
        }

        return Ok(new { token, expiresIn = (int)AuthTokens.StreamTokenLifetime.TotalSeconds });
    }

    /// <summary>
    /// Signs out: the session token this request was made with is refused from now on. With
    /// <c>allSessions</c>, every session token of the account is (as after a password change).
    /// </summary>
    [Authorize]
    [HttpPost("logout")]
    public async Task<ActionResult> Logout([FromBody] LogoutRequest? request = null)
    {
        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        var tokenId = User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Jti)?.Value;
        var expiresAt = long.TryParse(User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Exp)?.Value, out var exp)
            ? DateTimeOffset.FromUnixTimeSeconds(exp).UtcDateTime
            : DateTime.UtcNow.AddDays(1);
        await _authService.LogoutAsync(userId, tokenId, expiresAt, request?.AllSessions == true);
        return NoContent();
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
/// <param name="SetupToken">The one-time setup code from the server log, or <c>SETUP_TOKEN</c>.</param>
/// <param name="AllSessions">Sign out every session of the account, not just this one.</param>
public record LogoutRequest(bool AllSessions = false);
public record SetupRequest(string Username, string Password, string? Email, string? SetupToken = null);
