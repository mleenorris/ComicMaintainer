using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using ComicMaintainer.Core.Models.Auth;
using ComicMaintainer.Core.Services.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace ComicMaintainer.Tests.Integration;

/// <summary>
/// API keys in the real pipeline (BUG-1): a key created through <c>POST /api/auth/api-key</c> authenticates requests
/// that send it in the <c>X-API-Key</c> header, as its owner with the owner's current roles; it is never accepted
/// from a URL or as a bearer token, is stored only as a hash, records when it was last used, and stops working when
/// it is revoked or replaced or the account is disabled.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ApiKeyAuthenticationIntegrationTests : IClassFixture<SessionTokenIntegrationTests.JwtHost>
{
    private readonly SessionTokenIntegrationTests.JwtHost _host;

    public ApiKeyAuthenticationIntegrationTests(SessionTokenIntegrationTests.JwtHost host)
    {
        _host = host;
    }

    [Theory]
    [InlineData("ReadOnly", false, false)]
    [InlineData("User", true, false)]
    [InlineData("Admin", true, true)]
    public async Task AKeyActsAsItsOwnerWithTheOwnersRoles(string role, bool canModifyLibrary, bool canAdminister)
    {
        var (user, session) = await _host.SignInNewUserAsync(role);
        var apiKey = await CreateApiKeyAsync(session);

        var (status, body) = await SendAsync(HttpMethod.Get, "/api/auth/user", apiKey: apiKey);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(user.UserName, body.GetProperty("username").GetString());
        Assert.Equal(user.Id, body.GetProperty("userId").GetString());
        Assert.Equal(ApiKeys.AuthMethod, body.GetProperty("authMethod").GetString());
        Assert.Equal([role], body.GetProperty("roles").EnumerateArray().Select(r => r.GetString()!).ToArray());
        Assert.Equal(canModifyLibrary, body.GetProperty("canModifyLibrary").GetBoolean());
        Assert.Equal(canAdminister, body.GetProperty("canAdminister").GetBoolean());
        Assert.Equal(canAdminister ? HttpStatusCode.OK : HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Get, "/api/logs/files?type=auth", apiKey: apiKey)).Status);
    }

    // Proxies, browser history and Referer headers record URLs; and a key is not a session token.
    [Theory]
    [InlineData("/api/auth/user?apiKey={0}")]
    [InlineData("/api/auth/user?api_key={0}")]
    [InlineData("/api/auth/user?X-API-Key={0}")]
    [InlineData("/api/auth/user?access_token={0}")]
    [InlineData("/api/events/stream?access_token={0}")]
    public async Task AKeyIsNeverAcceptedFromTheQueryString(string pathFormat)
    {
        var (_, session) = await _host.SignInNewUserAsync("Admin");
        var apiKey = await CreateApiKeyAsync(session);

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, string.Format(pathFormat, Uri.EscapeDataString(apiKey)))).Status);
    }

    [Fact]
    public async Task AKeyIsNotABearerToken()
    {
        var (_, session) = await _host.SignInNewUserAsync();
        var apiKey = await CreateApiKeyAsync(session);

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "/api/auth/user", bearer: apiKey)).Status);
    }

    [Fact]
    public async Task AWrongKeyIsRefused()
    {
        var (_, session) = await _host.SignInNewUserAsync();
        var apiKey = await CreateApiKeyAsync(session);

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "/api/auth/user", apiKey: apiKey + "x")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "/api/auth/user", apiKey: ApiKeys.Generate())).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "/api/auth/user", apiKey: new string('k', 4096))).Status);
    }

    [Fact]
    public async Task OnlyTheKeysHashIsStored()
    {
        var (user, session) = await _host.SignInNewUserAsync();
        var apiKey = await CreateApiKeyAsync(session);

        var stored = await FindUserAsync(user.Id);
        Assert.StartsWith(ApiKeys.Prefix, apiKey);
        Assert.Equal(ApiKeys.Hash(apiKey), stored.ApiKeyHash);
        Assert.NotNull(stored.ApiKeyCreatedAt);
    }

    [Fact]
    public async Task ARevokedOrReplacedKeyStopsWorking()
    {
        var (_, session) = await _host.SignInNewUserAsync();
        var first = await CreateApiKeyAsync(session);
        var second = await CreateApiKeyAsync(session);

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "/api/auth/user", apiKey: first)).Status);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, "/api/auth/user", apiKey: second)).Status);

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, "/api/auth/api-key", bearer: session)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "/api/auth/user", apiKey: second)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Delete, "/api/auth/api-key", bearer: session)).Status);
    }

    [Fact]
    public async Task TheKeyOfADisabledAccountIsRefused()
    {
        var (user, session) = await _host.SignInNewUserAsync();
        var apiKey = await CreateApiKeyAsync(session);

        await _host.ChangeUserAsync(user.Id, async (users, account) =>
        {
            account.IsActive = false;
            await users.UpdateAsync(account);
        });

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "/api/auth/user", apiKey: apiKey)).Status);
    }

    [Fact]
    public async Task ARoleChangeAppliesToTheKeyAtOnce()
    {
        var (user, session) = await _host.SignInNewUserAsync("Admin");
        var apiKey = await CreateApiKeyAsync(session);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, "/api/logs/files?type=auth", apiKey: apiKey)).Status);

        await _host.ChangeUserAsync(user.Id, async (users, account) =>
        {
            await users.RemoveFromRoleAsync(account, "Admin");
            await users.AddToRoleAsync(account, "ReadOnly");
        });

        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Get, "/api/logs/files?type=auth", apiKey: apiKey)).Status);
    }

    [Fact]
    public async Task TheKeysStatusShowsWhenItWasCreatedAndLastUsed()
    {
        var (_, session) = await _host.SignInNewUserAsync();
        var (_, none) = await SendAsync(HttpMethod.Get, "/api/auth/api-key", bearer: session);
        Assert.False(none.GetProperty("hasApiKey").GetBoolean());

        var apiKey = await CreateApiKeyAsync(session);
        var (_, unused) = await SendAsync(HttpMethod.Get, "/api/auth/api-key", bearer: session);
        Assert.True(unused.GetProperty("hasApiKey").GetBoolean());
        Assert.Equal(JsonValueKind.Null, unused.GetProperty("lastUsedAt").ValueKind);
        Assert.Equal(ApiKeys.HeaderName, unused.GetProperty("header").GetString());

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, "/api/auth/user", apiKey: apiKey)).Status);
        var (_, used) = await SendAsync(HttpMethod.Get, "/api/auth/api-key", bearer: session);
        var lastUsed = used.GetProperty("lastUsedAt").GetDateTime();
        Assert.InRange(lastUsed, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(1));
    }

    // A leaked key must not be able to replace itself and lock its owner out.
    [Fact]
    public async Task AKeyCannotCreateAKeyButCanRevokeItself()
    {
        var (_, session) = await _host.SignInNewUserAsync();
        var apiKey = await CreateApiKeyAsync(session);

        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Post, "/api/auth/api-key", apiKey: apiKey)).Status);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, "/api/auth/user", apiKey: apiKey)).Status);

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, "/api/auth/api-key", apiKey: apiKey)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "/api/auth/user", apiKey: apiKey)).Status);
    }

    [Fact]
    public async Task AKeyCanWriteToTheLibraryWhenItsOwnerCan()
    {
        var (_, readerSession) = await _host.SignInNewUserAsync("ReadOnly");
        var (_, writerSession) = await _host.SignInNewUserAsync("User");
        var readerKey = await CreateApiKeyAsync(readerSession);
        var writerKey = await CreateApiKeyAsync(writerSession);

        // A library write (CanModifyLibrary): ReadOnly is refused; the owner who may write gets as far as validation.
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Post, "/api/status/clear-selected", apiKey: readerKey, body: new { files = Array.Empty<string>() })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Post, "/api/status/clear-selected", apiKey: writerKey, body: new { files = Array.Empty<string>() })).Status);
    }

    private async Task<string> CreateApiKeyAsync(string session)
    {
        var (status, body) = await SendAsync(HttpMethod.Post, "/api/auth/api-key", bearer: session);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(ApiKeys.HeaderName, body.GetProperty("header").GetString());
        return body.GetProperty("apiKey").GetString()!;
    }

    private async Task<ApplicationUser> FindUserAsync(string userId)
    {
        using var scope = _host.Factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByIdAsync(userId))!;
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(HttpMethod method, string path, string? apiKey = null, string? bearer = null, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : System.Net.Http.Json.JsonContent.Create(body) };
        if (apiKey is not null)
        {
            request.Headers.TryAddWithoutValidation(ApiKeys.HeaderName, apiKey);
        }

        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        using var response = await _host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        if (response.Content.Headers.ContentType?.MediaType != "application/json")
        {
            return (response.StatusCode, default);
        }

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (response.StatusCode, document.RootElement.Clone());
    }
}
