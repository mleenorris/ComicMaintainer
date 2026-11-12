using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Models.Auth;
using ComicMaintainer.Core.Services.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace ComicMaintainer.Tests.Integration;

/// <summary>
/// Session and stream tokens in the real pipeline (SEC-7 / FE-8): no endpoint takes a session token from the URL,
/// stream tokens open the event stream once and nothing else, and signing out, changing the password or the
/// account revokes issued tokens. Also checks that the raw logs are administrator-only (SEC-5).
/// </summary>
[Trait("Category", "Integration")]
public sealed class SessionTokenIntegrationTests : IClassFixture<SessionTokenIntegrationTests.JwtHost>
{
    /// <summary>The password of the accounts <see cref="JwtHost.SignInNewUserAsync"/> creates.</summary>
    internal const string Password = "Passw0rdOk";
    private readonly JwtHost _host;

    public SessionTokenIntegrationTests(JwtHost host)
    {
        _host = host;
    }

    [Fact]
    public async Task ASessionTokenWorksInTheAuthorizationHeader()
    {
        var (_, token) = await _host.SignInNewUserAsync();

        Assert.Equal(HttpStatusCode.OK, await GetStatusAsync("/api/auth/user", bearer: token));
    }

    // Proxies log request lines, query strings included: a session token must never be accepted from a URL.
    [Theory]
    [InlineData("/api/auth/user")]
    [InlineData("/api/settings")]
    [InlineData("/api/events/stream")]
    public async Task ASessionTokenInTheQueryStringIsRefusedEverywhere(string path)
    {
        var (_, token) = await _host.SignInNewUserAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, await GetStatusAsync($"{path}?access_token={token}"));
    }

    [Fact]
    public async Task AStreamTokenIsShortLivedSingleUseAndOnlyForTheEventStream()
    {
        var (_, session) = await _host.SignInNewUserAsync();
        var token = await GetStreamTokenAsync(session);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        Assert.Equal(["ComicMaintainerAPI/stream"], jwt.Audiences);
        Assert.Equal(AuthTokens.StreamTokenUse, jwt.Claims.Single(c => c.Type == AuthTokens.TokenUseClaim).Value);
        Assert.InRange(jwt.ValidTo - jwt.ValidFrom, TimeSpan.FromSeconds(55), TimeSpan.FromSeconds(65));
        Assert.DoesNotContain(jwt.Claims, c => c.Type == "role");

        // Not a session token, in the header or in the URL of any other endpoint.
        Assert.Equal(HttpStatusCode.Unauthorized, await GetStatusAsync("/api/auth/user", bearer: token));
        Assert.Equal(HttpStatusCode.Unauthorized, await GetStatusAsync("/api/auth/user?access_token=" + token));
        Assert.Equal(HttpStatusCode.Unauthorized, await GetStatusAsync("/api/events/stream", bearer: token));

        // It opens the event stream once.
        using (var stream = await OpenStreamAsync(token))
        {
            Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
            Assert.Equal("text/event-stream", stream.Content.Headers.ContentType?.MediaType);
        }

        using var replay = await OpenStreamAsync(token);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
    }

    // Before 3.0.0 GET /api/auth/sse-token handed out a full session token for the event stream URL.
    [Fact]
    public async Task TheOldSseTokenEndpointNoLongerIssuesTokens()
    {
        var (_, session) = await _host.SignInNewUserAsync();

        Assert.Equal(HttpStatusCode.NotFound, await GetStatusAsync("/api/auth/sse-token", bearer: session));
    }

    [Fact]
    public async Task AnExpiredStreamTokenIsRefused()
    {
        var (user, _) = await _host.SignInNewUserAsync();
        var expired = _host.WriteStreamToken(user, DateTime.UtcNow.AddMinutes(-2), DateTime.UtcNow.AddSeconds(-30));

        using var stream = await OpenStreamAsync(expired);

        Assert.Equal(HttpStatusCode.Unauthorized, stream.StatusCode);
    }

    [Fact]
    public async Task ATokenWithoutTheSessionClaimsIsRefused()
    {
        // A token signed with the right key but issued before 3.0.0, without a security-stamp fingerprint or id,
        // cannot be revoked and is no longer accepted.
        var (user, _) = await _host.SignInNewUserAsync();
        var legacy = _host.WriteSessionToken(user, withSessionClaims: false);
        var current = _host.WriteSessionToken(user, withSessionClaims: true);

        Assert.Equal(HttpStatusCode.Unauthorized, await GetStatusAsync("/api/auth/user", bearer: legacy));
        Assert.Equal(HttpStatusCode.OK, await GetStatusAsync("/api/auth/user", bearer: current));
    }

    [Fact]
    public async Task SigningOutRevokesThatSessionOnly()
    {
        var (user, first) = await _host.SignInNewUserAsync();
        var second = await _host.SignInAsync(user.UserName!, Password);

        using (var logout = await PostAsync("/api/auth/logout", first))
        {
            Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, await GetStatusAsync("/api/auth/user", bearer: first));
        Assert.Equal(HttpStatusCode.OK, await GetStatusAsync("/api/auth/user", bearer: second));
        // A stream token cannot be minted with the revoked session either.
        using var streamToken = await PostAsync("/api/auth/stream-token", first);
        Assert.Equal(HttpStatusCode.Unauthorized, streamToken.StatusCode);
    }

    [Fact]
    public async Task SigningOutOfAllSessionsRevokesEveryToken()
    {
        var (user, first) = await _host.SignInNewUserAsync();
        var second = await _host.SignInAsync(user.UserName!, Password);
        var stream = await GetStreamTokenAsync(second);

        using (var logout = await PostAsync("/api/auth/logout", first, new { allSessions = true }))
        {
            Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, await GetStatusAsync("/api/auth/user", bearer: first));
        Assert.Equal(HttpStatusCode.Unauthorized, await GetStatusAsync("/api/auth/user", bearer: second));
        using var streamResponse = await OpenStreamAsync(stream);
        Assert.Equal(HttpStatusCode.Unauthorized, streamResponse.StatusCode);
    }

    [Fact]
    public async Task ChangingThePasswordRevokesEveryOtherSessionAndReturnsANewToken()
    {
        var (user, first) = await _host.SignInNewUserAsync();
        var second = await _host.SignInAsync(user.UserName!, Password);

        using var change = await PostAsync("/api/auth/change-password", first, new { currentPassword = Password, newPassword = "N3wPassw0rd" });
        Assert.Equal(HttpStatusCode.OK, change.StatusCode);
        using var body = JsonDocument.Parse(await change.Content.ReadAsStringAsync());
        var renewed = body.RootElement.GetProperty("token").GetString()!;

        Assert.Equal(HttpStatusCode.Unauthorized, await GetStatusAsync("/api/auth/user", bearer: first));
        Assert.Equal(HttpStatusCode.Unauthorized, await GetStatusAsync("/api/auth/user", bearer: second));
        Assert.Equal(HttpStatusCode.OK, await GetStatusAsync("/api/auth/user", bearer: renewed));
    }

    [Fact]
    public async Task ChangingTheRolesDisablingOrDeletingTheAccountRevokesItsTokens()
    {
        var (demoted, demotedToken) = await _host.SignInNewUserAsync("Admin");
        var (disabled, disabledToken) = await _host.SignInNewUserAsync();
        var (deleted, deletedToken) = await _host.SignInNewUserAsync();

        await _host.ChangeUserAsync(demoted.Id, async (users, user) =>
        {
            await users.RemoveFromRoleAsync(user, "Admin");
            await users.AddToRoleAsync(user, "ReadOnly");
            await users.UpdateSecurityStampAsync(user);
        });
        await _host.ChangeUserAsync(disabled.Id, async (users, user) =>
        {
            user.IsActive = false;
            await users.UpdateAsync(user);
        });
        await _host.ChangeUserAsync(deleted.Id, async (users, user) => await users.DeleteAsync(user));

        Assert.Equal(HttpStatusCode.Unauthorized, await GetStatusAsync("/api/auth/user", bearer: demotedToken));
        Assert.Equal(HttpStatusCode.Unauthorized, await GetStatusAsync("/api/auth/user", bearer: disabledToken));
        Assert.Equal(HttpStatusCode.Unauthorized, await GetStatusAsync("/api/auth/user", bearer: deletedToken));
    }

    // SEC-5: auth.log and debug.log name every user, role, failed sign-in and client address.
    [Theory]
    [InlineData("ReadOnly", HttpStatusCode.Forbidden)]
    [InlineData("User", HttpStatusCode.Forbidden)]
    [InlineData("Admin", HttpStatusCode.OK)]
    public async Task TheRawLogsAreAdministratorOnly(string role, HttpStatusCode expected)
    {
        var (_, token) = await _host.SignInNewUserAsync(role);

        Assert.Equal(expected, await GetStatusAsync("/api/logs/files?type=auth", bearer: token));
        Assert.Equal(expected, await GetStatusAsync("/api/logs?type=debug", bearer: token));
    }

    private async Task<string> GetStreamTokenAsync(string session)
    {
        using var response = await PostAsync("/api/auth/stream-token", session);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(60, body.RootElement.GetProperty("expiresIn").GetInt32());
        return body.RootElement.GetProperty("token").GetString()!;
    }

    private async Task<HttpResponseMessage> OpenStreamAsync(string streamToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/events/stream?access_token=" + streamToken);
        return await _host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    }

    private async Task<HttpStatusCode> GetStatusAsync(string path, string? bearer = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        using var response = await _host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        return response.StatusCode;
    }

    private async Task<HttpResponseMessage> PostAsync(string path, string bearer, object? body = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return await _host.Client.SendAsync(request);
    }

    /// <summary>The app in JWT mode on a database and config directory of its own.</summary>
    public sealed class JwtHost : IAsyncLifetime
    {
        private readonly string _root = Path.Join(Path.GetTempPath(), "ComicMaintainer.Tests", "Sessions-" + Guid.NewGuid().ToString("N"));
        private readonly WebApplicationFactory<Program> _baseFactory = new();

        public WebApplicationFactory<Program> Factory { get; private set; } = null!;

        public HttpClient Client { get; private set; } = null!;

        public Task InitializeAsync()
        {
            var watched = Directory.CreateDirectory(Path.Join(_root, "watched")).FullName;
            var duplicates = Directory.CreateDirectory(Path.Join(_root, "duplicates")).FullName;
            var config = Directory.CreateDirectory(Path.Join(_root, "config")).FullName;

            Factory = _baseFactory.WithWebHostBuilder(builder =>
            {
                builder.UseSetting("AppSettings:WatchedDirectory", watched);
                builder.UseSetting("AppSettings:DuplicateDirectory", duplicates);
                builder.UseSetting("AppSettings:ConfigDirectory", config);
                builder.UseSetting("CONFIG_DIR", config);
                builder.ConfigureServices(services => services.AddSingleton<IStartupFilter, DistinctClientAddressStartupFilter>());
            });
            Client = Factory.CreateClient();
            return Task.CompletedTask;
        }

        // Each request comes from an address of its own, so that the per-client limit on the endpoints that check
        // a password (AuthenticationRateLimit) does not get in the way of the many requests these tests make.
        private sealed class DistinctClientAddressStartupFilter : IStartupFilter
        {
            private int _requests;

            public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
            {
                app.Use((context, nextMiddleware) =>
                {
                    var request = Interlocked.Increment(ref _requests);
                    context.Connection.RemoteIpAddress = new IPAddress([198, 18, (byte)(request >> 8), (byte)request]);
                    return nextMiddleware();
                });
                next(app);
            };
        }

        public async Task DisposeAsync()
        {
            Client?.Dispose();
            await _baseFactory.DisposeAsync();
            SqliteConnection.ClearAllPools();
            Helpers.TestIO.TryDeleteDirectory(_root);
        }

        public async Task<(ApplicationUser User, string Token)> SignInNewUserAsync(string role = "User")
        {
            var name = "session-" + Guid.NewGuid().ToString("N")[..12];
            var user = new ApplicationUser { UserName = name, Email = name + "@example.test", IsActive = true };
            using (var scope = Factory.Services.CreateScope())
            {
                var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                var created = await users.CreateAsync(user, Password);
                Assert.True(created.Succeeded, string.Join(", ", created.Errors.Select(e => e.Description)));
                Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
            }

            return (user, await SignInAsync(name, Password));
        }

        // Signs in through AuthService directly: the HTTP sign-in endpoint is rate limited per client.
        public async Task<string> SignInAsync(string username, string password)
        {
            using var scope = Factory.Services.CreateScope();
            var (success, token, error) = await scope.ServiceProvider
                .GetRequiredService<ComicMaintainer.Core.Interfaces.IAuthService>()
                .LoginAsync(username, password);
            Assert.True(success, error);
            return token;
        }

        /// <summary>A stream token for <paramref name="user"/> valid from <paramref name="notBefore"/> to <paramref name="expires"/>.</summary>
        public string WriteStreamToken(ApplicationUser user, DateTime notBefore, DateTime expires)
        {
            var settings = Factory.Services.GetRequiredService<IOptions<JwtSettings>>().Value;
            return WriteToken(
                settings,
                AuthTokens.StreamAudience(settings.Audience),
                [
                    new Claim(ClaimTypes.NameIdentifier, user.Id),
                    new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
                    new Claim(AuthTokens.SecurityStampClaim, AuthTokens.Fingerprint(FindUser(user.Id).SecurityStamp)),
                    new Claim(AuthTokens.TokenUseClaim, AuthTokens.StreamTokenUse),
                ],
                notBefore,
                expires);
        }

        /// <summary>
        /// A session token for <paramref name="user"/>, signed with the app's key; without
        /// <paramref name="withSessionClaims"/> it lacks the <c>jti</c> and security-stamp claims, like tokens issued
        /// before 3.0.0.
        /// </summary>
        public string WriteSessionToken(ApplicationUser user, bool withSessionClaims)
        {
            var settings = Factory.Services.GetRequiredService<IOptions<JwtSettings>>().Value;
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, user.Id), new(ClaimTypes.Name, user.UserName!) };
            if (withSessionClaims)
            {
                claims.Add(new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()));
                claims.Add(new Claim(AuthTokens.SecurityStampClaim, AuthTokens.Fingerprint(FindUser(user.Id).SecurityStamp)));
            }

            return WriteToken(settings, settings.Audience, claims, DateTime.UtcNow, DateTime.UtcNow.AddHours(1));
        }

        private ApplicationUser FindUser(string userId)
        {
            using var scope = Factory.Services.CreateScope();
            return scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByIdAsync(userId).GetAwaiter().GetResult()!;
        }

        private static string WriteToken(JwtSettings settings, string audience, IEnumerable<Claim> claims, DateTime notBefore, DateTime expires)
        {
            var credentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.Secret)), SecurityAlgorithms.HmacSha256);
            return new JwtSecurityTokenHandler().WriteToken(
                new JwtSecurityToken(settings.Issuer, audience, claims, notBefore, expires, credentials));
        }

        public async Task ChangeUserAsync(string userId, Func<UserManager<ApplicationUser>, ApplicationUser, Task> change)
        {
            using var scope = Factory.Services.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            await change(users, (await users.FindByIdAsync(userId))!);
            scope.ServiceProvider.GetRequiredService<IAuthSessionValidator>().Invalidate(userId);
        }
    }
}
