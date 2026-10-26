using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace ComicMaintainer.Tests.Integration;

/// <summary>
/// <c>AUTHELIA_TRUSTED_PROXIES</c> in the real pipeline: forward-auth headers sent to the app by a client
/// outside the allowlist (one that bypasses the reverse proxy) must not sign anybody in on any path,
/// while JWT authentication keeps working for that client.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AutheliaTrustedProxyIntegrationTests : IClassFixture<AutheliaAuthenticationHandlerIntegrationTests.AutheliaHost>
{
    private static readonly IPAddress UntrustedPeer = IPAddress.Parse("192.0.2.44");

    private readonly AutheliaAuthenticationHandlerIntegrationTests.AutheliaHost _host;

    public AutheliaTrustedProxyIntegrationTests(AutheliaAuthenticationHandlerIntegrationTests.AutheliaHost host)
    {
        _host = host;
    }

    [Fact]
    public async Task ForwardedHeaders_FromTheTrustedProxy_SignTheUserIn()
    {
        var user = await _host.CreateUserAsync();

        var (status, body) = await GetAsync("/api/auth/user", AutheliaAuthenticationHandlerIntegrationTests.AutheliaHost.ProxyAddress, Forwarded(user.UserName!, "admins"));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(user.UserName, body.GetProperty("username").GetString());
        Assert.Equal("authelia", body.GetProperty("authMethod").GetString());
    }

    [Fact]
    public async Task ForwardedHeaders_FromAnUntrustedPeer_DoNotSignAnybodyIn()
    {
        var user = await _host.CreateUserAsync();

        var (status, body) = await GetAsync("/api/auth/user", UntrustedPeer, Forwarded(user.UserName!, "admins"));

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal("Unauthorized", body.GetProperty("error").GetString());
        Assert.Empty(await _host.GetRolesAsync(user.Id));
    }

    [Fact]
    public async Task AuthStatus_IgnoresForwardedHeadersFromAnUntrustedPeer()
    {
        var user = await _host.CreateUserAsync();

        var (status, body) = await GetAsync("/api/auth/status", UntrustedPeer, Forwarded(user.UserName!, "admins"));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(body.GetProperty("isAuthenticated").GetBoolean());
        Assert.Equal("jwt", body.GetProperty("authMethod").GetString());
        Assert.True(body.GetProperty("requiresLogin").GetBoolean());
    }

    [Fact]
    public async Task Jwt_FromAnUntrustedPeer_StillAuthenticatesAndForgedHeadersAreIgnored()
    {
        var user = await _host.CreateUserAsync();
        var victim = await _host.CreateUserAsync();
        var (tokenStatus, tokenBody) = await GetAsync("/api/auth/sse-token", AutheliaAuthenticationHandlerIntegrationTests.AutheliaHost.ProxyAddress, Forwarded(user.UserName!, "users"));
        Assert.Equal(HttpStatusCode.OK, tokenStatus);

        var (status, body) = await GetAsync(
            "/api/auth/user",
            UntrustedPeer,
            [.. Forwarded(victim.UserName!, "admins"), ("Authorization", "Bearer " + tokenBody.GetProperty("token").GetString())]);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(user.UserName, body.GetProperty("username").GetString());
        Assert.Equal("jwt", body.GetProperty("authMethod").GetString());
        Assert.Equal(["User"], body.GetProperty("roles").EnumerateArray().Select(role => role.GetString()).ToArray());
        Assert.False(body.GetProperty("canAdminister").GetBoolean());
        Assert.Empty(await _host.GetRolesAsync(victim.Id));
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(string path, IPAddress peer, (string Name, string Value)[] headers)
    {
        // TestServer.SendAsync rather than the HttpClient, to control the connection's remote address.
        var context = await _host.Factory.Server.SendAsync(request =>
        {
            request.Request.Method = HttpMethods.Get;
            request.Request.Path = path;
            request.Connection.RemoteIpAddress = peer;
            foreach (var (name, value) in headers)
            {
                request.Request.Headers[name] = value;
            }
        });

        using var reader = new StreamReader(context.Response.Body);
        using var document = JsonDocument.Parse(await reader.ReadToEndAsync());
        return ((HttpStatusCode)context.Response.StatusCode, document.RootElement.Clone());
    }

    private static (string Name, string Value)[] Forwarded(string userName, string groups) =>
    [
        ("Remote-User", userName),
        ("Remote-Email", userName + "@example.test"),
        ("Remote-Name", "Reader " + userName),
        ("Remote-Groups", groups),
    ];
}
