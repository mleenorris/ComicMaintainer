using System.Security.Claims;
using ComicMaintainer.Core.Services.Auth;

namespace ComicMaintainer.WebApi.Authentication;

/// <summary>
/// What a request authenticated by an API key or by OPDS Basic credentials needs of its account, remembered for
/// <see cref="AuthSessionValidator.CacheDuration"/> so that a client repeating the same credential does not read the
/// account and its roles on every request. Only active accounts are remembered. The entry holds the account version
/// (<see cref="IAuthSessionValidator.GetAccountVersion"/>) read before the account was, and every change made in the
/// app (a new or revoked key, a role, password or status change, a lockout) moves that version on, so the next
/// request reads the account again.
/// </summary>
internal sealed class RememberedAccount
{
    private long _apiKeyLastUsedTicks;

    public RememberedAccount(string userId, string? userName, string? email, IEnumerable<string> roles, long accountVersion, DateTime? apiKeyLastUsedAt)
    {
        UserId = userId;
        UserName = userName;
        Email = email;
        Roles = roles.ToArray();
        AccountVersion = accountVersion;
        ApiKeyLastUsedAt = apiKeyLastUsedAt;
    }

    public string UserId { get; }

    public string? UserName { get; }

    public string? Email { get; }

    public string[] Roles { get; }

    public long AccountVersion { get; }

    /// <summary>When the use of the API key was last recorded, as far as this entry knows; moved on as uses are.</summary>
    public DateTime? ApiKeyLastUsedAt
    {
        get
        {
            var ticks = Interlocked.Read(ref _apiKeyLastUsedTicks);
            return ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc);
        }
        set => Interlocked.Exchange(ref _apiKeyLastUsedTicks, value?.Ticks ?? 0);
    }

    /// <summary>Whether nothing has changed the account in the app since it was read.</summary>
    public bool IsCurrent(IAuthSessionValidator sessions) => AccountVersion == sessions.GetAccountVersion(UserId);

    /// <summary>The principal of a request authenticated as this account by <paramref name="scheme"/>.</summary>
    public ClaimsPrincipal ToPrincipal(string scheme, string authMethod)
    {
        var claims = new List<Claim>(4 + Roles.Length)
        {
            new(ClaimTypes.NameIdentifier, UserId),
            new(ClaimTypes.Name, UserName ?? string.Empty),
            new(ClaimTypes.Email, Email ?? string.Empty),
            new("auth_method", authMethod),
        };
        claims.AddRange(Roles.Select(role => new Claim(ClaimTypes.Role, role)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, scheme));
    }
}
