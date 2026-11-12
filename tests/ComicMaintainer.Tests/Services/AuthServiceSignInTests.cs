using ComicMaintainer.Core.Models.Auth;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Password sign-in (<see cref="AuthService.LoginAsync"/>) against the real Identity stack: account lockout, the
/// failed-attempt log, and answers that do not tell which accounts exist (SEC-3).
/// </summary>
public sealed class AuthServiceSignInTests : IAsyncLifetime
{
    private const string Password = "Corr3ctHorse";
    private AuthTestHost _host = null!;

    public async Task InitializeAsync() => _host = await AuthTestHost.StartAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task FiveWrongPasswordsLockTheAccountEvenAgainstTheRightPassword()
    {
        await _host.CreateUserAsync("reader", Password);

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            var (success, _, error) = await SignInAsync("reader", "Wr0ngGuess" + attempt);
            Assert.False(success);
            Assert.Equal(AuthService.InvalidCredentialsError, error);
        }

        var (signedIn, token, lockedError) = await SignInAsync("reader", Password);

        Assert.False(signedIn);
        Assert.Empty(token);
        Assert.Equal(AuthService.InvalidCredentialsError, lockedError);
        var user = await _host.FindUserAsync("reader");
        Assert.True(await _host.InScopeAsync(s => s.GetRequiredService<UserManager<ApplicationUser>>().IsLockedOutAsync(user!)));
        Assert.InRange(user!.LockoutEnd!.Value, DateTimeOffset.UtcNow.AddMinutes(14), DateTimeOffset.UtcNow.AddMinutes(16));
        Assert.Contains(AuthEntries(), e => e.Level == LogLevel.Warning && e.Message.StartsWith("Locked out reader until", StringComparison.Ordinal));
        Assert.Contains(AuthEntries(), e => e.Level == LogLevel.Warning
            && e.Message == "Failed sign-in for reader from 203.0.113.7: the account is locked out after too many failed sign-ins");
    }

    [Fact]
    public async Task ASuccessfulSignInResetsTheFailedAttempts()
    {
        await _host.CreateUserAsync("reader", Password);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            await SignInAsync("reader", "Wr0ngGuess");
        }

        Assert.True((await SignInAsync("reader", Password)).Success);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            await SignInAsync("reader", "Wr0ngGuess");
        }

        Assert.True((await SignInAsync("reader", Password)).Success);
        Assert.Equal(0, (await _host.FindUserAsync("reader"))!.AccessFailedCount);
    }

    // Accounts of earlier versions could have lockout switched off (Identity's LockoutEnabled flag).
    [Fact]
    public async Task AnAccountCreatedWithLockoutDisabledStillLocksOut()
    {
        var user = await _host.CreateUserAsync("legacy", Password);
        await _host.InScopeAsync(async s =>
        {
            var users = s.GetRequiredService<UserManager<ApplicationUser>>();
            return await users.SetLockoutEnabledAsync((await users.FindByIdAsync(user.Id))!, false);
        });

        for (var attempt = 0; attempt < 5; attempt++)
        {
            await SignInAsync("legacy", "Wr0ngGuess");
        }

        Assert.False((await SignInAsync("legacy", Password)).Success);
        Assert.NotNull((await _host.FindUserAsync("legacy"))!.LockoutEnd);
    }

    [Fact]
    public async Task UnknownLockedOutDisabledAndWrongPasswordAccountsAllGetTheSameAnswer()
    {
        await _host.CreateUserAsync("wrong-password", Password);
        var disabled = await _host.CreateUserAsync("disabled", Password);
        await _host.InScopeAsync(async s =>
        {
            var users = s.GetRequiredService<UserManager<ApplicationUser>>();
            var account = (await users.FindByIdAsync(disabled.Id))!;
            account.IsActive = false;
            return await users.UpdateAsync(account);
        });
        var locked = await _host.CreateUserAsync("locked", Password);
        await _host.InScopeAsync(async s =>
        {
            var users = s.GetRequiredService<UserManager<ApplicationUser>>();
            return await users.SetLockoutEndDateAsync((await users.FindByIdAsync(locked.Id))!, DateTimeOffset.UtcNow.AddMinutes(10));
        });

        var answers = new[]
        {
            await SignInAsync("nobody", Password),
            await SignInAsync("wrong-password", "Wr0ngGuess"),
            await SignInAsync("disabled", Password),
            await SignInAsync("locked", Password),
        };

        Assert.All(answers, answer => Assert.Equal((false, string.Empty, AuthService.InvalidCredentialsError), answer));
        Assert.Equal(
            [
                "Failed sign-in for nobody from 203.0.113.7: there is no such account",
                "Failed sign-in for wrong-password from 203.0.113.7: wrong password",
                "Failed sign-in for disabled from 203.0.113.7: the account is disabled",
                "Failed sign-in for locked from 203.0.113.7: the account is locked out after too many failed sign-ins",
            ],
            AuthEntries().Where(e => e.Level == LogLevel.Warning).Select(e => e.Message));
    }

    [Fact]
    public async Task UsernamesAreSanitizedInTheAuthLog()
    {
        await SignInAsync("evil\nFake entry", Password);

        Assert.Contains(AuthEntries(), e => e.Message == "Failed sign-in for evil\\nFake entry from 203.0.113.7: there is no such account");
    }

    [Fact]
    public async Task ASuccessfulSignInIsLoggedAndRecordsTheTime()
    {
        await _host.CreateUserAsync("reader", Password);

        var (success, token, error) = await SignInAsync("reader", Password);

        Assert.True(success);
        Assert.NotEmpty(token);
        Assert.Null(error);
        Assert.NotNull((await _host.FindUserAsync("reader"))!.LastLoginAt);
        Assert.Contains(AuthEntries(), e => e.Level == LogLevel.Information && e.Message == "reader signed in from 203.0.113.7");
    }

    // An account seeded by an earlier version with admin / Admin123!.
    [Fact]
    public async Task TheRightButPublishedPasswordIsRefused()
    {
        await _host.InScopeAsync(async s =>
        {
            var users = s.GetRequiredService<UserManager<ApplicationUser>>();
            var admin = new ApplicationUser { UserName = "admin", Email = "admin@example.com", IsActive = true };
            admin.PasswordHash = users.PasswordHasher.HashPassword(admin, "Admin123!");
            return await users.CreateAsync(admin);
        });

        var (success, token, error) = await SignInAsync("admin", "Admin123!");

        Assert.False(success);
        Assert.Empty(token);
        Assert.Equal(AuthService.PublishedPasswordError, error);
        var refusal = Assert.Single(AuthEntries(), e => e.Level == LogLevel.Error && e.Message.Contains("password was published", StringComparison.Ordinal));
        // An operator condition: written to the log, but not filed as a bug report.
        Assert.Contains(refusal.Message, _host.Logs.ExcludedFromErrorReports);
        // Someone guessing the published password does not get to lock the account out either.
        Assert.Equal(0, (await _host.FindUserAsync("admin"))!.AccessFailedCount);
    }

    // --- changing the password ---------------------------------------------------------------------------------

    [Fact]
    public async Task WrongCurrentPasswordsAtAPasswordChangeLockTheAccountOut()
    {
        var user = await _host.CreateUserAsync("reader", Password);

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            var (changed, error) = await ChangePasswordAsync(user.Id, "Wr0ngGuess" + attempt, NewPassword);
            Assert.False(changed);
            Assert.NotEqual(AuthService.AccountLockedOutError, error);
        }

        Assert.NotNull((await _host.FindUserAsync("reader"))!.LockoutEnd);
        Assert.Contains(AuthEntries(), e => e.Level == LogLevel.Warning && e.Message == "Failed password change for reader from 203.0.113.7: wrong current password");
        Assert.Contains(AuthEntries(), e => e.Level == LogLevel.Warning && e.Message.StartsWith("Locked out reader until", StringComparison.Ordinal));

        // Even the right password no longer changes it, and the failure says why: the caller is signed in as this account.
        var (changedWhileLocked, lockedError) = await ChangePasswordAsync(user.Id, Password, NewPassword);
        Assert.False(changedWhileLocked);
        Assert.Equal(AuthService.AccountLockedOutError, lockedError);
        var (signedIn, _, _) = await SignInAsync("reader", NewPassword);
        Assert.False(signedIn);
    }

    [Fact]
    public async Task ARejectedNewPasswordDoesNotCountAsAWrongGuess()
    {
        var user = await _host.CreateUserAsync("reader", Password);

        var (changed, _) = await ChangePasswordAsync(user.Id, Password, "short");

        Assert.False(changed);
        Assert.Equal(0, (await _host.FindUserAsync("reader"))!.AccessFailedCount);
    }

    [Fact]
    public async Task ASuccessfulPasswordChangeClearsTheFailedAttempts()
    {
        var user = await _host.CreateUserAsync("reader", Password);
        await ChangePasswordAsync(user.Id, "Wr0ngGuess", NewPassword);
        Assert.Equal(1, (await _host.FindUserAsync("reader"))!.AccessFailedCount);

        var (changed, error) = await ChangePasswordAsync(user.Id, Password, NewPassword);

        Assert.True(changed, error);
        Assert.Equal(0, (await _host.FindUserAsync("reader"))!.AccessFailedCount);
    }

    // --- what revokes the API key ----------------------------------------------------------------------------

    [Fact]
    public async Task AChangedPasswordRevokesTheApiKey()
    {
        var user = await _host.CreateUserAsync("reader", Password);
        var apiKey = await CreateApiKeyAsync(user.Id);

        Assert.True((await ChangePasswordAsync(user.Id, Password, NewPassword)).Success);

        Assert.Null(await _host.WithAuthServiceAsync(auth => auth.GetUserByApiKeyAsync(apiKey)));
        var stored = (await _host.FindUserAsync("reader"))!;
        Assert.Null(stored.ApiKeyHash);
        Assert.Null(stored.ApiKeyCreatedAt);
        Assert.Null(stored.ApiKeyLastUsedAt);
    }

    [Theory]
    [InlineData("Wr0ngGuess", NewPassword)]
    [InlineData(Password, "short")]
    public async Task AFailedPasswordChangeLeavesTheApiKeyAlone(string currentPassword, string newPassword)
    {
        var user = await _host.CreateUserAsync("reader", Password);
        var apiKey = await CreateApiKeyAsync(user.Id);

        Assert.False((await ChangePasswordAsync(user.Id, currentPassword, newPassword)).Success);

        Assert.Equal(user.Id, (await _host.WithAuthServiceAsync(auth => auth.GetUserByApiKeyAsync(apiKey)))?.Id);
        Assert.NotNull((await _host.FindUserAsync("reader"))!.ApiKeyCreatedAt);
    }

    [Fact]
    public async Task SigningOutOfAllSessionsRevokesTheApiKey()
    {
        var user = await _host.CreateUserAsync("reader", Password);
        var apiKey = await CreateApiKeyAsync(user.Id);
        var stamp = (await _host.FindUserAsync("reader"))!.SecurityStamp;

        await _host.WithAuthServiceAsync(async auth =>
        {
            await auth.LogoutAsync(user.Id, "token-1", DateTime.UtcNow.AddHours(1), allSessions: true);
            return true;
        });

        Assert.Null(await _host.WithAuthServiceAsync(auth => auth.GetUserByApiKeyAsync(apiKey)));
        var stored = (await _host.FindUserAsync("reader"))!;
        Assert.Null(stored.ApiKeyHash);
        Assert.NotEqual(stamp, stored.SecurityStamp);
    }

    [Fact]
    public async Task SigningOutOfOneSessionLeavesTheApiKeyAlone()
    {
        var user = await _host.CreateUserAsync("reader", Password);
        var apiKey = await CreateApiKeyAsync(user.Id);

        await _host.WithAuthServiceAsync(async auth =>
        {
            await auth.LogoutAsync(user.Id, "token-1", DateTime.UtcNow.AddHours(1), allSessions: false);
            return true;
        });

        Assert.Equal(user.Id, (await _host.WithAuthServiceAsync(auth => auth.GetUserByApiKeyAsync(apiKey)))?.Id);
    }

    // --- confirming the password ------------------------------------------------------------------------------

    [Fact]
    public async Task ConfirmingTheRightPasswordSucceedsAndClearsTheFailedAttempts()
    {
        var user = await _host.CreateUserAsync("reader", Password);
        await ConfirmPasswordAsync(user.Id, "Wr0ngGuess");
        Assert.Equal(1, (await _host.FindUserAsync("reader"))!.AccessFailedCount);

        Assert.Equal((true, null), await ConfirmPasswordAsync(user.Id, Password));

        Assert.Equal(0, (await _host.FindUserAsync("reader"))!.AccessFailedCount);
    }

    [Fact]
    public async Task WrongPasswordsAtAConfirmationLockTheAccountOut()
    {
        var user = await _host.CreateUserAsync("reader", Password);

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            Assert.Equal((false, AuthService.WrongCurrentPasswordError), await ConfirmPasswordAsync(user.Id, "Wr0ngGuess" + attempt));
        }

        Assert.Contains(AuthEntries(), e => e.Level == LogLevel.Warning && e.Message == "Failed password confirmation for reader from 203.0.113.7: wrong password");
        Assert.Equal((false, AuthService.AccountLockedOutError), await ConfirmPasswordAsync(user.Id, Password));
        Assert.False((await SignInAsync("reader", Password)).Success);
    }

    // A client that sends no password is not guessing one.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task AConfirmationWithoutAPasswordIsRefusedWithoutCountingAgainstTheAccount(string? password)
    {
        var user = await _host.CreateUserAsync("reader", Password);

        Assert.Equal((false, AuthService.WrongCurrentPasswordError), await ConfirmPasswordAsync(user.Id, password));

        Assert.Equal(0, (await _host.FindUserAsync("reader"))!.AccessFailedCount);
    }

    [Fact]
    public async Task ConfirmingThePasswordOfAnUnknownAccountFails()
    {
        Assert.Equal((false, AuthService.WrongCurrentPasswordError), await ConfirmPasswordAsync("no-such-user", Password));
    }

    private const string NewPassword = "N3wPassw0rdOk";

    private Task<(bool Success, string? Error)> ChangePasswordAsync(string userId, string currentPassword, string newPassword) =>
        _host.WithAuthServiceAsync(auth => auth.ChangePasswordAsync(userId, currentPassword, newPassword, "203.0.113.7"));

    private Task<(bool Success, string? Error)> ConfirmPasswordAsync(string userId, string? password) =>
        _host.WithAuthServiceAsync(auth => auth.ConfirmPasswordAsync(userId, password, "203.0.113.7"));

    private async Task<string> CreateApiKeyAsync(string userId)
    {
        var (success, apiKey, error) = await _host.WithAuthServiceAsync(auth => auth.GenerateApiKeyAsync(userId));
        Assert.True(success, error);
        return apiKey!;
    }

    private Task<(bool Success, string Token, string? Error)> SignInAsync(string username, string password) =>
        _host.WithAuthServiceAsync(auth => auth.LoginAsync(username, password, "203.0.113.7"));

    private IEnumerable<(string Category, LogLevel Level, string Message)> AuthEntries() =>
        _host.Logs.Entries.Where(e => e.Category == typeof(AuthService).FullName);
}
