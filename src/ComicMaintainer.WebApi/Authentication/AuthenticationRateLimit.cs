namespace ComicMaintainer.WebApi.Authentication;

/// <summary>
/// The rate limit of the endpoints that check a password or a setup code (sign-in, registration, first-run setup,
/// changing the password and creating an API key), counted per client address on top of the global limit, in one
/// budget with the HTTP Basic credentials of the OPDS catalog (see <see cref="AuthenticationAttemptLimiter"/>). Together
/// with the account lockout (see <c>AuthServiceCollectionExtensions</c>) it slows down online guessing: the lockout
/// protects one account, this limit stops one client from trying many accounts.
/// </summary>
public static class AuthenticationRateLimit
{
    /// <summary>The name of the rate-limiting policy, for <c>[EnableRateLimiting]</c>.</summary>
    public const string PolicyName = "Authentication";

    /// <summary>Attempts one client may make in one <see cref="Window"/>.</summary>
    public const int PermitLimit = 10;

    /// <summary>The fixed window the attempts are counted in.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
}
