using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ComicMaintainer.Tests.Integration;

/// <summary>
/// The preferences API through the real app, JSON binding included.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PreferencesIntegrationTests : IClassFixture<SessionTokenIntegrationTests.JwtHost>
{
    /// <summary>The library view preference, removed in 3.0.0 together with the Files view.</summary>
    private const string RemovedViewField = "libraryViewMode";

    private readonly SessionTokenIntegrationTests.JwtHost _host;

    public PreferencesIntegrationTests(SessionTokenIntegrationTests.JwtHost host)
    {
        _host = host;
    }

    // A client written for 2.x may still send the field, even with a value that used to be refused.
    [Theory]
    [InlineData("series")]
    [InlineData("grid")]
    public async Task PutPreferences_IgnoresTheRemovedLibraryViewField(string view)
    {
        var (_, token) = await _host.SignInNewUserAsync();

        using var save = await SendAsync(HttpMethod.Put, token, $$"""{"theme":"dark","{{RemovedViewField}}":"{{view}}"}""");
        using var read = await SendAsync(HttpMethod.Get, token);

        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        foreach (var response in new[] { save, read })
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("dark", body.RootElement.GetProperty("theme").GetString());
            Assert.False(body.RootElement.TryGetProperty(RemovedViewField, out _));
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string token, string? json = null)
    {
        using var request = new HttpRequestMessage(method, "/api/preferences");
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _host.Client.SendAsync(request);
    }
}
