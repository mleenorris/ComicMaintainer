using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Security.Cryptography;
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
    // Upper case, lower case, digits and non-alphanumerics: every class Identity's
    // password validator can require.
    private static readonly string[] PasswordCharacterClasses =
    [
        "ABCDEFGHIJKLMNOPQRSTUVWXYZ",
        "abcdefghijklmnopqrstuvwxyz",
        "0123456789",
        "!@#$%^&*",
    ];

    private static readonly string PasswordAlphabet = string.Concat(PasswordCharacterClasses);

    // Domain of the placeholder email given to a user the proxy sends no email for.
    private const string FallbackEmailDomain = "authelia.local";

    private readonly AutheliaSettings _autheliaSettings;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly RoleManager<ApplicationRole> _roleManager;
    private readonly IDbContextFactory<ComicMaintainerDbContext> _dbContextFactory;
    private readonly ILogger<AutheliaAuthenticationHandler> _logger;

    // Untrusted peers already reported at Warning level (later requests log at Debug); bounded so that
    // a scan from many addresses cannot grow it without limit.
    private const int MaxReportedUntrustedPeers = 1000;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> ReportedUntrustedPeers = new();

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

        // Only the reverse proxy may assert an identity: anyone reaching the app directly could forge these headers.
        if (!IsFromTrustedProxy())
        {
            return AuthenticateResult.NoResult();
        }

        var username = usernameValues.ToString();
        if (string.IsNullOrWhiteSpace(username))
        {
            _logger.LogWarning("Empty {HeaderName} header received", _autheliaSettings.UserHeader);
            return AuthenticateResult.Fail($"Empty {_autheliaSettings.UserHeader} header");
        }

        _logger.LogInformation("Authenticating user via Authelia: {Username}", username);

        // Get optional headers. A missing header reads as "" rather than null, so blank
        // values are treated as absent to let the email/name fallbacks below apply.
        Request.Headers.TryGetValue(_autheliaSettings.EmailHeader, out var emailValues);
        Request.Headers.TryGetValue(_autheliaSettings.NameHeader, out var nameValues);
        Request.Headers.TryGetValue(_autheliaSettings.GroupsHeader, out var groupsValues);

        var email = NullIfBlank(emailValues.ToString());
        var displayName = NullIfBlank(nameValues.ToString());
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
            new(ClaimTypes.Name, username),  // Always use the username from Authelia header
            new(ClaimTypes.Email, user.Email ?? email ?? FallbackEmail(username)),
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

    /// <summary>
    /// Whether the direct peer of the connection is in <c>AUTHELIA_TRUSTED_PROXIES</c>. X-Forwarded-For is not
    /// consulted: it is just another header the client controls.
    /// </summary>
    private bool IsFromTrustedProxy()
    {
        var remoteIp = Context.Connection.RemoteIpAddress;
        if (TrustedProxyAllowlist.TryParse(_autheliaSettings.TrustedProxies, out var trustedProxies, out _)
            && trustedProxies.Contains(remoteIp))
        {
            return true;
        }

        var peer = ComicMaintainer.Core.Utilities.LoggingHelper.SanitizeForLog(
            remoteIp is null ? "unknown" : TrustedProxyAllowlist.Normalize(remoteIp).ToString());
        if (ReportedUntrustedPeers.Count < MaxReportedUntrustedPeers && ReportedUntrustedPeers.TryAdd(peer, 0))
        {
            _logger.LogWarning(
                "Ignoring Authelia headers from {RemoteIp}: the address is not in AUTHELIA_TRUSTED_PROXIES. " +
                "If this is your reverse proxy, add its IP address or subnet to AUTHELIA_TRUSTED_PROXIES.", peer);
        }
        else
        {
            _logger.LogDebug("Ignoring Authelia headers from {RemoteIp} (not in AUTHELIA_TRUSTED_PROXIES)", peer);
        }

        return false;
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
                Email = email ?? FallbackEmail(username),
                FullName = displayName ?? username,
                EmailConfirmed = true, // Authelia handles email verification
                IsActive = true
            };

            // Generate a random password (won't be used since Authelia handles auth)
            var randomPassword = GenerateRandomPassword(_userManager.Options.Password);
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

    private static string? NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>
    /// The email of a user the proxy sends no email for. Identity only creates an account whose email it
    /// accepts as an address and no other account has (<c>RequireUniqueEmail</c>), so this is the username
    /// itself when it is an address, such as <c>alice@corp.example</c>, and otherwise
    /// <c>&lt;username&gt;@authelia.local</c> with every '@' of the username replaced by '='. A username
    /// cannot contain '=' (Identity's default <c>AllowedUserNameCharacters</c>) and is never used as is
    /// at <c>authelia.local</c>, so no two usernames get the same email.
    /// </summary>
    private static string FallbackEmail(string username) =>
        new EmailAddressAttribute().IsValid(username)
            && !username.EndsWith("@" + FallbackEmailDomain, StringComparison.OrdinalIgnoreCase)
            ? username
            : $"{username.Replace('@', '=')}@{FallbackEmailDomain}";

    /// <summary>
    /// A random password that satisfies <paramref name="policy"/>. It is never used to sign in,
    /// but Identity validates it when the account is created, so it holds at least one character
    /// of every class Identity can require, enough distinct characters and at least 32 characters
    /// (more if the policy requires it). Characters are drawn and shuffled with
    /// <see cref="RandomNumberGenerator"/>, without modulo bias.
    /// </summary>
    private static string GenerateRandomPassword(PasswordOptions policy)
    {
        var uniqueChars = Math.Clamp(policy.RequiredUniqueChars, 0, PasswordAlphabet.Length);
        var length = Math.Max(32, Math.Max(policy.RequiredLength, uniqueChars + PasswordCharacterClasses.Length));
        var password = new char[length];

        var distinct = PasswordAlphabet.ToCharArray();
        RandomNumberGenerator.Shuffle(distinct.AsSpan());
        distinct.AsSpan(0, uniqueChars).CopyTo(password);

        var next = uniqueChars;
        foreach (var characterClass in PasswordCharacterClasses)
        {
            password[next++] = characterClass[RandomNumberGenerator.GetInt32(characterClass.Length)];
        }

        RandomNumberGenerator.GetItems(PasswordAlphabet.AsSpan(), password.AsSpan(next));
        RandomNumberGenerator.Shuffle(password.AsSpan());
        return new string(password);
    }
}

/// <summary>
/// Authentication options for Authelia
/// </summary>
public class AutheliaAuthenticationOptions : AuthenticationSchemeOptions
{
}
