using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Threading.RateLimiting;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models.Auth;
using ComicMaintainer.Core.Services.Auth;
using ComicMaintainer.Core.Utilities;
using ComicMaintainer.WebApi.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace ComicMaintainer.WebApi.Authentication;

/// <summary>
/// HTTP Basic authentication for the OPDS catalog, the only place it is accepted (see <see cref="PolicyName"/>): OPDS
/// clients send a username and either the account's password or its API key. A password is checked by the rules of a
/// sign-in (lockout, disabled accounts, published passwords); both draw on the sign-in attempt budget
/// (<see cref="AuthenticationAttemptLimiter"/>), because they are guessable credentials checked before any other limit.
/// </summary>
/// <remarks>
/// <para>While Authelia is enabled only API keys are accepted. An account Authelia created has only a random local
/// password, and <c>/opds/*</c> has to bypass Authelia because OPDS clients cannot follow its login, so accepting
/// local passwords there would let anyone try them without Authelia's checks.</para>
/// <para>OPDS clients repeat the credentials on every request, so a password that has passed is remembered for a short
/// while to spare a key derivation per page; the memory holds a salted hash of it, never the password. Every request
/// still reads the account: a disabled, locked out or deleted account, a changed security stamp (a password change, a
/// sign-out of all sessions) or a role change applies at once. API keys are looked up on every request.</para>
/// </remarks>
public sealed class OpdsBasicAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    /// <summary>The scheme name. It is in no default policy: <see cref="PolicyName"/> is the only policy that lists it.</summary>
    public const string SchemeName = "OpdsBasic";

    /// <summary>The authorization policy of the OPDS routes: an authenticated user, by this scheme alone.</summary>
    public const string PolicyName = "OpdsBasic";

    /// <summary>The <c>auth_method</c> claim of a request authenticated with a password over HTTP Basic.</summary>
    public const string PasswordAuthMethod = "opds_basic_password";

    /// <summary>The challenge that asks a client for credentials.</summary>
    public const string Challenge = "Basic realm=\"ComicMaintainer OPDS\", charset=\"UTF-8\"";

    /// <summary>How long a credential that passed is remembered after its last use.</summary>
    public static readonly TimeSpan RememberedFor = TimeSpan.FromMinutes(5);

    /// <summary>How long a credential can be remembered in all, however often it is used.</summary>
    public static readonly TimeSpan RememberedAtMost = TimeSpan.FromHours(1);

    // Far above what a person types, and the 256-bit API keys; a longer value is not worth hashing.
    private const int MaxUsernameLength = 256;
    private const int MaxSecretLength = 1024;

    private const string RateLimitedItem = "OpdsBasic.RetryAfterSeconds";
    private const string CacheKeyPrefix = "opds-basic:";

    private static readonly byte[] CacheSalt = RandomNumberGenerator.GetBytes(32);
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly IAuthService _authService;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IDbContextFactory<ComicMaintainerDbContext> _dbContextFactory;
    private readonly ClientAddressResolver _clientAddresses;
    private readonly AuthenticationAttemptLimiter _attempts;
    private readonly IMemoryCache _cache;
    private readonly AutheliaSettings _authelia;

    public OpdsBasicAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IAuthService authService,
        UserManager<ApplicationUser> userManager,
        IDbContextFactory<ComicMaintainerDbContext> dbContextFactory,
        ClientAddressResolver clientAddresses,
        AuthenticationAttemptLimiter attempts,
        IMemoryCache cache,
        IOptions<AutheliaSettings> autheliaSettings)
        : base(options, logger, encoder)
    {
        _authService = authService;
        _userManager = userManager;
        _dbContextFactory = dbContextFactory;
        _clientAddresses = clientAddresses;
        _attempts = attempts;
        _cache = cache;
        _authelia = autheliaSettings.Value;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(header))
        {
            return AuthenticateResult.NoResult();
        }

        if (!TryParseBasicCredentials(header, out var username, out var secret))
        {
            return AuthenticateResult.Fail("Malformed Basic credentials");
        }

        var cacheKey = CacheKeyPrefix + Fingerprint(username, secret);
        if (_cache.TryGetValue(cacheKey, out RememberedCredential? remembered) && remembered is not null)
        {
            var user = await RecheckAsync(remembered, username, secret);
            if (user is not null)
            {
                return await SucceedAsync(user, remembered.IsApiKey);
            }

            _cache.Remove(cacheKey);
        }

        // Every check that is not of a credential remembered above takes one attempt, right or wrong, as the sign-in
        // endpoint does: taken up front, so concurrent guesses cannot get ahead of the count.
        using var attempt = _attempts.AttemptAcquire(_clientAddresses.Resolve(Context));
        if (!attempt.IsAcquired)
        {
            Context.Items[RateLimitedItem] = attempt.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
                ? (int?)Math.Ceiling(retryAfter.TotalSeconds)
                : null;
            // Debug: the failed attempts themselves are in the auth log, and a flood past the limit must not fill it.
            Logger.LogDebug(
                "Refused OPDS credentials from {ClientAddress} for {Path}: too many attempts",
                LoggingHelper.SanitizeForLog(_clientAddresses.Describe(Context)),
                LoggingHelper.SanitizeForLog(Request.Path.Value));
            return AuthenticateResult.Fail("Too many attempts");
        }

        var (verified, isApiKey) = await VerifyAsync(username, secret);
        if (verified is null)
        {
            return AuthenticateResult.Fail("Invalid credentials");
        }

        _cache.Set(
            cacheKey,
            new RememberedCredential(isApiKey, verified.SecurityStamp),
            new MemoryCacheEntryOptions { SlidingExpiration = RememberedFor, AbsoluteExpirationRelativeToNow = RememberedAtMost });

        return await SucceedAsync(verified, isApiKey);
    }

    /// <summary>The one answer to a request without valid credentials on the OPDS routes, or the wait after too many.</summary>
    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        if (Response.HasStarted)
        {
            return;
        }

        string error;
        if (Context.Items.TryGetValue(RateLimitedItem, out var item))
        {
            var retryAfterSeconds = (int?)item;
            Response.StatusCode = StatusCodes.Status429TooManyRequests;
            if (retryAfterSeconds is { } seconds)
            {
                Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
            }

            error = RateLimitRejection.DescribeWait(retryAfterSeconds);
        }
        else
        {
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            Response.Headers.WWWAuthenticate = Challenge;
            error = "Authentication required. Use your username with your password or API key.";
        }

        var problem = new ProblemDetails
        {
            Status = Response.StatusCode,
            Title = error,
            Instance = Request.Path,
        };
        problem.Extensions["error"] = error;
        await Response.WriteAsJsonAsync(problem, options: null, contentType: "application/problem+json", Context.RequestAborted);
    }

    /// <summary>Whether the credential remembered for this exact username and secret still stands for a live account.</summary>
    private async Task<ApplicationUser?> RecheckAsync(RememberedCredential remembered, string username, string secret)
    {
        if (remembered.IsApiKey)
        {
            var keyOwner = await _authService.GetUserByApiKeyAsync(secret);
            return keyOwner is not null && IsNamed(keyOwner, username) ? keyOwner : null;
        }

        var user = await _userManager.FindByNameAsync(username);
        return user is { IsActive: true }
            && string.Equals(user.SecurityStamp, remembered.SecurityStamp, StringComparison.Ordinal)
            && !await _userManager.IsLockedOutAsync(user)
                ? user
                : null;
    }

    /// <summary>
    /// The account these credentials stand for: an API key, whose owner must be the named user, or (unless Authelia
    /// is enabled) a password, by the rules of a sign-in.
    /// </summary>
    private async Task<(ApplicationUser? User, bool IsApiKey)> VerifyAsync(string username, string secret)
    {
        var client = _clientAddresses.Describe(Context);

        // Only a value that can be a key is looked up. Anything else goes on to the password check, as may a value
        // with the key prefix that matches no key: a password can start that way too.
        if (secret.StartsWith(ApiKeys.Prefix, StringComparison.Ordinal) && secret.Length <= ApiKeys.MaxLength)
        {
            var keyOwner = await _authService.GetUserByApiKeyAsync(secret);
            if (keyOwner is not null && IsNamed(keyOwner, username))
            {
                return (keyOwner, true);
            }
        }

        if (_authelia.Enabled)
        {
            Logger.LogWarning(
                "Refused OPDS credentials for {Username} from {ClientAddress}: with Authelia enabled only an API key is accepted here",
                LoggingHelper.SanitizeForLog(username),
                LoggingHelper.SanitizeForLog(client));
            return (null, false);
        }

        return (await _authService.VerifyPasswordAsync(username, secret, client), false);
    }

    private bool IsNamed(ApplicationUser user, string username) =>
        string.Equals(_userManager.NormalizeName(user.UserName), _userManager.NormalizeName(username), StringComparison.Ordinal);

    private async Task<AuthenticateResult> SucceedAsync(ApplicationUser user, bool isApiKey)
    {
        if (isApiKey)
        {
            await ApiKeyAuthenticationHandler.RecordUseAsync(_dbContextFactory, user, Logger, Context.RequestAborted);
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id),
            new(ClaimTypes.Name, user.UserName ?? string.Empty),
            new(ClaimTypes.Email, user.Email ?? string.Empty),
            new("auth_method", isApiKey ? ApiKeys.AuthMethod : PasswordAuthMethod),
        };
        claims.AddRange((await _userManager.GetRolesAsync(user)).Select(role => new Claim(ClaimTypes.Role, role)));

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }

    private static bool TryParseBasicCredentials(string header, out string username, out string secret)
    {
        username = secret = string.Empty;
        const string scheme = "Basic ";
        if (!header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string decoded;
        try
        {
            decoded = StrictUtf8.GetString(Convert.FromBase64String(header[scheme.Length..].Trim()));
        }
        catch (Exception ex) when (ex is FormatException or DecoderFallbackException)
        {
            return false;
        }

        // The first colon separates them: a password may contain colons, a username may not (RFC 7617).
        var separator = decoded.IndexOf(':');
        if (separator <= 0 || separator == decoded.Length - 1)
        {
            return false;
        }

        username = decoded[..separator];
        secret = decoded[(separator + 1)..];
        return username.Length <= MaxUsernameLength && secret.Length <= MaxSecretLength;
    }

    /// <summary>A hash of the username and secret under a key made for this run, so the memory never holds the secret.</summary>
    private static string Fingerprint(string username, string secret) =>
        Convert.ToHexString(HMACSHA256.HashData(CacheSalt, Encoding.UTF8.GetBytes(username + "\0" + secret)));

    /// <summary>What was verified: the kind of credential and, for a password, the security stamp it passed under.</summary>
    private sealed record RememberedCredential(bool IsApiKey, string? SecurityStamp);
}
