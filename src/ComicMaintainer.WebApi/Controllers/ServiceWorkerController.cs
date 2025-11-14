using ComicMaintainer.Core.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.WebApi.Controllers;

/// <summary>
/// Controller for serving the service worker file.
/// This controller serves sw.js directly to avoid redirect issues that can occur
/// when serving static files behind reverse proxies or with HTTPS redirects.
/// ServiceWorker specification requires the script to be served without redirects.
/// </summary>
[ApiController]
[Route("")]
public class ServiceWorkerController : ControllerBase
{
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<ServiceWorkerController> _logger;
    private readonly AppSettings _appSettings;

    public ServiceWorkerController(
        IWebHostEnvironment environment, 
        ILogger<ServiceWorkerController> logger,
        IOptions<AppSettings> appSettings)
    {
        _environment = environment;
        _logger = logger;
        _appSettings = appSettings.Value;
    }

    /// <summary>
    /// Serves the service worker JavaScript file with proper headers to prevent redirect issues.
    /// Injects version information into the service worker content to force browser updates.
    /// This endpoint must be publicly accessible for PWA functionality to work correctly.
    /// </summary>
    /// <returns>The service worker JavaScript file</returns>
    [HttpGet("sw.js")]
    [AllowAnonymous]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult GetServiceWorker()
    {
        try
        {
            var swPath = Path.Combine(_environment.WebRootPath, "sw.js");
            
            if (!System.IO.File.Exists(swPath))
            {
                _logger.LogError("Service worker file not found at: {Path}", swPath);
                return NotFound();
            }

            var swContent = System.IO.File.ReadAllText(swPath);
            
            // Inject version into service worker content to force browser updates
            // This ensures the browser detects changes to the service worker file
            var version = System.Reflection.Assembly.GetExecutingAssembly()
                .GetName()
                .Version?
                .ToString() ?? "1.0.0";
            
            // Add version as a comment at the top of the file
            // Browsers use byte-for-byte comparison to detect SW changes
            swContent = $"// Service Worker Version: {version}\n{swContent}";
            
            // Set proper headers to prevent caching and ensure correct MIME type
            // This prevents redirect issues that can occur with cached or improperly served files
            Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
            Response.Headers["Pragma"] = "no-cache";
            Response.Headers["Expires"] = "0";
            
            // Add CORS headers to allow service worker to be accessed from any origin
            // This is necessary for PWA functionality to work correctly, especially when
            // the application is behind a reverse proxy with authentication
            Response.Headers["Access-Control-Allow-Origin"] = "*";
            Response.Headers["Access-Control-Allow-Methods"] = "GET, OPTIONS";
            Response.Headers["Access-Control-Allow-Headers"] = "Content-Type";
            
            return Content(swContent, "application/javascript; charset=utf-8");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error serving service worker");
            return StatusCode(500);
        }
    }

    /// <summary>
    /// Serves the PWA manifest file with proper cache headers.
    /// While the manifest doesn't change often, we ensure it's not cached indefinitely
    /// to allow for updates when the application is updated.
    /// This endpoint must be publicly accessible for PWA installation to work correctly.
    /// </summary>
    /// <returns>The manifest.json file</returns>
    [HttpGet("manifest.json")]
    [AllowAnonymous]
    [ResponseCache(Duration = 3600, Location = ResponseCacheLocation.Any, VaryByQueryKeys = new[] { "v" })]
    public IActionResult GetManifest()
    {
        try
        {
            var manifestPath = Path.Combine(_environment.WebRootPath, "manifest.json");
            
            if (!System.IO.File.Exists(manifestPath))
            {
                _logger.LogError("Manifest file not found at: {Path}", manifestPath);
                return NotFound();
            }

            var manifestContent = System.IO.File.ReadAllText(manifestPath);
            
            // Allow caching for 1 hour, but use version query parameter for cache busting
            Response.Headers["Cache-Control"] = "public, max-age=3600";
            
            // Add CORS headers to allow manifest.json to be accessed from any origin
            // This is necessary for PWA installation to work correctly, especially when
            // the application is behind a reverse proxy with authentication
            Response.Headers["Access-Control-Allow-Origin"] = "*";
            Response.Headers["Access-Control-Allow-Methods"] = "GET, OPTIONS";
            Response.Headers["Access-Control-Allow-Headers"] = "Content-Type";
            
            return Content(manifestContent, "application/manifest+json; charset=utf-8");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error serving manifest");
            return StatusCode(500);
        }
    }
}
