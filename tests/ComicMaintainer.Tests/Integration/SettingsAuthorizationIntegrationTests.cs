using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace ComicMaintainer.Tests.Integration;

/// <summary>
/// Who may save settings, through the real authentication and authorization pipeline.
/// <c>PUT /api/settings</c> is the widest settings write, so it must stay administrator-only.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SettingsAuthorizationIntegrationTests : IClassFixture<SessionTokenIntegrationTests.JwtHost>
{
    private readonly SessionTokenIntegrationTests.JwtHost _host;

    public SettingsAuthorizationIntegrationTests(SessionTokenIntegrationTests.JwtHost host)
    {
        _host = host;
    }

    [Theory]
    [InlineData("ReadOnly", HttpStatusCode.Forbidden)]
    [InlineData("User", HttpStatusCode.Forbidden)]
    [InlineData("Admin", HttpStatusCode.OK)]
    public async Task PutSettings_RequiresAnAdministrator(string role, HttpStatusCode expected)
    {
        var (_, token) = await _host.SignInNewUserAsync(role);

        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/settings") { Content = JsonContent.Create(new { }) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _host.Client.SendAsync(request);

        Assert.Equal(expected, response.StatusCode);
    }

    // On a public ntfy server the topic works as a password.
    [Theory]
    [InlineData("ReadOnly", "")]
    [InlineData("User", "")]
    [InlineData("Admin", "library-news")]
    public async Task GetSettings_ReturnsTheNtfyTopicOnlyToAnAdministrator(string role, string expected)
    {
        var (_, adminToken) = await _host.SignInNewUserAsync("Admin");
        using var save = new HttpRequestMessage(HttpMethod.Put, "/api/settings")
        {
            Content = JsonContent.Create(new { notifications = new { ntfy = new { topic = "library-news" } } }),
        };
        save.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        using var saved = await _host.Client.SendAsync(save);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var (_, token) = await _host.SignInNewUserAsync(role);
        using var read = new HttpRequestMessage(HttpMethod.Get, "/api/settings");
        read.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _host.Client.SendAsync(read);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(expected, body.RootElement.GetProperty("notify_ntfy_topic").GetString());
    }

    [Theory]
    [InlineData("ReadOnly", HttpStatusCode.Forbidden)]
    [InlineData("User", HttpStatusCode.Forbidden)]
    [InlineData("Admin", HttpStatusCode.BadRequest)]
    public async Task NotificationTest_RequiresAnAdministrator(string role, HttpStatusCode expected)
    {
        var (_, token) = await _host.SignInNewUserAsync(role);

        // The Admin case gets past the authorization: no ntfy URL is set in this host.
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/notifications/ntfy/test");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _host.Client.SendAsync(request);

        Assert.Equal(expected, response.StatusCode);
    }
}
