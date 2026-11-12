using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Models.Auth;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Core.Services.Auth;
using ComicMaintainer.Tests.Helpers;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Stream tokens (<see cref="AuthService.GenerateStreamTokenAsync"/>) against the real Identity stack: who gets one,
/// what the token says and which audience it is for, because an event stream URL is the one place a token travels.
/// </summary>
public sealed class AuthServiceStreamTokenTests : IAsyncLifetime
{
    private const string Password = "Corr3ctHorse";
    private AuthTestHost _host = null!;

    public async Task InitializeAsync() => _host = await AuthTestHost.StartAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task AStreamTokenNamesTheAccountAndOnlyOpensTheEventStream()
    {
        var user = await _host.CreateUserAsync("reader", Password, "Admin");
        var before = DateTime.UtcNow;

        var (success, token, error) = await GenerateAsync(user.Id);

        Assert.True(success, error);
        Assert.Null(error);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        var settings = _host.Services.GetRequiredService<IOptions<JwtSettings>>().Value;
        Assert.Equal(new[] { AuthTokens.StreamAudience(settings.Audience) }, jwt.Audiences);
        Assert.Equal(settings.Issuer, jwt.Issuer);
        Assert.Equal(AuthTokens.StreamTokenUse, Claim(jwt, AuthTokens.TokenUseClaim));
        Assert.Equal(user.Id, Claim(jwt, ClaimTypes.NameIdentifier));
        Assert.Equal("reader", Claim(jwt, ClaimTypes.Name));
        Assert.False(string.IsNullOrEmpty(Claim(jwt, JwtRegisteredClaimNames.Jti)));
        Assert.Equal(AuthTokens.Fingerprint((await _host.FindUserAsync("reader"))!.SecurityStamp), Claim(jwt, AuthTokens.SecurityStampClaim));

        // It carries no roles: it only says who is listening.
        Assert.DoesNotContain(jwt.Claims, c => c.Type is ClaimTypes.Role or "role");
        Assert.InRange(jwt.ValidTo, before, DateTime.UtcNow.Add(AuthTokens.StreamTokenLifetime).AddSeconds(5));
    }

    [Fact]
    public async Task EveryStreamTokenIsDifferent()
    {
        var user = await _host.CreateUserAsync("reader", Password);

        var first = await GenerateAsync(user.Id);
        var second = await GenerateAsync(user.Id);

        Assert.NotEqual(first.Token, second.Token);
    }

    [Fact]
    public async Task AnUnknownAccountGetsNoStreamToken()
    {
        var (success, token, error) = await GenerateAsync("no-such-user");

        Assert.False(success);
        Assert.Empty(token);
        Assert.Equal("User not found", error);
    }

    [Fact]
    public async Task ADisabledAccountGetsNoStreamToken()
    {
        var user = await _host.CreateUserAsync("reader", Password);
        await ChangeAsync(user.Id, u => u.IsActive = false);

        var (success, token, error) = await GenerateAsync(user.Id);

        Assert.False(success);
        Assert.Empty(token);
        Assert.Equal("User not found", error);
    }

    // An account written into the database without a stamp gets one, so that its tokens can be revoked.
    [Fact]
    public async Task AnAccountWithoutASecurityStampGetsOneBeforeTheTokenIsIssued()
    {
        var user = await _host.CreateUserAsync("reader", Password);
        await ChangeAsync(user.Id, u => u.SecurityStamp = null);

        var (success, token, error) = await GenerateAsync(user.Id);

        Assert.True(success, error);
        var stamp = (await _host.FindUserAsync("reader"))!.SecurityStamp;
        Assert.False(string.IsNullOrEmpty(stamp));
        Assert.Equal(AuthTokens.Fingerprint(stamp), Claim(new JwtSecurityTokenHandler().ReadJwtToken(token), AuthTokens.SecurityStampClaim));
    }

    private Task<(bool Success, string Token, string? Error)> GenerateAsync(string userId) =>
        _host.WithAuthServiceAsync(auth => auth.GenerateStreamTokenAsync(userId));

    private static string Claim(JwtSecurityToken jwt, string type) => jwt.Claims.Single(c => c.Type == type).Value;

    private Task ChangeAsync(string userId, Action<ApplicationUser> change) =>
        _host.InScopeAsync(async services =>
        {
            var db = services.GetRequiredService<ComicMaintainerDbContext>();
            var user = await db.Users.SingleAsync(u => u.Id == userId);
            change(user);
            return await db.SaveChangesAsync();
        });
}
