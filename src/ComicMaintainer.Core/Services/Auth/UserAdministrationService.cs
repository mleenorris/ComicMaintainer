using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models.Auth;
using ComicMaintainer.Core.Utilities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.Core.Services.Auth;

/// <summary>
/// The default <see cref="IUserAdministrationService"/>, on Identity's <see cref="UserManager{TUser}"/> and the same
/// <see cref="ComicMaintainerDbContext"/> it saves through, so that deleting an account and its data is one
/// transaction. Every change, and every refusal, is logged to the auth log with the administrator who asked.
/// </summary>
public sealed class UserAdministrationService : IUserAdministrationService
{
    /// <summary>The roles an administrator can give, in order of how much they allow.</summary>
    public static readonly IReadOnlyList<string> Roles = ["Admin", "User", "ReadOnly"];

    /// <summary>The shortest username accepted, as in first-run setup.</summary>
    public const int MinimumUsernameLength = 3;

    /// <summary>The longest display name accepted.</summary>
    public const int MaximumFullNameLength = 256;

    public const string InvalidRoleError = "Choose a role: Admin, User or ReadOnly.";
    public const string UsernameTooShortError = "Username must be at least 3 characters.";
    public const string PasswordRequiredError = "Enter a password.";
    public const string FullNameTooLongError = "The full name can be at most 256 characters.";
    public const string DuplicateUsernameError = "An account with that username already exists.";
    public const string DuplicateEmailError = "Another account already uses that email address.";

    public const string OwnRoleError =
        "You cannot change your own role. Another administrator has to do it.";
    public const string OwnPasswordError =
        "You cannot set your own password here. Use Change Password in the account menu, which asks for your current password.";
    public const string OwnActiveError = "You cannot disable or enable your own account.";
    public const string OwnDeleteError = "You cannot delete your own account.";

    public const string LastAdministratorDemoteError =
        "This is the only active administrator, so it must keep the Admin role. Make another account an administrator first.";
    public const string LastAdministratorDisableError =
        "This is the only active administrator, so it cannot be disabled. Make another account an administrator first.";
    public const string LastAdministratorDeleteError =
        "This is the only active administrator, so it cannot be deleted. Make another account an administrator first.";

    public const string AutheliaCreateError =
        "Authelia is enabled, so accounts are created when someone first signs in through Authelia. Add the person in Authelia instead.";
    public const string AutheliaRoleError =
        "Authelia is enabled, so roles come from Authelia groups (AUTHELIA_ADMIN_GROUPS) and are set again on every request. Change the person's groups in Authelia instead.";
    public const string AutheliaPasswordError =
        "Authelia is enabled, so people sign in with their Authelia password. A password set here would only be a way around Authelia. Change it in Authelia instead.";

    private const string AdminRole = "Admin";
    private const string NormalizedAdminRole = "ADMIN";

    // One change at a time, so that two administrators demoting, disabling or deleting each other at the same moment
    // cannot both pass the last-administrator check.
    private static readonly SemaphoreSlim ChangeLock = new(1, 1);

    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ComicMaintainerDbContext _db;
    private readonly IAuthService _authService;
    private readonly IAuthSessionValidator _sessions;
    private readonly AutheliaSettings _autheliaSettings;
    private readonly ILogger<UserAdministrationService> _logger;

    public UserAdministrationService(
        UserManager<ApplicationUser> userManager,
        ComicMaintainerDbContext db,
        IAuthService authService,
        IAuthSessionValidator sessions,
        IOptions<AutheliaSettings> autheliaSettings,
        ILogger<UserAdministrationService> logger)
    {
        _userManager = userManager;
        _db = db;
        _authService = authService;
        _sessions = sessions;
        _autheliaSettings = autheliaSettings.Value;
        _logger = logger;
    }

    /// <summary>
    /// The role named <paramref name="role"/> as stored (<c>Admin</c>, <c>User</c> or <c>ReadOnly</c>), matched
    /// case-insensitively, or <see langword="null"/> when it is none of them.
    /// </summary>
    public static string? NormalizeRole(string? role) =>
        Roles.FirstOrDefault(known => string.Equals(known, role?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <inheritdoc />
    public async Task<UserAccountList> ListAsync(string currentUserId, CancellationToken cancellationToken = default)
    {
        var users = await _db.Users.AsNoTracking().ToListAsync(cancellationToken);
        var roles = await LoadRolesAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var summaries = users
            .Select(user => ToSummary(user, PrimaryRole(roles, user.Id), currentUserId, now))
            .OrderBy(user => user.Username, StringComparer.OrdinalIgnoreCase)
            .ThenBy(user => user.Username, StringComparer.Ordinal)
            .ToList();
        return new UserAccountList(_autheliaSettings.Enabled, summaries);
    }

    /// <inheritdoc />
    public async Task<UserAdministrationResult> CreateAsync(UserAdministrationActor actor, NewUserAccount account)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(account);

        if (_autheliaSettings.Enabled)
        {
            return Refuse(actor, "create an account", AutheliaCreateError);
        }

        var username = account.Username?.Trim() ?? string.Empty;
        if (username.Length < MinimumUsernameLength)
        {
            return UserAdministrationResult.Invalid(UsernameTooShortError);
        }

        if (string.IsNullOrEmpty(account.Password))
        {
            return UserAdministrationResult.Invalid(PasswordRequiredError);
        }

        var role = NormalizeRole(account.Role);
        if (role is null)
        {
            return UserAdministrationResult.Invalid(InvalidRoleError);
        }

        var fullName = string.IsNullOrWhiteSpace(account.FullName) ? null : account.FullName.Trim();
        if (fullName?.Length > MaximumFullNameLength)
        {
            return UserAdministrationResult.Invalid(FullNameTooLongError);
        }

        var email = string.IsNullOrWhiteSpace(account.Email) ? $"{username}@comicmaintainer.local" : account.Email.Trim();

        await ChangeLock.WaitAsync();
        try
        {
            if (await _userManager.FindByNameAsync(username) is not null)
            {
                return UserAdministrationResult.Conflict(DuplicateUsernameError);
            }

            if (await _userManager.FindByEmailAsync(email) is not null)
            {
                return UserAdministrationResult.Conflict(DuplicateEmailError);
            }

            if (await ConfirmActorAsync(actor) is { } refused)
            {
                return refused;
            }

            var user = new ApplicationUser { UserName = username, Email = email, FullName = fullName, IsActive = true };
            await using (var transaction = await _db.Database.BeginTransactionAsync())
            {
                var created = await _userManager.CreateAsync(user, account.Password);
                if (!created.Succeeded)
                {
                    return FromIdentityErrors(created);
                }

                EnsureSucceeded(await _userManager.AddToRoleAsync(user, role), "give the new account its role");
                await transaction.CommitAsync();
            }

            _logger.LogInformation(
                "{Administrator} created the account {Username} with the role {Role}",
                DescribeActor(actor), LoggingHelper.SanitizeForLog(username), role);
            return UserAdministrationResult.Created(ToSummary(user, role, actor.UserId, DateTimeOffset.UtcNow));
        }
        finally
        {
            ChangeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<UserAdministrationResult> ChangeRoleAsync(UserAdministrationActor actor, string userId, string? role)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (_autheliaSettings.Enabled)
        {
            return Refuse(actor, "change the role of the account " + LoggingHelper.SanitizeForLog(userId), AutheliaRoleError);
        }

        var newRole = NormalizeRole(role);
        if (newRole is null)
        {
            return UserAdministrationResult.Invalid(InvalidRoleError);
        }

        await ChangeLock.WaitAsync();
        try
        {
            var user = await FindAsync(userId);
            if (user is null)
            {
                return UserAdministrationResult.NotFound();
            }

            var target = LogName(user);
            if (IsActor(actor, user))
            {
                return Refuse(actor, "change their own role", OwnRoleError);
            }

            var currentRoles = await _userManager.GetRolesAsync(user);
            if (newRole != AdminRole && await IsLastActiveAdministratorAsync(user, currentRoles))
            {
                return Refuse(actor, "demote " + target, LastAdministratorDemoteError);
            }

            if (await ConfirmActorAsync(actor) is { } refused)
            {
                return refused;
            }

            if (currentRoles.Count == 1 && currentRoles[0] == newRole)
            {
                return UserAdministrationResult.Done(await SummarizeAsync(user, actor));
            }

            await using (var transaction = await _db.Database.BeginTransactionAsync())
            {
                if (currentRoles.Count > 0)
                {
                    EnsureSucceeded(await _userManager.RemoveFromRolesAsync(user, currentRoles), "remove the account's roles");
                }

                EnsureSucceeded(await _userManager.AddToRoleAsync(user, newRole), "give the account its new role");

                // Session tokens carry the roles they were issued with: a new security stamp ends them, so the account
                // cannot keep acting with its old role.
                EnsureSucceeded(await _userManager.UpdateSecurityStampAsync(user), "end the account's sessions");
                await transaction.CommitAsync();
            }

            _sessions.Invalidate(user.Id);
            _logger.LogInformation(
                "{Administrator} changed the role of {Username} from {OldRole} to {NewRole}; its sessions are signed out",
                DescribeActor(actor), target, currentRoles.Count == 0 ? "none" : string.Join(", ", currentRoles.Order(StringComparer.Ordinal)), newRole);
            return UserAdministrationResult.Done(await SummarizeAsync(user, actor));
        }
        finally
        {
            ChangeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<UserAdministrationResult> ResetPasswordAsync(UserAdministrationActor actor, string userId, string? newPassword)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (_autheliaSettings.Enabled)
        {
            return Refuse(actor, "set the password of the account " + LoggingHelper.SanitizeForLog(userId), AutheliaPasswordError);
        }

        if (string.IsNullOrEmpty(newPassword))
        {
            return UserAdministrationResult.Invalid(PasswordRequiredError);
        }

        await ChangeLock.WaitAsync();
        try
        {
            var user = await FindAsync(userId);
            if (user is null)
            {
                return UserAdministrationResult.NotFound();
            }

            var target = LogName(user);
            if (IsActor(actor, user))
            {
                return Refuse(actor, "set their own password", OwnPasswordError);
            }

            if (await ConfirmActorAsync(actor) is { } refused)
            {
                return refused;
            }

            // One write: the new password with a new security stamp (which ends the account's sessions) and no API
            // key, as in a password change, so whoever had the account's access loses all of it; and no lockout, so
            // that its owner can sign in with the new password straight away.
            var before = Snapshot(user);
            ClearApiKey(user);
            user.LockoutEnd = null;
            user.AccessFailedCount = 0;
            var token = await _userManager.GeneratePasswordResetTokenAsync(user);
            var reset = await _userManager.ResetPasswordAsync(user, token, newPassword);
            if (!reset.Succeeded)
            {
                Restore(user, before);
                return UserAdministrationResult.Invalid(DescribeErrors(reset));
            }

            _sessions.Invalidate(user.Id);
            _logger.LogInformation(
                "{Administrator} set a new password for {Username}; its sessions are signed out, its API key is revoked and any lockout is cleared",
                DescribeActor(actor), target);
            return UserAdministrationResult.Done(await SummarizeAsync(user, actor));
        }
        finally
        {
            ChangeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<UserAdministrationResult> SetActiveAsync(UserAdministrationActor actor, string userId, bool isActive)
    {
        ArgumentNullException.ThrowIfNull(actor);

        await ChangeLock.WaitAsync();
        try
        {
            var user = await FindAsync(userId);
            if (user is null)
            {
                return UserAdministrationResult.NotFound();
            }

            var target = LogName(user);
            if (IsActor(actor, user))
            {
                return Refuse(actor, isActive ? "enable their own account" : "disable their own account", OwnActiveError);
            }

            return isActive ? await EnableAsync(actor, user, target) : await DisableAsync(actor, user, target);
        }
        finally
        {
            ChangeLock.Release();
        }
    }

    private async Task<UserAdministrationResult> EnableAsync(UserAdministrationActor actor, ApplicationUser user, string target)
    {
        if (await ConfirmActorAsync(actor) is { } refused)
        {
            return refused;
        }

        if (!user.IsActive)
        {
            user.IsActive = true;
            EnsureSucceeded(await _userManager.UpdateAsync(user), "enable the account");
            _sessions.Invalidate(user.Id);
            _logger.LogInformation("{Administrator} enabled the account {Username}", DescribeActor(actor), target);
        }

        return UserAdministrationResult.Done(await SummarizeAsync(user, actor));
    }

    private async Task<UserAdministrationResult> DisableAsync(UserAdministrationActor actor, ApplicationUser user, string target)
    {
        if (await IsLastActiveAdministratorAsync(user, await _userManager.GetRolesAsync(user)))
        {
            return Refuse(actor, "disable " + target, LastAdministratorDisableError);
        }

        if (user.IsActive)
        {
            // A disabled account is refused on every request anyway. A new security stamp and no API key also make
            // sure that enabling it again does not bring back sessions or a key someone may have taken.
            user.IsActive = false;
            ClearApiKey(user);
            EnsureSucceeded(await _userManager.UpdateSecurityStampAsync(user), "disable the account");
            _sessions.Invalidate(user.Id);
            _logger.LogInformation(
                "{Administrator} disabled the account {Username}; its sessions are signed out and its API key is revoked",
                DescribeActor(actor), target);
        }

        return UserAdministrationResult.Done(await SummarizeAsync(user, actor));
    }

    /// <inheritdoc />
    public async Task<UserAdministrationResult> DeleteAsync(UserAdministrationActor actor, string userId)
    {
        ArgumentNullException.ThrowIfNull(actor);

        await ChangeLock.WaitAsync();
        try
        {
            var user = await FindAsync(userId);
            if (user is null)
            {
                return UserAdministrationResult.NotFound();
            }

            var target = LogName(user);
            if (IsActor(actor, user))
            {
                return Refuse(actor, "delete their own account", OwnDeleteError);
            }

            if (await IsLastActiveAdministratorAsync(user, await _userManager.GetRolesAsync(user)))
            {
                return Refuse(actor, "delete " + target, LastAdministratorDeleteError);
            }

            int devices;
            int deliveries;
            await using (var transaction = await _db.Database.BeginTransactionAsync())
            {
                (devices, deliveries) = await DeleteOwnDataAsync(user.Id);
                EnsureSucceeded(await _userManager.DeleteAsync(user), "delete the account");
                await transaction.CommitAsync();
            }

            _sessions.Invalidate(user.Id);
            _logger.LogInformation(
                "{Administrator} deleted the account {Username} with its reading progress, read status, preferences, " +
                "e-reader devices ({DeviceCount}) and email deliveries ({DeliveryCount})",
                DescribeActor(actor), target, devices, deliveries);
            return UserAdministrationResult.Done(null);
        }
        finally
        {
            ChangeLock.Release();
        }
    }

    // Everything that is the account's alone. Devices and deliveries without an owner (saved before they had owners,
    // when no administrator existed to inherit them) belong to no one account and are shared by the administrators,
    // so they stay; so does everything other accounts own. Identity's own rows (roles, claims, logins, tokens) go
    // with the account through their cascading foreign keys.
    private async Task<(int Devices, int Deliveries)> DeleteOwnDataAsync(string userId)
    {
        var ownDevices = _db.EreaderDevices.Where(d => d.OwnerUserId != null && d.OwnerUserId == userId);
        var ownDeviceIds = ownDevices.Select(d => d.Id);
        await _db.SeriesEmailSubscriptions.Where(s => ownDeviceIds.Contains(s.DeviceId)).ExecuteDeleteAsync();
        var deliveries = await _db.ComicEmailDeliveries.Where(d => d.OwnerUserId != null && d.OwnerUserId == userId).ExecuteDeleteAsync();
        var devices = await ownDevices.ExecuteDeleteAsync();

        await _db.ReadingProgresses.Where(p => p.UserId == userId).ExecuteDeleteAsync();
        await _db.ReadingSessions.Where(s => s.UserId == userId).ExecuteDeleteAsync();
        await _db.UserFileReadStatuses.Where(s => s.UserId == userId).ExecuteDeleteAsync();
        await _db.ReaderPreferences.Where(p => p.UserId == userId).ExecuteDeleteAsync();
        await _db.UserPreferences.Where(p => p.UserId == userId).ExecuteDeleteAsync();
        await _db.RevokedTokens.Where(t => t.UserId == userId).ExecuteDeleteAsync();
        return (devices, deliveries);
    }

    // Asks for the administrator's own password, unless they came through Authelia. Called outside any transaction:
    // a wrong password counts towards the administrator's lockout, which must not be rolled back with the change.
    private async Task<UserAdministrationResult?> ConfirmActorAsync(UserAdministrationActor actor)
    {
        if (actor.SignedInThroughAuthelia)
        {
            return null;
        }

        var (confirmed, error) = await _authService.ConfirmPasswordAsync(actor.UserId, actor.CurrentPassword, actor.ClientAddress);
        return confirmed ? null : UserAdministrationResult.PasswordNotConfirmed(error);
    }

    private UserAdministrationResult Refuse(UserAdministrationActor actor, string change, string error)
    {
        _logger.LogInformation("Refused to let {Administrator} {Change}: {Reason}", DescribeActor(actor), change, error);
        return UserAdministrationResult.Conflict(error);
    }

    // Whether the account is an active administrator and no other is.
    private async Task<bool> IsLastActiveAdministratorAsync(ApplicationUser user, IList<string> roles)
    {
        if (!user.IsActive || !roles.Contains(AdminRole))
        {
            return false;
        }

        var activeAdministrators = await (
                from account in _db.Users
                join userRole in _db.UserRoles on account.Id equals userRole.UserId
                join role in _db.Roles on userRole.RoleId equals role.Id
                where account.IsActive && role.NormalizedName == NormalizedAdminRole
                select account.Id)
            .Distinct()
            .CountAsync();
        return activeAdministrators <= 1;
    }

    private async Task<ApplicationUser?> FindAsync(string userId) =>
        string.IsNullOrEmpty(userId) ? null : await _userManager.FindByIdAsync(userId);

    private static bool IsActor(UserAdministrationActor actor, ApplicationUser user) =>
        string.Equals(actor.UserId, user.Id, StringComparison.Ordinal);

    private async Task<Dictionary<string, List<string>>> LoadRolesAsync(CancellationToken cancellationToken)
    {
        var memberships = await (
                from userRole in _db.UserRoles
                join role in _db.Roles on userRole.RoleId equals role.Id
                select new { userRole.UserId, role.Name })
            .ToListAsync(cancellationToken);
        return memberships
            .Where(m => m.Name is not null)
            .GroupBy(m => m.UserId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(m => m.Name!).ToList(), StringComparer.Ordinal);
    }

    // The role an account reports: the one with the most access of the known roles, else any other it has.
    private static string? PrimaryRole(Dictionary<string, List<string>> roles, string userId) =>
        roles.TryGetValue(userId, out var names) ? PrimaryRole(names) : null;

    private static string? PrimaryRole(IList<string> names) =>
        Roles.FirstOrDefault(names.Contains) ?? names.Order(StringComparer.Ordinal).FirstOrDefault();

    private async Task<UserAccountSummary> SummarizeAsync(ApplicationUser user, UserAdministrationActor actor) =>
        ToSummary(user, PrimaryRole(await _userManager.GetRolesAsync(user)), actor.UserId, DateTimeOffset.UtcNow);

    private static UserAccountSummary ToSummary(ApplicationUser user, string? role, string currentUserId, DateTimeOffset now) => new(
        user.Id,
        user.UserName ?? string.Empty,
        user.FullName,
        user.Email,
        role,
        user.IsActive,
        user.LockoutEnd is { } end && end > now ? end.UtcDateTime : null,
        DateTime.SpecifyKind(user.CreatedAt, DateTimeKind.Utc),
        user.LastLoginAt is { } lastLogin ? DateTime.SpecifyKind(lastLogin, DateTimeKind.Utc) : null,
        user.ApiKeyHash is not null,
        string.Equals(user.Id, currentUserId, StringComparison.Ordinal));

    private static void ClearApiKey(ApplicationUser user)
    {
        user.ApiKeyHash = null;
        user.ApiKeyCreatedAt = null;
        user.ApiKeyLastUsedAt = null;
    }

    private static AccountState Snapshot(ApplicationUser user) =>
        new(user.ApiKeyHash, user.ApiKeyCreatedAt, user.ApiKeyLastUsedAt, user.LockoutEnd, user.AccessFailedCount);

    // Puts back what a refused change set on the tracked account, so that nothing saved later in the request writes it.
    private static void Restore(ApplicationUser user, AccountState state)
    {
        user.ApiKeyHash = state.ApiKeyHash;
        user.ApiKeyCreatedAt = state.ApiKeyCreatedAt;
        user.ApiKeyLastUsedAt = state.ApiKeyLastUsedAt;
        user.LockoutEnd = state.LockoutEnd;
        user.AccessFailedCount = state.AccessFailedCount;
    }

    private readonly record struct AccountState(
        string? ApiKeyHash, DateTime? ApiKeyCreatedAt, DateTime? ApiKeyLastUsedAt, DateTimeOffset? LockoutEnd, int AccessFailedCount);

    private static UserAdministrationResult FromIdentityErrors(IdentityResult result)
    {
        var codes = result.Errors.Select(e => e.Code).ToHashSet(StringComparer.Ordinal);
        if (codes.Contains(nameof(IdentityErrorDescriber.DuplicateUserName)))
        {
            return UserAdministrationResult.Conflict(DuplicateUsernameError);
        }

        if (codes.Contains(nameof(IdentityErrorDescriber.DuplicateEmail)))
        {
            return UserAdministrationResult.Conflict(DuplicateEmailError);
        }

        return UserAdministrationResult.Invalid(DescribeErrors(result));
    }

    private static string DescribeErrors(IdentityResult result) => string.Join(" ", result.Errors.Select(e => e.Description));

    // Identity only fails these for a broken database or a missing role, which no request can fix: the global exception
    // handler answers, and the transaction rolls back.
    private static void EnsureSucceeded(IdentityResult result, string what)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"Could not {what}: {DescribeErrors(result)}");
        }
    }

    private static string LogName(ApplicationUser user) => LoggingHelper.SanitizeForLog(user.UserName ?? user.Id);

    private static string DescribeActor(UserAdministrationActor actor) =>
        LoggingHelper.SanitizeForLog(string.IsNullOrEmpty(actor.Username) ? actor.UserId : actor.Username);
}
