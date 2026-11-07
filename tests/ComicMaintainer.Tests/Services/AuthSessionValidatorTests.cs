using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Models.Auth;
using ComicMaintainer.Core.Services.Auth;
using ComicMaintainer.Tests.Helpers;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// <see cref="AuthSessionValidator"/> against the real Identity store (SEC-7): a token stands only while its account is
/// active, its security stamp is unchanged and it was not signed out; stream tokens are single-use.
/// </summary>
public sealed class AuthSessionValidatorTests : IAsyncLifetime
{
    private AuthTestHost _host = null!;
    private ApplicationUser _user = null!;

    public async Task InitializeAsync()
    {
        _host = await AuthTestHost.StartAsync();
        _user = await _host.CreateUserAsync("session-reader", "Passw0rdOk");
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private IAuthSessionValidator Validator => _host.Services.GetRequiredService<IAuthSessionValidator>();

    private async Task<string> CurrentFingerprintAsync()
    {
        var user = await _host.FindUserAsync(_user.UserName!);
        return AuthTokens.Fingerprint(user!.SecurityStamp);
    }

    private Task ChangeUserAsync(Func<UserManager<ApplicationUser>, ApplicationUser, Task> change) =>
        _host.InScopeAsync<bool>(async services =>
        {
            var users = services.GetRequiredService<UserManager<ApplicationUser>>();
            await change(users, (await users.FindByIdAsync(_user.Id))!);
            return true;
        });

    [Fact]
    public async Task ATokenOfAnActiveAccountWithTheCurrentStampIsValid()
    {
        Assert.True(await Validator.IsSessionValidAsync(_user.Id, await CurrentFingerprintAsync(), "token-1"));
    }

    // Tokens issued before 3.0.0 carry neither claim: they cannot be revoked, so they are refused.
    [Theory]
    [InlineData(null, "fingerprint", "token-1")]
    [InlineData("user", null, "token-1")]
    [InlineData("user", "fingerprint", null)]
    [InlineData("", "fingerprint", "token-1")]
    public async Task ATokenWithoutUserStampOrIdIsRefused(string? userId, string? fingerprint, string? tokenId)
    {
        var resolvedUser = userId == "user" ? _user.Id : userId;
        var resolvedFingerprint = fingerprint == "fingerprint" ? await CurrentFingerprintAsync() : fingerprint;

        Assert.False(await Validator.IsSessionValidAsync(resolvedUser, resolvedFingerprint, tokenId));
    }

    [Fact]
    public async Task ANewSecurityStampEndsTheSessionOnceTheAccountIsInvalidated()
    {
        var fingerprint = await CurrentFingerprintAsync();
        Assert.True(await Validator.IsSessionValidAsync(_user.Id, fingerprint, "token-1"));

        await ChangeUserAsync((users, user) => users.UpdateSecurityStampAsync(user));
        Validator.Invalidate(_user.Id);

        Assert.False(await Validator.IsSessionValidAsync(_user.Id, fingerprint, "token-1"));
        Assert.True(await Validator.IsSessionValidAsync(_user.Id, await CurrentFingerprintAsync(), "token-2"));
    }

    [Fact]
    public async Task ADisabledOrDeletedAccountHasNoValidSession()
    {
        var fingerprint = await CurrentFingerprintAsync();

        await ChangeUserAsync(async (users, user) =>
        {
            user.IsActive = false;
            await users.UpdateAsync(user);
        });
        Validator.Invalidate(_user.Id);
        Assert.False(await Validator.IsSessionValidAsync(_user.Id, fingerprint, "token-1"));

        await ChangeUserAsync((users, user) => users.DeleteAsync(user));
        Validator.Invalidate(_user.Id);
        Assert.False(await Validator.IsSessionValidAsync(_user.Id, fingerprint, "token-1"));
    }

    [Fact]
    public async Task ARevokedTokenIsRefusedAndOthersOfTheAccountAreNot()
    {
        var fingerprint = await CurrentFingerprintAsync();
        Assert.True(await Validator.IsSessionValidAsync(_user.Id, fingerprint, "token-1"));

        await Validator.RevokeTokenAsync("token-1", _user.Id, DateTime.UtcNow.AddHours(1));

        Assert.False(await Validator.IsSessionValidAsync(_user.Id, fingerprint, "token-1"));
        Assert.True(await Validator.IsSessionValidAsync(_user.Id, fingerprint, "token-2"));
    }

    [Fact]
    public async Task RevocationsAreKeptInTheDatabaseUntilTheTokenExpires()
    {
        await Validator.RevokeTokenAsync("expired-token", _user.Id, DateTime.UtcNow.AddMinutes(-1));
        await Validator.RevokeTokenAsync("live-token", _user.Id, DateTime.UtcNow.AddHours(1));
        await Validator.RevokeTokenAsync("live-token", _user.Id, DateTime.UtcNow.AddHours(1));

        var factory = _host.Services.GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>();
        List<string> stored;
        await using (var db = await factory.CreateDbContextAsync())
        {
            stored = await db.RevokedTokens.Select(t => t.TokenId).ToListAsync();
        }

        Assert.Equal(["live-token"], stored);

        // After a restart (a new validator with an empty cache) the live one is still refused.
        var restarted = new AuthSessionValidator(factory, new MemoryCache(new MemoryCacheOptions()), NullLogger<AuthSessionValidator>.Instance);
        Assert.False(await restarted.IsSessionValidAsync(_user.Id, await CurrentFingerprintAsync(), "live-token"));
    }

    [Fact]
    public void ASingleUseTokenCanBeConsumedOnce()
    {
        var expires = DateTime.UtcNow.AddMinutes(1);

        Assert.True(Validator.TryConsumeSingleUseToken("stream-1", expires));
        Assert.False(Validator.TryConsumeSingleUseToken("stream-1", expires));
        Assert.True(Validator.TryConsumeSingleUseToken("stream-2", expires));
    }
}
