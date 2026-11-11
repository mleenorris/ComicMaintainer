using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ComicMaintainer.Core.Models.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace ComicMaintainer.Tests.Integration;

/// <summary>
/// Who may create an account with <c>POST /api/auth/register</c>, through the real authentication and authorization
/// pipeline. The web app has no sign-up page, so the route is how administrators add accounts, and only they may.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RegisterAuthorizationIntegrationTests : IClassFixture<SessionTokenIntegrationTests.JwtHost>
{
    private const string NewPassword = "N3wAccountOk";

    private readonly SessionTokenIntegrationTests.JwtHost _host;

    public RegisterAuthorizationIntegrationTests(SessionTokenIntegrationTests.JwtHost host)
    {
        _host = host;
    }

    [Fact]
    public async Task AnAnonymousCaller_IsRefusedWith401_AndNoAccountIsCreated()
    {
        var username = NewUsername();

        using var response = await RegisterAsync(username, token: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(await FindUserAsync(username));
    }

    [Theory]
    [InlineData("User")]
    [InlineData("ReadOnly")]
    public async Task ASignedInNonAdministrator_IsRefusedWith403_AndNoAccountIsCreated(string role)
    {
        var (_, token) = await _host.SignInNewUserAsync(role);
        var username = NewUsername();

        using var response = await RegisterAsync(username, token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(await FindUserAsync(username));
    }

    [Fact]
    public async Task AnAdministrator_CreatesAUserAccountThatCanSignIn()
    {
        var (_, token) = await _host.SignInNewUserAsync("Admin");
        var username = NewUsername();

        using var response = await RegisterAsync(username, token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using (var scope = _host.Factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var created = await users.FindByNameAsync(username);
            Assert.NotNull(created);
            Assert.Equal(new[] { "User" }, await users.GetRolesAsync(created));
        }

        Assert.False(string.IsNullOrEmpty(await _host.SignInAsync(username, NewPassword)));
    }

    private static string NewUsername() => "registered-" + Guid.NewGuid().ToString("N")[..12];

    private async Task<HttpResponseMessage> RegisterAsync(string username, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register")
        {
            Content = JsonContent.Create(new { username, password = NewPassword, email = username + "@example.test" }),
        };
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await _host.Client.SendAsync(request);
    }

    private async Task<ApplicationUser?> FindUserAsync(string username)
    {
        using var scope = _host.Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByNameAsync(username);
    }
}
