using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

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
}
