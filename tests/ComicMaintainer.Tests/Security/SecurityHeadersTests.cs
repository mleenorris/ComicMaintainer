using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Xunit;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;

namespace ComicMaintainer.Tests.Security;

public class SecurityHeadersTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public SecurityHeadersTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    // Helper class to inject X-Forwarded-Proto header for testing reverse proxy scenarios
    private class ForwardedProtoHandler : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Add("X-Forwarded-Proto", "https");
            return base.SendAsync(request, cancellationToken);
        }
    }

    [Fact]
    public async Task SecurityHeaders_XContentTypeOptions_IsPresent()
    {
        // Arrange
        var client = _factory.CreateClient();

        // Act
        var response = await client.GetAsync("/api/version");

        // Assert
        Assert.True(response.Headers.Contains("X-Content-Type-Options"));
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").First());
    }

    [Fact]
    public async Task SecurityHeaders_XFrameOptions_IsPresent()
    {
        // Arrange
        var client = _factory.CreateClient();

        // Act
        var response = await client.GetAsync("/api/version");

        // Assert
        Assert.True(response.Headers.Contains("X-Frame-Options"));
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").First());
    }

    [Fact]
    public async Task SecurityHeaders_XXssProtection_IsPresent()
    {
        // Arrange
        var client = _factory.CreateClient();

        // Act
        var response = await client.GetAsync("/api/version");

        // Assert
        Assert.True(response.Headers.Contains("X-XSS-Protection"));
        Assert.Equal("1; mode=block", response.Headers.GetValues("X-XSS-Protection").First());
    }

    [Fact]
    public async Task SecurityHeaders_ReferrerPolicy_IsPresent()
    {
        // Arrange
        var client = _factory.CreateClient();

        // Act
        var response = await client.GetAsync("/api/version");

        // Assert
        Assert.True(response.Headers.Contains("Referrer-Policy"));
        Assert.Equal("strict-origin-when-cross-origin", response.Headers.GetValues("Referrer-Policy").First());
    }

    [Fact]
    public async Task SecurityHeaders_PermissionsPolicy_IsPresent()
    {
        // Arrange
        var client = _factory.CreateClient();

        // Act
        var response = await client.GetAsync("/api/version");

        // Assert
        Assert.True(response.Headers.Contains("Permissions-Policy"));
        var policy = response.Headers.GetValues("Permissions-Policy").First();
        Assert.Contains("geolocation=()", policy);
        Assert.Contains("microphone=()", policy);
        Assert.Contains("camera=()", policy);
    }

    [Fact]
    public async Task SecurityHeaders_WithHttpsForwardedProto_HstsIsPresent()
    {
        // Arrange
        // Create a custom factory with middleware that simulates reverse proxy behavior
        var customFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                // Services are configured correctly by default
            });
        });

        // Use the server instance to add the header directly to the request
        var client = customFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/version");
        request.Headers.Add("X-Forwarded-Proto", "https");

        // Act
        var response = await client.SendAsync(request);

        // Assert
        Assert.True(response.Headers.Contains("Strict-Transport-Security"),
            $"Expected Strict-Transport-Security header. Headers: {string.Join(", ", response.Headers.Select(h => h.Key))}");
        var hsts = response.Headers.GetValues("Strict-Transport-Security").First();
        Assert.Contains("max-age=31536000", hsts);
        Assert.Contains("includeSubDomains", hsts);
    }

    [Fact]
    public async Task SecurityHeaders_WithHttpsForwardedProto_CspIsPresent()
    {
        // Arrange
        // Create a custom factory with middleware that simulates reverse proxy behavior
        var customFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                // Services are configured correctly by default
            });
        });

        var client = customFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/version");
        request.Headers.Add("X-Forwarded-Proto", "https");

        // Act
        var response = await client.SendAsync(request);

        // Assert
        Assert.True(response.Headers.Contains("Content-Security-Policy"),
            $"Expected Content-Security-Policy header. Headers: {string.Join(", ", response.Headers.Select(h => h.Key))}");
        Assert.Equal("upgrade-insecure-requests", response.Headers.GetValues("Content-Security-Policy").First());
    }

    [Fact]
    public async Task SecurityHeaders_WithoutHttpsForwardedProto_NoHsts()
    {
        // Arrange
        var client = _factory.CreateClient();

        // Act
        var response = await client.GetAsync("/api/version");

        // Assert
        Assert.False(response.Headers.Contains("Strict-Transport-Security"));
    }

    [Fact]
    public async Task ApiEndpoints_CacheControl_IsNoStore()
    {
        // Arrange
        var client = _factory.CreateClient();

        // Act
        var response = await client.GetAsync("/api/version");

        // Assert
        Assert.True(response.Headers.Contains("Cache-Control"));
        var cacheControl = response.Headers.GetValues("Cache-Control").First();
        Assert.Contains("no-store", cacheControl);
        Assert.Contains("no-cache", cacheControl);
        Assert.Contains("must-revalidate", cacheControl);
        Assert.Contains("private", cacheControl);
    }

    [Fact]
    public async Task ApiEndpoints_Pragma_IsNoCache()
    {
        // Arrange
        var client = _factory.CreateClient();

        // Act
        var response = await client.GetAsync("/api/version");

        // Assert
        Assert.True(response.Headers.Contains("Pragma"));
        Assert.Equal("no-cache", response.Headers.GetValues("Pragma").First());
    }

    [Fact]
    public async Task StaticFiles_DoNotHaveNoCacheHeaders()
    {
        // Arrange
        var client = _factory.CreateClient();

        // Act
        var response = await client.GetAsync("/index.html");

        // Assert - Static files should allow caching
        if (response.Headers.Contains("Cache-Control"))
        {
            var cacheControl = response.Headers.GetValues("Cache-Control").First();
            Assert.DoesNotContain("no-store", cacheControl);
        }
    }

    [Theory]
    [InlineData("/api/auth/setup-required")]
    [InlineData("/api/version")]
    public async Task AllSecurityHeaders_ArePresentOnAllEndpoints(string endpoint)
    {
        // Arrange
        var client = _factory.CreateClient();

        // Act
        var response = await client.GetAsync(endpoint);

        // Assert - Check all basic security headers
        Assert.True(response.Headers.Contains("X-Content-Type-Options"), "Missing X-Content-Type-Options");
        Assert.True(response.Headers.Contains("X-Frame-Options"), "Missing X-Frame-Options");
        Assert.True(response.Headers.Contains("X-XSS-Protection"), "Missing X-XSS-Protection");
        Assert.True(response.Headers.Contains("Referrer-Policy"), "Missing Referrer-Policy");
        Assert.True(response.Headers.Contains("Permissions-Policy"), "Missing Permissions-Policy");
    }
}
