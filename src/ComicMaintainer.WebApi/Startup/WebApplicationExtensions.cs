using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.ErrorReporting;
using ComicMaintainer.Core.Models.Auth;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Core.Services.Auth;
using ComicMaintainer.Core.Utilities;
using ComicMaintainer.WebApi.Middleware;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

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
            IDatabaseBackupService? backups = null;
            if (databasePath is not null)
            {
                DatabaseBackupService.ApplyPendingRestore(databasePath, logger);

                // An upgrade's migrations cannot be undone, so the database is backed up before they run.
                backups = scope.ServiceProvider.GetService<IDatabaseBackupService>();
                await PreMigrationBackup.CreateIfMigrationsPendingAsync(db.Database, databasePath, backups, logger);
            }

            db.Database.Migrate();
            // Reached only when the migrations succeeded: until then a restart retries the upgrade with the same backup.
            backups?.MarkMigrationsApplied();

            // Seed roles
            var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
            var roles = new[] { "Admin", "User", "ReadOnly" };
            
            foreach (var roleName in roles)
            {
                await EnsureRoleExistsAsync(roleManager, db, roleName);
            }
            
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            await SeedAdminFromEnvironmentAsync(userManager, logger);

            var autheliaSettings = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ComicMaintainer.Core.Configuration.AutheliaSettings>>().Value;
            if (await FirstRunSetup.IsAvailableAsync(userManager, autheliaSettings))
            {
                LogFirstRunSetupCode(scope.ServiceProvider.GetRequiredService<ISetupTokenProvider>(), logger);
            }
        }
    }

    // First-run setup requires a one-time code, so that whoever reaches a fresh, exposed instance first cannot claim
    // it: only someone who can read this log (docker logs) or set SETUP_TOKEN can create the first administrator.
    private static void LogFirstRunSetupCode(ISetupTokenProvider setupToken, Microsoft.Extensions.Logging.ILogger logger)
    {
        if (setupToken.IsConfigured)
        {
            logger.LogWarning(
                "No account exists yet. Open ComicMaintainer in a browser and enter the setup code set in SETUP_TOKEN " +
                "to create the first administrator.");
        }
        else
        {
            logger.LogWarning(
                "No account exists yet. Open ComicMaintainer in a browser and enter the setup code {SetupCode} to create " +
                "the first administrator. A new code is generated on every start until then; set SETUP_TOKEN to choose your own.",
                setupToken.Token);
        }
    }

    /// <summary>
    /// Creates the administrator named by <c>ADMIN_USERNAME</c> with the password in <c>ADMIN_PASSWORD</c> (or the
    /// file <c>ADMIN_PASSWORD_FILE</c> names, such as a Docker secret) when both are set and no account has that name.
    /// A password published with ComicMaintainer (see <see cref="PublishedPasswords"/>) is refused, so that the
    /// first-run setup runs instead. An existing account with that name is left alone, unless it still has a
    /// published password: that is replaced by <c>ADMIN_PASSWORD</c> when it is a password of the operator's own.
    /// </summary>
    private static async Task SeedAdminFromEnvironmentAsync(UserManager<ApplicationUser> userManager, Microsoft.Extensions.Logging.ILogger logger)
    {
        var adminUsername = Environment.GetEnvironmentVariable("ADMIN_USERNAME");
        var adminPassword = SecretEnvironmentVariable.Read("ADMIN_PASSWORD", out var credentialSetting);
        if (string.IsNullOrEmpty(adminUsername))
        {
            return;
        }

        // Everything logged below is about the operator's ADMIN_* settings: they need to see it, but it is not a
        // defect to file an issue for.
        using var errorReportExclusion = logger.BeginScope(ErrorReportLogProperties.Exclude("operator configuration: admin account settings"));

        var safeUsername = LoggingHelper.SanitizeForLog(adminUsername);
        var existing = await userManager.FindByNameAsync(adminUsername);
        if (existing is not null)
        {
            await ReplacePublishedAdminPasswordAsync(userManager, existing, adminPassword, credentialSetting, safeUsername, logger);
            return;
        }

        if (string.IsNullOrEmpty(adminPassword))
        {
            return;
        }

        if (PublishedPasswords.IsPublished(adminPassword))
        {
            logger.LogError(
                "Not creating the administrator '{Username}': {PasswordSource} is set to a password that was published " +
                "with ComicMaintainer, so anyone could sign in with it. Open the web UI to create the first administrator " +
                "with first-run setup, or set {PasswordSource} to a password of your own and restart.",
                safeUsername, credentialSetting, credentialSetting);
            return;
        }

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
        if (!result.Succeeded)
        {
            logger.LogError(
                "Not creating the administrator '{Username}' from {PasswordSource}: {Errors}",
                safeUsername, credentialSetting, string.Join(" ", result.Errors.Select(e => e.Description)));
            return;
        }

        await userManager.AddToRoleAsync(adminUser, "Admin");
        logger.LogInformation("Admin user '{Username}' created from environment variables", safeUsername);
    }

    // Before 3.0.0 the shipped docker-compose.dotnet.yml and .env.example seeded admin / Admin123!. Sign-in with a
    // published password is refused (AuthService), so give the operator a way back in: setting ADMIN_PASSWORD to a
    // password of their own replaces it on the next start.
    private static async Task ReplacePublishedAdminPasswordAsync(
        UserManager<ApplicationUser> userManager,
        ApplicationUser admin,
        string? adminPassword,
        string? credentialSetting,
        string safeUsername,
        Microsoft.Extensions.Logging.ILogger logger)
    {
        if (!PublishedPasswords.MatchesUser(userManager.PasswordHasher, admin))
        {
            return;
        }

        if (string.IsNullOrEmpty(adminPassword) || PublishedPasswords.IsPublished(adminPassword))
        {
            logger.LogError(
                "The account '{Username}' still has a password that was published with ComicMaintainer, so signing in " +
                "with it is refused. Set ADMIN_PASSWORD (or ADMIN_PASSWORD_FILE) to a password of your own and restart " +
                "to replace it.",
                safeUsername);
            return;
        }

        var resetToken = await userManager.GeneratePasswordResetTokenAsync(admin);
        var result = await userManager.ResetPasswordAsync(admin, resetToken, adminPassword);
        if (result.Succeeded)
        {
            logger.LogWarning(
                "Replaced the published password of the account '{Username}' with the one in {PasswordSource}",
                safeUsername, credentialSetting);
        }
        else
        {
            logger.LogError(
                "Could not replace the published password of the account '{Username}' with the one in {PasswordSource}: {Errors}",
                safeUsername, credentialSetting, string.Join(" ", result.Errors.Select(e => e.Description)));
        }
    }

    private static async Task EnsureRoleExistsAsync(
        RoleManager<ApplicationRole> roleManager,
        ComicMaintainerDbContext db,
        string roleName)
    {
        if (await roleManager.RoleExistsAsync(roleName))
        {
            return;
        }

        var role = new ApplicationRole
        {
            Name = roleName,
            Description = $"{roleName} role"
        };

        try
        {
            await roleManager.CreateAsync(role);
        }
        catch (DbUpdateException)
        {
            if (!await roleManager.RoleExistsAsync(roleName))
            {
                throw;
            }

            db.Entry(role).State = EntityState.Detached;
        }
    }

    public static WebApplication UseComicMaintainerPipeline(this WebApplication app)
    {
        // BASE_PATH deployments. A reverse proxy either forwards the prefix (/comics/js/x.js, as
        // docs/swag-configs/comicmaintainer.subfolder.conf does) or strips it (/js/x.js); this
        // moves a forwarded prefix into PathBase and leaves a stripped request alone, so the rest
        // of the pipeline sees the same path either way. It must run before UseRouting.
        var configuredBasePath = app.Services.GetRequiredService<IOptions<AppSettings>>().Value.BasePath;
        var basePath = AppBasePath.Normalize(configuredBasePath);
        if (AppBasePath.IsInvalid(configuredBasePath))
        {
            app.Logger.LogError(
                "Ignoring BASE_PATH '{BasePath}': use a path such as /comics, with only letters, digits, '.', '_', '~' and '-' in each segment. Serving the app at the domain root.",
                LoggingHelper.SanitizeForLog(configuredBasePath));
        }
        if (basePath.Length > 0)
        {
            app.UsePathBase(basePath);
            app.Logger.LogInformation("Serving the app under the base path {BasePath}", basePath);
        }

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
            // does however rely on inline event-handler attributes (onclick=...), a
            // couple of small inline <script> blocks in index.html, and inline
            // <style> blocks and style attributes, so script-src and style-src must
            // allow 'unsafe-inline' until those are removed as well. The
            // remaining directives still provide meaningful protection: injected
            // markup cannot load an attacker-hosted script, open a fetch, XHR,
            // EventSource or WebSocket connection to another origin (connect-src
            // is same-origin only; the app's only stream is the same-origin SSE
            // endpoint), embed plugins, rewrite <base>, or post forms off-origin.
            //
            // img-src allows https: because series cover candidates are previewed
            // straight from external metadata providers, and blob:/data: because
            // covers and comic pages are rendered from object URLs. That means an
            // injected script could still leak data in the URL of an image request,
            // so the CSP limits the damage of an XSS but does not prevent it.
            const string baseCspDirectives =
                "default-src 'self'; " +
                "script-src 'self' 'unsafe-inline'; " +
                "style-src 'self' 'unsafe-inline'; " +
                "img-src 'self' data: blob: https:; " +
                "font-src 'self' data:; " +
                "media-src 'self' blob:; " +
                "connect-src 'self'; " +
                "worker-src 'self'; " +
                "manifest-src 'self'; " +
                "object-src 'none'; " +
                "base-uri 'self'; " +
                "form-action 'self'; " +
                "frame-ancestors 'none'";

            // Add HSTS and CSP headers when behind HTTPS proxy
            // Use case-insensitive comparison as HTTP headers are case-insensitive per RFC 7230.
            // The first value is the client's scheme; a proxy that sets the header twice sends a list.
            if (context.Request.Headers.TryGetValue("X-Forwarded-Proto", out var forwardedProto) &&
                forwardedProto.ToString().Split(',')[0].Trim().Equals("https", StringComparison.OrdinalIgnoreCase))
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
            
            // Prevent caching for API endpoints and HTML pages. The OPDS feeds are per user too; its pages and
            // covers set their own, private, cache policy.
            if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments(OpdsFeatureGateMiddleware.PathPrefix))
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
        // manifest.json too, because its URLs get the base path filled in there.
        app.UseWhen(
            ctx => !ctx.Request.Path.Equals("/sw.js", StringComparison.OrdinalIgnoreCase) &&
                   !ctx.Request.Path.Equals("/manifest.json", StringComparison.OrdinalIgnoreCase),
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
                    var isVersioned = !string.IsNullOrEmpty(requestedVersion) &&
                        string.Equals(requestedVersion, appVersion, StringComparison.Ordinal);
                    ctx.Context.Response.Headers["Cache-Control"] = isVersioned
                        ? "public, max-age=31536000, immutable"
                        : "no-cache, must-revalidate";
                }
                // Cache other static assets (images, icons, fonts) for 1 hour with cache busting via query string
                else
                {
                    ctx.Context.Response.Headers["Cache-Control"] = "public, max-age=3600";
                }
            }
        }));

        // Before routing and authentication: while the OPDS catalog is off its routes do not exist, even to clients
        // that send no credentials.
        app.UseMiddleware<OpdsFeatureGateMiddleware>();

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
        // Index is served via a custom delegate so that its placeholders are substituted
        // (see HtmlVersionInjectionMiddleware) on SPA routes that don't match a real file.
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

            // Only serve index.html for non-API, non-hub and non-OPDS routes
            if (context.Request.Path.StartsWithSegments("/api") ||
                context.Request.Path.StartsWithSegments("/hubs") ||
                context.Request.Path.StartsWithSegments(OpdsFeatureGateMiddleware.PathPrefix))
            {
                context.Response.StatusCode = 404;
                context.Response.ContentType = "application/json; charset=utf-8";
                await context.Response.WriteAsync(UnknownEndpointResponse.Body);
                return;
            }

            var env = context.RequestServices.GetRequiredService<IWebHostEnvironment>();
            var indexPath = Path.Join(env.WebRootPath ?? string.Empty, "index.html");
            if (!File.Exists(indexPath))
            {
                context.Response.StatusCode = 404;
                return;
            }

            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
            var html = HtmlVersionInjectionMiddleware.InjectPlaceholders(await File.ReadAllTextAsync(indexPath), version, basePath);

            context.Response.Headers["Cache-Control"] = "no-store, private";
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync(html);
        });

        return app;
    }
}
