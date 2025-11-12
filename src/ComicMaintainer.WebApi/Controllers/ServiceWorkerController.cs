using Microsoft.AspNetCore.Mvc;

namespace ComicMaintainer.WebApi.Controllers;

/// <summary>
/// Controller for serving the service worker file.
/// This controller serves sw.js directly to avoid redirect issues that can occur
/// when serving static files behind reverse proxies or with HTTPS redirects.
/// ServiceWorker specification requires the script to be served without redirects.
/// </summary>
[ApiController]
public class ServiceWorkerController : ControllerBase
{
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<ServiceWorkerController> _logger;

    public ServiceWorkerController(IWebHostEnvironment environment, ILogger<ServiceWorkerController> logger)
    {
        _environment = environment;
        _logger = logger;
    }

    /// <summary>
    /// Serves the service worker JavaScript file with proper headers to prevent redirect issues.
    /// </summary>
    /// <returns>The service worker JavaScript file</returns>
    [HttpGet("sw.js")]
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
            
            // Set proper headers to prevent caching and ensure correct MIME type
            // This prevents redirect issues that can occur with cached or improperly served files
            Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
            Response.Headers["Pragma"] = "no-cache";
            Response.Headers["Expires"] = "0";
            
            return Content(swContent, "application/javascript; charset=utf-8");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error serving service worker");
            return StatusCode(500);
        }
    }
}
