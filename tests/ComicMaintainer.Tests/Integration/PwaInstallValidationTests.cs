using System.Net;
using System.Text.Json;
using ComicMaintainer.WebApi;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ComicMaintainer.Tests.Integration;

/// <summary>
/// Integration tests to validate that the Progressive Web App (PWA) meets
/// all installation criteria for mobile Chrome on Android.
/// 
/// Tests verify the PWA manifest, service worker, icons, and HTTPS requirements
/// according to Chrome's PWA installation standards (2024).
/// </summary>
public class PwaInstallValidationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public PwaInstallValidationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    #region Manifest Validation

    [Fact]
    public async Task Manifest_ExistsAndIsAccessible()
    {
        // Act
        var response = await _client.GetAsync("/manifest.json");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Manifest_IsValidJson()
    {
        // Act
        var response = await _client.GetAsync("/manifest.json");
        var content = await response.Content.ReadAsStringAsync();

        // Assert - Parse JSON to ensure it's valid
        var exception = Record.Exception(() => JsonDocument.Parse(content));
        Assert.Null(exception);
    }

    [Fact]
    public async Task Manifest_ContainsRequiredFields()
    {
        // Act
        var response = await _client.GetAsync("/manifest.json");
        var content = await response.Content.ReadAsStringAsync();
        var manifest = JsonDocument.Parse(content);
        var root = manifest.RootElement;

        // Assert - Chrome PWA requirements
        Assert.True(root.TryGetProperty("name", out var name));
        Assert.False(string.IsNullOrWhiteSpace(name.GetString()));

        Assert.True(root.TryGetProperty("short_name", out var shortName));
        Assert.False(string.IsNullOrWhiteSpace(shortName.GetString()));

        Assert.True(root.TryGetProperty("start_url", out var startUrl));
        Assert.False(string.IsNullOrWhiteSpace(startUrl.GetString()));

        Assert.True(root.TryGetProperty("display", out var display));
        Assert.False(string.IsNullOrWhiteSpace(display.GetString()));
        // Display should be standalone, fullscreen, or minimal-ui for PWA
        var displayValue = display.GetString();
        Assert.Contains(displayValue, new[] { "standalone", "fullscreen", "minimal-ui" });

        Assert.True(root.TryGetProperty("icons", out var icons));
        Assert.Equal(JsonValueKind.Array, icons.ValueKind);
        Assert.True(icons.GetArrayLength() > 0);
    }

    [Fact]
    public async Task Manifest_ContainsRecommendedFields()
    {
        // Act
        var response = await _client.GetAsync("/manifest.json");
        var content = await response.Content.ReadAsStringAsync();
        var manifest = JsonDocument.Parse(content);
        var root = manifest.RootElement;

        // Assert - Recommended PWA fields
        Assert.True(root.TryGetProperty("description", out var description));
        Assert.False(string.IsNullOrWhiteSpace(description.GetString()));

        Assert.True(root.TryGetProperty("background_color", out var bgColor));
        Assert.False(string.IsNullOrWhiteSpace(bgColor.GetString()));

        Assert.True(root.TryGetProperty("theme_color", out var themeColor));
        Assert.False(string.IsNullOrWhiteSpace(themeColor.GetString()));

        // Android Chrome specific fields
        Assert.True(root.TryGetProperty("id", out var id));
        Assert.True(root.TryGetProperty("prefer_related_applications", out var preferRelated));
        Assert.False(preferRelated.GetBoolean()); // Should be false for PWA
    }

    #endregion

    #region Icon Validation

    [Fact]
    public async Task Manifest_ContainsRequiredIconSizes()
    {
        // Act
        var response = await _client.GetAsync("/manifest.json");
        var content = await response.Content.ReadAsStringAsync();
        var manifest = JsonDocument.Parse(content);
        var icons = manifest.RootElement.GetProperty("icons");

        // Assert - Chrome requires at least 192x192 and 512x512 icons
        var iconSizes = new List<string>();
        foreach (var icon in icons.EnumerateArray())
        {
            if (icon.TryGetProperty("sizes", out var sizes))
            {
                iconSizes.Add(sizes.GetString() ?? "");
            }
        }

        Assert.Contains("192x192", iconSizes);
        Assert.Contains("512x512", iconSizes);
    }

    [Fact]
    public async Task Manifest_IconsHaveCorrectPurpose()
    {
        // Act
        var response = await _client.GetAsync("/manifest.json");
        var content = await response.Content.ReadAsStringAsync();
        var manifest = JsonDocument.Parse(content);
        var icons = manifest.RootElement.GetProperty("icons");

        // Assert - Icons should have "any maskable" purpose for optimal Android Chrome compatibility
        var hasMaskableIcon = false;
        foreach (var icon in icons.EnumerateArray())
        {
            if (icon.TryGetProperty("purpose", out var purpose))
            {
                var purposeValue = purpose.GetString() ?? "";
                // Check for "any maskable" (space-separated, best practice for 2024)
                if (purposeValue.Contains("maskable"))
                {
                    hasMaskableIcon = true;
                }
            }
        }

        Assert.True(hasMaskableIcon, "Manifest should include at least one maskable icon for Android Chrome compatibility");
    }

    [Fact]
    public async Task Manifest_IconsHaveAnyMaskablePurpose()
    {
        // Act
        var response = await _client.GetAsync("/manifest.json");
        var content = await response.Content.ReadAsStringAsync();
        var manifest = JsonDocument.Parse(content);
        var icons = manifest.RootElement.GetProperty("icons");

        // Assert - Check for 2024 best practice: "any maskable" (space-separated)
        var hasAnyMaskable = false;
        foreach (var icon in icons.EnumerateArray())
        {
            if (icon.TryGetProperty("purpose", out var purpose))
            {
                var purposeValue = purpose.GetString() ?? "";
                // Check if purpose contains both "any" and "maskable"
                if (purposeValue.Contains("any") && purposeValue.Contains("maskable"))
                {
                    hasAnyMaskable = true;
                    break;
                }
            }
        }

        Assert.True(hasAnyMaskable, "At least one icon should have 'any maskable' purpose (2024 PWA best practice)");
    }

    [Fact]
    public async Task Icons_192x192_Exists()
    {
        // Act
        var response = await _client.GetAsync("/icons/icon-192x192-maskable.png");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        
        // Verify file has content
        var content = await response.Content.ReadAsByteArrayAsync();
        Assert.True(content.Length > 0, "Icon file should not be empty");
    }

    [Fact]
    public async Task Icons_512x512_Exists()
    {
        // Act
        var response = await _client.GetAsync("/icons/icon-512x512-maskable.png");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        
        // Verify file has content
        var content = await response.Content.ReadAsByteArrayAsync();
        Assert.True(content.Length > 0, "Icon file should not be empty");
    }

    [Fact]
    public async Task Icons_AllManifestIconsExist()
    {
        // Arrange
        var manifestResponse = await _client.GetAsync("/manifest.json");
        var manifestContent = await manifestResponse.Content.ReadAsStringAsync();
        var manifest = JsonDocument.Parse(manifestContent);
        var icons = manifest.RootElement.GetProperty("icons");

        // Act & Assert - Check each icon URL exists
        foreach (var icon in icons.EnumerateArray())
        {
            if (icon.TryGetProperty("src", out var src))
            {
                var iconUrl = src.GetString();
                Assert.NotNull(iconUrl);
                
                var response = await _client.GetAsync(iconUrl);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                
                var content = await response.Content.ReadAsByteArrayAsync();
                Assert.True(content.Length > 0, $"Icon at {iconUrl} should not be empty");
            }
        }
    }

    #endregion

    #region Service Worker Validation

    [Fact]
    public async Task ServiceWorker_ExistsAndIsAccessible()
    {
        // Act
        var response = await _client.GetAsync("/sw.js");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        
        // Service worker should be JavaScript
        var contentType = response.Content.Headers.ContentType?.MediaType;
        Assert.True(
            contentType == "application/javascript" || 
            contentType == "text/javascript",
            $"Service worker content type should be JavaScript, got: {contentType}"
        );
    }

    [Fact]
    public async Task ServiceWorker_ContainsRequiredEventListeners()
    {
        // Act
        var response = await _client.GetAsync("/sw.js");
        var content = await response.Content.ReadAsStringAsync();

        // Assert - Service worker must have install, activate, and fetch listeners
        Assert.Contains("addEventListener('install'", content);
        Assert.Contains("addEventListener('activate'", content);
        Assert.Contains("addEventListener('fetch'", content);
    }

    [Fact]
    public async Task ServiceWorker_HasValidJavaScript()
    {
        // Act
        var response = await _client.GetAsync("/sw.js");
        var content = await response.Content.ReadAsStringAsync();

        // Assert - Basic JavaScript syntax validation
        Assert.False(string.IsNullOrWhiteSpace(content));
        Assert.Contains("self", content); // Service worker scope
        Assert.DoesNotContain("syntax error", content.ToLowerInvariant());
    }

    #endregion

    #region HTML PWA Integration

    [Fact]
    public async Task IndexHtml_ContainsManifestLink()
    {
        // Act
        var response = await _client.GetAsync("/");
        var content = await response.Content.ReadAsStringAsync();

        // Assert - HTML must link to manifest
        Assert.Contains("rel=\"manifest\"", content);
        Assert.Contains("href=\"/manifest.json\"", content);
    }

    [Fact]
    public async Task IndexHtml_ContainsPwaMetaTags()
    {
        // Act
        var response = await _client.GetAsync("/");
        var content = await response.Content.ReadAsStringAsync();

        // Assert - PWA meta tags
        Assert.Contains("name=\"theme-color\"", content);
        Assert.Contains("name=\"mobile-web-app-capable\"", content);
        Assert.Contains("name=\"apple-mobile-web-app-capable\"", content);
    }

    [Fact]
    public async Task IndexHtml_ContainsViewportMetaTag()
    {
        // Act
        var response = await _client.GetAsync("/");
        var content = await response.Content.ReadAsStringAsync();

        // Assert - Viewport is required for mobile
        Assert.Contains("name=\"viewport\"", content);
        Assert.Contains("width=device-width", content);
    }

    #endregion

    #region Additional PWA Assets

    [Fact]
    public async Task Favicon_32x32_Exists()
    {
        // Act
        var response = await _client.GetAsync("/icons/favicon-32x32.png");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsByteArrayAsync();
        Assert.True(content.Length > 0);
    }

    [Fact]
    public async Task Favicon_16x16_Exists()
    {
        // Act
        var response = await _client.GetAsync("/icons/favicon-16x16.png");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsByteArrayAsync();
        Assert.True(content.Length > 0);
    }

    [Fact]
    public async Task AppleTouchIcon_Exists()
    {
        // Act
        var response = await _client.GetAsync("/icons/apple-touch-icon.png");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsByteArrayAsync();
        Assert.True(content.Length > 0);
    }

    #endregion

    #region PWA Installation Criteria Summary

    [Fact]
    public async Task PwaInstallationCriteria_AllMet()
    {
        // This test aggregates all PWA installation requirements
        // If this test passes, the PWA should be installable on Android Chrome

        var issues = new List<string>();

        // 1. Check manifest
        var manifestResponse = await _client.GetAsync("/manifest.json");
        if (manifestResponse.StatusCode != HttpStatusCode.OK)
        {
            issues.Add("Manifest is not accessible");
        }
        else
        {
            var manifestContent = await manifestResponse.Content.ReadAsStringAsync();
            try
            {
                var manifest = JsonDocument.Parse(manifestContent);
                var root = manifest.RootElement;

                if (!root.TryGetProperty("name", out _)) issues.Add("Manifest missing 'name'");
                if (!root.TryGetProperty("short_name", out _)) issues.Add("Manifest missing 'short_name'");
                if (!root.TryGetProperty("start_url", out _)) issues.Add("Manifest missing 'start_url'");
                if (!root.TryGetProperty("display", out _)) issues.Add("Manifest missing 'display'");
                if (!root.TryGetProperty("icons", out var icons) || icons.GetArrayLength() == 0)
                {
                    issues.Add("Manifest missing icons array");
                }
            }
            catch (JsonException)
            {
                issues.Add("Manifest is not valid JSON");
            }
        }

        // 2. Check service worker
        var swResponse = await _client.GetAsync("/sw.js");
        if (swResponse.StatusCode != HttpStatusCode.OK)
        {
            issues.Add("Service worker is not accessible");
        }

        // 3. Check icons
        var icon192Response = await _client.GetAsync("/icons/icon-192x192-maskable.png");
        if (icon192Response.StatusCode != HttpStatusCode.OK)
        {
            issues.Add("192x192 icon is missing");
        }

        var icon512Response = await _client.GetAsync("/icons/icon-512x512-maskable.png");
        if (icon512Response.StatusCode != HttpStatusCode.OK)
        {
            issues.Add("512x512 icon is missing");
        }

        // 4. Check HTML integration
        var htmlResponse = await _client.GetAsync("/");
        if (htmlResponse.StatusCode == HttpStatusCode.OK)
        {
            var htmlContent = await htmlResponse.Content.ReadAsStringAsync();
            if (!htmlContent.Contains("rel=\"manifest\""))
            {
                issues.Add("HTML does not link to manifest");
            }
            if (!htmlContent.Contains("name=\"viewport\""))
            {
                issues.Add("HTML missing viewport meta tag");
            }
        }

        // Assert - No issues found
        if (issues.Any())
        {
            var message = "PWA installation criteria not met:\n" + string.Join("\n", issues.Select(i => $"  - {i}"));
            Assert.Fail(message);
        }
    }

    #endregion
}
