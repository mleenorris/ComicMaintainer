using System.Net;
using System.Net.Http.Json;
using ComicMaintainer.WebApi;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace ComicMaintainer.Tests.Integration;

/// <summary>
/// Integration tests for authentication responses.
/// These tests verify that API endpoints return proper JSON responses for authentication failures.
/// </summary>
[Trait("Category", "Integration")]
public class AuthResponseTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public AuthResponseTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task UnauthorizedApiRequest_ReturnsJsonNotHtml()
    {
        // Act - Call an authenticated endpoint without auth token
        var response = await _client.GetAsync("/api/settings");

        // Assert - Should return 401 with JSON content, not HTML
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("application/json", response.Content.Headers.ContentType?.ToString() ?? "");
        
        // Verify the content is JSON and not HTML
        var content = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("<!DOCTYPE", content);
        Assert.DoesNotContain("<html", content);
        Assert.Contains("error", content.ToLower());
    }
    
    [Fact]
    public async Task MultipleProtectedEndpoints_ReturnJsonOn401()
    {
        // Arrange - List of endpoints that require authentication
        var endpoints = new[]
        {
            "/api/settings",
            "/api/settings/filename-format",
            "/api/settings/issue-number-padding",
            "/api/settings/log-max-bytes",
            "/api/settings/watcher-enable-rename",
            "/api/settings/watcher-enable-normalize"
        };

        foreach (var endpoint in endpoints)
        {
            // Act
            var response = await _client.GetAsync(endpoint);

            // Assert
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            var contentType = response.Content.Headers.ContentType?.ToString() ?? "";
            Assert.Contains("application/json", contentType);
            
            var content = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("<!DOCTYPE", content);
            Assert.DoesNotContain("<html", content);
        }
    }
    
    [Fact]
    public async Task NonExistentApiEndpoint_ReturnsJsonNotHtml()
    {
        // Act - Call a non-existent API endpoint
        var response = await _client.GetAsync("/api/nonexistent/endpoint");

        // Assert - Should return 404 with JSON content, not HTML
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("application/json", response.Content.Headers.ContentType?.ToString() ?? "");
        
        // Verify the content is JSON and not HTML
        var content = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("<!DOCTYPE", content);
        Assert.DoesNotContain("<html", content);
        Assert.Contains("error", content.ToLower());
    }
}
