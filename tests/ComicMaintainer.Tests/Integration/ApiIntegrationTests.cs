using System.Net;
using System.Net.Http.Json;
using ComicMaintainer.WebApi;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ComicMaintainer.Tests.Integration;

/// <summary>
/// Integration tests for the API endpoints.
/// These tests verify end-to-end functionality of the web API.
/// </summary>
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
    public async Task GetWatcherStatus_ReturnsSuccess()
    {
        // Act
        var response = await _client.GetAsync("/api/watcher");

        // Assert
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("enabled", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetFiles_ReturnsSuccess()
    {
        // Act
        var response = await _client.GetAsync("/api/files");

        // Assert
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();
        Assert.NotNull(content);
    }

    [Fact]
    public async Task GetSettings_ReturnsSuccess()
    {
        // Act
        var response = await _client.GetAsync("/api/settings");

        // Assert
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();
        // Check for snake_case naming convention used in API
        Assert.Contains("filename_format", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetJobs_ReturnsSuccess()
    {
        // Act
        var response = await _client.GetAsync("/api/jobs");

        // Assert
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();
        Assert.NotNull(content);
    }

    [Fact]
    public async Task ApiEndpoints_ReturnJsonContentType()
    {
        // Arrange
        var endpoints = new[]
        {
            "/api/version",
            "/api/watcher",
            "/api/files",
            "/api/settings",
            "/api/jobs"
        };

        foreach (var endpoint in endpoints)
        {
            // Act
            var response = await _client.GetAsync(endpoint);

            // Assert
            response.EnsureSuccessStatusCode();
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
