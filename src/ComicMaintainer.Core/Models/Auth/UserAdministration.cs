namespace ComicMaintainer.Core.Models.Auth;

/// <summary>
/// What an administrator sees of an account on the Users page (<c>GET /api/users</c>). It never carries the password
/// hash, the security stamp or the API key hash.
/// </summary>
/// <param name="Id">The account's id.</param>
/// <param name="Username">The username it signs in with.</param>
/// <param name="FullName">The display name, if any.</param>
/// <param name="Email">The email address, if any.</param>
/// <param name="Role">
/// <c>Admin</c>, <c>User</c> or <c>ReadOnly</c>, or <see langword="null"/> for an account without one of them. An
/// account in several roles reports the one with the most access.
/// </param>
/// <param name="IsActive">Whether the account may sign in. A disabled account cannot.</param>
/// <param name="LockedOutUntil">When a lockout after too many wrong passwords ends (UTC); null unless locked out now.</param>
/// <param name="CreatedAt">When the account was created (UTC).</param>
/// <param name="LastLoginAt">When the account last signed in with its password (UTC), if ever.</param>
/// <param name="HasApiKey">Whether the account has an API key.</param>
/// <param name="IsCurrentUser">Whether this is the account of the administrator asking.</param>
public sealed record UserAccountSummary(
    string Id,
    string Username,
    string? FullName,
    string? Email,
    string? Role,
    bool IsActive,
    DateTime? LockedOutUntil,
    DateTime CreatedAt,
    DateTime? LastLoginAt,
    bool HasApiKey,
    bool IsCurrentUser);

/// <summary>Every account, for the Users page.</summary>
/// <param name="AutheliaEnabled">
/// Whether Authelia is enabled. Its groups then set roles, and accounts are created when someone first signs in
/// through it, so creating accounts, changing roles and setting passwords are refused.
/// </param>
/// <param name="Users">The accounts, sorted by username.</param>
public sealed record UserAccountList(bool AutheliaEnabled, IReadOnlyList<UserAccountSummary> Users);

/// <summary>The details of an account an administrator creates.</summary>
/// <param name="Username">At least three characters, of the characters Identity allows.</param>
/// <param name="Password">The account's password; it must meet the password rules.</param>
/// <param name="Email">Optional; defaults to <c>{username}@comicmaintainer.local</c>, like first-run setup.</param>
/// <param name="FullName">Optional display name.</param>
/// <param name="Role"><c>Admin</c>, <c>User</c> or <c>ReadOnly</c>, in any case.</param>
public sealed record NewUserAccount(string? Username, string? Password, string? Email, string? FullName, string? Role);

/// <summary>
/// The administrator making a change on the Users page, and how they confirmed it.
/// </summary>
/// <param name="UserId">The administrator's account id.</param>
/// <param name="Username">The administrator's username, for the auth log.</param>
/// <param name="CurrentPassword">
/// The administrator's own password. Changes that grant lasting access (creating an account, changing a role, setting
/// a password, enabling an account) require it, so that a stolen session token alone cannot create or take over an
/// account.
/// </param>
/// <param name="SignedInThroughAuthelia">
/// Whether the request came through Authelia, whose accounts have no password of their own here; such a caller is not
/// asked for one, as when creating an API key.
/// </param>
/// <param name="ClientAddress">The client's address, for the auth log.</param>
public sealed record UserAdministrationActor(
    string UserId,
    string? Username,
    string? CurrentPassword,
    bool SignedInThroughAuthelia,
    string? ClientAddress);

/// <summary>How a change on the Users page ended.</summary>
public enum UserAdministrationOutcome
{
    /// <summary>The change was made (or there was nothing to change).</summary>
    Succeeded,

    /// <summary>A new account was created.</summary>
    Created,

    /// <summary>The request was invalid, for example a password that does not meet the rules (400).</summary>
    Invalid,

    /// <summary>No account has that id (404).</summary>
    NotFound,

    /// <summary>A rule forbids the change, for example on the administrator's own account (409).</summary>
    Conflict,

    /// <summary>The administrator's own password was missing or wrong, or their account is locked out (403).</summary>
    PasswordNotConfirmed,
}

/// <summary>The outcome of a change on the Users page, with the account as it now is or the reason it was refused.</summary>
public sealed record UserAdministrationResult(UserAdministrationOutcome Outcome, string? Error = null, UserAccountSummary? User = null)
{
    /// <summary>Whether the change was made.</summary>
    public bool Succeeded => Outcome is UserAdministrationOutcome.Succeeded or UserAdministrationOutcome.Created;

    internal static UserAdministrationResult Done(UserAccountSummary? user) => new(UserAdministrationOutcome.Succeeded, null, user);

    internal static UserAdministrationResult Created(UserAccountSummary user) => new(UserAdministrationOutcome.Created, null, user);

    internal static UserAdministrationResult Invalid(string error) => new(UserAdministrationOutcome.Invalid, error);

    internal static UserAdministrationResult NotFound() => new(UserAdministrationOutcome.NotFound, "No account has that id.");

    internal static UserAdministrationResult Conflict(string error) => new(UserAdministrationOutcome.Conflict, error);

    internal static UserAdministrationResult PasswordNotConfirmed(string? error) =>
        new(UserAdministrationOutcome.PasswordNotConfirmed, error);
}
