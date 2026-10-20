using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Models.Auth;
using ComicMaintainer.Core.Services;
using ComicMaintainer.WebApi.Hubs;
using ComicMaintainer.WebApi.Middleware;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ComicMaintainer.WebApi.Startup;

/// <summary>
/// Database initialization/seeding and HTTP request pipeline configuration.
/// </summary>
internal static class WebApplicationExtensions
{
    public const string SwaggerEnabledKey = "Api:EnableSwagger";

    public static async Task InitializeComicMaintainerDatabaseAsync(this WebApplication app, Microsoft.Extensions.Logging.ILogger logger)
    {
        // Initialize database and seed roles
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ComicMaintainerDbContext>();

            // A restore staged through /api/backups is swapped in here, before anything
            // has opened the database and before migrations bring it up to date.
            var databasePath = CoreServiceCollectionExtensions.ResolveSqliteDatabasePath(db.Database.GetConnectionString());
            if (databasePath is not null)
            {
                DatabaseBackupService.ApplyPendingRestore(databasePath, logger);
            }

            db.Database.Migrate();
            
            // Seed roles
            var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
            var roles = new[] { "Admin", "User", "ReadOnly" };
            
            foreach (var roleName in roles)
            {
                if (!await roleManager.RoleExistsAsync(roleName))
                {
                    await roleManager.CreateAsync(new ApplicationRole 
                    { 
                        Name = roleName,
                        Description = $"{roleName} role"
                    });
                }
            }
            
            // Check if admin user should be seeded from environment variables (for backward compatibility)
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var adminUsername = Environment.GetEnvironmentVariable("ADMIN_USERNAME");
            var adminPassword = Environment.GetEnvironmentVariable("ADMIN_PASSWORD");
            
            // Only create default admin if both ADMIN_USERNAME and ADMIN_PASSWORD are explicitly set
            if (!string.IsNullOrEmpty(adminUsername) && !string.IsNullOrEmpty(adminPassword))
            {
                if (await userManager.FindByNameAsync(adminUsername) == null)
                {
                    var adminEmail = Environment.GetEnvironmentVariable("ADMIN_EMAIL") ?? $"{adminUsername}@comicmaintainer.local";
                    var adminUser = new ApplicationUser
                    {
                        UserName = adminUsername,
                        Email = adminEmail,
                        FullName = "Administrator",
                        IsActive = true,
                        EmailConfirmed = true
                    };
                    
                    var result = await userManager.CreateAsync(adminUser, adminPassword);
                    if (result.Succeeded)
                    {
                        await userManager.AddToRoleAsync(adminUser, "Admin");
                        logger.LogInformation("Admin user '{Username}' created from environment variables", adminUsername);
                    }
                }
            }
        }
    }

    public static WebApplication UseComicMaintainerPipeline(this WebApplication app)
    {
        // Configure the HTTP request pipeline
        //
        // The correlation middleware runs first so that *everything* downstream —
        // including the exception handler, the security headers and every log line —
        // shares one request id.
        app.UseMiddleware<RequestCorrelationMiddleware>();

        // Catch anything a controller lets escape and turn it into a traceable problem
        // document (see GlobalExceptionHandler). Must wrap the rest of the pipeline.
        app.UseExceptionHandler();

        app.UseCors();

        // Use response compression
        app.UseResponseCompression();

        // Use output caching
        app.UseOutputCache();

        // Add security headers middleware
        app.Use(async (context, next) =>
        {
            // Prevent MIME type sniffing
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            
            // Control referrer information
            context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            
            // Restrict dangerous browser features
            context.Response.Headers["Permissions-Policy"] = "geolocation=(), microphone=(), camera=()";
            
            // Content-Security-Policy.
            //
            // The web UI is entirely first-party: no CDN scripts, styles or fonts. It
            // does however rely on inline <script>/<style> blocks and inline style
            // attributes inside index.html/reader.html, so script-src and style-src
            // must allow 'unsafe-inline' until the inline code is extracted. The
            // remaining directives still provide meaningful protection: injected
            // markup cannot load an attacker-hosted script, exfiltrate data to a
            // third-party origin, embed plugins, rewrite <base>, or post forms
            // off-origin.
            //
            // img-src allows https: because series covers may be fetched directly from
            // external metadata providers, and blob:/data: because covers and comic
            // pages are rendered from object URLs.
            const string baseCspDirectives =
                "default-src 'self'; " +
                "script-src 'self' 'unsafe-inline'; " +
                "style-src 'self' 'unsafe-inline'; " +
                "img-src 'self' data: blob: https:; " +
                "font-src 'self' data:; " +
                "media-src 'self' blob:; " +
                "connect-src 'self' ws: wss:; " +
                "worker-src 'self'; " +
                "manifest-src 'self'; " +
                "object-src 'none'; " +
                "base-uri 'self'; " +
                "form-action 'self'; " +
                "frame-ancestors 'none'";

            // Add HSTS and CSP headers when behind HTTPS proxy
            // Use case-insensitive comparison as HTTP headers are case-insensitive per RFC 7230
            if (context.Request.Headers.TryGetValue("X-Forwarded-Proto", out var forwardedProto) && 
                forwardedProto.ToString().Equals("https", StringComparison.OrdinalIgnoreCase))
            {
                // HSTS: Force HTTPS for 1 year
                context.Response.Headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
                
                // CSP: Upgrade insecure requests and prevent framing (replaces X-Frame-Options)
                if (!context.Response.Headers.ContainsKey("Content-Security-Policy"))
                {
                    context.Response.Headers["Content-Security-Policy"] =
                        baseCspDirectives + "; upgrade-insecure-requests";
                }
            }
            else
            {
                // CSP: Prevent framing even without HTTPS (replaces X-Frame-Options)
                if (!context.Response.Headers.ContainsKey("Content-Security-Policy"))
                {
                    context.Response.Headers["Content-Security-Policy"] = baseCspDirectives;
                }
            }
            
            // Prevent caching for API endpoints and HTML pages
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.Headers["Cache-Control"] = "no-store, private";
            }
            
            await next();
        });

        // Add path validation middleware for security
        app.UseMiddleware<PathValidationMiddleware>();

        // Swagger is always on in Development; elsewhere only when Api:EnableSwagger
        // (env API__ENABLESWAGGER) is true. The UI and document are anonymous and only
        // describe the API surface — every endpoint they list still enforces its own auth.
        // Registered after the security headers and path validation so its responses
        // carry the same CSP (the bundled UI needs nothing beyond 'self', inline
        // script/style and data: images, all of which the policy allows).
        if (app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>(SwaggerEnabledKey))
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }

        // Prometheus scraping endpoint (/metrics) when Telemetry:PrometheusEnabled is set.
        // Branches off before static files, routing, authentication and the SPA fallback.
        app.UseComicMaintainerMetricsEndpoint();

        // Inject the current assembly version into HTML pages (replaces __APP_VERSION__).
        // This MUST run before UseStaticFiles so that direct requests for *.html
        // (and the root "/") never get served as raw static files containing the
        // unsubstituted placeholder.
        app.UseMiddleware<HtmlVersionInjectionMiddleware>();

        // The version the HTML version-injection middleware stamps into /css and /js
        // URLs. Requests whose ?v= matches it can be cached immutably (see below).
        var appVersion = System.Reflection.Assembly.GetExecutingAssembly()
            .GetName()
            .Version?
            .ToString() ?? "1.0.0";

        // Serve static files from wwwroot with cache control.
        // sw.js is served via ServiceWorkerController (so that the response carries
        // the injected version comment, no-store cache headers, and no redirects).
        // We must skip the static-file middleware entirely for /sw.js because
        // StaticFileMiddleware short-circuits the pipeline as soon as it finds a
        // matching file on disk — using OnPrepareResponse to flip the status to
        // 404 there only changes the status code, the file body still gets written
        // AND the controller never runs, so the browser sees a 404 with the file
        // contents and refuses to register the service worker
        // ("A bad HTTP response code (404) was received when fetching the script").
        app.UseWhen(
            ctx => !ctx.Request.Path.Equals("/sw.js", StringComparison.OrdinalIgnoreCase),
            branch => branch.UseStaticFiles(new StaticFileOptions
        {
            OnPrepareResponse = ctx =>
            {
                // Don't cache HTML files to ensure users always get the latest version.
                // (Normally these are served by HtmlVersionInjectionMiddleware above and
                // never reach the static file handler, but keep this as defense in depth.)
                if (ctx.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                {
                    ctx.Context.Response.Headers["Cache-Control"] = "no-store, private";
                }
                // CSS and JS are versioned via ?v=<app-version> query strings emitted in
                // the HTML. When the query string matches the running app version the
                // URL is guaranteed to be content-stable for the lifetime of that
                // release, so it can be cached immutably — the HTML (which is never
                // cached) hands out a new URL on the next deploy.
                //
                // Anything else under /css or /js (no ?v=, or a stale/mismatched one)
                // must not be cached without revalidation, otherwise a new HTML
                // referencing a new version could still pull an old asset body from the
                // disk cache. "no-cache" still allows 304 revalidation, it just refuses
                // to serve the body without checking with the origin first.
                else if (ctx.Context.Request.Path.StartsWithSegments("/css") ||
                         ctx.Context.Request.Path.StartsWithSegments("/js"))
                {
                    var requestedVersion = ctx.Context.Request.Query["v"].ToString();
                    if (!string.IsNullOrEmpty(requestedVersion) &&
                        string.Equals(requestedVersion, appVersion, StringComparison.Ordinal))
                    {
                        ctx.Context.Response.Headers["Cache-Control"] = "public, max-age=31536000, immutable";
                    }
                    else
                    {
                        ctx.Context.Response.Headers["Cache-Control"] = "no-cache, must-revalidate";
                    }
                }
                // Cache other static assets (images, icons, fonts) for 1 hour with cache busting via query string
                else
                {
                    ctx.Context.Response.Headers["Cache-Control"] = "public, max-age=3600";
                }
            }
        }));

        app.UseRouting();

        // Request timeouts must run *after* routing: RequestTimeoutsMiddleware reads the
        // policy off the matched endpoint, so before UseRouting() there is no endpoint
        // and every request — a condensed-book build, a multi-gigabyte download, the SSE
        // stream — silently got the 30 second default with no way to opt out.
        app.UseRequestTimeouts();

        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();

        app.MapControllers();
        // A hub connection is open for as long as the page is, so it must never be
        // subject to the default request timeout.
        app.MapHub<ProgressHub>("/hubs/progress").DisableRequestTimeout();

        // Map health check endpoints. Both are anonymous so probes do not need credentials.
        app.MapHealthChecks("/health", new HealthCheckOptions
        {
            // Liveness: run no checks, just confirm the app responds.
            Predicate = _ => false
        }).AllowAnonymous();

        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("ready")
        }).AllowAnonymous();

        // Map default route to serve index.html for non-API routes only
        // This prevents the fallback from catching API requests, ensuring they always return JSON.
        // Index is served via a custom delegate so that __APP_VERSION__ is substituted
        // (mirroring HtmlVersionInjectionMiddleware) on SPA routes that don't match a real file.
        app.MapFallback(async context =>
        {
            // Don't serve index.html for sw.js - it's handled by ServiceWorkerController
            if (context.Request.Path.StartsWithSegments("/sw.js"))
            {
                context.Response.StatusCode = 404;
                context.Response.ContentType = "application/json; charset=utf-8";
                await context.Response.WriteAsync("{\"error\":\"Not Found\",\"message\":\"Service worker not found\"}");
                return;
            }

            // Only serve index.html for non-API and non-hub routes
            if (context.Request.Path.StartsWithSegments("/api") ||
                context.Request.Path.StartsWithSegments("/hubs"))
            {
                context.Response.StatusCode = 404;
                context.Response.ContentType = "application/json; charset=utf-8";
                await context.Response.WriteAsync("{\"error\":\"Not Found\",\"message\":\"The requested endpoint does not exist\"}");
                return;
            }

            var env = context.RequestServices.GetRequiredService<IWebHostEnvironment>();
            var indexPath = Path.Combine(env.WebRootPath ?? string.Empty, "index.html");
            if (!File.Exists(indexPath))
            {
                context.Response.StatusCode = 404;
                return;
            }

            var html = await File.ReadAllTextAsync(indexPath);
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
            html = html.Replace("__APP_VERSION__", version);

            context.Response.Headers["Cache-Control"] = "no-store, private";
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync(html);
        });

        return app;
    }
}
