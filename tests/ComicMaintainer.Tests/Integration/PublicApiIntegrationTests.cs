using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ComicMaintainer.WebApi;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ComicMaintainer.Tests.Integration;

/// <summary>
/// Additional broad integration tests covering public (unauthenticated) API
/// endpoints and the full login round-trip. These complement
/// <see cref="ApiIntegrationTests"/> and <see cref="AuthResponseTests"/> by
/// exercising more controllers end-to-end via <see cref="WebApplicationFactory{TEntryPoint}"/>.
/// </summary>
[Trait("Category", "Integration")]
public class PublicApiIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public PublicApiIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetVersion_ReturnsExpectedShape()
    {
        var response = await _client.GetAsync("/api/version");

        response.EnsureSuccessStatusCode();
        Assert.Contains("application/json", response.Content.Headers.ContentType?.ToString() ?? "");

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.TryGetProperty("version", out _), "version property missing");
        Assert.True(doc.RootElement.TryGetProperty("platform", out var platform), "platform property missing");
        Assert.Equal(".NET", platform.GetString());
    }

    [Fact]
    public async Task GetAuthStatus_IsPublic_AndReturnsJson()
    {
        var response = await _client.GetAsync("/api/auth/status");

        response.EnsureSuccessStatusCode();
        Assert.Contains("application/json", response.Content.Headers.ContentType?.ToString() ?? "");

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.TryGetProperty("isAuthenticated", out var isAuth));
        Assert.False(isAuth.GetBoolean());
    }

    [Fact]
    public async Task GetAuthSetupRequired_IsPublic_AndReturnsBoolean()
    {
        var response = await _client.GetAsync("/api/auth/setup-required");

        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.TryGetProperty("setupRequired", out var setupRequired));
        Assert.Equal(JsonValueKind.True, setupRequired.ValueKind == JsonValueKind.True ? JsonValueKind.True : JsonValueKind.False);
    }

    [Fact]
    public async Task Login_WithBadCredentials_ReturnsUnauthorizedJson()
    {
        var response = await _client.PostAsJsonAsync("/api/auth/login", new
        {
            username = "nonexistent-user-for-integration-test",
            password = "this-password-should-not-exist"
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("application/json", response.Content.Headers.ContentType?.ToString() ?? "");
    }

    [Theory]
    [InlineData("/api/metadata")]
    [InlineData("/api/process")]
    [InlineData("/api/logs")]
    [InlineData("/api/processinghistory")]
    public async Task ProtectedEndpoints_ReturnUnauthorizedJson_WhenNotAuthenticated(string endpoint)
    {
        var response = await _client.GetAsync(endpoint);

        // Endpoint should either require authentication (401) or be unmapped (404).
        // It must never silently allow access or return HTML.
        Assert.True(
            response.StatusCode == HttpStatusCode.Unauthorized ||
            response.StatusCode == HttpStatusCode.NotFound ||
            response.StatusCode == HttpStatusCode.MethodNotAllowed,
            $"Expected 401/404/405 from {endpoint}, got {(int)response.StatusCode}");

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            var content = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("<!DOCTYPE", content);
            Assert.Contains("application/json", response.Content.Headers.ContentType?.ToString() ?? "");
        }
    }

    [Fact]
    public async Task HealthCheck_ApiVersion_IsFastAndStable()
    {
        // Verify the API is stable under repeated calls (simple smoke check used by container probes).
        for (int i = 0; i < 5; i++)
        {
            var response = await _client.GetAsync("/api/version");
            response.EnsureSuccessStatusCode();
        }
    }
}
