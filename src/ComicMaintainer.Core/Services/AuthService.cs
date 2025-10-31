using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Authentication service implementation with JWT and API key support
/// </summary>
public class AuthService : IAuthService
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly JwtSettings _jwtSettings;

    public AuthService(UserManager<ApplicationUser> userManager, IOptions<JwtSettings> jwtSettings)
    {
        _userManager = userManager;
        _jwtSettings = jwtSettings.Value;
    }

    public async Task<(bool Success, string Token, string? Error)> LoginAsync(string username, string password)
    {
        var user = await _userManager.FindByNameAsync(username);
        if (user == null)
        {
            return (false, string.Empty, "Invalid username or password");
        }

        if (!user.IsActive)
        {
            return (false, string.Empty, "Account is disabled");
        }

        var result = await _userManager.CheckPasswordAsync(user, password);
        if (!result)
        {
            return (false, string.Empty, "Invalid username or password");
        }

        // Update last login
        user.LastLoginAt = DateTime.UtcNow;
        await _userManager.UpdateAsync(user);

        var token = await GenerateJwtTokenAsync(user);
        return (true, token, null);
    }

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
        return (await _userManager.GetUsersInRoleAsync("User"))
            .FirstOrDefault(u => u.ApiKey == apiKey && u.IsActive);
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

    private static string GenerateSecureApiKey()
    {
        var randomBytes = new byte[32];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(randomBytes);
        return Convert.ToBase64String(randomBytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');
    }
}
