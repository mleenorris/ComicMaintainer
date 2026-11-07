using System.Security.Claims;
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
using Microsoft.Extensions.Options;

namespace ComicMaintainer.WebApi.Authentication;

/// <summary>
/// Authenticates requests that carry an API key in the <c>X-API-Key</c> header (<see cref="ApiKeys"/>) as the
/// account that owns the key, with that account's current roles. Keys are never read from the query string or any
/// other place a proxy or browser history would record. Requests without the header are left to the other schemes;
/// a wrong key, or the key of a disabled account, fails and is logged to the auth log with the client's address.
/// </summary>
public sealed class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    /// <summary>The scheme name, listed by every authorization policy next to the session-token schemes.</summary>
    public const string SchemeName = "ApiKey";

    /// <summary>How stale <see cref="ApplicationUser.ApiKeyLastUsedAt"/> may get before a request updates it.</summary>
    public static readonly TimeSpan LastUsedResolution = TimeSpan.FromMinutes(1);

    private readonly IAuthService _authService;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IDbContextFactory<ComicMaintainerDbContext> _dbContextFactory;
    private readonly ClientAddressResolver _clientAddresses;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IAuthService authService,
        UserManager<ApplicationUser> userManager,
        IDbContextFactory<ComicMaintainerDbContext> dbContextFactory,
        ClientAddressResolver clientAddresses)
        : base(options, logger, encoder)
    {
        _authService = authService;
        _userManager = userManager;
        _dbContextFactory = dbContextFactory;
        _clientAddresses = clientAddresses;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ApiKeys.HeaderName, out var values) || values.Count == 0)
        {
            return AuthenticateResult.NoResult();
        }

        var apiKey = values.Count == 1 ? values[0]?.Trim() : null;
        var user = string.IsNullOrEmpty(apiKey) ? null : await _authService.GetUserByApiKeyAsync(apiKey);
        if (user is null)
        {
            Logger.LogWarning(
                "Refused an API key from {ClientAddress} for {Method} {Path}: no active account has it",
                LoggingHelper.SanitizeForLog(_clientAddresses.Describe(Context)),
                LoggingHelper.SanitizeForLog(Request.Method),
                LoggingHelper.SanitizeForLog(Request.Path.Value));
            return AuthenticateResult.Fail("Invalid API key");
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id),
            new(ClaimTypes.Name, user.UserName ?? string.Empty),
            new(ClaimTypes.Email, user.Email ?? string.Empty),
            new("auth_method", ApiKeys.AuthMethod),
        };
        claims.AddRange((await _userManager.GetRolesAsync(user)).Select(role => new Claim(ClaimTypes.Role, role)));

        await RecordUseAsync(user);
        Logger.LogDebug("Authenticated {Username} with an API key", LoggingHelper.SanitizeForLog(user.UserName));

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
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
    private async Task RecordUseAsync(ApplicationUser user)
    {
        var now = DateTime.UtcNow;
        if (user.ApiKeyLastUsedAt is { } lastUsed && now - lastUsed < LastUsedResolution)
        {
            return;
        }

        try
        {
            var threshold = now - LastUsedResolution;
            await using var db = await _dbContextFactory.CreateDbContextAsync(Context.RequestAborted);
            await db.Users
                .Where(u => u.Id == user.Id && (u.ApiKeyLastUsedAt == null || u.ApiKeyLastUsedAt < threshold))
                .ExecuteUpdateAsync(set => set.SetProperty(u => u.ApiKeyLastUsedAt, now), Context.RequestAborted);
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            // Bookkeeping only: a busy database must not refuse the request.
            Logger.LogDebug(ex, "Could not record the use of the API key of {UserId}", LoggingHelper.SanitizeForLog(user.Id));
        }
    }
}
