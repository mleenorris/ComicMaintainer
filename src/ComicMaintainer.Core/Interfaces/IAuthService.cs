using ComicMaintainer.Core.Models.Auth;
using ComicMaintainer.Core.Services.Auth;

namespace ComicMaintainer.Core.Interfaces;

/// <summary>
/// Authentication service interface
/// </summary>
public interface IAuthService
{
    /// <summary>
    /// Signs in with a password and returns a session token. A failure always reports the same error, and is logged
    /// with <paramref name="clientAddress"/>; repeated failures lock the account out for a while.
    /// </summary>
    Task<(bool Success, string Token, string? Error)> LoginAsync(string username, string password, string? clientAddress = null);
    Task<(bool Success, string? Error)> RegisterAsync(string username, string password, string email, string? fullName = null);
    /// <summary>Changes the password; this signs every session of the account out.</summary>
    Task<(bool Success, string? Error)> ChangePasswordAsync(string userId, string currentPassword, string newPassword);
    /// <summary>The active account whose API key is <paramref name="apiKey"/> (see <see cref="ApiKeys"/>), or null.</summary>
    Task<ApplicationUser?> GetUserByApiKeyAsync(string apiKey);

    /// <summary>Creates a new API key for the account, replacing (and so revoking) its previous one. The key is only returned here.</summary>
    Task<(bool Success, string? ApiKey, string? Error)> GenerateApiKeyAsync(string userId);

    /// <summary>Revokes the account's API key. Returns false when it had none.</summary>
    Task<bool> RevokeApiKeyAsync(string userId);

    /// <summary>Whether the account has an API key, and when it was created and last used.</summary>
    Task<ApiKeyStatus> GetApiKeyStatusAsync(string userId);
    /// <summary>Whether first-run setup may create the first administrator: no account exists and Authelia is not enabled.</summary>
    Task<bool> IsSetupRequiredAsync();

    /// <summary>Creates the first administrator when setup is available and <paramref name="setupToken"/> is the setup code.</summary>
    Task<(SetupOutcome Outcome, string? Error)> SetupAdminAsync(string username, string password, string? email, string? setupToken, string? clientAddress = null);
    /// <summary>Issues a new session token for an active account, like a sign-in.</summary>
    Task<(bool Success, string Token, string? Error)> GenerateTokenForUserAsync(string userId);

    /// <summary>Issues a short-lived, single-use token that only opens the event stream (see <see cref="AuthTokens"/>).</summary>
    Task<(bool Success, string Token, string? Error)> GenerateStreamTokenAsync(string userId);

    /// <summary>Revokes one session token, or with <paramref name="allSessions"/> every token of the account.</summary>
    Task LogoutAsync(string userId, string? tokenId, DateTime expiresAtUtc, bool allSessions);
}
