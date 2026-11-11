using System.Security.Claims;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models.Auth;
using ComicMaintainer.WebApi.Authentication;
using ComicMaintainer.WebApi.Authorization;
using ComicMaintainer.WebApi.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ComicMaintainer.WebApi.Controllers;

/// <summary>
/// Account administration for the Users page (<c>#/users</c>): list, create, change the role, set the password,
/// disable or enable, and delete accounts. Administrators only, the reads included: the list names every account and
/// its email address. The rules live in <see cref="IUserAdministrationService"/>; this maps their outcomes to
/// <c>400</c> (invalid request), <c>403</c> (the administrator's own password is missing or wrong), <c>404</c> (no such
/// account) and <c>409</c> (a rule forbids it), each with an <c>error</c>.
/// </summary>
/// <remarks>
/// Creating an account, changing a role, setting a password and enabling an account grant lasting access, so they need
/// the administrator's own password in <c>currentPassword</c>, as creating an API key does: a stolen session token alone
/// cannot create or take over an account. Wrong passwords count towards the administrator's lockout. A caller signed in
/// through Authelia has no password here and is not asked for one. Disabling and deleting only take access away and
/// need no password. These routes share no per-client rate limit with sign-in: the only password they check is the
/// caller's own, which the lockout already protects, and the caller must already be a signed-in administrator.
/// </remarks>
[ApiController]
[Route("api/users")]
[Authorize(Policy = AuthorizationPolicies.CanAdminister)]
public class UsersController : ControllerBase
{
    private readonly IUserAdministrationService _users;
    private readonly ClientAddressResolver _clientAddresses;

    public UsersController(IUserAdministrationService users, ClientAddressResolver clientAddresses)
    {
        _users = users;
        _clientAddresses = clientAddresses;
    }

    /// <summary>
    /// Every account, sorted by username, and whether Authelia is enabled. Never includes password hashes, security
    /// stamps or API key hashes.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<UserAccountList>> List(CancellationToken cancellationToken)
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        return Ok(await _users.ListAsync(userId, cancellationToken));
    }

    /// <summary>
    /// Creates an active account with a password and a role (<c>Admin</c>, <c>User</c> or <c>ReadOnly</c>). Answers
    /// <c>201</c> with the account. A missing email becomes <c>{username}@comicmaintainer.local</c>. A username or email
    /// another account has answers <c>409</c>, as does any request while Authelia is enabled.
    /// </summary>
    [HttpPost]
    [FailureMessage("The account could not be created.")]
    public async Task<ActionResult> Create([FromBody] CreateUserRequest request)
    {
        if (Actor(request.CurrentPassword) is not { } actor)
        {
            return Unauthorized();
        }

        var result = await _users.CreateAsync(
            actor,
            new NewUserAccount(request.Username, request.Password, request.Email, request.FullName, request.Role));
        return ToResponse(result);
    }

    /// <summary>
    /// Gives the account one role in place of its roles. Its sessions end, because session tokens carry the roles they
    /// were issued with. Refused (<c>409</c>) for the caller's own account, for the last active administrator, and while
    /// Authelia is enabled.
    /// </summary>
    [HttpPut("{id}/role")]
    [FailureMessage("The role could not be changed.")]
    public async Task<ActionResult> ChangeRole(string id, [FromBody] ChangeUserRoleRequest request)
    {
        if (Actor(request.CurrentPassword) is not { } actor)
        {
            return Unauthorized();
        }

        return ToResponse(await _users.ChangeRoleAsync(actor, id, request.Role));
    }

    /// <summary>
    /// Sets the account's password. This also clears a lockout, revokes its API key and ends its sessions. Refused
    /// (<c>409</c>) for the caller's own account (Change Password asks for the current password) and while Authelia is
    /// enabled.
    /// </summary>
    [HttpPost("{id}/password")]
    [FailureMessage("The password could not be set.")]
    public async Task<ActionResult> ResetPassword(string id, [FromBody] ResetUserPasswordRequest request)
    {
        if (Actor(request.CurrentPassword) is not { } actor)
        {
            return Unauthorized();
        }

        return ToResponse(await _users.ResetPasswordAsync(actor, id, request.NewPassword));
    }

    /// <summary>
    /// Disables or enables the account. Disabling ends its sessions and revokes its API key at once. Only enabling needs
    /// <c>currentPassword</c>. Refused (<c>409</c>) for the caller's own account and for disabling the last active
    /// administrator.
    /// </summary>
    [HttpPut("{id}/active")]
    [FailureMessage("The account could not be changed.")]
    public async Task<ActionResult> SetActive(string id, [FromBody] SetUserActiveRequest request)
    {
        if (request.IsActive is not { } isActive)
        {
            return BadRequest(new { error = "Say whether the account should be active (isActive: true or false)." });
        }

        if (Actor(request.CurrentPassword) is not { } actor)
        {
            return Unauthorized();
        }

        return ToResponse(await _users.SetActiveAsync(actor, id, isActive));
    }

    /// <summary>
    /// Deletes the account with its own data: reading progress and sessions, read status, reader and app preferences,
    /// e-reader devices with their series subscriptions, and email delivery history. Comic files are not touched.
    /// Refused (<c>409</c>) for the caller's own account and for the last active administrator.
    /// </summary>
    [HttpDelete("{id}")]
    [FailureMessage("The account could not be deleted.")]
    public async Task<ActionResult> Delete(string id)
    {
        if (Actor(currentPassword: null) is not { } actor)
        {
            return Unauthorized();
        }

        var result = await _users.DeleteAsync(actor, id);
        return result.Succeeded ? NoContent() : ToResponse(result);
    }

    private UserAdministrationActor? Actor(string? currentPassword)
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId))
        {
            return null;
        }

        return new UserAdministrationActor(
            userId,
            User.Identity?.Name,
            currentPassword,
            User.FindFirst("auth_method")?.Value == AutheliaAuthenticationHandler.AuthMethod,
            _clientAddresses.Describe(HttpContext));
    }

    private ActionResult ToResponse(UserAdministrationResult result) => result.Outcome switch
    {
        UserAdministrationOutcome.Created => StatusCode(StatusCodes.Status201Created, result.User),
        UserAdministrationOutcome.Succeeded => Ok(result.User),
        UserAdministrationOutcome.Invalid => BadRequest(new { error = result.Error }),
        UserAdministrationOutcome.NotFound => NotFound(new { error = result.Error }),
        UserAdministrationOutcome.Conflict => Conflict(new { error = result.Error }),
        _ => StatusCode(StatusCodes.Status403Forbidden, new { error = result.Error }),
    };
}

/// <summary>The body of <c>POST /api/users</c>.</summary>
/// <param name="Username">At least three characters.</param>
/// <param name="Password">The new account's password.</param>
/// <param name="Email">Optional.</param>
/// <param name="FullName">Optional display name.</param>
/// <param name="Role"><c>Admin</c>, <c>User</c> or <c>ReadOnly</c>, in any case.</param>
/// <param name="CurrentPassword">The administrator's own password; not needed when signed in through Authelia.</param>
public record CreateUserRequest(
    string? Username,
    string? Password,
    string? Email = null,
    string? FullName = null,
    string? Role = null,
    string? CurrentPassword = null);

/// <summary>The body of <c>PUT /api/users/{id}/role</c>.</summary>
/// <param name="Role"><c>Admin</c>, <c>User</c> or <c>ReadOnly</c>, in any case.</param>
/// <param name="CurrentPassword">The administrator's own password; not needed when signed in through Authelia.</param>
public record ChangeUserRoleRequest(string? Role, string? CurrentPassword = null);

/// <summary>The body of <c>POST /api/users/{id}/password</c>.</summary>
/// <param name="NewPassword">The account's new password.</param>
/// <param name="CurrentPassword">The administrator's own password; not needed when signed in through Authelia.</param>
public record ResetUserPasswordRequest(string? NewPassword, string? CurrentPassword = null);

/// <summary>The body of <c>PUT /api/users/{id}/active</c>.</summary>
/// <param name="IsActive">Whether the account may sign in.</param>
/// <param name="CurrentPassword">The administrator's own password, needed to enable an account (not when signed in through Authelia).</param>
public record SetUserActiveRequest(bool? IsActive, string? CurrentPassword = null);
