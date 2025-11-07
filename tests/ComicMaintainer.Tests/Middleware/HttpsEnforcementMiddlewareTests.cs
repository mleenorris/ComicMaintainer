using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.WebApi.Middleware;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace ComicMaintainer.Tests.Middleware;

/// <summary>
/// Tests for HTTPS enforcement middleware that prevents password transmission over insecure connections
/// </summary>
public class HttpsEnforcementMiddlewareTests
{
    [Fact]
    public async Task Login_OverHttp_WhenEnforcementEnabled_ReturnsUpgradeRequired()
    {
        // Arrange
        var client = CreateTestClient(requireHttps: true);

        // Act
        var response = await client.PostAsync("/api/auth/login", 
            new StringContent("{\"username\":\"test\",\"password\":\"test123\"}", Encoding.UTF8, "application/json"));

        // Assert
        Assert.Equal(HttpStatusCode.UpgradeRequired, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("HTTPS Required", content);
        Assert.Contains("password transmission", content.ToLower());
    }

    [Fact]
    public async Task Login_OverHttps_WhenEnforcementEnabled_AllowsRequest()
    {
        // Arrange
        var client = CreateTestClient(requireHttps: true, useHttps: true);

        // Act
        var response = await client.PostAsync("/api/auth/login", 
            new StringContent("{\"username\":\"test\",\"password\":\"test123\"}", Encoding.UTF8, "application/json"));

        // Assert - The request should pass through the middleware
        // It will fail auth (404 or 401) but not with 426 Upgrade Required
        Assert.NotEqual(HttpStatusCode.UpgradeRequired, response.StatusCode);
    }

    [Fact]
    public async Task Register_OverHttp_WhenEnforcementEnabled_ReturnsUpgradeRequired()
    {
        // Arrange
        var client = CreateTestClient(requireHttps: true);

        // Act
        var response = await client.PostAsync("/api/auth/register", 
            new StringContent("{\"username\":\"test\",\"password\":\"test123\",\"email\":\"test@test.com\"}", Encoding.UTF8, "application/json"));

        // Assert
        Assert.Equal(HttpStatusCode.UpgradeRequired, response.StatusCode);
    }

    [Fact]
    public async Task Setup_OverHttp_WhenEnforcementEnabled_ReturnsUpgradeRequired()
    {
        // Arrange
        var client = CreateTestClient(requireHttps: true);

        // Act
        var response = await client.PostAsync("/api/auth/setup", 
            new StringContent("{\"username\":\"admin\",\"password\":\"Admin123!\"}", Encoding.UTF8, "application/json"));

        // Assert
        Assert.Equal(HttpStatusCode.UpgradeRequired, response.StatusCode);
    }

    [Fact]
    public async Task ChangePassword_OverHttp_WhenEnforcementEnabled_ReturnsUpgradeRequired()
    {
        // Arrange
        var client = CreateTestClient(requireHttps: true);

        // Act
        var response = await client.PostAsync("/api/auth/change-password", 
            new StringContent("{\"currentPassword\":\"old\",\"newPassword\":\"new\"}", Encoding.UTF8, "application/json"));

        // Assert
        Assert.Equal(HttpStatusCode.UpgradeRequired, response.StatusCode);
    }

    [Fact]
    public async Task Login_OverHttp_WhenEnforcementDisabled_AllowsRequest()
    {
        // Arrange
        var client = CreateTestClient(requireHttps: false);

        // Act
        var response = await client.PostAsync("/api/auth/login", 
            new StringContent("{\"username\":\"test\",\"password\":\"test123\"}", Encoding.UTF8, "application/json"));

        // Assert - Should not return 426, but will fail with other error (no actual auth setup)
        Assert.NotEqual(HttpStatusCode.UpgradeRequired, response.StatusCode);
    }

    [Fact]
    public async Task Login_WithForwardedProtoHttps_AllowsRequest()
    {
        // Arrange
        var client = CreateTestClient(requireHttps: true);
        
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = new StringContent("{\"username\":\"test\",\"password\":\"test123\"}", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Forwarded-Proto", "https");

        // Act
        var response = await client.SendAsync(request);

        // Assert - Should not return 426
        Assert.NotEqual(HttpStatusCode.UpgradeRequired, response.StatusCode);
    }

    [Fact]
    public async Task NonProtectedEndpoint_OverHttp_AllowsRequest()
    {
        // Arrange
        var client = CreateTestClient(requireHttps: true);

        // Act
        var response = await client.GetAsync("/api/auth/setup-required");

        // Assert - Should not be blocked (setup-required is not a password endpoint)
        Assert.NotEqual(HttpStatusCode.UpgradeRequired, response.StatusCode);
    }

    private HttpClient CreateTestClient(bool requireHttps, bool useHttps = false)
    {
        var builder = new WebHostBuilder()
            .ConfigureServices(services =>
            {
                services.AddLogging();
                services.Configure<AppSettings>(options =>
                {
                    options.RequireHttpsForAuth = requireHttps;
                });
            })
            .Configure(app =>
            {
                app.UseMiddleware<HttpsEnforcementMiddleware>();
                
                // Add a dummy endpoint that always returns OK for testing
                app.Run(async context =>
                {
                    context.Response.StatusCode = 200;
                    await context.Response.WriteAsync("OK");
                });
            });

        var server = new TestServer(builder);
        var client = server.CreateClient();
        
        if (useHttps)
        {
            client.BaseAddress = new Uri("https://localhost");
        }
        else
        {
            client.BaseAddress = new Uri("http://localhost");
        }

        return client;
    }
}
