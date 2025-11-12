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
    public async Task SecurityHeaders_CspFrameAncestors_IsPresent()
    {
        // Arrange
        var client = _factory.CreateClient();

        // Act
        var response = await client.GetAsync("/api/version");

        // Assert
        // CSP with frame-ancestors replaces X-Frame-Options (which is deprecated)
        Assert.True(response.Headers.Contains("Content-Security-Policy"));
        Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").First());
    }

    [Fact]
    public async Task SecurityHeaders_NoDeprecatedHeaders()
    {
        // Arrange
        var client = _factory.CreateClient();

        // Act
        var response = await client.GetAsync("/api/version");

        // Assert - X-XSS-Protection is deprecated and should not be present
        Assert.False(response.Headers.Contains("X-XSS-Protection"), 
            "X-XSS-Protection header should not be present (deprecated)");
        
        // X-Frame-Options is deprecated in favor of CSP frame-ancestors
        Assert.False(response.Headers.Contains("X-Frame-Options"), 
            "X-Frame-Options header should not be present (superseded by CSP frame-ancestors)");
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
        var csp = response.Headers.GetValues("Content-Security-Policy").First();
        Assert.Contains("upgrade-insecure-requests", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
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
        Assert.Contains("private", cacheControl);
        // Should not have must-revalidate or no-cache alongside no-store (redundant/conflicting directives)
        Assert.DoesNotContain("must-revalidate", cacheControl);
        Assert.DoesNotContain("no-cache", cacheControl);
    }

    [Fact]
    public async Task ApiEndpoints_NoDeprecatedCacheHeaders()
    {
        // Arrange
        var client = _factory.CreateClient();

        // Act
        var response = await client.GetAsync("/api/version");

        // Assert - Pragma is deprecated and should not be present
        Assert.False(response.Headers.Contains("Pragma"), 
            "Pragma header should not be present (deprecated, request-only header)");
        
        // Expires is a content header, not a response header
        Assert.False(response.Content.Headers.Contains("Expires"), 
            "Expires header should not be present (superseded by Cache-Control)");
    }

    [Fact]
    public async Task StaticFiles_HtmlHasNoStoreCache()
    {
        // Arrange
        var client = _factory.CreateClient();

        // Act
        var response = await client.GetAsync("/index.html");

        // Assert - HTML files should not be cached to ensure users get the latest version
        Assert.True(response.Headers.Contains("Cache-Control"));
        var cacheControl = response.Headers.GetValues("Cache-Control").First();
        Assert.Contains("no-store", cacheControl);
        Assert.Contains("private", cacheControl);
    }
    
    [Fact]
    public async Task StaticFiles_NonHtmlArePublicCached()
    {
        // Arrange
        var client = _factory.CreateClient();

        // Act - CSS files should be cached
        var response = await client.GetAsync("/css/main.css");

        // Assert - Non-HTML static files should allow caching
        Assert.True(response.Headers.Contains("Cache-Control"));
        var cacheControl = response.Headers.GetValues("Cache-Control").First();
        Assert.Contains("public", cacheControl);
        Assert.Contains("max-age=3600", cacheControl);
        Assert.DoesNotContain("no-store", cacheControl);
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

        // Assert - Check all modern security headers
        Assert.True(response.Headers.Contains("X-Content-Type-Options"), "Missing X-Content-Type-Options");
        Assert.True(response.Headers.Contains("Content-Security-Policy"), "Missing Content-Security-Policy");
        Assert.True(response.Headers.Contains("Referrer-Policy"), "Missing Referrer-Policy");
        Assert.True(response.Headers.Contains("Permissions-Policy"), "Missing Permissions-Policy");
        
        // Ensure deprecated headers are not present
        Assert.False(response.Headers.Contains("X-Frame-Options"), "X-Frame-Options should not be present (superseded by CSP)");
        Assert.False(response.Headers.Contains("X-XSS-Protection"), "X-XSS-Protection should not be present (deprecated)");
    }
}
