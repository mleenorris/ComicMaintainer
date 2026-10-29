using ComicMaintainer.Tests.Helpers;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.WebApi.Controllers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Controllers;

public class ServiceWorkerControllerTests : IDisposable
{
    private readonly Mock<IWebHostEnvironment> _mockEnvironment;
    private readonly Mock<ILogger<ServiceWorkerController>> _mockLogger;
    private readonly Mock<IOptionsMonitor<AppSettings>> _mockAppSettings;
    private readonly ServiceWorkerController _controller;
    private readonly string _tempDirectory;

    public ServiceWorkerControllerTests()
    {
        _tempDirectory = Path.Join(Path.GetTempPath(), "sw_test_" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDirectory);

        _mockEnvironment = new Mock<IWebHostEnvironment>();
        _mockEnvironment.Setup(e => e.WebRootPath).Returns(_tempDirectory);

        _mockLogger = new Mock<ILogger<ServiceWorkerController>>();

        var appSettings = new AppSettings();
        _mockAppSettings = new Mock<IOptionsMonitor<AppSettings>>();
        _mockAppSettings.Setup(a => a.CurrentValue).Returns(appSettings);

        _controller = new ServiceWorkerController(
            _mockEnvironment.Object,
            _mockLogger.Object,
            _mockAppSettings.Object);

        // Setup ControllerContext with HttpContext to allow Response.Headers access
        var httpContext = new DefaultHttpContext();
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };
    }

    public void Dispose()
    {
        TestIO.TryDeleteDirectory(_tempDirectory);
    }

    [Fact]
    public void Dispose_DeletesTheTempDirectory()
    {
        // Regression test for #876: every test used to leave its directory behind.
        File.WriteAllText(Path.Join(_tempDirectory, "sw.js"), "// Service Worker");

        // xUnit only disposes test classes that implement IDisposable.
        Assert.IsAssignableFrom<IDisposable>(this);
        Dispose();

        Assert.False(Directory.Exists(_tempDirectory));
    }

    [Fact]
    public void GetServiceWorker_WhenFileExists_ReturnsContentWithCorrectContentType()
    {
        // Arrange
        var swContent = "// Service Worker for Comic Maintainer PWA\nconsole.log('Service worker loaded');";
        var swPath = Path.Join(_tempDirectory, "sw.js");
        File.WriteAllText(swPath, swContent);

        // Act
        var result = _controller.GetServiceWorker();

        // Assert
        var contentResult = Assert.IsType<ContentResult>(result);
        Assert.Equal("application/javascript; charset=utf-8", contentResult.ContentType);
        
        // Verify content includes version comment (injected by controller) and original content
        Assert.NotNull(contentResult.Content);
        Assert.StartsWith("// Service Worker Version:", contentResult.Content);
        Assert.Contains(swContent, contentResult.Content);
        
        // Verify cache control headers were set
        Assert.True(_controller.Response.Headers.TryGetValue("Cache-Control", out var cacheControl));
        Assert.Equal("no-cache, no-store, must-revalidate", cacheControl.ToString());
        
        // Verify CORS headers were set
        Assert.True(_controller.Response.Headers.TryGetValue("Access-Control-Allow-Origin", out var allowOrigin));
        Assert.Equal("*", allowOrigin.ToString());
        Assert.True(_controller.Response.Headers.TryGetValue("Access-Control-Allow-Methods", out var allowMethods));
        Assert.Equal("GET, OPTIONS", allowMethods.ToString());
        
        // Cleanup
        File.Delete(swPath);
    }

    [Fact]
    public void GetServiceWorker_WhenFileDoesNotExist_ReturnsNotFound()
    {
        // Act
        var result = _controller.GetServiceWorker();

        // Assert
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public void GetServiceWorker_WhenExceptionOccurs_ReturnsServerError()
    {
        // Arrange - Set WebRootPath to null to cause an exception
        _mockEnvironment.Setup(e => e.WebRootPath).Returns(default(string)!);

        var controller = new ServiceWorkerController(
            _mockEnvironment.Object,
            _mockLogger.Object,
            _mockAppSettings.Object);

        // Act
        var result = controller.GetServiceWorker();

        // Assert
        var statusCodeResult = Assert.IsType<StatusCodeResult>(result);
        Assert.Equal(500, statusCodeResult.StatusCode);
    }

    [Fact]
    public void GetServiceWorker_HasResponseCacheAttribute()
    {
        // Arrange
        var method = typeof(ServiceWorkerController).GetMethod("GetServiceWorker");

        // Act
        var attributes = method?.GetCustomAttributes(typeof(ResponseCacheAttribute), false);

        // Assert
        Assert.NotNull(attributes);
        Assert.Single(attributes);
        var cacheAttribute = attributes[0] as ResponseCacheAttribute;
        Assert.NotNull(cacheAttribute);
        Assert.True(cacheAttribute.NoStore);
        Assert.Equal(ResponseCacheLocation.None, cacheAttribute.Location);
    }

    [Fact]
    public void GetManifest_WhenFileExists_ReturnsContentWithCorrectContentType()
    {
        // Arrange
        var manifestContent = "{\"name\": \"Comic Maintainer\", \"short_name\": \"ComicMaintainer\"}";
        var manifestPath = Path.Join(_tempDirectory, "manifest.json");
        File.WriteAllText(manifestPath, manifestContent);

        // Act
        var result = _controller.GetManifest();

        // Assert
        var contentResult = Assert.IsType<ContentResult>(result);
        Assert.Equal("application/manifest+json; charset=utf-8", contentResult.ContentType);
        Assert.Equal(manifestContent, contentResult.Content);
        
        // Verify cache control headers were set
        Assert.True(_controller.Response.Headers.TryGetValue("Cache-Control", out var cacheControl));
        Assert.Equal("public, max-age=3600", cacheControl.ToString());
        
        // Verify CORS headers were set
        Assert.True(_controller.Response.Headers.TryGetValue("Access-Control-Allow-Origin", out var allowOrigin));
        Assert.Equal("*", allowOrigin.ToString());
        Assert.True(_controller.Response.Headers.TryGetValue("Access-Control-Allow-Methods", out var allowMethods));
        Assert.Equal("GET, OPTIONS", allowMethods.ToString());
        
        // Cleanup
        File.Delete(manifestPath);
    }

    [Fact]
    public void GetManifest_WhenFileDoesNotExist_ReturnsNotFound()
    {
        // Act
        var result = _controller.GetManifest();

        // Assert
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public void GetServiceWorker_HasAllowAnonymousAttribute()
    {
        // Arrange
        var method = typeof(ServiceWorkerController).GetMethod("GetServiceWorker");

        // Act
        var attributes = method?.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute), false);

        // Assert
        Assert.NotNull(attributes);
        Assert.Single(attributes);
    }

    [Fact]
    public void GetManifest_HasAllowAnonymousAttribute()
    {
        // Arrange
        var method = typeof(ServiceWorkerController).GetMethod("GetManifest");

        // Act
        var attributes = method?.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute), false);

        // Assert
        Assert.NotNull(attributes);
        Assert.Single(attributes);
    }

    [Fact]
    public void GetManifest_WhenExceptionOccurs_ReturnsServerError()
    {
        // Arrange - Set WebRootPath to null to cause an exception
        _mockEnvironment.Setup(e => e.WebRootPath).Returns(default(string)!);

        var controller = new ServiceWorkerController(
            _mockEnvironment.Object,
            _mockLogger.Object,
            _mockAppSettings.Object);

        // Act
        var result = controller.GetManifest();

        // Assert
        var statusCodeResult = Assert.IsType<StatusCodeResult>(result);
        Assert.Equal(500, statusCodeResult.StatusCode);
    }

    [Fact]
    public void GetManifest_HasResponseCacheAttribute()
    {
        // Arrange
        var method = typeof(ServiceWorkerController).GetMethod("GetManifest");

        // Act
        var attributes = method?.GetCustomAttributes(typeof(ResponseCacheAttribute), false);

        // Assert
        Assert.NotNull(attributes);
        Assert.Single(attributes);
        var cacheAttribute = attributes[0] as ResponseCacheAttribute;
        Assert.NotNull(cacheAttribute);
        Assert.Equal(3600, cacheAttribute.Duration);
        Assert.Equal(ResponseCacheLocation.Any, cacheAttribute.Location);
    }
}
