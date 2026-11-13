using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Models.Auth;
using ComicMaintainer.Core.Services.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.WebApi.Authentication;

/// <summary>
/// Authentication handler for Authelia forward authentication
/// </summary>
public class AutheliaAuthenticationHandler : AuthenticationHandler<AutheliaAuthenticationOptions>
{
    /// <summary>The <c>auth_method</c> claim of a request authenticated by Authelia.</summary>
    public const string AuthMethod = "authelia";

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
    private static readonly EmailAddressAttribute EmailValidator = new();

    private readonly AutheliaSettings _autheliaSettings;
    private readonly IDbContextFactory<ComicMaintainerDbContext> _dbContextFactory;
    private readonly IAuthSessionValidator _sessions;
    private readonly IMemoryCache _cache;
    private readonly ILogger<AutheliaAuthenticationHandler> _logger;

    private const string AccountCacheKeyPrefix = "authelia:account:";

    // The last AUTHELIA_TRUSTED_PROXIES value parsed, and its allowlist (null when it is not valid).
    private static volatile ParsedTrustedProxies? s_trustedProxies;

    // Untrusted peers already reported at Warning level (later requests log at Debug); bounded so that
    // a scan from many addresses cannot grow it without limit.
    private const int MaxReportedUntrustedPeers = 1000;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> ReportedUntrustedPeers = new();

    // A handler is made for every request while Authelia is enabled. The account services (a DbContext and its
    // stores) are resolved only when an account has to be read: a request whose account is remembered needs neither.
    public AutheliaAuthenticationHandler(
        IOptionsMonitor<AutheliaAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IOptions<AutheliaSettings> autheliaSettings,
        IDbContextFactory<ComicMaintainerDbContext> dbContextFactory,
        IAuthSessionValidator sessions,
        IMemoryCache cache)
        : base(options, logger, encoder)
    {
        _sessions = sessions;
        _cache = cache;
        _autheliaSettings = autheliaSettings.Value;
        _dbContextFactory = dbContextFactory;
        _logger = logger.CreateLogger<AutheliaAuthenticationHandler>();
    }

    private UserManager<ApplicationUser> UserManager => Context.RequestServices.GetRequiredService<UserManager<ApplicationUser>>();

    private RoleManager<ApplicationRole> RoleManager => Context.RequestServices.GetRequiredService<RoleManager<ApplicationRole>>();

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

        // Get optional headers. A missing header reads as "" rather than null, so blank
        // values are treated as absent to let the email/name fallbacks below apply.
        Request.Headers.TryGetValue(_autheliaSettings.EmailHeader, out var emailValues);
        Request.Headers.TryGetValue(_autheliaSettings.NameHeader, out var nameValues);
        Request.Headers.TryGetValue(_autheliaSettings.GroupsHeader, out var groupsValues);

        var email = NullIfBlank(emailValues.ToString());
        var displayName = NullIfBlank(nameValues.ToString());
        var groups = groupsValues.ToString();

        // Every request carries the headers, a page of covers included, so the account found or provisioned for them
        // is remembered briefly instead of being looked up again each time. Different headers (a new email, name or
        // group) are a different key, and any change to the account made in the app moves its version on.
        var cacheKey = AccountCacheKeyPrefix + string.Join('\0', username, email, displayName, groups);
        if (_cache.TryGetValue(cacheKey, out CachedAccount? cached)
            && cached is not null
            && cached.AccountVersion == _sessions.GetAccountVersion(cached.UserId))
        {
            _logger.LogDebug("Authenticated user via Authelia (remembered): {Username}", username);
            return Authenticated(username, cached.UserId, cached.Email ?? email ?? FallbackEmail(username), cached.Roles, groups);
        }

        _logger.LogDebug("Authenticating user via Authelia: {Username}", username);

        // Ensure user exists in the database
        var account = await EnsureUserExistsAsync(username, email, displayName, groups);
        if (account is not { } found)
        {
            _logger.LogError("Failed to create or retrieve user: {Username}", username);
            return AuthenticateResult.Fail("Failed to create or retrieve user");
        }

        var (user, roles) = found;

        // An administrator can disable an account on the Users page; Authelia still vouches for the person, but they
        // may not use ComicMaintainer, as with a password sign-in or an API key.
        if (!user.IsActive)
        {
            _logger.LogWarning(
                "Refused the Authelia sign-in of {Username}: the account is disabled in ComicMaintainer",
                ComicMaintainer.Core.Utilities.LoggingHelper.SanitizeForLog(username));
            return AuthenticateResult.Fail("The account is disabled");
        }

        _cache.Set(
            cacheKey,
            new CachedAccount(user.Id, user.Email, roles.ToArray(), _sessions.GetAccountVersion(user.Id)),
            AuthSessionValidator.CacheDuration);

        _logger.LogDebug("Authenticated user via Authelia: {Username} with roles: {Roles}",
            username, string.Join(", ", roles));

        return Authenticated(username, user.Id, user.Email ?? email ?? FallbackEmail(username), roles, groups);
    }

    private AuthenticateResult Authenticated(string username, string userId, string email, IEnumerable<string> roles, string groups)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId),
            new(ClaimTypes.Name, username),  // Always use the username from Authelia header
            new(ClaimTypes.Email, email),
            new("auth_method", AuthMethod)
        };

        // Add role claims
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
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }

    /// <summary>
    /// Whether the direct peer of the connection is in <c>AUTHELIA_TRUSTED_PROXIES</c>. X-Forwarded-For is not
    /// consulted: it is just another header the client controls.
    /// </summary>
    private bool IsFromTrustedProxy()
    {
        var remoteIp = Context.Connection.RemoteIpAddress;
        if (ParseTrustedProxies(_autheliaSettings.TrustedProxies) is { } trustedProxies
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

    /// <summary>
    /// The allowlist in <paramref name="value"/>, or <see langword="null"/> when it is not valid. The setting does not
    /// change while the app runs, so the last parse is reused rather than repeated on every request.
    /// </summary>
    private static TrustedProxyAllowlist? ParseTrustedProxies(string? value)
    {
        var parsed = s_trustedProxies;
        if (parsed is null || !string.Equals(parsed.Value, value, StringComparison.Ordinal))
        {
            parsed = new ParsedTrustedProxies(value, TrustedProxyAllowlist.TryParse(value, out var allowlist, out _) ? allowlist : null);
            s_trustedProxies = parsed;
        }

        return parsed.Allowlist;
    }

    private async Task<(ApplicationUser User, IList<string> Roles)?> EnsureUserExistsAsync(
        string username, string? email, string? displayName, string? groups)
    {
        try
        {
            // Try to find existing user
            var user = await UserManager.FindByNameAsync(username);
            
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
                    await UserManager.UpdateAsync(user);

                    // An account remembered by an API key or OPDS request carries its email.
                    _sessions.Invalidate(user.Id);
                    _logger.LogInformation("Updated user information for Authelia user: {Username}", username);
                }
                
                // Ensure user has appropriate role based on groups
                return (user, await EnsureUserRoleAsync(user, groups));
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
            var randomPassword = GenerateRandomPassword(UserManager.Options.Password);
            var result = await UserManager.CreateAsync(user, randomPassword);

            if (!result.Succeeded)
            {
                _logger.LogError("Failed to create Authelia user {Username}: {Errors}", 
                    username, string.Join(", ", result.Errors.Select(e => e.Description)));
                return null;
            }

            _logger.LogInformation("Created new user from Authelia: {Username}", username);

            // Assign role based on groups or default role
            return (user, await EnsureUserRoleAsync(user, groups));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Error ensuring Authelia user exists: {Username}", username);
            return null;
        }
    }

    /// <summary>Gives <paramref name="user"/> the role its groups call for, and returns the roles it now has.</summary>
    private async Task<IList<string>> EnsureUserRoleAsync(ApplicationUser user, string? groups)
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
        if (!await RoleManager.RoleExistsAsync(targetRole))
        {
            _logger.LogWarning("Target role {Role} does not exist, using 'User' instead", targetRole);
            targetRole = "User";
        }

        // Check if user already has the correct role
        var currentRoles = await UserManager.GetRolesAsync(user);
        if (currentRoles.Contains(targetRole))
        {
            return currentRoles;
        }

        // Remove all current roles and add the target role
        if (currentRoles.Any())
        {
            await UserManager.RemoveFromRolesAsync(user, currentRoles);
        }

        await UserManager.AddToRoleAsync(user, targetRole);

        // Tokens carry the roles they were issued with: a new security stamp ends the account's sessions
        // (session and stream tokens), so a demoted user cannot keep acting with the old role.
        await UserManager.UpdateSecurityStampAsync(user);
        _sessions.Invalidate(user.Id);
        _logger.LogInformation("Assigned role {Role} to Authelia user: {Username}", targetRole, user.UserName);
        return [targetRole];
    }

    private static string? NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>An active account found or provisioned for a set of Authelia headers.</summary>
    private sealed record CachedAccount(string UserId, string? Email, string[] Roles, long AccountVersion);

    private sealed record ParsedTrustedProxies(string? Value, TrustedProxyAllowlist? Allowlist);

    /// <summary>
    /// The email of a user the proxy sends no email for. Identity only creates an account whose email it
    /// accepts as an address and no other account has (<c>RequireUniqueEmail</c>), so this is the username
    /// itself when it is an address, such as <c>alice@corp.example</c>, and otherwise
    /// <c>&lt;username&gt;@authelia.local</c> with every '@' of the username replaced by '='. A username
    /// cannot contain '=' (Identity's default <c>AllowedUserNameCharacters</c>) and is never used as is
    /// at <c>authelia.local</c>, so no two usernames get the same email.
    /// </summary>
    private static string FallbackEmail(string username) =>
        EmailValidator.IsValid(username)
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
