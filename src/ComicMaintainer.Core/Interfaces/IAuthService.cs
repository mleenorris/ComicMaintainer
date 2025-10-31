using ComicMaintainer.Core.Models.Auth;

namespace ComicMaintainer.Core.Interfaces;

/// <summary>
/// Authentication service interface
/// </summary>
public interface IAuthService
{
    Task<(bool Success, string Token, string? Error)> LoginAsync(string username, string password);
    Task<(bool Success, string? Error)> RegisterAsync(string username, string password, string email, string? fullName = null);
    Task<(bool Success, string? Error)> ChangePasswordAsync(string userId, string currentPassword, string newPassword);
    Task<ApplicationUser?> GetUserByApiKeyAsync(string apiKey);
    Task<(bool Success, string? ApiKey, string? Error)> GenerateApiKeyAsync(string userId);
    Task<bool> ValidateApiKeyAsync(string apiKey);
}
