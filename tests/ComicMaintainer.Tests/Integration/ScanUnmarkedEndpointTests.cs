using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ComicMaintainer.Tests.Integration;

/// <summary>
/// The library page's "Scan Unmarked" action through the real pipeline. It only reads, so every
/// role may run it, and as a GET its answer must never come from a cache: a second scan has to
/// report what the library holds by then.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ScanUnmarkedEndpointTests : IClassFixture<SessionTokenIntegrationTests.JwtHost>
{
    private readonly SessionTokenIntegrationTests.JwtHost _host;

    public ScanUnmarkedEndpointTests(SessionTokenIntegrationTests.JwtHost host) => _host = host;

    [Theory]
    [InlineData("ReadOnly")]
    [InlineData("User")]
    [InlineData("Admin")]
    public async Task EveryRoleGetsTheCountsThePageShows(string role)
    {
        var (_, session) = await _host.SignInNewUserAsync(role);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/files/scan-unmarked");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session);

        using var response = await _host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore, "A scan result must not be cached.");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, body.RootElement.GetProperty("total_count").GetInt32());
        Assert.Equal(0, body.RootElement.GetProperty("unmarked_count").GetInt32());
        Assert.Equal(0, body.RootElement.GetProperty("marked_count").GetInt32());
    }
}
