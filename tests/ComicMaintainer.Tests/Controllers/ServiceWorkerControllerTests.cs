using ComicMaintainer.Core.Configuration;
using ComicMaintainer.WebApi.Controllers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Controllers;

public class ServiceWorkerControllerTests
{
    private readonly Mock<IWebHostEnvironment> _mockEnvironment;
    private readonly Mock<ILogger<ServiceWorkerController>> _mockLogger;
    private readonly Mock<IOptions<AppSettings>> _mockAppSettings;
    private readonly ServiceWorkerController _controller;
    private readonly string _tempDirectory;

    public ServiceWorkerControllerTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "sw_test_" + Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDirectory);

        _mockEnvironment = new Mock<IWebHostEnvironment>();
        _mockEnvironment.Setup(e => e.WebRootPath).Returns(_tempDirectory);

        _mockLogger = new Mock<ILogger<ServiceWorkerController>>();

        var appSettings = new AppSettings();
        _mockAppSettings = new Mock<IOptions<AppSettings>>();
        _mockAppSettings.Setup(a => a.Value).Returns(appSettings);

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

    [Fact]
    public void GetServiceWorker_WhenFileExists_ReturnsContentWithCorrectContentType()
    {
        // Arrange
        var swContent = "// Service Worker for Comic Maintainer PWA\nconsole.log('Service worker loaded');";
        var swPath = Path.Combine(_tempDirectory, "sw.js");
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
        Assert.True(_controller.Response.Headers.ContainsKey("Cache-Control"));
        Assert.Equal("no-cache, no-store, must-revalidate", _controller.Response.Headers["Cache-Control"].ToString());
        
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
        _mockEnvironment.Setup(e => e.WebRootPath).Returns((string)null!);

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
        var manifestPath = Path.Combine(_tempDirectory, "manifest.json");
        File.WriteAllText(manifestPath, manifestContent);

        // Act
        var result = _controller.GetManifest();

        // Assert
        var contentResult = Assert.IsType<ContentResult>(result);
        Assert.Equal("application/manifest+json; charset=utf-8", contentResult.ContentType);
        Assert.Equal(manifestContent, contentResult.Content);
        
        // Verify cache control headers were set
        Assert.True(_controller.Response.Headers.ContainsKey("Cache-Control"));
        Assert.Equal("public, max-age=3600", _controller.Response.Headers["Cache-Control"].ToString());
        
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
}
