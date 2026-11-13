using System.Net;
using System.Security.Claims;
using System.Text;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models.Auth;
using ComicMaintainer.Core.Services.Auth;
using ComicMaintainer.Tests.Helpers;
using ComicMaintainer.WebApi.Authentication;
using ComicMaintainer.WebApi.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace ComicMaintainer.Tests.Authentication;

/// <summary>
/// The accounts the API-key and OPDS Basic handlers remember between requests, on the real auth services: a repeated
/// credential is answered from memory for <see cref="AuthSessionValidator.CacheDuration"/>, and every change the app
/// makes to the account (a new or revoked key, a password, role or status change, a lockout) applies to the very next
/// request.
/// </summary>
public sealed class RememberedAccountTests : IAsyncLifetime
{
    private const string Password = "Corr3ctHorse";
    private const string NewPassword = "N3wPassw0rdOk";

    private readonly ManualClock _clock = new();
    private AuthTestHost _host = null!;
    private ApplicationUser _reader = null!;
    private UserAdministrationActor _admin = null!;

    public async Task InitializeAsync()
    {
        _host = await AuthTestHost.StartAsync(configureServices: services =>
        {
            services.AddSingleton<IMemoryCache>(new MemoryCache(new MemoryCacheOptions { Clock = _clock }));
            services.AddSingleton(ClientAddressResolver.Direct);
            services.AddSingleton<AuthenticationAttemptLimiter>();
        });
        _reader = await _host.CreateUserAsync("reader", Password);
        var admin = await _host.CreateUserAsync("admin", Password, "Admin");
        _admin = new UserAdministrationActor(admin.Id, "admin", Password, SignedInThroughAuthelia: false, ClientAddress: null);
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task AnApiKeyUsedAgain_IsAnsweredFromMemoryUntilTheCacheDurationPasses()
    {
        var apiKey = await CreateApiKeyAsync();
        Assert.True((await WithApiKeyAsync(apiKey)).Succeeded);

        // Behind the app's back, so nothing tells the handler: it keeps the account it read.
        await RemoveApiKeyFromTheDatabaseAsync();
        Assert.True((await WithApiKeyAsync(apiKey)).Succeeded);

        _clock.Advance(AuthSessionValidator.CacheDuration + TimeSpan.FromSeconds(1));
        Assert.False((await WithApiKeyAsync(apiKey)).Succeeded);
    }

    public static TheoryData<string> ChangesThatEndAnApiKey() =>
        ["replaced", "revoked", "password changed", "signed out everywhere", "disabled", "password reset by an administrator"];

    [Theory]
    [MemberData(nameof(ChangesThatEndAnApiKey))]
    public async Task ARememberedApiKey_IsRefusedRightAfterTheAccountChanges(string change)
    {
        var apiKey = await CreateApiKeyAsync();
        Assert.True((await WithApiKeyAsync(apiKey)).Succeeded);
        Assert.True((await WithApiKeyAsync(apiKey)).Succeeded);

        await ChangeAsync(change);

        var result = await WithApiKeyAsync(apiKey);
        Assert.Equal("Invalid API key", result.Failure?.Message);
    }

    [Fact]
    public async Task ARememberedApiKey_CarriesANewRoleRightAfterTheChange()
    {
        var apiKey = await CreateApiKeyAsync();
        Assert.Equal(["User"], Roles(await WithApiKeyAsync(apiKey)));

        await ChangeAsync("made read-only");

        Assert.Equal(["ReadOnly"], Roles(await WithApiKeyAsync(apiKey)));
    }

    [Fact]
    public async Task ARememberedOpdsPassword_IsAnsweredFromMemoryUntilTheCacheDurationPasses()
    {
        Assert.True((await WithOpdsCredentialsAsync(Password)).Succeeded);
        Assert.True((await WithOpdsCredentialsAsync(Password)).Succeeded);

        await DisableBehindTheAppsBackAsync();
        Assert.True((await WithOpdsCredentialsAsync(Password)).Succeeded);

        _clock.Advance(AuthSessionValidator.CacheDuration + TimeSpan.FromSeconds(1));
        Assert.False((await WithOpdsCredentialsAsync(Password)).Succeeded);
    }

    [Theory]
    [InlineData("password changed")]
    [InlineData("disabled")]
    [InlineData("locked out")]
    public async Task ARememberedOpdsPassword_IsRefusedRightAfterTheAccountChanges(string change)
    {
        Assert.True((await WithOpdsCredentialsAsync(Password)).Succeeded);
        Assert.True((await WithOpdsCredentialsAsync(Password)).Succeeded);

        await ChangeAsync(change);

        Assert.False((await WithOpdsCredentialsAsync(Password)).Succeeded);
    }

    [Fact]
    public async Task ARememberedOpdsPassword_CarriesANewRoleRightAfterTheChange()
    {
        Assert.Equal(["User"], Roles(await WithOpdsCredentialsAsync(Password)));
        Assert.Equal(["User"], Roles(await WithOpdsCredentialsAsync(Password)));

        await ChangeAsync("made read-only");

        Assert.Equal(["ReadOnly"], Roles(await WithOpdsCredentialsAsync(Password)));
    }

    [Theory]
    [InlineData("replaced")]
    [InlineData("revoked")]
    public async Task ARememberedOpdsApiKey_IsRefusedRightAfterTheKeyChanges(string change)
    {
        var apiKey = await CreateApiKeyAsync();
        Assert.True((await WithOpdsCredentialsAsync(apiKey)).Succeeded);
        Assert.True((await WithOpdsCredentialsAsync(apiKey)).Succeeded);

        await ChangeAsync(change);

        Assert.False((await WithOpdsCredentialsAsync(apiKey)).Succeeded);
    }

    private async Task ChangeAsync(string change)
    {
        switch (change)
        {
            case "replaced":
                await CreateApiKeyAsync();
                break;
            case "revoked":
                Assert.True(await _host.WithAuthServiceAsync(auth => auth.RevokeApiKeyAsync(_reader.Id)));
                break;
            case "password changed":
                Assert.True((await _host.WithAuthServiceAsync(auth => auth.ChangePasswordAsync(_reader.Id, Password, NewPassword))).Success);
                break;
            case "signed out everywhere":
                await _host.WithAuthServiceAsync(async auth =>
                {
                    await auth.LogoutAsync(_reader.Id, tokenId: null, DateTime.UtcNow, allSessions: true);
                    return true;
                });
                break;
            case "disabled":
                await AdministerAsync(users => users.SetActiveAsync(_admin, _reader.Id, isActive: false));
                break;
            case "password reset by an administrator":
                await AdministerAsync(users => users.ResetPasswordAsync(_admin, _reader.Id, NewPassword));
                break;
            case "made read-only":
                await AdministerAsync(users => users.ChangeRoleAsync(_admin, _reader.Id, "ReadOnly"));
                break;
            case "locked out":
                for (var attempt = 0; attempt < 5; attempt++)
                {
                    await _host.WithAuthServiceAsync(auth => auth.LoginAsync("reader", "Wr0ngGuess", "203.0.113.7"));
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(change), change, null);
        }
    }

    private async Task AdministerAsync(Func<IUserAdministrationService, Task<UserAdministrationResult>> action)
    {
        var result = await _host.InScopeAsync(services => action(services.GetRequiredService<IUserAdministrationService>()));
        Assert.True(result.Succeeded, result.Error);
    }

    private async Task<string> CreateApiKeyAsync()
    {
        var (success, apiKey, error) = await _host.WithAuthServiceAsync(auth => auth.GenerateApiKeyAsync(_reader.Id));
        Assert.True(success, error);
        return apiKey!;
    }

    private Task RemoveApiKeyFromTheDatabaseAsync() =>
        _host.InScopeAsync(services => services.GetRequiredService<ComicMaintainerDbContext>().Users
            .Where(u => u.Id == _reader.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(u => u.ApiKeyHash, (string?)null)));

    private Task DisableBehindTheAppsBackAsync() =>
        _host.InScopeAsync(services => services.GetRequiredService<ComicMaintainerDbContext>().Users
            .Where(u => u.Id == _reader.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(u => u.IsActive, false)));

    private Task<AuthenticateResult> WithApiKeyAsync(string apiKey) =>
        AuthenticateAsync(ApiKeyAuthenticationHandler.SchemeName, request => request.Headers[ApiKeys.HeaderName] = apiKey);

    private Task<AuthenticateResult> WithOpdsCredentialsAsync(string secret) =>
        AuthenticateAsync(OpdsBasicAuthenticationHandler.SchemeName, request =>
            request.Headers.Authorization = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("reader:" + secret)));

    /// <summary>Authenticates one request, in a scope of its own, with the handler the app would make for it.</summary>
    private Task<AuthenticateResult> AuthenticateAsync(string scheme, Action<HttpRequest> addCredentials) =>
        _host.InScopeAsync(async services =>
        {
            var context = new DefaultHttpContext { RequestServices = services };
            context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.10");
            context.Request.Method = HttpMethods.Get;
            context.Request.Path = "/opds/v1.2/catalog";
            addCredentials(context.Request);
            var handler = await services.GetRequiredService<IAuthenticationHandlerProvider>().GetHandlerAsync(context, scheme);
            return await handler!.AuthenticateAsync();
        });

    private static string[] Roles(AuthenticateResult result)
    {
        Assert.True(result.Succeeded, result.Failure?.Message);
        return result.Principal!.FindAll(ClaimTypes.Role).Select(c => c.Value).Order(StringComparer.Ordinal).ToArray();
    }

    private sealed class ManualClock : Microsoft.Extensions.Internal.ISystemClock
    {
        public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.UtcNow;

        public void Advance(TimeSpan by) => UtcNow += by;
    }
}
