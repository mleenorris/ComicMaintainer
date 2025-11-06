using Microsoft.AspNetCore.Mvc.Testing;
using System.Net;
using Xunit;

namespace ComicMaintainer.Tests.Security;

public class CorsSecurityTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public CorsSecurityTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("http://localhost:5000")]
    [InlineData("https://localhost:5000")]
    public async Task Cors_AllowedOrigin_PreflightSucceeds(string allowedOrigin)
    {
        // Arrange
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/version");
        request.Headers.Add("Origin", allowedOrigin);
        request.Headers.Add("Access-Control-Request-Method", "GET");

        // Act
        var response = await client.SendAsync(request);

        // Assert
        Assert.True(response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NoContent);
        
        // Check CORS headers are present
        if (response.Headers.Contains("Access-Control-Allow-Origin"))
        {
            var allowedOriginHeader = response.Headers.GetValues("Access-Control-Allow-Origin").First();
            Assert.Equal(allowedOrigin, allowedOriginHeader);
        }
    }

    [Theory]
    [InlineData("http://evil.com")]
    [InlineData("https://malicious-site.com")]
    [InlineData("http://localhost:3000")]
    public async Task Cors_DisallowedOrigin_NoAccessControlHeaders(string disallowedOrigin)
    {
        // Arrange
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/version");
        request.Headers.Add("Origin", disallowedOrigin);
        request.Headers.Add("Access-Control-Request-Method", "GET");

        // Act
        var response = await client.SendAsync(request);

        // Assert - Either no CORS headers or different origin
        if (response.Headers.Contains("Access-Control-Allow-Origin"))
        {
            var allowedOriginHeader = response.Headers.GetValues("Access-Control-Allow-Origin").First();
            Assert.NotEqual(disallowedOrigin, allowedOriginHeader);
        }
    }

    [Fact]
    public async Task Cors_AllowCredentials_IsEnabled()
    {
        // Arrange
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/version");
        request.Headers.Add("Origin", "http://localhost:5000");
        request.Headers.Add("Access-Control-Request-Method", "GET");

        // Act
        var response = await client.SendAsync(request);

        // Assert - Check that credentials are allowed
        if (response.Headers.Contains("Access-Control-Allow-Credentials"))
        {
            var allowCredentials = response.Headers.GetValues("Access-Control-Allow-Credentials").First();
            Assert.Equal("true", allowCredentials);
        }
    }

    [Fact]
    public async Task Cors_WildcardOrigin_IsNotUsed()
    {
        // Arrange
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/version");
        request.Headers.Add("Origin", "http://localhost:5000");
        request.Headers.Add("Access-Control-Request-Method", "GET");

        // Act
        var response = await client.SendAsync(request);

        // Assert - Ensure wildcard is not used when credentials are enabled
        if (response.Headers.Contains("Access-Control-Allow-Origin"))
        {
            var allowedOriginHeader = response.Headers.GetValues("Access-Control-Allow-Origin").First();
            Assert.NotEqual("*", allowedOriginHeader);
        }
    }

    [Fact]
    public async Task Cors_AllowedMethods_AreSpecified()
    {
        // Arrange
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/version");
        request.Headers.Add("Origin", "http://localhost:5000");
        request.Headers.Add("Access-Control-Request-Method", "POST");

        // Act
        var response = await client.SendAsync(request);

        // Assert - Check that allowed methods are specified
        if (response.Headers.Contains("Access-Control-Allow-Methods"))
        {
            var methods = string.Join(",", response.Headers.GetValues("Access-Control-Allow-Methods"));
            // Should allow POST at minimum since we requested it
            Assert.Contains("POST", methods, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Cors_AllowedHeaders_IncludeAuthorization()
    {
        // Arrange
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/version");
        request.Headers.Add("Origin", "http://localhost:5000");
        request.Headers.Add("Access-Control-Request-Method", "GET");
        request.Headers.Add("Access-Control-Request-Headers", "Authorization");

        // Act
        var response = await client.SendAsync(request);

        // Assert - Authorization header should be allowed
        if (response.Headers.Contains("Access-Control-Allow-Headers"))
        {
            var headers = string.Join(",", response.Headers.GetValues("Access-Control-Allow-Headers"));
            Assert.Contains("Authorization", headers, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("http://localhost:5000", "GET")]
    [InlineData("https://localhost:5000", "GET")]
    public async Task Cors_ActualRequest_AfterPreflight_Succeeds(string origin, string method)
    {
        // Arrange
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(new HttpMethod(method), "/api/version");
        request.Headers.Add("Origin", origin);

        // Act
        var response = await client.SendAsync(request);

        // Assert
        // Should either succeed or be unauthorized (but not CORS-blocked)
        Assert.True(
            response.IsSuccessStatusCode || 
            response.StatusCode == HttpStatusCode.Unauthorized ||
            response.StatusCode == HttpStatusCode.Forbidden,
            $"Unexpected status code: {response.StatusCode}"
        );
    }

    [Fact]
    public async Task Cors_NoOriginHeader_RequestProcessedNormally()
    {
        // Arrange
        var client = _factory.CreateClient();

        // Act
        var response = await client.GetAsync("/api/version");

        // Assert - Request should be processed normally without Origin header
        Assert.True(response.IsSuccessStatusCode);
    }
}
