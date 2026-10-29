using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using ComicMaintainer.Core.Models.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace ComicMaintainer.Tests.Integration;

/// <summary>
/// End-to-end checks of Authelia forward authentication in the real pipeline: the scheme registered by
/// <c>AddComicMaintainerAuth</c> when <c>AutheliaSettings:Enabled</c> is on, the handler resolved from the
/// app's container, the authorization policies and the auth endpoints. Most accounts are created up front
/// to exercise the sign-in of known users; first-time provisioning goes through the real Identity
/// validators (unique email, password policy).
/// </summary>
[Trait("Category", "Integration")]
public sealed class AutheliaAuthenticationHandlerIntegrationTests : IClassFixture<AutheliaAuthenticationHandlerIntegrationTests.AutheliaHost>
{
    private readonly AutheliaHost _host;

    public AutheliaAuthenticationHandlerIntegrationTests(AutheliaHost host)
    {
        _host = host;
    }

    [Fact]
    public async Task MemberOfAnAdminGroup_IsSignedInAsAnAdministrator()
    {
        var user = await _host.CreateUserAsync();

        // Group names are matched case-insensitively and the list is trimmed.
        var (status, body) = await GetAsync("/api/auth/user", Forwarded(user, "users, Admins"));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(user.UserName, body.GetProperty("username").GetString());
        Assert.Equal(user.Id, body.GetProperty("userId").GetString());
        Assert.Equal(user.Email, body.GetProperty("email").GetString());
        Assert.Equal("authelia", body.GetProperty("authMethod").GetString());
        Assert.Equal(["Admin"], Roles(body));
        Assert.True(body.GetProperty("canModifyLibrary").GetBoolean());
        Assert.True(body.GetProperty("canAdminister").GetBoolean());
    }

    [Fact]
    public async Task UserOutsideTheAdminGroups_GetsTheDefaultRole()
    {
        var user = await _host.CreateUserAsync();

        var (status, body) = await GetAsync("/api/auth/user", Forwarded(user, "users,family"));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(["User"], Roles(body));
        Assert.True(body.GetProperty("canModifyLibrary").GetBoolean());
        Assert.False(body.GetProperty("canAdminister").GetBoolean());
    }

    [Fact]
    public async Task Role_FollowsTheForwardedGroupsOnEveryRequest()
    {
        var user = await _host.CreateUserAsync();

        var (_, promoted) = await GetAsync("/api/auth/user", Forwarded(user, "operators"));
        Assert.Equal(["Admin"], Roles(promoted));

        var (status, demoted) = await GetAsync("/api/auth/user", Forwarded(user, "users"));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(["User"], Roles(demoted));
        Assert.False(demoted.GetProperty("canAdminister").GetBoolean());
        Assert.Equal(["User"], await _host.GetRolesAsync(user.Id));
    }

    [Fact]
    public async Task ForwardedEmailAndName_AreSavedToTheAccount()
    {
        var user = await _host.CreateUserAsync();
        var newEmail = "moved-" + user.Email;

        var (status, body) = await GetAsync("/api/auth/user", Forwarded(user, "users", newEmail, "Renamed Reader"));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(newEmail, body.GetProperty("email").GetString());
        var saved = await _host.FindUserAsync(user.Id);
        Assert.Equal(newEmail, saved.Email);
        Assert.Equal("Renamed Reader", saved.FullName);
    }

    [Fact]
    public async Task FirstSignInWithoutForwardedEmailOrName_ProvisionsTheAccountWithFallbacks()
    {
        var name = "authelia-new-" + Guid.NewGuid().ToString("N")[..12];
        Action<HttpRequestHeaders> onlyUserAndGroups = headers =>
        {
            headers.Add("Remote-User", name);
            headers.Add("Remote-Groups", "users");
        };

        var (status, body) = await GetAsync("/api/auth/user", onlyUserAndGroups);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(name, body.GetProperty("username").GetString());
        Assert.Equal(name + "@authelia.local", body.GetProperty("email").GetString());
        Assert.Equal(["User"], Roles(body));
        var userId = body.GetProperty("userId").GetString()!;
        var saved = await _host.FindUserAsync(userId);
        Assert.Equal(name, saved.UserName);
        Assert.Equal(name + "@authelia.local", saved.Email);
        Assert.Equal(name, saved.FullName);
        Assert.Equal(["User"], await _host.GetRolesAsync(userId));

        // The next request signs the provisioned account in rather than creating another one.
        var (againStatus, again) = await GetAsync("/api/auth/user", onlyUserAndGroups);

        Assert.Equal(HttpStatusCode.OK, againStatus);
        Assert.Equal(userId, again.GetProperty("userId").GetString());
    }

    [Theory]
    [InlineData("@corp.example", "@corp.example")]                  // an email address: the account uses it
    [InlineData("@corp@example", "=corp=example@authelia.local")]   // not one: every '@' becomes '=' in the placeholder
    public async Task FirstSignInWithoutForwardedEmail_WhenTheUsernameContainsAnAtSign_ProvisionsAValidEmail(
        string usernameSuffix, string emailSuffix)
    {
        // Regression test for #874: the placeholder "<username>@authelia.local" has two '@' for such a username,
        // so Identity rejected the account and every request was answered with 401.
        var prefix = "authelia-at-" + Guid.NewGuid().ToString("N")[..12];
        var name = prefix + usernameSuffix;
        var expectedEmail = prefix + emailSuffix;

        var (status, body) = await GetAsync("/api/auth/user", headers =>
        {
            headers.Add("Remote-User", name);
            headers.Add("Remote-Groups", "users");
        });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(name, body.GetProperty("username").GetString());
        Assert.Equal(expectedEmail, body.GetProperty("email").GetString());
        var saved = await _host.FindUserAsync(body.GetProperty("userId").GetString()!);
        Assert.Equal(name, saved.UserName);
        Assert.Equal(expectedEmail, saved.Email);
    }

    [Theory]
    [InlineData("@", "_")]
    [InlineData("@authelia.local", "")]
    [InlineData("@AUTHELIA.LOCAL", "")]
    public async Task FirstSignInsWithoutForwardedEmail_OfUsernamesThatCouldShareAFallbackEmail_BothSucceed(
        string usernameSuffix, string otherUsernameSuffix)
    {
        // Identity refuses an email that another account already has, so each of these accounts needs its own
        // fallback email; otherwise whichever user signed in second was answered with 401 on every request.
        var prefix = "authelia-pair-" + Guid.NewGuid().ToString("N")[..12];
        var userIds = new List<string>();

        foreach (var name in new[] { prefix + usernameSuffix, prefix + otherUsernameSuffix })
        {
            var (status, body) = await GetAsync("/api/auth/user", headers =>
            {
                headers.Add("Remote-User", name);
                headers.Add("Remote-Groups", "users");
            });

            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal(name, body.GetProperty("username").GetString());
            userIds.Add(body.GetProperty("userId").GetString()!);
        }

        Assert.NotEqual(userIds[0], userIds[1]);
    }

    [Fact]
    public async Task WithoutForwardedHeaders_ProtectedEndpointsAreUnauthorized()
    {
        var (status, body) = await GetAsync("/api/auth/user");

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal("Unauthorized", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task AuthStatus_ReportsWhetherTheRequestCarriesAnAutheliaSession()
    {
        var user = await _host.CreateUserAsync();

        var (anonymousStatus, anonymous) = await GetAsync("/api/auth/status");
        var (signedInStatus, signedIn) = await GetAsync("/api/auth/status", Forwarded(user, "users"));

        Assert.Equal(HttpStatusCode.OK, anonymousStatus);
        Assert.True(anonymous.GetProperty("autheliaEnabled").GetBoolean());
        Assert.False(anonymous.GetProperty("isAuthenticated").GetBoolean());
        Assert.Equal("jwt", anonymous.GetProperty("authMethod").GetString());
        Assert.True(anonymous.GetProperty("requiresLogin").GetBoolean());

        Assert.Equal(HttpStatusCode.OK, signedInStatus);
        Assert.True(signedIn.GetProperty("autheliaEnabled").GetBoolean());
        Assert.True(signedIn.GetProperty("isAuthenticated").GetBoolean());
        Assert.Equal(user.UserName, signedIn.GetProperty("username").GetString());
        Assert.Equal("authelia", signedIn.GetProperty("authMethod").GetString());
        Assert.False(signedIn.GetProperty("requiresLogin").GetBoolean());
    }

    [Fact]
    public async Task SseToken_IssuedToAnAutheliaSession_IsAcceptedWithoutTheForwardedHeaders()
    {
        var user = await _host.CreateUserAsync();

        var (tokenStatus, tokenBody) = await GetAsync("/api/auth/sse-token", Forwarded(user, "users"));
        Assert.Equal(HttpStatusCode.OK, tokenStatus);
        var token = tokenBody.GetProperty("token").GetString();

        // EventSource connections cannot send the forwarded headers, so the JWT scheme must still be accepted.
        var (status, body) = await GetAsync("/api/auth/user", headers => headers.Authorization = new AuthenticationHeaderValue("Bearer", token));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(user.UserName, body.GetProperty("username").GetString());
        Assert.Equal("jwt", body.GetProperty("authMethod").GetString());
        Assert.Equal(["User"], Roles(body));
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(string path, Action<HttpRequestHeaders>? headers = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        headers?.Invoke(request.Headers);
        using var response = await _host.Client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(text);
        return (response.StatusCode, document.RootElement.Clone());
    }

    private static Action<HttpRequestHeaders> Forwarded(ApplicationUser user, string groups, string? email = null, string? name = null) => headers =>
    {
        headers.Add("Remote-User", user.UserName);
        headers.Add("Remote-Email", email ?? user.Email);
        headers.Add("Remote-Name", name ?? user.FullName);
        headers.Add("Remote-Groups", groups);
    };

    private static string[] Roles(JsonElement body) =>
        body.GetProperty("roles").EnumerateArray().Select(role => role.GetString()!).ToArray();

    /// <summary>One app instance with Authelia enabled, on its own temp directories and SQLite database.</summary>
    public sealed class AutheliaHost : IAsyncLifetime
    {
        /// <summary>
        /// The reverse proxy (inside the configured <c>AutheliaSettings:TrustedProxies</c>, as an IPv4 peer of a
        /// dual-mode socket) that requests come from when a test does not set the connection's remote address
        /// itself; TestServer leaves it null.
        /// </summary>
        public static readonly IPAddress ProxyAddress = IPAddress.Parse("::ffff:172.18.0.2");

        private readonly string _root = Path.Join(Path.GetTempPath(), "ComicMaintainer.Tests", "Authelia-" + Guid.NewGuid().ToString("N"));
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
                builder.UseEnvironment("Development");
                builder.UseSetting("AppSettings:WatchedDirectory", watched);
                builder.UseSetting("AppSettings:DuplicateDirectory", duplicates);
                builder.UseSetting("AppSettings:ConfigDirectory", config);
                builder.UseSetting("CONFIG_DIR", config);
                builder.UseSetting("AutheliaSettings:Enabled", "true");
                builder.UseSetting("AutheliaSettings:AdminGroups", "admins, operators");
                builder.UseSetting("AutheliaSettings:TrustedProxies", "172.18.0.0/16");
                builder.ConfigureServices(services => services.AddSingleton<IStartupFilter, ProxyPeerStartupFilter>());
            });
            Client = Factory.CreateClient();
            return Task.CompletedTask;
        }

        public async Task DisposeAsync()
        {
            Client?.Dispose();
            await _baseFactory.DisposeAsync();
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort cleanup.
            }
        }

        public async Task<ApplicationUser> CreateUserAsync()
        {
            var name = "authelia-" + Guid.NewGuid().ToString("N")[..12];
            var user = new ApplicationUser
            {
                UserName = name,
                Email = name + "@example.test",
                FullName = "Reader " + name,
                EmailConfirmed = true,
            };

            using var scope = Factory.Services.CreateScope();
            var result = await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().CreateAsync(user, "Passw0rdOk");
            Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(e => e.Description)));
            return user;
        }

        public async Task<ApplicationUser> FindUserAsync(string id)
        {
            using var scope = Factory.Services.CreateScope();
            var user = await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByIdAsync(id);
            return Assert.IsType<ApplicationUser>(user);
        }

        public async Task<IList<string>> GetRolesAsync(string id)
        {
            using var scope = Factory.Services.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            return await users.GetRolesAsync(Assert.IsType<ApplicationUser>(await users.FindByIdAsync(id)));
        }

        /// <summary>Makes requests without a remote address come from <see cref="ProxyAddress"/>.</summary>
        private sealed class ProxyPeerStartupFilter : IStartupFilter
        {
            public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
            {
                app.Use((context, nextMiddleware) =>
                {
                    context.Connection.RemoteIpAddress ??= ProxyAddress;
                    return nextMiddleware(context);
                });
                next(app);
            };
        }
    }
}
