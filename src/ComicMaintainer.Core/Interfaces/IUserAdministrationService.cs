using ComicMaintainer.Core.Models.Auth;

namespace ComicMaintainer.Core.Interfaces;

/// <summary>
/// Account administration for the Users page (<c>/api/users</c>): listing, creating, changing the role, setting the
/// password, disabling, enabling and deleting accounts. The caller has already been authorized as an administrator;
/// this enforces the rules on top: no administrator can change, disable or delete their own account here or set their
/// own password, the last active administrator is never demoted, disabled or deleted, and changes that grant lasting
/// access need the administrator's own password (see <see cref="UserAdministrationActor"/>).
/// </summary>
public interface IUserAdministrationService
{
    /// <summary>Every account, sorted by username; <paramref name="currentUserId"/> marks the caller's own.</summary>
    Task<UserAccountList> ListAsync(string currentUserId, CancellationToken cancellationToken = default);

    /// <summary>Creates an active account with a password and one role. Refused while Authelia is enabled.</summary>
    Task<UserAdministrationResult> CreateAsync(UserAdministrationActor actor, NewUserAccount account);

    /// <summary>
    /// Gives the account <paramref name="role"/> in place of its roles. Its sessions end, because session tokens
    /// carry the roles they were issued with. Refused while Authelia is enabled, whose groups set roles.
    /// </summary>
    Task<UserAdministrationResult> ChangeRoleAsync(UserAdministrationActor actor, string userId, string? role);

    /// <summary>
    /// Sets the account's password, clears any lockout, revokes its API key and ends its sessions, as a password
    /// change does. Refused while Authelia is enabled, where people sign in with their Authelia password.
    /// </summary>
    Task<UserAdministrationResult> ResetPasswordAsync(UserAdministrationActor actor, string userId, string? newPassword);

    /// <summary>
    /// Disables or enables the account. Disabling ends its sessions and revokes its API key at once; enabling it again
    /// does not bring them back. Only enabling needs the administrator's password.
    /// </summary>
    Task<UserAdministrationResult> SetActiveAsync(UserAdministrationActor actor, string userId, bool isActive);

    /// <summary>
    /// Deletes the account with everything that is its own: reading progress and sessions, read status, reader and
    /// app preferences, e-reader devices with their series subscriptions, email delivery history and signed-out
    /// session records, in one transaction. Rows without an owner, which administrators share, are left alone.
    /// </summary>
    Task<UserAdministrationResult> DeleteAsync(UserAdministrationActor actor, string userId);
}
