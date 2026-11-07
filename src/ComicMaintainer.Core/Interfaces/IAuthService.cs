using ComicMaintainer.Core.Models.Auth;

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
    Task<(bool Success, string? Error)> ChangePasswordAsync(string userId, string currentPassword, string newPassword);
    Task<ApplicationUser?> GetUserByApiKeyAsync(string apiKey);
    Task<(bool Success, string? ApiKey, string? Error)> GenerateApiKeyAsync(string userId);
    Task<bool> ValidateApiKeyAsync(string apiKey);
    Task<bool> IsSetupRequiredAsync();
    Task<(bool Success, string? Error)> SetupAdminAsync(string username, string password, string? email = null);
    Task<(bool Success, string Token, string? Error)> GenerateTokenForUserAsync(string userId);
}
