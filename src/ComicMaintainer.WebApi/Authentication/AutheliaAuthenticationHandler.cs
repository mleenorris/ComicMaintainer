using System.Security.Claims;
using System.Text.Encodings.Web;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Models.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.WebApi.Authentication;

/// <summary>
/// Authentication handler for Authelia forward authentication
/// </summary>
public class AutheliaAuthenticationHandler : AuthenticationHandler<AutheliaAuthenticationOptions>
{
    private readonly AutheliaSettings _autheliaSettings;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly RoleManager<ApplicationRole> _roleManager;
    private readonly IDbContextFactory<ComicMaintainerDbContext> _dbContextFactory;
    private readonly ILogger<AutheliaAuthenticationHandler> _logger;

    public AutheliaAuthenticationHandler(
        IOptionsMonitor<AutheliaAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IOptions<AutheliaSettings> autheliaSettings,
        UserManager<ApplicationUser> userManager,
        RoleManager<ApplicationRole> roleManager,
        IDbContextFactory<ComicMaintainerDbContext> dbContextFactory)
        : base(options, logger, encoder)
    {
        _autheliaSettings = autheliaSettings.Value;
        _userManager = userManager;
        _roleManager = roleManager;
        _dbContextFactory = dbContextFactory;
        _logger = logger.CreateLogger<AutheliaAuthenticationHandler>();
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Only handle authentication if Authelia is enabled
        if (!_autheliaSettings.Enabled)
        {
            return AuthenticateResult.NoResult();
        }

        // Get the username from Authelia headers
        if (!Request.Headers.TryGetValue(_autheliaSettings.UserHeader, out var usernameValues))
        {
            _logger.LogDebug("No {HeaderName} header found, skipping Authelia authentication", _autheliaSettings.UserHeader);
            return AuthenticateResult.NoResult();
        }

        var username = usernameValues.ToString();
        if (string.IsNullOrWhiteSpace(username))
        {
            _logger.LogWarning("Empty {HeaderName} header received", _autheliaSettings.UserHeader);
            return AuthenticateResult.Fail($"Empty {_autheliaSettings.UserHeader} header");
        }

        _logger.LogInformation("Authenticating user via Authelia: {Username}", username);

        // Get optional headers
        Request.Headers.TryGetValue(_autheliaSettings.EmailHeader, out var emailValues);
        Request.Headers.TryGetValue(_autheliaSettings.NameHeader, out var nameValues);
        Request.Headers.TryGetValue(_autheliaSettings.GroupsHeader, out var groupsValues);

        var email = emailValues.ToString();
        var displayName = nameValues.ToString();
        var groups = groupsValues.ToString();

        // Ensure user exists in the database
        var user = await EnsureUserExistsAsync(username, email, displayName, groups);
        if (user == null)
        {
            _logger.LogError("Failed to create or retrieve user: {Username}", username);
            return AuthenticateResult.Fail("Failed to create or retrieve user");
        }

        // Create claims
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id),
            new(ClaimTypes.Name, user.UserName ?? username),
            new(ClaimTypes.Email, user.Email ?? email ?? $"{username}@authelia.local"),
            new("auth_method", "authelia")
        };

        // Add role claims
        var roles = await _userManager.GetRolesAsync(user);
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));

        // Add groups as claims if provided
        if (!string.IsNullOrWhiteSpace(groups))
        {
            foreach (var group in groups.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                claims.Add(new Claim("authelia_group", group));
            }
        }

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        _logger.LogInformation("Successfully authenticated user via Authelia: {Username} with roles: {Roles}", 
            username, string.Join(", ", roles));

        return AuthenticateResult.Success(ticket);
    }

    private async Task<ApplicationUser?> EnsureUserExistsAsync(string username, string? email, string? displayName, string? groups)
    {
        try
        {
            // Try to find existing user
            var user = await _userManager.FindByNameAsync(username);
            
            if (user != null)
            {
                // Update user information if changed
                var needsUpdate = false;
                
                if (!string.IsNullOrWhiteSpace(email) && user.Email != email)
                {
                    user.Email = email;
                    needsUpdate = true;
                }
                
                if (!string.IsNullOrWhiteSpace(displayName) && user.FullName != displayName)
                {
                    user.FullName = displayName;
                    needsUpdate = true;
                }
                
                if (needsUpdate)
                {
                    await _userManager.UpdateAsync(user);
                    _logger.LogInformation("Updated user information for Authelia user: {Username}", username);
                }
                
                // Ensure user has appropriate role based on groups
                await EnsureUserRoleAsync(user, groups);
                
                return user;
            }

            // Create new user
            user = new ApplicationUser
            {
                UserName = username,
                Email = email ?? $"{username}@authelia.local",
                FullName = displayName ?? username,
                EmailConfirmed = true, // Authelia handles email verification
                IsActive = true
            };

            // Generate a random password (won't be used since Authelia handles auth)
            var randomPassword = GenerateRandomPassword();
            var result = await _userManager.CreateAsync(user, randomPassword);

            if (!result.Succeeded)
            {
                _logger.LogError("Failed to create Authelia user {Username}: {Errors}", 
                    username, string.Join(", ", result.Errors.Select(e => e.Description)));
                return null;
            }

            _logger.LogInformation("Created new user from Authelia: {Username}", username);

            // Assign role based on groups or default role
            await EnsureUserRoleAsync(user, groups);

            return user;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error ensuring Authelia user exists: {Username}", username);
            return null;
        }
    }

    private async Task EnsureUserRoleAsync(ApplicationUser user, string? groups)
    {
        // Determine appropriate role based on groups
        var targetRole = _autheliaSettings.DefaultRole;
        
        if (!string.IsNullOrWhiteSpace(groups) && !string.IsNullOrWhiteSpace(_autheliaSettings.AdminGroups))
        {
            var adminGroups = _autheliaSettings.AdminGroups
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(g => g.ToLowerInvariant())
                .ToHashSet();
            
            var userGroups = groups
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(g => g.ToLowerInvariant());
            
            // If user is in any admin group, assign Admin role
            if (userGroups.Any(g => adminGroups.Contains(g)))
            {
                targetRole = "Admin";
            }
        }

        // Ensure the target role exists
        if (!await _roleManager.RoleExistsAsync(targetRole))
        {
            _logger.LogWarning("Target role {Role} does not exist, using 'User' instead", targetRole);
            targetRole = "User";
        }

        // Check if user already has the correct role
        var currentRoles = await _userManager.GetRolesAsync(user);
        if (currentRoles.Contains(targetRole))
        {
            return;
        }

        // Remove all current roles and add the target role
        if (currentRoles.Any())
        {
            await _userManager.RemoveFromRolesAsync(user, currentRoles);
        }

        await _userManager.AddToRoleAsync(user, targetRole);
        _logger.LogInformation("Assigned role {Role} to Authelia user: {Username}", targetRole, user.UserName);
    }

    private static string GenerateRandomPassword()
    {
        // Generate a secure random password (won't be used but required for user creation)
        // Using RandomNumberGenerator for cryptographic randomness
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789!@#$%^&*";
        var password = new char[32];
        var randomBytes = new byte[32];
        
        using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
        {
            rng.GetBytes(randomBytes);
        }
        
        for (int i = 0; i < 32; i++)
        {
            password[i] = chars[randomBytes[i] % chars.Length];
        }
        
        return new string(password);
    }
}

/// <summary>
/// Authentication options for Authelia
/// </summary>
public class AutheliaAuthenticationOptions : AuthenticationSchemeOptions
{
}
