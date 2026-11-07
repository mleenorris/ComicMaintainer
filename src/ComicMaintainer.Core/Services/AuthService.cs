using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models.Auth;
using ComicMaintainer.Core.Services.Auth;
using ComicMaintainer.Core.Utilities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Authentication service implementation with JWT and API key support
/// </summary>
public class AuthService : IAuthService
{
    /// <summary>
    /// The one error of a failed sign-in, whether the account does not exist, is locked out or disabled, or the
    /// password is wrong, so that the answer does not tell which accounts exist.
    /// </summary>
    public const string InvalidCredentialsError = "Invalid username or password";

    /// <summary>The error of a sign-in with the right password when that password is a <see cref="PublishedPasswords"/> one.</summary>
    public const string PublishedPasswordError =
        "This password was published as an example with ComicMaintainer, so it can no longer be used to sign in. " +
        "If this is the administrator created from ADMIN_PASSWORD, set ADMIN_PASSWORD to a password of your own and " +
        "restart ComicMaintainer to replace it.";

    // A failed sign-in for an account that does not exist or is locked out still checks a password against a hash,
    // with the cost of the app's own hasher, so that the response time does not tell those cases apart.
    private static readonly PasswordHasher<ApplicationUser> TimingHasher = new();
    private static readonly ApplicationUser TimingUser = new();
    private static readonly Lazy<string> TimingHash =
        new(() => TimingHasher.HashPassword(TimingUser, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));

    private readonly UserManager<ApplicationUser> _userManager;
    private readonly JwtSettings _jwtSettings;
    private readonly ILogger<AuthService> _logger;

    public AuthService(UserManager<ApplicationUser> userManager, IOptions<JwtSettings> jwtSettings, ILogger<AuthService> logger)
    {
        _userManager = userManager;
        _jwtSettings = jwtSettings.Value;
        _logger = logger;
    }

    /// <summary>
    /// Signs in with a password. Every failed attempt is logged to the auth log with the client's address; after
    /// <c>MaxFailedAccessAttempts</c> wrong passwords in a row the account is locked out for
    /// <c>DefaultLockoutTimeSpan</c> (Identity's lockout options). The caller only ever sees
    /// <see cref="InvalidCredentialsError"/>, so it cannot tell unknown, locked-out and disabled accounts apart.
    /// </summary>
    public async Task<(bool Success, string Token, string? Error)> LoginAsync(string username, string password, string? clientAddress = null)
    {
        var safeUsername = LoggingHelper.SanitizeForLog(username);
        var client = LoggingHelper.SanitizeForLog(string.IsNullOrEmpty(clientAddress) ? "unknown" : clientAddress);

        var user = string.IsNullOrEmpty(username) ? null : await _userManager.FindByNameAsync(username);
        if (user is null)
        {
            SpendAPasswordCheck(password);
            return FailedSignIn(safeUsername, client, "there is no such account");
        }

        if (await _userManager.IsLockedOutAsync(user))
        {
            SpendAPasswordCheck(password);
            return FailedSignIn(safeUsername, client, "the account is locked out after too many failed sign-ins");
        }

        if (!await _userManager.CheckPasswordAsync(user, password))
        {
            await RecordFailedAttemptAsync(user, safeUsername, client);
            return FailedSignIn(safeUsername, client, "wrong password");
        }

        if (!user.IsActive)
        {
            return FailedSignIn(safeUsername, client, "the account is disabled");
        }

        if (PublishedPasswords.IsPublished(password))
        {
            _logger.LogError(
                "Refused the sign-in of {Username} from {ClientAddress}: the account's password was published with " +
                "ComicMaintainer. Set ADMIN_PASSWORD to a password of your own and restart to replace it.",
                safeUsername, client);
            return (false, string.Empty, PublishedPasswordError);
        }

        await _userManager.ResetAccessFailedCountAsync(user);
        user.LastLoginAt = DateTime.UtcNow;
        await _userManager.UpdateAsync(user);

        _logger.LogInformation("{Username} signed in from {ClientAddress}", safeUsername, client);
        var token = await GenerateJwtTokenAsync(user);
        return (true, token, null);
    }

    private (bool Success, string Token, string? Error) FailedSignIn(string safeUsername, string client, string reason)
    {
        _logger.LogWarning("Failed sign-in for {Username} from {ClientAddress}: {Reason}", safeUsername, client, reason);
        return (false, string.Empty, InvalidCredentialsError);
    }

    private async Task RecordFailedAttemptAsync(ApplicationUser user, string safeUsername, string client)
    {
        // Accounts created with lockout disabled (Identity's per-account LockoutEnabled flag) would never lock out.
        if (!await _userManager.GetLockoutEnabledAsync(user))
        {
            await _userManager.SetLockoutEnabledAsync(user, true);
        }

        await _userManager.AccessFailedAsync(user);
        if (await _userManager.IsLockedOutAsync(user))
        {
            _logger.LogWarning(
                "Locked out {Username} until {LockoutEnd:u} after {MaxAttempts} failed sign-ins, the last from {ClientAddress}",
                safeUsername, await _userManager.GetLockoutEndDateAsync(user), _userManager.Options.Lockout.MaxFailedAccessAttempts, client);
        }
    }

    private static void SpendAPasswordCheck(string? password) =>
        TimingHasher.VerifyHashedPassword(TimingUser, TimingHash.Value, password ?? string.Empty);

    public async Task<(bool Success, string? Error)> RegisterAsync(string username, string password, string email, string? fullName = null)
    {
        var user = new ApplicationUser
        {
            UserName = username,
            Email = email,
            FullName = fullName,
            IsActive = true
        };

        var result = await _userManager.CreateAsync(user, password);
        if (!result.Succeeded)
        {
            return (false, string.Join(", ", result.Errors.Select(e => e.Description)));
        }

        // Assign default role
        await _userManager.AddToRoleAsync(user, "User");

        return (true, null);
    }

    public async Task<(bool Success, string? Error)> ChangePasswordAsync(string userId, string currentPassword, string newPassword)
    {
        var user = await _userManager.FindByIdAsync(userId);
        if (user == null)
        {
            return (false, "User not found");
        }

        var result = await _userManager.ChangePasswordAsync(user, currentPassword, newPassword);
        if (!result.Succeeded)
        {
            return (false, string.Join(", ", result.Errors.Select(e => e.Description)));
        }

        return (true, null);
    }

    public async Task<ApplicationUser?> GetUserByApiKeyAsync(string apiKey)
    {
        if (string.IsNullOrEmpty(apiKey))
        {
            return null;
        }

        // Query directly on the backing IQueryable instead of pulling every user
        // in the "User" role into memory and filtering client-side. EF Core
        // translates this into a single indexed SELECT (IX_AspNetUsers_ApiKey).
        // We use the synchronous LINQ overload so that the call works against
        // both real EF Core providers and mocked IQueryable<T> instances in
        // unit tests; for SQLite this is a fast in-process round-trip.
        var user = _userManager.Users
            .FirstOrDefault(u => u.ApiKey == apiKey && u.IsActive);

        if (user is null)
        {
            return null;
        }

        // Preserve original semantics: API keys are only valid for users in the
        // "User" role.
        if (!await _userManager.IsInRoleAsync(user, "User"))
        {
            return null;
        }

        return user;
    }

    public async Task<(bool Success, string? ApiKey, string? Error)> GenerateApiKeyAsync(string userId)
    {
        var user = await _userManager.FindByIdAsync(userId);
        if (user == null)
        {
            return (false, null, "User not found");
        }

        // Generate a secure API key
        var apiKey = GenerateSecureApiKey();
        user.ApiKey = apiKey;

        var result = await _userManager.UpdateAsync(user);
        if (!result.Succeeded)
        {
            return (false, null, "Failed to update user");
        }

        return (true, apiKey, null);
    }

    public async Task<bool> ValidateApiKeyAsync(string apiKey)
    {
        var user = await GetUserByApiKeyAsync(apiKey);
        return user != null;
    }

    private async Task<string> GenerateJwtTokenAsync(ApplicationUser user)
    {
        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id),
            new Claim(ClaimTypes.Name, user.UserName ?? string.Empty),
            new Claim(ClaimTypes.Email, user.Email ?? string.Empty),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        // Add roles
        var roles = await _userManager.GetRolesAsync(user);
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtSettings.Secret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var expires = DateTime.UtcNow.AddMinutes(_jwtSettings.ExpirationMinutes);

        var token = new JwtSecurityToken(
            issuer: _jwtSettings.Issuer,
            audience: _jwtSettings.Audience,
            claims: claims,
            expires: expires,
            signingCredentials: creds
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public async Task<bool> IsSetupRequiredAsync()
    {
        // Check if any admin users exist
        var adminUsers = await _userManager.GetUsersInRoleAsync("Admin");
        return adminUsers.Count == 0;
    }

    public async Task<(bool Success, string? Error)> SetupAdminAsync(string username, string password, string? email = null)
    {
        // Check if setup is still required
        // Note: This check provides basic protection against concurrent setup attempts.
        // The database's unique constraints on username and email provide additional protection.
        if (!await IsSetupRequiredAsync())
        {
            return (false, "Admin user already exists");
        }

        // Create the admin user
        var adminUser = new ApplicationUser
        {
            UserName = username,
            Email = email ?? $"{username}@comicmaintainer.local",
            FullName = "Administrator",
            IsActive = true,
            EmailConfirmed = true
        };

        var result = await _userManager.CreateAsync(adminUser, password);
        if (!result.Succeeded)
        {
            return (false, string.Join(", ", result.Errors.Select(e => e.Description)));
        }

        // Assign admin role
        await _userManager.AddToRoleAsync(adminUser, "Admin");

        return (true, null);
    }

    public async Task<(bool Success, string Token, string? Error)> GenerateTokenForUserAsync(string userId)
    {
        var user = await _userManager.FindByIdAsync(userId);
        if (user == null)
        {
            return (false, string.Empty, "User not found");
        }

        if (!user.IsActive)
        {
            return (false, string.Empty, "Account is disabled");
        }

        var token = await GenerateJwtTokenAsync(user);
        return (true, token, null);
    }

    private static string GenerateSecureApiKey()
    {
        var randomBytes = new byte[32];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(randomBytes);
        return Convert.ToBase64String(randomBytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');
    }
}
