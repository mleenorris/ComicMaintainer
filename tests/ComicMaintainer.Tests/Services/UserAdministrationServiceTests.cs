using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models.Auth;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Core.Services.Auth;
using ComicMaintainer.Tests.Helpers;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Account administration (<see cref="UserAdministrationService"/>) against the real Identity stack and database: what
/// each change does to the account, its sessions, API key and data, the rules that refuse a change, and the
/// administrator's own password that changes granting lasting access need.
/// </summary>
public sealed class UserAdministrationServiceTests : IAsyncLifetime
{
    private const string Password = "Corr3ctHorse";
    private const string NewPassword = "N3wBattery9";
    private const string ClientAddress = "203.0.113.9";

    private AuthTestHost _host = null!;
    private ApplicationUser _admin = null!;

    public async Task InitializeAsync()
    {
        _host = await AuthTestHost.StartAsync();
        _admin = await _host.CreateUserAsync("admin", Password, "Admin");
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    // ── List ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task List_SortsByUsername_MarksTheCaller_AndReportsEachAccountsState()
    {
        var zed = await _host.CreateUserAsync("Zed", Password, "ReadOnly");
        var bob = await _host.CreateUserAsync("bob", Password);
        await ChangeAsync(bob.Id, async (users, user) =>
        {
            user.FullName = "Bob Builder";
            user.ApiKeyHash = ApiKeys.Hash(ApiKeys.Generate());
            user.LastLoginAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            user.LockoutEnd = DateTimeOffset.UtcNow.AddMinutes(10);
            return await users.UpdateAsync(user);
        });
        await ChangeAsync(zed.Id, async (users, user) =>
        {
            user.IsActive = false;
            user.LockoutEnd = DateTimeOffset.UtcNow.AddMinutes(-10);
            return await users.UpdateAsync(user);
        });

        var list = await WithServiceAsync(service => service.ListAsync(_admin.Id));

        Assert.False(list.AutheliaEnabled);
        Assert.Equal(["admin", "bob", "Zed"], list.Users.Select(u => u.Username));
        var admin = list.Users[0];
        Assert.True(admin.IsCurrentUser);
        Assert.Equal("Admin", admin.Role);
        Assert.False(admin.HasApiKey);
        Assert.Null(admin.LastLoginAt);
        Assert.Equal(DateTimeKind.Utc, admin.CreatedAt.Kind);

        var bobSummary = list.Users[1];
        Assert.False(bobSummary.IsCurrentUser);
        Assert.Equal("User", bobSummary.Role);
        Assert.Equal("Bob Builder", bobSummary.FullName);
        Assert.Equal("bob@example.com", bobSummary.Email);
        Assert.True(bobSummary.HasApiKey);
        Assert.True(bobSummary.IsActive);
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), bobSummary.LastLoginAt);
        Assert.Equal(DateTimeKind.Utc, bobSummary.LastLoginAt!.Value.Kind);
        Assert.NotNull(bobSummary.LockedOutUntil);
        Assert.InRange(bobSummary.LockedOutUntil!.Value, DateTime.UtcNow.AddMinutes(9), DateTime.UtcNow.AddMinutes(11));

        // A lockout that has ended is not reported.
        var zedSummary = list.Users[2];
        Assert.Equal("ReadOnly", zedSummary.Role);
        Assert.False(zedSummary.IsActive);
        Assert.Null(zedSummary.LockedOutUntil);
    }

    [Fact]
    public async Task List_ReportsTheRoleWithTheMostAccess_AndNoneForAnAccountWithoutARole()
    {
        var both = await _host.CreateUserAsync("both", Password, "ReadOnly");
        await ChangeAsync(both.Id, (users, user) => users.AddToRoleAsync(user, "Admin"));
        await _host.InScopeAsync(async services =>
        {
            var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
            return await roles.CreateAsync(new ApplicationRole { Name = "Custom" });
        });
        var custom = await _host.CreateUserAsync("custom", Password, "Custom");
        var none = await _host.CreateUserAsync("none", Password);
        await ChangeAsync(none.Id, (users, user) => users.RemoveFromRoleAsync(user, "User"));

        var list = await WithServiceAsync(service => service.ListAsync(_admin.Id));

        Assert.Equal("Admin", list.Users.Single(u => u.Id == both.Id).Role);
        Assert.Equal("Custom", list.Users.Single(u => u.Id == custom.Id).Role);
        Assert.Null(list.Users.Single(u => u.Id == none.Id).Role);
    }

    [Fact]
    public async Task List_SaysWhenAutheliaIsEnabled()
    {
        await using var authelia = await StartAutheliaHostAsync();

        var list = await authelia.InScopeAsync(services => services.GetRequiredService<IUserAdministrationService>().ListAsync("nobody"));

        Assert.True(list.AutheliaEnabled);
        Assert.Empty(list.Users);
    }

    // ── Create ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_MakesAnActiveAccountWithTheRole_ThatCanSignIn()
    {
        var result = await WithServiceAsync(service => service.CreateAsync(
            Actor(),
            new NewUserAccount("  reader  ", NewPassword, " reader@example.test ", " Rita Reader ", "readonly")));

        Assert.Equal(UserAdministrationOutcome.Created, result.Outcome);
        Assert.True(result.Succeeded);
        var created = Assert.IsType<UserAccountSummary>(result.User);
        Assert.Equal("reader", created.Username);
        Assert.Equal("reader@example.test", created.Email);
        Assert.Equal("Rita Reader", created.FullName);
        Assert.Equal("ReadOnly", created.Role);
        Assert.True(created.IsActive);
        Assert.False(created.IsCurrentUser);
        Assert.Equal(["ReadOnly"], await GetRolesAsync(created.Id));
        Assert.True((await _host.WithAuthServiceAsync(auth => auth.LoginAsync("reader", NewPassword))).Success);
        Assert.Contains(AdministrationLog(), e => e.Level == LogLevel.Information
            && e.Message == "admin created the account reader with the role ReadOnly");
        Assert.DoesNotContain(_host.Logs.Entries, e => e.Message.Contains(NewPassword, StringComparison.Ordinal) || e.Message.Contains(Password, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Create_WithoutAnEmail_UsesTheSameDefaultAsFirstRunSetup()
    {
        var result = await WithServiceAsync(service => service.CreateAsync(Actor(), new NewUserAccount("carol", NewPassword, null, "  ", "User")));

        Assert.Equal(UserAdministrationOutcome.Created, result.Outcome);
        Assert.Equal("carol@comicmaintainer.local", result.User!.Email);
        Assert.Null(result.User.FullName);
    }

    [Theory]
    [InlineData("ab", NewPassword, "User", UserAdministrationService.UsernameTooShortError)]
    [InlineData("   ", NewPassword, "User", UserAdministrationService.UsernameTooShortError)]
    [InlineData(null, NewPassword, "User", UserAdministrationService.UsernameTooShortError)]
    [InlineData("dave", "", "User", UserAdministrationService.PasswordRequiredError)]
    [InlineData("dave", null, "User", UserAdministrationService.PasswordRequiredError)]
    [InlineData("dave", NewPassword, "Owner", UserAdministrationService.InvalidRoleError)]
    [InlineData("dave", NewPassword, null, UserAdministrationService.InvalidRoleError)]
    public async Task Create_RefusesAnInvalidRequest(string? username, string? password, string? role, string error)
    {
        var result = await WithServiceAsync(service => service.CreateAsync(Actor(), new NewUserAccount(username, password, null, null, role)));

        Assert.Equal(UserAdministrationOutcome.Invalid, result.Outcome);
        Assert.Equal(error, result.Error);
        Assert.Null(await _host.FindUserAsync("dave"));
    }

    [Fact]
    public async Task Create_RefusesAFullNameThatIsTooLong()
    {
        var result = await WithServiceAsync(service => service.CreateAsync(
            Actor(), new NewUserAccount("dave", NewPassword, null, new string('x', UserAdministrationService.MaximumFullNameLength + 1), "User")));

        Assert.Equal(UserAdministrationOutcome.Invalid, result.Outcome);
        Assert.Equal(UserAdministrationService.FullNameTooLongError, result.Error);
    }

    [Theory]
    [InlineData("weakpass", "dave", null, "Passwords must have at least one digit")]
    [InlineData(NewPassword, "dave", "not-an-email", "Email 'not-an-email' is invalid.")]
    [InlineData(NewPassword, "da ve", null, "Username 'da ve' is invalid")]
    public async Task Create_ReturnsIdentitysReasons(string password, string username, string? email, string reason)
    {
        var result = await WithServiceAsync(service => service.CreateAsync(Actor(), new NewUserAccount(username, password, email, null, "User")));

        Assert.Equal(UserAdministrationOutcome.Invalid, result.Outcome);
        Assert.Contains(reason, result.Error, StringComparison.Ordinal);
        Assert.Null(await _host.FindUserAsync(username));
    }

    [Fact]
    public async Task Create_RefusesAUsernameOrEmailAnotherAccountHas()
    {
        await _host.CreateUserAsync("erin", Password);

        var sameName = await WithServiceAsync(service => service.CreateAsync(Actor(), new NewUserAccount("ERIN", NewPassword, null, null, "User")));
        var sameEmail = await WithServiceAsync(service => service.CreateAsync(Actor(), new NewUserAccount("erin2", NewPassword, "Erin@Example.com", null, "User")));

        Assert.Equal(UserAdministrationOutcome.Conflict, sameName.Outcome);
        Assert.Equal(UserAdministrationService.DuplicateUsernameError, sameName.Error);
        Assert.Equal(UserAdministrationOutcome.Conflict, sameEmail.Outcome);
        Assert.Equal(UserAdministrationService.DuplicateEmailError, sameEmail.Error);
        Assert.Null(await _host.FindUserAsync("erin2"));
    }

    [Theory]
    [InlineData(null, AuthService.WrongCurrentPasswordError)]
    [InlineData("Wr0ngGuess", AuthService.WrongCurrentPasswordError)]
    public async Task Create_NeedsTheAdministratorsOwnPassword_AndAWrongOneCountsTowardsTheirLockout(string? password, string error)
    {
        var result = await WithServiceAsync(service => service.CreateAsync(Actor(password), new NewUserAccount("frank", NewPassword, null, null, "Admin")));

        Assert.Equal(UserAdministrationOutcome.PasswordNotConfirmed, result.Outcome);
        Assert.Equal(error, result.Error);
        Assert.Null(await _host.FindUserAsync("frank"));
        Assert.Equal(password is null ? 0 : 1, (await _host.FindUserAsync("admin"))!.AccessFailedCount);
    }

    [Fact]
    public async Task Create_IsRefusedWhileTheAdministratorIsLockedOut()
    {
        await ChangeAsync(_admin.Id, async (users, user) => await users.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddMinutes(5)));

        var result = await WithServiceAsync(service => service.CreateAsync(Actor(), new NewUserAccount("frank", NewPassword, null, null, "User")));

        Assert.Equal(UserAdministrationOutcome.PasswordNotConfirmed, result.Outcome);
        Assert.Equal(AuthService.AccountLockedOutError, result.Error);
    }

    [Fact]
    public async Task Create_IsRefusedWhileAutheliaIsEnabled()
    {
        await using var authelia = await StartAutheliaHostAsync();
        var admin = await authelia.CreateUserAsync("admin", Password, "Admin");

        var result = await authelia.InScopeAsync(services => services.GetRequiredService<IUserAdministrationService>()
            .CreateAsync(Actor(admin, null, autheliaSession: true), new NewUserAccount("grace", NewPassword, null, null, "User")));

        Assert.Equal(UserAdministrationOutcome.Conflict, result.Outcome);
        Assert.Equal(UserAdministrationService.AutheliaCreateError, result.Error);
        Assert.Null(await authelia.FindUserAsync("grace"));
    }

    // ── Change role ───────────────────────────────────────────────────────

    [Fact]
    public async Task ChangeRole_ReplacesTheRoles_AndEndsTheAccountsSessions()
    {
        var user = await _host.CreateUserAsync("henry", Password, "ReadOnly");
        await ChangeAsync(user.Id, (users, account) => users.AddToRoleAsync(account, "User"));
        var stamp = (await _host.FindUserAsync("henry"))!.SecurityStamp;

        var result = await WithServiceAsync(service => service.ChangeRoleAsync(Actor(), user.Id, "admin"));

        Assert.Equal(UserAdministrationOutcome.Succeeded, result.Outcome);
        Assert.Equal("Admin", result.User!.Role);
        Assert.Equal(["Admin"], await GetRolesAsync(user.Id));
        Assert.NotEqual(stamp, (await _host.FindUserAsync("henry"))!.SecurityStamp);
        Assert.Contains(AdministrationLog(), e => e.Level == LogLevel.Information
            && e.Message == "admin changed the role of henry from ReadOnly, User to Admin; its sessions are signed out");
    }

    [Fact]
    public async Task ChangeRole_GivesARoleToAnAccountWithout()
    {
        var user = await _host.CreateUserAsync("ivy", Password);
        await ChangeAsync(user.Id, (users, account) => users.RemoveFromRoleAsync(account, "User"));

        var result = await WithServiceAsync(service => service.ChangeRoleAsync(Actor(), user.Id, "User"));

        Assert.Equal(UserAdministrationOutcome.Succeeded, result.Outcome);
        Assert.Equal(["User"], await GetRolesAsync(user.Id));
        Assert.Contains(AdministrationLog(), e => e.Message == "admin changed the role of ivy from none to User; its sessions are signed out");
    }

    [Fact]
    public async Task ChangeRole_ToTheRoleTheAccountHas_ChangesNothing()
    {
        var user = await _host.CreateUserAsync("jack", Password);
        var stamp = (await _host.FindUserAsync("jack"))!.SecurityStamp;

        var result = await WithServiceAsync(service => service.ChangeRoleAsync(Actor(), user.Id, "User"));

        Assert.Equal(UserAdministrationOutcome.Succeeded, result.Outcome);
        Assert.Equal("User", result.User!.Role);
        Assert.Equal(stamp, (await _host.FindUserAsync("jack"))!.SecurityStamp);
    }

    [Fact]
    public async Task ChangeRole_RefusesTheAdministratorsOwnAccount()
    {
        var result = await WithServiceAsync(service => service.ChangeRoleAsync(Actor(), _admin.Id, "User"));

        Assert.Equal(UserAdministrationOutcome.Conflict, result.Outcome);
        Assert.Equal(UserAdministrationService.OwnRoleError, result.Error);
        Assert.Equal(["Admin"], await GetRolesAsync(_admin.Id));
        Assert.Contains(AdministrationLog(), e => e.Message.StartsWith("Refused to let admin change their own role", StringComparison.Ordinal));
    }

    // Through the API the caller is itself an active administrator, so the last one can only be reached by a caller
    // who administers without the Admin role: Authelia with no admin groups lets every non-ReadOnly account administer.
    [Fact]
    public async Task ChangeRole_NeverDemotesTheLastActiveAdministrator()
    {
        var caller = await _host.CreateUserAsync("kim", Password);
        var disabledAdmin = await _host.CreateUserAsync("former", Password, "Admin");
        await ChangeAsync(disabledAdmin.Id, async (users, user) =>
        {
            user.IsActive = false;
            return await users.UpdateAsync(user);
        });

        var result = await WithServiceAsync(service => service.ChangeRoleAsync(Actor(caller), _admin.Id, "ReadOnly"));

        Assert.Equal(UserAdministrationOutcome.Conflict, result.Outcome);
        Assert.Equal(UserAdministrationService.LastAdministratorDemoteError, result.Error);
        Assert.Equal(["Admin"], await GetRolesAsync(_admin.Id));
    }

    [Fact]
    public async Task ChangeRole_DemotesAnAdministratorWhileAnotherIsActive()
    {
        var other = await _host.CreateUserAsync("leo", Password, "Admin");

        var result = await WithServiceAsync(service => service.ChangeRoleAsync(Actor(), other.Id, "User"));

        Assert.Equal(UserAdministrationOutcome.Succeeded, result.Outcome);
        Assert.Equal(["User"], await GetRolesAsync(other.Id));
    }

    [Fact]
    public async Task ChangeRole_NeedsTheAdministratorsOwnPassword()
    {
        var user = await _host.CreateUserAsync("mia", Password);

        var result = await WithServiceAsync(service => service.ChangeRoleAsync(Actor("Wr0ngGuess"), user.Id, "Admin"));

        Assert.Equal(UserAdministrationOutcome.PasswordNotConfirmed, result.Outcome);
        Assert.Equal(["User"], await GetRolesAsync(user.Id));
    }

    [Fact]
    public async Task ChangeRole_RefusesAnUnknownRoleOrAccount()
    {
        var user = await _host.CreateUserAsync("ned", Password);

        var badRole = await WithServiceAsync(service => service.ChangeRoleAsync(Actor(), user.Id, "Owner"));
        var unknown = await WithServiceAsync(service => service.ChangeRoleAsync(Actor(), "no-such-id", "User"));
        var empty = await WithServiceAsync(service => service.ChangeRoleAsync(Actor(), "", "User"));

        Assert.Equal(UserAdministrationOutcome.Invalid, badRole.Outcome);
        Assert.Equal(UserAdministrationService.InvalidRoleError, badRole.Error);
        Assert.Equal(UserAdministrationOutcome.NotFound, unknown.Outcome);
        Assert.Equal(UserAdministrationOutcome.NotFound, empty.Outcome);
    }

    [Fact]
    public async Task ChangeRole_IsRefusedWhileAutheliaIsEnabled()
    {
        await using var authelia = await StartAutheliaHostAsync();
        var admin = await authelia.CreateUserAsync("admin", Password, "Admin");
        var user = await authelia.CreateUserAsync("olive", Password);

        var result = await authelia.InScopeAsync(services => services.GetRequiredService<IUserAdministrationService>()
            .ChangeRoleAsync(Actor(admin, null, autheliaSession: true), user.Id, "Admin"));

        Assert.Equal(UserAdministrationOutcome.Conflict, result.Outcome);
        Assert.Equal(UserAdministrationService.AutheliaRoleError, result.Error);
    }

    // ── Reset password ────────────────────────────────────────────────────

    [Fact]
    public async Task ResetPassword_SetsThePassword_ClearsTheLockout_RevokesTheApiKey_AndEndsTheSessions()
    {
        var user = await _host.CreateUserAsync("paul", Password);
        await ChangeAsync(user.Id, async (users, account) =>
        {
            account.ApiKeyHash = ApiKeys.Hash(ApiKeys.Generate());
            account.ApiKeyCreatedAt = DateTime.UtcNow;
            account.AccessFailedCount = 3;
            account.LockoutEnd = DateTimeOffset.UtcNow.AddMinutes(10);
            return await users.UpdateAsync(account);
        });
        var stamp = (await _host.FindUserAsync("paul"))!.SecurityStamp;

        var result = await WithServiceAsync(service => service.ResetPasswordAsync(Actor(), user.Id, NewPassword));

        Assert.Equal(UserAdministrationOutcome.Succeeded, result.Outcome);
        Assert.False(result.User!.HasApiKey);
        Assert.Null(result.User.LockedOutUntil);
        var saved = (await _host.FindUserAsync("paul"))!;
        Assert.Null(saved.ApiKeyHash);
        Assert.Null(saved.ApiKeyCreatedAt);
        Assert.Null(saved.LockoutEnd);
        Assert.Equal(0, saved.AccessFailedCount);
        Assert.NotEqual(stamp, saved.SecurityStamp);
        Assert.True((await _host.WithAuthServiceAsync(auth => auth.LoginAsync("paul", NewPassword))).Success);
        Assert.False((await _host.WithAuthServiceAsync(auth => auth.LoginAsync("paul", Password))).Success);
        Assert.Contains(AdministrationLog(), e => e.Level == LogLevel.Information
            && e.Message == "admin set a new password for paul; its sessions are signed out, its API key is revoked and any lockout is cleared");
        Assert.DoesNotContain(_host.Logs.Entries, e => e.Message.Contains(NewPassword, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResetPassword_RefusesAPasswordThatBreaksTheRules_AndChangesNothing()
    {
        var user = await _host.CreateUserAsync("quinn", Password);
        var key = ApiKeys.Hash(ApiKeys.Generate());
        await ChangeAsync(user.Id, async (users, account) =>
        {
            account.ApiKeyHash = key;
            account.AccessFailedCount = 2;
            return await users.UpdateAsync(account);
        });

        var weak = await WithServiceAsync(service => service.ResetPasswordAsync(Actor(), user.Id, "short"));
        var published = await WithServiceAsync(service => service.ResetPasswordAsync(Actor(), user.Id, "Admin123!"));
        var missing = await WithServiceAsync(service => service.ResetPasswordAsync(Actor(), user.Id, null));

        Assert.Equal(UserAdministrationOutcome.Invalid, weak.Outcome);
        Assert.Contains("Passwords must be at least 8 characters.", weak.Error, StringComparison.Ordinal);
        Assert.Equal(UserAdministrationOutcome.Invalid, published.Outcome);
        Assert.Equal(UserAdministrationOutcome.Invalid, missing.Outcome);
        Assert.Equal(UserAdministrationService.PasswordRequiredError, missing.Error);
        var saved = (await _host.FindUserAsync("quinn"))!;
        Assert.Equal(key, saved.ApiKeyHash);
        Assert.Equal(2, saved.AccessFailedCount);
        Assert.True((await _host.WithAuthServiceAsync(auth => auth.LoginAsync("quinn", Password))).Success);
    }

    [Fact]
    public async Task ResetPassword_RefusesTheAdministratorsOwnAccount_AnUnknownOne_AndAWrongPassword()
    {
        var user = await _host.CreateUserAsync("rosa", Password);

        var own = await WithServiceAsync(service => service.ResetPasswordAsync(Actor(), _admin.Id, NewPassword));
        var unknown = await WithServiceAsync(service => service.ResetPasswordAsync(Actor(), "no-such-id", NewPassword));
        var wrong = await WithServiceAsync(service => service.ResetPasswordAsync(Actor(null), user.Id, NewPassword));

        Assert.Equal(UserAdministrationOutcome.Conflict, own.Outcome);
        Assert.Equal(UserAdministrationService.OwnPasswordError, own.Error);
        Assert.Equal(UserAdministrationOutcome.NotFound, unknown.Outcome);
        Assert.Equal(UserAdministrationOutcome.PasswordNotConfirmed, wrong.Outcome);
        Assert.True((await _host.WithAuthServiceAsync(auth => auth.LoginAsync("admin", Password))).Success);
        Assert.True((await _host.WithAuthServiceAsync(auth => auth.LoginAsync("rosa", Password))).Success);
    }

    [Fact]
    public async Task ResetPassword_IsRefusedWhileAutheliaIsEnabled()
    {
        await using var authelia = await StartAutheliaHostAsync();
        var admin = await authelia.CreateUserAsync("admin", Password, "Admin");
        var user = await authelia.CreateUserAsync("sam", Password);

        var result = await authelia.InScopeAsync(services => services.GetRequiredService<IUserAdministrationService>()
            .ResetPasswordAsync(Actor(admin, null, autheliaSession: true), user.Id, NewPassword));

        Assert.Equal(UserAdministrationOutcome.Conflict, result.Outcome);
        Assert.Equal(UserAdministrationService.AutheliaPasswordError, result.Error);
    }

    // ── Disable and enable ────────────────────────────────────────────────

    [Fact]
    public async Task Disable_NeedsNoPassword_RevokesTheApiKey_AndEndsTheSessions()
    {
        var user = await _host.CreateUserAsync("tara", Password);
        await ChangeAsync(user.Id, async (users, account) =>
        {
            account.ApiKeyHash = ApiKeys.Hash(ApiKeys.Generate());
            return await users.UpdateAsync(account);
        });
        var stamp = (await _host.FindUserAsync("tara"))!.SecurityStamp;

        var result = await WithServiceAsync(service => service.SetActiveAsync(Actor(null), user.Id, isActive: false));

        Assert.Equal(UserAdministrationOutcome.Succeeded, result.Outcome);
        Assert.False(result.User!.IsActive);
        Assert.False(result.User.HasApiKey);
        var saved = (await _host.FindUserAsync("tara"))!;
        Assert.False(saved.IsActive);
        Assert.Null(saved.ApiKeyHash);
        Assert.NotEqual(stamp, saved.SecurityStamp);
        Assert.False((await _host.WithAuthServiceAsync(auth => auth.LoginAsync("tara", Password))).Success);
        Assert.Contains(AdministrationLog(), e => e.Level == LogLevel.Information
            && e.Message == "admin disabled the account tara; its sessions are signed out and its API key is revoked");
    }

    [Fact]
    public async Task Disable_OfADisabledAccount_ChangesNothing()
    {
        var user = await _host.CreateUserAsync("uma", Password);
        await WithServiceAsync(service => service.SetActiveAsync(Actor(), user.Id, isActive: false));
        var stamp = (await _host.FindUserAsync("uma"))!.SecurityStamp;

        var result = await WithServiceAsync(service => service.SetActiveAsync(Actor(), user.Id, isActive: false));

        Assert.Equal(UserAdministrationOutcome.Succeeded, result.Outcome);
        Assert.Equal(stamp, (await _host.FindUserAsync("uma"))!.SecurityStamp);
        Assert.Single(AdministrationLog(), e => e.Message.StartsWith("admin disabled the account uma", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Disable_NeverDisablesTheLastActiveAdministrator_OrTheAdministratorsOwnAccount()
    {
        var caller = await _host.CreateUserAsync("vic", Password);

        var last = await WithServiceAsync(service => service.SetActiveAsync(Actor(caller), _admin.Id, isActive: false));
        var own = await WithServiceAsync(service => service.SetActiveAsync(Actor(), _admin.Id, isActive: false));

        Assert.Equal(UserAdministrationOutcome.Conflict, last.Outcome);
        Assert.Equal(UserAdministrationService.LastAdministratorDisableError, last.Error);
        Assert.Equal(UserAdministrationOutcome.Conflict, own.Outcome);
        Assert.Equal(UserAdministrationService.OwnActiveError, own.Error);
        Assert.True((await _host.FindUserAsync("admin"))!.IsActive);
    }

    [Fact]
    public async Task Disable_OfAnAdministratorWhileAnotherIsActive_IsAllowed()
    {
        var other = await _host.CreateUserAsync("wren", Password, "Admin");

        var result = await WithServiceAsync(service => service.SetActiveAsync(Actor(), other.Id, isActive: false));

        Assert.Equal(UserAdministrationOutcome.Succeeded, result.Outcome);
        Assert.False((await _host.FindUserAsync("wren"))!.IsActive);
    }

    [Fact]
    public async Task Enable_NeedsTheAdministratorsOwnPassword_AndDoesNotBringTheApiKeyBack()
    {
        var user = await _host.CreateUserAsync("xena", Password);
        await ChangeAsync(user.Id, async (users, account) =>
        {
            account.ApiKeyHash = ApiKeys.Hash(ApiKeys.Generate());
            return await users.UpdateAsync(account);
        });
        await WithServiceAsync(service => service.SetActiveAsync(Actor(null), user.Id, isActive: false));

        var refused = await WithServiceAsync(service => service.SetActiveAsync(Actor(null), user.Id, isActive: true));
        Assert.Equal(UserAdministrationOutcome.PasswordNotConfirmed, refused.Outcome);
        Assert.False((await _host.FindUserAsync("xena"))!.IsActive);

        var enabled = await WithServiceAsync(service => service.SetActiveAsync(Actor(), user.Id, isActive: true));

        Assert.Equal(UserAdministrationOutcome.Succeeded, enabled.Outcome);
        Assert.True(enabled.User!.IsActive);
        Assert.False(enabled.User.HasApiKey);
        Assert.True((await _host.WithAuthServiceAsync(auth => auth.LoginAsync("xena", Password))).Success);
        Assert.Contains(AdministrationLog(), e => e.Message == "admin enabled the account xena");
    }

    [Fact]
    public async Task Enable_OfAnActiveAccount_ChangesNothing()
    {
        var user = await _host.CreateUserAsync("yan", Password);

        var result = await WithServiceAsync(service => service.SetActiveAsync(Actor(), user.Id, isActive: true));

        Assert.Equal(UserAdministrationOutcome.Succeeded, result.Outcome);
        Assert.DoesNotContain(AdministrationLog(), e => e.Message.Contains("enabled the account yan", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SetActive_OfAnUnknownAccount_IsNotFound()
    {
        var result = await WithServiceAsync(service => service.SetActiveAsync(Actor(), "no-such-id", isActive: false));

        Assert.Equal(UserAdministrationOutcome.NotFound, result.Outcome);
    }

    // Under Authelia the administrator has no password of their own here, so enabling an account asks for none; the
    // account can be disabled and enabled, since that is ComicMaintainer's to decide.
    [Fact]
    public async Task Enable_UnderAutheliaAsksAnAutheliaSessionForNoPassword()
    {
        await using var authelia = await StartAutheliaHostAsync();
        var admin = await authelia.CreateUserAsync("admin", Password, "Admin");
        var user = await authelia.CreateUserAsync("zoe", Password);

        var disabled = await authelia.InScopeAsync(services => services.GetRequiredService<IUserAdministrationService>()
            .SetActiveAsync(Actor(admin, null, autheliaSession: true), user.Id, isActive: false));
        var enabled = await authelia.InScopeAsync(services => services.GetRequiredService<IUserAdministrationService>()
            .SetActiveAsync(Actor(admin, null, autheliaSession: true), user.Id, isActive: true));

        Assert.Equal(UserAdministrationOutcome.Succeeded, disabled.Outcome);
        Assert.Equal(UserAdministrationOutcome.Succeeded, enabled.Outcome);
        Assert.True((await authelia.FindUserAsync("zoe"))!.IsActive);
    }

    // ── Delete ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_RemovesTheAccountAndEverythingItOwns_AndNothingElse()
    {
        var doomed = await _host.CreateUserAsync("doomed", Password);
        var keeper = await _host.CreateUserAsync("keeper", Password);
        await SeedOwnDataAsync(doomed.Id, "doomed");
        await SeedOwnDataAsync(keeper.Id, "keeper");
        await SeedOwnDataAsync(null, "shared");

        var result = await WithServiceAsync(service => service.DeleteAsync(Actor(null), doomed.Id));

        Assert.Equal(UserAdministrationOutcome.Succeeded, result.Outcome);
        Assert.Null(result.User);
        Assert.Null(await _host.FindUserAsync("doomed"));
        Assert.Equal(DataCounts.None, await CountOwnDataAsync(doomed.Id));
        Assert.Equal(DataCounts.All, await CountOwnDataAsync(keeper.Id));
        Assert.Equal(DataCounts.Unowned, await CountOwnDataAsync(null));
        var roleRows = await _host.InScopeAsync(services =>
            services.GetRequiredService<ComicMaintainerDbContext>().UserRoles.CountAsync(r => r.UserId == doomed.Id));
        Assert.Equal(0, roleRows);
        Assert.Contains(AdministrationLog(), e => e.Level == LogLevel.Information
            && e.Message == "admin deleted the account doomed with its reading progress, read status, preferences, e-reader devices (1) and email deliveries (1)");
    }

    [Fact]
    public async Task Delete_NeverDeletesTheLastActiveAdministrator_OrTheAdministratorsOwnAccount()
    {
        var caller = await _host.CreateUserAsync("abe", Password);

        var last = await WithServiceAsync(service => service.DeleteAsync(Actor(caller), _admin.Id));
        var own = await WithServiceAsync(service => service.DeleteAsync(Actor(), _admin.Id));
        var unknown = await WithServiceAsync(service => service.DeleteAsync(Actor(), "no-such-id"));

        Assert.Equal(UserAdministrationOutcome.Conflict, last.Outcome);
        Assert.Equal(UserAdministrationService.LastAdministratorDeleteError, last.Error);
        Assert.Equal(UserAdministrationOutcome.Conflict, own.Outcome);
        Assert.Equal(UserAdministrationService.OwnDeleteError, own.Error);
        Assert.Equal(UserAdministrationOutcome.NotFound, unknown.Outcome);
        Assert.NotNull(await _host.FindUserAsync("admin"));
    }

    [Fact]
    public async Task Delete_OfADisabledAdministrator_IsAllowedWhileTheOnlyActiveOneRemains()
    {
        var former = await _host.CreateUserAsync("former", Password, "Admin");
        await WithServiceAsync(service => service.SetActiveAsync(Actor(), former.Id, isActive: false));

        var result = await WithServiceAsync(service => service.DeleteAsync(Actor(), former.Id));

        Assert.Equal(UserAdministrationOutcome.Succeeded, result.Outcome);
        Assert.Null(await _host.FindUserAsync("former"));
    }

    [Fact]
    public void NormalizeRole_MatchesTheKnownRolesInAnyCase()
    {
        Assert.Equal("Admin", UserAdministrationService.NormalizeRole(" ADMIN "));
        Assert.Equal("ReadOnly", UserAdministrationService.NormalizeRole("readonly"));
        Assert.Equal("User", UserAdministrationService.NormalizeRole("user"));
        Assert.Null(UserAdministrationService.NormalizeRole("read-only"));
        Assert.Null(UserAdministrationService.NormalizeRole(null));
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private UserAdministrationActor Actor(string? password = Password) => Actor(_admin, password);

    private static UserAdministrationActor Actor(ApplicationUser admin, string? password = Password, bool autheliaSession = false) =>
        new(admin.Id, admin.UserName, password, autheliaSession, ClientAddress);

    private Task<T> WithServiceAsync<T>(Func<IUserAdministrationService, Task<T>> action) =>
        _host.InScopeAsync(services => action(services.GetRequiredService<IUserAdministrationService>()));

    private static Task<AuthTestHost> StartAutheliaHostAsync() => AuthTestHost.StartAsync(new Dictionary<string, string?>
    {
        ["AutheliaSettings:Enabled"] = "true",
        ["AutheliaSettings:TrustedProxies"] = "172.18.0.0/16",
    });

    private async Task ChangeAsync(string userId, Func<UserManager<ApplicationUser>, ApplicationUser, Task<IdentityResult>> change)
    {
        var result = await _host.InScopeAsync(async services =>
        {
            var users = services.GetRequiredService<UserManager<ApplicationUser>>();
            return await change(users, (await users.FindByIdAsync(userId))!);
        });
        Assert.True(result.Succeeded, string.Join(" ", result.Errors.Select(e => e.Description)));
    }

    private Task<IList<string>> GetRolesAsync(string userId) => _host.InScopeAsync(async services =>
    {
        var users = services.GetRequiredService<UserManager<ApplicationUser>>();
        return await users.GetRolesAsync((await users.FindByIdAsync(userId))!);
    });

    private IEnumerable<(string Category, LogLevel Level, string Message)> AdministrationLog() =>
        _host.Logs.Entries.Where(e => e.Category == typeof(UserAdministrationService).FullName);

    // One row in every table that holds an account's own data; with no owner, only the device, its subscription and
    // a delivery, which is all that can be unowned.
    private Task SeedOwnDataAsync(string? userId, string tag) => _host.InScopeAsync(async services =>
    {
        var db = services.GetRequiredService<ComicMaintainerDbContext>();
        var device = new EreaderDeviceEntity { OwnerUserId = userId, Name = "Kindle " + tag, EmailAddress = tag + "@kindle.example" };
        db.EreaderDevices.Add(device);
        await db.SaveChangesAsync();
        db.SeriesEmailSubscriptions.Add(new SeriesEmailSubscriptionEntity { NormalizedSeriesKey = "saga-" + tag, SeriesTitle = "Saga", DeviceId = device.Id });
        db.ComicEmailDeliveries.Add(new ComicEmailDeliveryEntity
        {
            OwnerUserId = userId, FilePath = $"/comics/{tag}.cbz", DeviceId = device.Id, DeviceName = device.Name, DeviceEmail = device.EmailAddress,
        });
        if (userId is not null)
        {
            db.ReadingProgresses.Add(new ReadingProgressEntity { UserId = userId, ContentId = "content-" + tag, LastReadAt = DateTime.UtcNow });
            db.ReadingSessions.Add(new ReadingSessionEntity { SessionId = Guid.NewGuid(), UserId = userId, ContentId = "content-" + tag, StartedAt = DateTime.UtcNow });
            db.UserFileReadStatuses.Add(new UserFileReadStatusEntity { UserId = userId, FilePath = $"/comics/{tag}.cbz", IsRead = true });
            db.ReaderPreferences.Add(new ReaderPreferencesEntity { UserId = userId });
            db.UserPreferences.Add(new UserPreferencesEntity { UserId = userId, Theme = "dark" });
            db.RevokedTokens.Add(new RevokedTokenEntity { TokenId = Guid.NewGuid().ToString("N"), UserId = userId, ExpiresAt = DateTime.UtcNow.AddHours(1) });
        }

        return await db.SaveChangesAsync();
    });

    private Task<DataCounts> CountOwnDataAsync(string? userId) => _host.InScopeAsync(async services =>
    {
        var db = services.GetRequiredService<ComicMaintainerDbContext>();
        var deviceIds = await db.EreaderDevices.Where(d => d.OwnerUserId == userId).Select(d => d.Id).ToListAsync();
        var id = userId ?? string.Empty;
        return new DataCounts(
            deviceIds.Count,
            await db.SeriesEmailSubscriptions.CountAsync(s => deviceIds.Contains(s.DeviceId)),
            await db.ComicEmailDeliveries.CountAsync(d => d.OwnerUserId == userId),
            await db.ReadingProgresses.CountAsync(p => p.UserId == id)
                + await db.ReadingSessions.CountAsync(s => s.UserId == id)
                + await db.UserFileReadStatuses.CountAsync(s => s.UserId == id)
                + await db.ReaderPreferences.CountAsync(p => p.UserId == id)
                + await db.UserPreferences.CountAsync(p => p.UserId == id)
                + await db.RevokedTokens.CountAsync(t => t.UserId == id));
    });

    private sealed record DataCounts(int Devices, int Subscriptions, int Deliveries, int PerUserRows)
    {
        public static readonly DataCounts None = new(0, 0, 0, 0);
        public static readonly DataCounts All = new(1, 1, 1, 6);
        public static readonly DataCounts Unowned = new(1, 1, 1, 0);
    }
}
