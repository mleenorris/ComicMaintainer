using System.Collections.Concurrent;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Models.Auth;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace ComicMaintainer.Core.Services.Auth;

/// <summary>
/// Decides, for every request, whether a JWT the app issued still stands for a live session, so that tokens can be
/// revoked before they expire.
/// </summary>
public interface IAuthSessionValidator
{
    /// <summary>
    /// Whether the token of <paramref name="userId"/> with security-stamp fingerprint <paramref name="stampFingerprint"/>
    /// and id <paramref name="tokenId"/> is still valid: the account exists and is active, its security stamp has not
    /// changed since the token was issued, and the token was not signed out.
    /// </summary>
    Task<bool> IsSessionValidAsync(string? userId, string? stampFingerprint, string? tokenId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether <paramref name="apiKey"/> is still the API key of the active account <paramref name="userId"/>, for
    /// requests that outlive their authentication (an event stream). Always asks the database.
    /// </summary>
    Task<bool> IsApiKeyValidAsync(string? userId, string? apiKey, CancellationToken cancellationToken = default);

    /// <summary>Signs one token out: it is refused from now on, until it would have expired anyway.</summary>
    Task RevokeTokenAsync(string tokenId, string userId, DateTime expiresAtUtc, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records that a single-use token (a stream token) was used. Returns <see langword="false"/> when it was used
    /// before, so that a stream token copied from a URL cannot be replayed.
    /// </summary>
    bool TryConsumeSingleUseToken(string tokenId, DateTime expiresAtUtc);

    /// <summary>
    /// Forgets what is cached about <paramref name="userId"/>, so the next request sees a security stamp, role or
    /// status change right away. Call it after changing the account.
    /// </summary>
    void Invalidate(string userId);
}

/// <summary>
/// The default <see cref="IAuthSessionValidator"/>: compares the token's security-stamp fingerprint with the account's
/// current stamp and looks the token up in <see cref="ComicMaintainerDbContext.RevokedTokens"/>. Both answers are
/// cached for <see cref="CacheDuration"/> so requests do not each query the database; changes made through this app
/// call <see cref="Invalidate"/> (or revoke through it), so they take effect immediately, and changes made behind its
/// back (a restored database) within <see cref="CacheDuration"/>.
/// </summary>
public sealed class AuthSessionValidator : IAuthSessionValidator
{
    /// <summary>How long an account's stamp and a token's revocation status are cached.</summary>
    public static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);

    private const string AccountKeyPrefix = "auth-session:account:";
    private const string RevokedKeyPrefix = "auth-session:revoked:";
    private const int SingleUsePurgeThreshold = 1024;

    private readonly IDbContextFactory<ComicMaintainerDbContext> _dbContextFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<AuthSessionValidator> _logger;
    private readonly ConcurrentDictionary<string, DateTime> _usedSingleUseTokens = new(StringComparer.Ordinal);

    public AuthSessionValidator(
        IDbContextFactory<ComicMaintainerDbContext> dbContextFactory,
        IMemoryCache cache,
        ILogger<AuthSessionValidator> logger)
    {
        _dbContextFactory = dbContextFactory;
        _cache = cache;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> IsSessionValidAsync(string? userId, string? stampFingerprint, string? tokenId, CancellationToken cancellationToken = default)
    {
        // Tokens issued before 3.0.0 carry no fingerprint and no id: they cannot be revoked, so they are refused.
        if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(stampFingerprint) || string.IsNullOrEmpty(tokenId))
        {
            return false;
        }

        var account = await GetAccountAsync(userId, cancellationToken);
        if (account is null || !account.IsActive || !string.Equals(account.StampFingerprint, stampFingerprint, StringComparison.Ordinal))
        {
            return false;
        }

        return !await IsRevokedAsync(tokenId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> IsApiKeyValidAsync(string? userId, string? apiKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(apiKey) || apiKey.Length > ApiKeys.MaxLength)
        {
            return false;
        }

        var hash = ApiKeys.Hash(apiKey);
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Users.AsNoTracking().AnyAsync(u => u.Id == userId && u.ApiKeyHash == hash && u.IsActive, cancellationToken);
    }

    /// <inheritdoc />
    public async Task RevokeTokenAsync(string tokenId, string userId, DateTime expiresAtUtc, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        await using (var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            // Expired tokens are refused anyway; their rows are no longer needed.
            await db.RevokedTokens.Where(t => t.ExpiresAt < now).ExecuteDeleteAsync(cancellationToken);
            if (expiresAtUtc > now && !await db.RevokedTokens.AnyAsync(t => t.TokenId == tokenId, cancellationToken))
            {
                db.RevokedTokens.Add(new RevokedTokenEntity { TokenId = tokenId, UserId = userId, ExpiresAt = expiresAtUtc, RevokedAt = now });
                try
                {
                    await db.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 19 }) // UNIQUE constraint
                {
                    // Another sign-out of the same token got there between the check and this insert: it is revoked.
                    _logger.LogDebug("Token {TokenId} was already signed out", tokenId);
                }
            }
        }

        _cache.Set(RevokedKeyPrefix + tokenId, true, new MemoryCacheEntryOptions
        {
            AbsoluteExpiration = expiresAtUtc > now ? expiresAtUtc : now.Add(CacheDuration),
        });
    }

    /// <inheritdoc />
    public bool TryConsumeSingleUseToken(string tokenId, DateTime expiresAtUtc)
    {
        var now = DateTime.UtcNow;
        if (_usedSingleUseTokens.Count >= SingleUsePurgeThreshold)
        {
            foreach (var (id, expires) in _usedSingleUseTokens)
            {
                if (expires < now)
                {
                    _usedSingleUseTokens.TryRemove(id, out _);
                }
            }
        }

        return _usedSingleUseTokens.TryAdd(tokenId, expiresAtUtc);
    }

    /// <inheritdoc />
    public void Invalidate(string userId) => _cache.Remove(AccountKeyPrefix + userId);

    private async Task<AccountSnapshot?> GetAccountAsync(string userId, CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(AccountKeyPrefix + userId, out AccountSnapshot? cached))
        {
            return cached;
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var account = await db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.SecurityStamp, u.IsActive })
            .FirstOrDefaultAsync(cancellationToken);

        var snapshot = account is null ? null : new AccountSnapshot(AuthTokens.Fingerprint(account.SecurityStamp), account.IsActive);
        if (account is null)
        {
            _logger.LogDebug("Refusing a token of {UserId}: the account no longer exists", userId);
        }

        _cache.Set(AccountKeyPrefix + userId, snapshot, CacheDuration);
        return snapshot;
    }

    private async Task<bool> IsRevokedAsync(string tokenId, CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(RevokedKeyPrefix + tokenId, out bool cached))
        {
            return cached;
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var revoked = await db.RevokedTokens.AsNoTracking().AnyAsync(t => t.TokenId == tokenId, cancellationToken);
        _cache.Set(RevokedKeyPrefix + tokenId, revoked, CacheDuration);
        return revoked;
    }

    private sealed record AccountSnapshot(string StampFingerprint, bool IsActive);
}
