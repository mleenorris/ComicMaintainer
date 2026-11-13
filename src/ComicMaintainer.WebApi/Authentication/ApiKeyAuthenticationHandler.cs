using System.Text.Encodings.Web;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models.Auth;
using ComicMaintainer.Core.Services.Auth;
using ComicMaintainer.Core.Utilities;
using ComicMaintainer.WebApi.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.WebApi.Authentication;

/// <summary>
/// Authenticates requests that carry an API key in the <c>X-API-Key</c> header (<see cref="ApiKeys"/>) as the
/// account that owns the key, with that account's current roles. Keys are never read from the query string or any
/// other place a proxy or browser history would record. Requests without the header are left to the other schemes;
/// a wrong key, or the key of a disabled account, fails and is logged to the auth log with the client's address.
/// The account a key stands for is remembered briefly (<see cref="RememberedAccount"/>); any change to it made in the
/// app applies to the next request.
/// </summary>
public sealed class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    /// <summary>The scheme name, listed by every authorization policy next to the session-token schemes.</summary>
    public const string SchemeName = "ApiKey";

    /// <summary>How stale <see cref="ApplicationUser.ApiKeyLastUsedAt"/> may get before a request updates it.</summary>
    public static readonly TimeSpan LastUsedResolution = TimeSpan.FromMinutes(1);

    private const string CacheKeyPrefix = "api-key:account:";

    private readonly IDbContextFactory<ComicMaintainerDbContext> _dbContextFactory;
    private readonly ClientAddressResolver _clientAddresses;
    private readonly IAuthSessionValidator _sessions;
    private readonly IMemoryCache _cache;

    // Every policy lists this scheme, so a handler is made for every request, nearly all of them without a key. It
    // takes only singletons; the account services (a DbContext and its stores) are resolved once a key is to be read.
    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IDbContextFactory<ComicMaintainerDbContext> dbContextFactory,
        ClientAddressResolver clientAddresses,
        IAuthSessionValidator sessions,
        IMemoryCache cache)
        : base(options, logger, encoder)
    {
        _dbContextFactory = dbContextFactory;
        _clientAddresses = clientAddresses;
        _sessions = sessions;
        _cache = cache;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ApiKeys.HeaderName, out var values) || values.Count == 0)
        {
            return AuthenticateResult.NoResult();
        }

        var apiKey = values.Count == 1 ? values[0]?.Trim() : null;
        var account = string.IsNullOrEmpty(apiKey) ? null : await FindAccountAsync(apiKey);
        if (account is null)
        {
            Logger.LogWarning(
                "Refused an API key from {ClientAddress} for {Method} {Path}: no active account has it",
                LoggingHelper.SanitizeForLog(_clientAddresses.Describe(Context)),
                LoggingHelper.SanitizeForLog(Request.Method),
                LoggingHelper.SanitizeForLog(Request.Path.Value));
            return AuthenticateResult.Fail("Invalid API key");
        }

        await RecordUseAsync(_dbContextFactory, account, Logger, Context.RequestAborted);
        Logger.LogDebug("Authenticated {Username} with an API key", LoggingHelper.SanitizeForLog(account.UserName));

        return AuthenticateResult.Success(new AuthenticationTicket(account.ToPrincipal(Scheme.Name, ApiKeys.AuthMethod), Scheme.Name));
    }

    /// <summary>
    /// The active account whose key <paramref name="apiKey"/> is, remembered by the key's hash (the memory never holds
    /// the key) for <see cref="AuthSessionValidator.CacheDuration"/> unless the account changes in the meantime.
    /// </summary>
    private async Task<RememberedAccount?> FindAccountAsync(string apiKey)
    {
        var cacheKey = CacheKeyPrefix + ApiKeys.Hash(apiKey);
        if (_cache.TryGetValue(cacheKey, out RememberedAccount? remembered) && remembered is not null && remembered.IsCurrent(_sessions))
        {
            return remembered;
        }

        var authService = Context.RequestServices.GetRequiredService<IAuthService>();
        var owner = await authService.GetUserByApiKeyAsync(apiKey);
        if (owner is null)
        {
            _cache.Remove(cacheKey);
            return null;
        }

        // The version is read before what is remembered with it: the key is looked up again, so a change committed
        // before the version was read (a new key, a disabled account) is in what is read, and one committed after it
        // has moved the version on.
        var version = _sessions.GetAccountVersion(owner.Id);
        var roles = await Context.RequestServices.GetRequiredService<UserManager<ApplicationUser>>().GetRolesAsync(owner);
        var current = await authService.GetUserByApiKeyAsync(apiKey);
        if (current is null || current.Id != owner.Id)
        {
            _cache.Remove(cacheKey);
            return null;
        }

        var account = new RememberedAccount(current.Id, current.UserName, current.Email, roles, version, current.ApiKeyLastUsedAt);
        _cache.Set(cacheKey, account, AuthSessionValidator.CacheDuration);
        return account;
    }

    // Several schemes answer the challenge of a policy that lists them; the session-token scheme writes the JSON body.
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        if (!Response.HasStarted)
        {
            Response.StatusCode = StatusCodes.Status401Unauthorized;
        }

        return Task.CompletedTask;
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        if (!Response.HasStarted)
        {
            Response.StatusCode = StatusCodes.Status403Forbidden;
        }

        return Task.CompletedTask;
    }

    // Records when the key was last used, to the minute, so that scripts polling the API do not write on every request.
    internal static async Task RecordUseAsync(
        IDbContextFactory<ComicMaintainerDbContext> dbContextFactory,
        RememberedAccount account,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        if (account.ApiKeyLastUsedAt is { } lastUsed && now - lastUsed < LastUsedResolution)
        {
            return;
        }

        try
        {
            var threshold = now - LastUsedResolution;
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            await db.Users
                .Where(u => u.Id == account.UserId && (u.ApiKeyLastUsedAt == null || u.ApiKeyLastUsedAt < threshold))
                .ExecuteUpdateAsync(set => set.SetProperty(u => u.ApiKeyLastUsedAt, now), cancellationToken);

            // Whether this request wrote it or another did, the use is now recorded: the account remembered for the
            // key need not try again within the resolution.
            account.ApiKeyLastUsedAt = now;
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            // Bookkeeping only: a busy database must not refuse the request.
            logger.LogDebug(ex, "Could not record the use of the API key of {UserId}", LoggingHelper.SanitizeForLog(account.UserId));
        }
    }
}
