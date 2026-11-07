using ComicMaintainer.Core.Models.Auth;
using Microsoft.AspNetCore.Identity;

namespace ComicMaintainer.Core.Services.Auth;

/// <summary>
/// Example passwords that were published with ComicMaintainer: the fallback of docker-compose.dotnet.yml and the
/// value in .env.example before 3.0.0, and the examples in the deployment docs. They meet the password policy, so
/// deployments set up with them may still pass one in, but anyone can look them up. Like the published JWT secret
/// placeholders (see <c>JwtSecretResolver</c>), they are never accepted as a password.
/// </summary>
public static class PublishedPasswords
{
    private static readonly string[] Values =
    [
        "Admin123!",
        "YourStrongPassword123!",
        "YourSecurePassword123!",
    ];

    /// <summary>Whether <paramref name="password"/> is one of the published example passwords.</summary>
    public static bool IsPublished(string? password)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            return false;
        }

        var trimmed = password.Trim();
        return Array.Exists(Values, value => string.Equals(value, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Whether <paramref name="user"/>'s password is one of the published example passwords. Checking costs one hash
    /// per published password, so use it once at startup rather than per request.
    /// </summary>
    public static bool MatchesUser(IPasswordHasher<ApplicationUser> hasher, ApplicationUser user)
    {
        if (string.IsNullOrEmpty(user.PasswordHash))
        {
            return false;
        }

        return Array.Exists(Values, value =>
            hasher.VerifyHashedPassword(user, user.PasswordHash, value) != PasswordVerificationResult.Failed);
    }
}

/// <summary>
/// Rejects the <see cref="PublishedPasswords"/> wherever Identity validates a new password: first-run setup,
/// registration, password changes and the admin account seeded from <c>ADMIN_PASSWORD</c>.
/// </summary>
public sealed class PublishedPasswordValidator : IPasswordValidator<ApplicationUser>
{
    /// <summary>The <see cref="IdentityError.Code"/> of the error this validator reports.</summary>
    public const string ErrorCode = "PublishedPassword";

    /// <inheritdoc />
    public Task<IdentityResult> ValidateAsync(UserManager<ApplicationUser> manager, ApplicationUser user, string? password) =>
        Task.FromResult(PublishedPasswords.IsPublished(password)
            ? IdentityResult.Failed(new IdentityError
            {
                Code = ErrorCode,
                Description = "This password was published as an example with ComicMaintainer, so anyone could guess it. Choose a password of your own.",
            })
            : IdentityResult.Success);
}
