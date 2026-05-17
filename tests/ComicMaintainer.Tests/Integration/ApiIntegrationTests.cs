using System.Net;
using System.Net.Http.Json;
using ComicMaintainer.WebApi;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ComicMaintainer.Tests.Integration;

/// <summary>
/// Integration tests for the API endpoints.
/// These tests verify end-to-end functionality of the web API.
/// </summary>
[Trait("Category", "Integration")]
public class ApiIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public ApiIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetVersion_ReturnsSuccessAndVersion()
    {
        // Act
        var response = await _client.GetAsync("/api/version");

        // Assert
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("version", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2.0", content);
    }

    [Fact]
    public async Task GetWatcherStatus_RequiresAuthentication()
    {
        // Act
        var response = await _client.GetAsync("/api/watcher");

        // Assert - Watcher endpoint now requires authentication
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        
        // Verify it returns JSON, not HTML
        var content = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("<!DOCTYPE", content);
        Assert.Contains("application/json", response.Content.Headers.ContentType?.ToString() ?? "");
    }

    [Fact]
    public async Task GetFiles_RequiresAuthentication()
    {
        // Act
        var response = await _client.GetAsync("/api/files");

        // Assert - Files endpoint now requires authentication
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        
        // Verify it returns JSON, not HTML
        var content = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("<!DOCTYPE", content);
        Assert.Contains("application/json", response.Content.Headers.ContentType?.ToString() ?? "");
    }

    [Fact]
    public async Task GetSettings_RequiresAuthentication()
    {
        // Act
        var response = await _client.GetAsync("/api/settings");

        // Assert - Settings endpoint requires authentication
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        
        // Verify it returns JSON, not HTML
        var content = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("<!DOCTYPE", content);
        Assert.Contains("application/json", response.Content.Headers.ContentType?.ToString() ?? "");
    }

    [Fact]
    public async Task GetJobs_RequiresAuthentication()
    {
        // Act
        var response = await _client.GetAsync("/api/jobs");

        // Assert - Jobs endpoint now requires authentication
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        
        // Verify it returns JSON, not HTML
        var content = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("<!DOCTYPE", content);
        Assert.Contains("application/json", response.Content.Headers.ContentType?.ToString() ?? "");
    }

    [Fact]
    public async Task ApiEndpoints_ReturnJsonContentType()
    {
        // Arrange - Test endpoints that don't require authentication
        var publicEndpoints = new[]
        {
            "/api/version"
        };

        foreach (var endpoint in publicEndpoints)
        {
            // Act
            var response = await _client.GetAsync(endpoint);

            // Assert
            response.EnsureSuccessStatusCode();
            Assert.Contains("application/json", response.Content.Headers.ContentType?.ToString() ?? "");
        }
        
        // Test that protected endpoints return JSON for 401 errors
        var protectedEndpoints = new[] 
        { 
            "/api/settings",
            "/api/watcher",
            "/api/files",
            "/api/jobs",
            "/api/preferences"
        };
        foreach (var endpoint in protectedEndpoints)
        {
            var response = await _client.GetAsync(endpoint);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Contains("application/json", response.Content.Headers.ContentType?.ToString() ?? "");
        }
    }

    [Fact]
    public async Task ApiRespondsToRequests()
    {
        // Act - Test that the API is responding (may have fallback routes)
        var response = await _client.GetAsync("/api/version");

        // Assert - Just verify the app is running and responding
        Assert.True(response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound,
            $"API should respond with Success or NotFound, but got {response.StatusCode}");
    }

    [Fact]
    public async Task StaticFiles_AreAccessible()
    {
        // Act
        var response = await _client.GetAsync("/");

        // Assert
        response.EnsureSuccessStatusCode();
        Assert.Contains("text/html", response.Content.Headers.ContentType?.ToString() ?? "");
    }
}
