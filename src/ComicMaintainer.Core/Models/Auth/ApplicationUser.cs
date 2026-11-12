using Microsoft.AspNetCore.Identity;

namespace ComicMaintainer.Core.Models.Auth;

/// <summary>
/// Application user extending ASP.NET Core Identity
/// </summary>
public class ApplicationUser : IdentityUser
{
    public string? FullName { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// The SHA-256 hash of the account's API key (<see cref="Services.Auth.ApiKeys.Hash"/>), or
    /// <see langword="null"/> when it has none. The key itself is never stored.
    /// </summary>
    public string? ApiKeyHash { get; set; }

    /// <summary>When the current API key was created.</summary>
    public DateTime? ApiKeyCreatedAt { get; set; }

    /// <summary>When the current API key was last used, to the minute (see <c>ApiKeyAuthenticationHandler</c>).</summary>
    public DateTime? ApiKeyLastUsedAt { get; set; }
}
