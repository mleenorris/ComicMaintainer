using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Models.Auth;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Core.Services.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ComicMaintainer.Tests.Integration;

/// <summary>
/// The Users page's API (<c>/api/users</c>) through the real authentication and authorization pipeline, with session
/// tokens: who may call it, what each change does to the account's existing sessions and API key, the rules that refuse
/// a change, and the administrator's own password that changes granting lasting access need. The last-administrator
/// rule cannot be reached here (the caller is always another active administrator); the service tests cover it.
/// </summary>
[Trait("Category", "Integration")]
public sealed class UsersApiIntegrationTests : IClassFixture<SessionTokenIntegrationTests.JwtHost>
{
    private const string Password = SessionTokenIntegrationTests.Password;
    private const string NewPassword = "N3wAccountOk";
    private const string MissingId = "00000000-0000-0000-0000-000000000000";

    private readonly SessionTokenIntegrationTests.JwtHost _host;

    public UsersApiIntegrationTests(SessionTokenIntegrationTests.JwtHost host)
    {
        _host = host;
    }

    public static TheoryData<string, string> Endpoints => new()
    {
        { "GET", "/api/users" },
        { "POST", "/api/users" },
        { "PUT", $"/api/users/{MissingId}/role" },
        { "POST", $"/api/users/{MissingId}/password" },
        { "PUT", $"/api/users/{MissingId}/active" },
        { "DELETE", $"/api/users/{MissingId}" },
    };

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task AnAnonymousCaller_IsRefusedWith401(string method, string path)
    {
        var (status, _) = await SendAsync(method, path, body: AnyBody(path));

        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }

    [Theory]
    [InlineData("User")]
    [InlineData("ReadOnly")]
    public async Task ASignedInNonAdministrator_IsRefusedWith403Everywhere_AndNoAccountIsCreated(string role)
    {
        var (_, token) = await _host.SignInNewUserAsync(role);
        var username = NewUsername();

        foreach (var (method, path) in EndpointList())
        {
            var body = path == "/api/users" ? CreateBody(username) : AnyBody(path);
            var (status, _) = await SendAsync(method, path, token, body: body);
            Assert.True(status == HttpStatusCode.Forbidden, $"{method} {path} answered {status}");
        }

        Assert.Null(await FindUserAsync(username));
    }

    [Fact]
    public async Task List_ShowsEveryAccount_AndNothingSecret()
    {
        var (admin, token) = await _host.SignInNewUserAsync("Admin");
        var (other, _) = await _host.SignInNewUserAsync("ReadOnly");

        var (status, body) = await SendAsync("GET", "/api/users", token);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(body.GetProperty("autheliaEnabled").GetBoolean());
        var users = body.GetProperty("users").EnumerateArray().ToList();
        var mine = users.Single(u => u.GetProperty("id").GetString() == admin.Id);
        Assert.True(mine.GetProperty("isCurrentUser").GetBoolean());
        Assert.Equal("Admin", mine.GetProperty("role").GetString());
        var theirs = users.Single(u => u.GetProperty("id").GetString() == other.Id);
        Assert.False(theirs.GetProperty("isCurrentUser").GetBoolean());
        Assert.Equal("ReadOnly", theirs.GetProperty("role").GetString());
        Assert.Equal(other.Email, theirs.GetProperty("email").GetString());
        Assert.EndsWith("Z", theirs.GetProperty("createdAt").GetString());

        string[] expected = ["id", "username", "fullName", "email", "role", "isActive", "lockedOutUntil", "createdAt", "lastLoginAt", "hasApiKey", "isCurrentUser"];
        foreach (var user in users)
        {
            Assert.Equal(expected, user.EnumerateObject().Select(p => p.Name));
        }

        var names = users.Select(u => u.GetProperty("username").GetString()!).ToList();
        Assert.Equal(names.Order(StringComparer.OrdinalIgnoreCase), names);
    }

    [Fact]
    public async Task Create_AddsAnAccountWithTheRole_ThatCanSignIn()
    {
        var (_, token) = await _host.SignInNewUserAsync("Admin");
        var username = NewUsername();

        var (status, body) = await SendAsync("POST", "/api/users", token, body: CreateBody(username, "readonly", Password));

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal(username, body.GetProperty("username").GetString());
        Assert.Equal("ReadOnly", body.GetProperty("role").GetString());
        Assert.Equal(username + "@comicmaintainer.local", body.GetProperty("email").GetString());
        var newToken = await _host.SignInAsync(username, NewPassword);
        var (meStatus, me) = await SendAsync("GET", "/api/auth/user", newToken);
        Assert.Equal(HttpStatusCode.OK, meStatus);
        Assert.False(me.GetProperty("canModifyLibrary").GetBoolean());
    }

    [Fact]
    public async Task Create_WithoutTheAdministratorsPassword_OrAWrongOne_Is403_AndAWrongOneCountsTowardsTheirLockout()
    {
        var (admin, token) = await _host.SignInNewUserAsync("Admin");
        var username = NewUsername();

        var (missing, missingBody) = await SendAsync("POST", "/api/users", token, body: CreateBody(username, "User", currentPassword: null));
        var (wrong, wrongBody) = await SendAsync("POST", "/api/users", token, body: CreateBody(username, "User", "Wr0ngGuess"));

        Assert.Equal(HttpStatusCode.Forbidden, missing);
        Assert.Equal(AuthService.WrongCurrentPasswordError, missingBody.GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, wrong);
        Assert.Equal(AuthService.WrongCurrentPasswordError, wrongBody.GetProperty("error").GetString());
        Assert.Null(await FindUserAsync(username));
        Assert.Equal(1, (await FindUserByIdAsync(admin.Id))!.AccessFailedCount);
    }

    [Fact]
    public async Task Create_RefusesADuplicateWith409_AndAnInvalidRequestWith400()
    {
        var (existing, _) = await _host.SignInNewUserAsync();
        var (_, token) = await _host.SignInNewUserAsync("Admin");

        var (duplicate, duplicateBody) = await SendAsync("POST", "/api/users", token, body: CreateBody(existing.UserName!, "User", Password));
        var (badRole, badRoleBody) = await SendAsync("POST", "/api/users", token, body: CreateBody(NewUsername(), "Owner", Password));
        var (weak, weakBody) = await SendAsync("POST", "/api/users", token, body: new { username = NewUsername(), password = "short", role = "User", currentPassword = Password });

        Assert.Equal(HttpStatusCode.Conflict, duplicate);
        Assert.Equal(UserAdministrationService.DuplicateUsernameError, duplicateBody.GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, badRole);
        Assert.Equal(UserAdministrationService.InvalidRoleError, badRoleBody.GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, weak);
        Assert.Contains("at least 8 characters", weakBody.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChangeRole_EndsTheAccountsSessions_WhichCarryTheOldRole()
    {
        var (_, adminToken) = await _host.SignInNewUserAsync("Admin");
        var (target, targetToken) = await _host.SignInNewUserAsync("User");

        var (missing, _) = await SendAsync("PUT", $"/api/users/{target.Id}/role", adminToken, body: new { role = "ReadOnly" });
        Assert.Equal(HttpStatusCode.Forbidden, missing);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync("GET", "/api/auth/user", targetToken)).Status);

        var (status, body) = await SendAsync("PUT", $"/api/users/{target.Id}/role", adminToken, body: new { role = "ReadOnly", currentPassword = Password });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("ReadOnly", body.GetProperty("role").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync("GET", "/api/auth/user", targetToken)).Status);
        var (_, me) = await SendAsync("GET", "/api/auth/user", await _host.SignInAsync(target.UserName!, Password));
        Assert.Equal(["ReadOnly"], me.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
    }

    [Fact]
    public async Task ResetPassword_EndsTheSessions_RevokesTheApiKey_AndSetsThePassword()
    {
        var (_, adminToken) = await _host.SignInNewUserAsync("Admin");
        var (target, targetToken) = await _host.SignInNewUserAsync("User");
        var apiKey = await CreateApiKeyAsync(targetToken);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync("GET", "/api/auth/user", apiKey: apiKey)).Status);

        var (wrong, _) = await SendAsync("POST", $"/api/users/{target.Id}/password", adminToken, body: new { newPassword = NewPassword, currentPassword = "Wr0ngGuess" });
        Assert.Equal(HttpStatusCode.Forbidden, wrong);

        var (status, body) = await SendAsync("POST", $"/api/users/{target.Id}/password", adminToken, body: new { newPassword = NewPassword, currentPassword = Password });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(body.GetProperty("hasApiKey").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync("GET", "/api/auth/user", targetToken)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync("GET", "/api/auth/user", apiKey: apiKey)).Status);
        Assert.False(string.IsNullOrEmpty(await _host.SignInAsync(target.UserName!, NewPassword)));
    }

    [Fact]
    public async Task Disable_EndsTheSessionsAndTheApiKeyAtOnce_AndEnablingNeedsTheAdministratorsPassword()
    {
        var (_, adminToken) = await _host.SignInNewUserAsync("Admin");
        var (target, targetToken) = await _host.SignInNewUserAsync("User");
        var apiKey = await CreateApiKeyAsync(targetToken);

        var (status, body) = await SendAsync("PUT", $"/api/users/{target.Id}/active", adminToken, body: new { isActive = false });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(body.GetProperty("isActive").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync("GET", "/api/auth/user", targetToken)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync("GET", "/api/auth/user", apiKey: apiKey)).Status);
        await using (var scope = _host.Factory.Services.CreateAsyncScope())
        {
            var (signedIn, _, _) = await scope.ServiceProvider.GetRequiredService<ComicMaintainer.Core.Interfaces.IAuthService>().LoginAsync(target.UserName!, Password);
            Assert.False(signedIn);
        }

        var (refused, _) = await SendAsync("PUT", $"/api/users/{target.Id}/active", adminToken, body: new { isActive = true });
        Assert.Equal(HttpStatusCode.Forbidden, refused);

        var (enabled, enabledBody) = await SendAsync("PUT", $"/api/users/{target.Id}/active", adminToken, body: new { isActive = true, currentPassword = Password });

        Assert.Equal(HttpStatusCode.OK, enabled);
        Assert.True(enabledBody.GetProperty("isActive").GetBoolean());
        // Enabling does not bring back the sessions and the key from before.
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync("GET", "/api/auth/user", targetToken)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync("GET", "/api/auth/user", apiKey: apiKey)).Status);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync("GET", "/api/auth/user", await _host.SignInAsync(target.UserName!, Password))).Status);
    }

    [Fact]
    public async Task SetActive_WithoutSayingWhich_Is400()
    {
        var (_, adminToken) = await _host.SignInNewUserAsync("Admin");
        var (target, _) = await _host.SignInNewUserAsync("User");

        var (status, body) = await SendAsync("PUT", $"/api/users/{target.Id}/active", adminToken, body: new { currentPassword = Password });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.True(body.TryGetProperty("error", out _));
        Assert.True((await FindUserByIdAsync(target.Id))!.IsActive);
    }

    [Fact]
    public async Task Delete_RemovesTheAccountAndItsData_AndItsTokensStopWorking()
    {
        var (_, adminToken) = await _host.SignInNewUserAsync("Admin");
        var (target, targetToken) = await _host.SignInNewUserAsync("User");
        await using (var scope = _host.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ComicMaintainerDbContext>();
            db.UserPreferences.Add(new UserPreferencesEntity { UserId = target.Id, Theme = "dark" });
            db.EreaderDevices.Add(new EreaderDeviceEntity { OwnerUserId = target.Id, Name = "Kindle", EmailAddress = target.Id + "@kindle.example" });
            await db.SaveChangesAsync();
        }

        var (status, _) = await SendAsync("DELETE", $"/api/users/{target.Id}", adminToken);

        Assert.Equal(HttpStatusCode.NoContent, status);
        Assert.Null(await FindUserByIdAsync(target.Id));
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync("GET", "/api/auth/user", targetToken)).Status);
        await using (var scope = _host.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ComicMaintainerDbContext>();
            Assert.False(await db.UserPreferences.AnyAsync(p => p.UserId == target.Id));
            Assert.False(await db.EreaderDevices.AnyAsync(d => d.OwnerUserId == target.Id));
        }

        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync("DELETE", $"/api/users/{target.Id}", adminToken)).Status);
    }

    [Fact]
    public async Task TheAdministratorsOwnAccount_IsRefusedWith409()
    {
        var (admin, token) = await _host.SignInNewUserAsync("Admin");

        var (role, roleBody) = await SendAsync("PUT", $"/api/users/{admin.Id}/role", token, body: new { role = "User", currentPassword = Password });
        var (password, passwordBody) = await SendAsync("POST", $"/api/users/{admin.Id}/password", token, body: new { newPassword = NewPassword, currentPassword = Password });
        var (active, activeBody) = await SendAsync("PUT", $"/api/users/{admin.Id}/active", token, body: new { isActive = false });
        var (delete, deleteBody) = await SendAsync("DELETE", $"/api/users/{admin.Id}", token);

        Assert.Equal(HttpStatusCode.Conflict, role);
        Assert.Equal(UserAdministrationService.OwnRoleError, roleBody.GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.Conflict, password);
        Assert.Equal(UserAdministrationService.OwnPasswordError, passwordBody.GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.Conflict, active);
        Assert.Equal(UserAdministrationService.OwnActiveError, activeBody.GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.Conflict, delete);
        Assert.Equal(UserAdministrationService.OwnDeleteError, deleteBody.GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.OK, (await SendAsync("GET", "/api/auth/user", token)).Status);
    }

    [Fact]
    public async Task AnUnknownAccount_Is404()
    {
        var (_, token) = await _host.SignInNewUserAsync("Admin");

        foreach (var (method, path) in EndpointList().Where(e => e.Path != "/api/users"))
        {
            var (status, body) = await SendAsync(method, path, token, body: AnyBody(path));
            Assert.True(status == HttpStatusCode.NotFound, $"{method} {path} answered {status}");
            Assert.True(body.TryGetProperty("error", out _));
        }
    }

    private static IEnumerable<(string Method, string Path)> EndpointList() =>
        Endpoints.Select(row => ((string)row[0], (string)row[1]));

    private static string NewUsername() => "managed-" + Guid.NewGuid().ToString("N")[..12];

    private static object CreateBody(string username, string role = "User", string? currentPassword = Password) =>
        new { username, password = NewPassword, role, currentPassword };

    // A body every endpoint accepts, so that what is checked is who may call it, not the request.
    private static object? AnyBody(string path) => path switch
    {
        "/api/users" => CreateBody(NewUsername()),
        _ when path.EndsWith("/role", StringComparison.Ordinal) => new { role = "User", currentPassword = Password },
        _ when path.EndsWith("/password", StringComparison.Ordinal) => new { newPassword = NewPassword, currentPassword = Password },
        _ when path.EndsWith("/active", StringComparison.Ordinal) => new { isActive = false },
        _ => null,
    };

    private async Task<string> CreateApiKeyAsync(string session)
    {
        var (status, body) = await SendAsync("POST", "/api/auth/api-key", session, body: new { currentPassword = Password });
        Assert.Equal(HttpStatusCode.OK, status);
        return body.GetProperty("apiKey").GetString()!;
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(
        string method, string path, string? bearer = null, string? apiKey = null, object? body = null)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = body is null ? null : JsonContent.Create(body) };
        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        if (apiKey is not null)
        {
            request.Headers.Add(ApiKeys.HeaderName, apiKey);
        }

        using var response = await _host.Client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
        return (response.StatusCode, document.RootElement.Clone());
    }

    private async Task<ApplicationUser?> FindUserAsync(string username)
    {
        using var scope = _host.Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByNameAsync(username);
    }

    private async Task<ApplicationUser?> FindUserByIdAsync(string id)
    {
        using var scope = _host.Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByIdAsync(id);
    }
}

/// <summary>
/// <c>/api/users</c> with Authelia enabled: Authelia creates the accounts, sets their roles from its groups and keeps
/// their passwords, so creating accounts, changing roles and setting passwords are refused; disabling and enabling are
/// ComicMaintainer's own and keep working, and an Authelia session is not asked for a password it does not have here.
/// </summary>
[Trait("Category", "Integration")]
public sealed class UsersApiAutheliaIntegrationTests : IClassFixture<AutheliaAuthenticationHandlerIntegrationTests.AutheliaHost>
{
    private readonly AutheliaAuthenticationHandlerIntegrationTests.AutheliaHost _host;

    public UsersApiAutheliaIntegrationTests(AutheliaAuthenticationHandlerIntegrationTests.AutheliaHost host)
    {
        _host = host;
    }

    [Fact]
    public async Task CreatingAccounts_ChangingRoles_AndSettingPasswords_AreRefusedWith409()
    {
        var admin = await _host.CreateUserAsync();
        var target = await _host.CreateUserAsync();

        var (list, listBody) = await SendAsync("GET", "/api/users", admin, "admins");
        var (create, createBody) = await SendAsync("POST", "/api/users", admin, "admins", new { username = "authelia-made", password = "N3wAccountOk", role = "User" });
        var (role, roleBody) = await SendAsync("PUT", $"/api/users/{target.Id}/role", admin, "admins", new { role = "Admin" });
        var (password, passwordBody) = await SendAsync("POST", $"/api/users/{target.Id}/password", admin, "admins", new { newPassword = "N3wAccountOk" });

        Assert.Equal(HttpStatusCode.OK, list);
        Assert.True(listBody.GetProperty("autheliaEnabled").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, create);
        Assert.Equal(UserAdministrationService.AutheliaCreateError, createBody.GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.Conflict, role);
        Assert.Equal(UserAdministrationService.AutheliaRoleError, roleBody.GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.Conflict, password);
        Assert.Equal(UserAdministrationService.AutheliaPasswordError, passwordBody.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ADisabledAccount_IsRefusedEvenThoughAutheliaVouchesForIt_AndEnablingAsksAnAutheliaSessionForNoPassword()
    {
        var admin = await _host.CreateUserAsync();
        var target = await _host.CreateUserAsync();
        Assert.Equal(HttpStatusCode.OK, (await SendAsync("GET", "/api/auth/user", target, "users")).Status);

        var (disabled, _) = await SendAsync("PUT", $"/api/users/{target.Id}/active", admin, "admins", new { isActive = false });

        Assert.Equal(HttpStatusCode.OK, disabled);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync("GET", "/api/auth/user", target, "users")).Status);

        var (enabled, _) = await SendAsync("PUT", $"/api/users/{target.Id}/active", admin, "admins", new { isActive = true });

        Assert.Equal(HttpStatusCode.OK, enabled);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync("GET", "/api/auth/user", target, "users")).Status);
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(string method, string path, ApplicationUser user, string groups, object? body = null)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Add("Remote-User", user.UserName);
        request.Headers.Add("Remote-Email", user.Email);
        request.Headers.Add("Remote-Name", user.FullName);
        request.Headers.Add("Remote-Groups", groups);
        using var response = await _host.Client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
        return (response.StatusCode, document.RootElement.Clone());
    }
}
