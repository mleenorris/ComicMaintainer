using System.Text;
using System.Text.Json;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models.Auth;
using ComicMaintainer.Core.Reader.Interfaces;
using ComicMaintainer.Core.Reader.Services;
using ComicMaintainer.Core.Services;
using ComicMaintainer.WebApi.Authentication;
using ComicMaintainer.WebApi.Hubs;
using ComicMaintainer.WebApi.Middleware;
using ComicMaintainer.WebApi.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using Serilog.Events;

// Configure Serilog for dual logging: console (clean) and file (debug)
var configDir = Environment.GetEnvironmentVariable("CONFIG_DIR") 
    ?? "/Config";

// Ensure log directory exists
try
{
    Directory.CreateDirectory(configDir);
}
catch (UnauthorizedAccessException)
{
    // Fallback to temp directory if we don't have permission
    configDir = Path.Combine(Path.GetTempPath(), "ComicMaintainer");
    Directory.CreateDirectory(configDir);
}

// Ensure user-settings.json exists and is in the new shape (object root under "AppSettings")
// so that the configuration provider can register it as a reload-on-change source from startup.
var userSettingsPath = Path.Combine(configDir, "user-settings.json");
EnsureUserSettingsFileMigrated(userSettingsPath);

// Load user settings to get LogMaxBytes
var logMaxBytes = LoadLogMaxBytes(userSettingsPath);

var builder = WebApplication.CreateBuilder(args);

// Register user-settings.json as a live (reload-on-change) configuration source so that
// SettingsService writes are picked up at runtime by IOptionsMonitor<AppSettings>.
// The file is intentionally optional (it may have been removed) and is reloaded on change.
builder.Configuration.AddJsonFile(userSettingsPath, optional: true, reloadOnChange: true);

// Use Serilog for logging - configure with the builder context to ensure proper integration
builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .MinimumLevel.Debug()
    // Console sink - only show Information and above, clean formatting
    .WriteTo.Console(
        restrictedToMinimumLevel: LogEventLevel.Information,
        outputTemplate: "[{Timestamp:HH:mm:ss}] [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    // Basic/Info log file - Information level and above, excluding watcher logs
    .WriteTo.Logger(lc => lc
        .Filter.ByExcluding(e => 
            e.Properties.ContainsKey("SourceContext") && 
            (e.Properties["SourceContext"].ToString().Contains("FileWatcherService") ||
             e.Properties["SourceContext"].ToString().Contains("FileWatcherHostedService")))
        .WriteTo.File(
            Path.Combine(configDir, "app.log"),
            restrictedToMinimumLevel: LogEventLevel.Information,
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 7,
            fileSizeLimitBytes: logMaxBytes,
            outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{Level:u3}] {Message:lj}{NewLine}{Exception}"))
    // Debug log file - capture everything at Debug level and above (including watcher)
    .WriteTo.File(
        Path.Combine(configDir, "debug.log"),
        restrictedToMinimumLevel: LogEventLevel.Debug,
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 3,
        fileSizeLimitBytes: logMaxBytes,
        outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}")
    // Watcher-specific log file - capture all watcher-related logs
    .WriteTo.Logger(lc => lc
        .Filter.ByIncludingOnly(e => 
            e.Properties.ContainsKey("SourceContext") && 
            (e.Properties["SourceContext"].ToString().Contains("FileWatcherService") ||
             e.Properties["SourceContext"].ToString().Contains("FileWatcherHostedService")))
        .WriteTo.File(
            Path.Combine(configDir, "watcher.log"),
            restrictedToMinimumLevel: LogEventLevel.Debug,
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 7,
            fileSizeLimitBytes: logMaxBytes,
            outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{Level:u3}] {Message:lj}{NewLine}{Exception}"))
    // Override specific namespaces to reduce console noise
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Migrations", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Model.Validation", LogEventLevel.Error));

// Configure settings from appsettings.json + user-settings.json (live-reload) under "AppSettings"
// section. Environment variable overrides are applied via IPostConfigureOptions so they always
// win, mirroring the previous precedence: defaults → appsettings.json → user-settings.json → env.
builder.Services.Configure<AppSettings>(builder.Configuration.GetSection("AppSettings"));
builder.Services.AddSingleton<Microsoft.Extensions.Options.IPostConfigureOptions<AppSettings>, AppSettingsEnvironmentPostConfigure>();

// Configure JWT settings
builder.Services.Configure<JwtSettings>(options =>
{
    builder.Configuration.GetSection("JwtSettings").Bind(options);
    
    // Override with environment variable if present
    var jwtSecret = Environment.GetEnvironmentVariable("JWT_SECRET");
    if (!string.IsNullOrEmpty(jwtSecret))
        options.Secret = jwtSecret;
});

// Configure Authelia settings
builder.Services.Configure<AutheliaSettings>(options =>
{
    builder.Configuration.GetSection("AutheliaSettings").Bind(options);
    
    // Override with environment variables if present
    var autheliaEnabled = Environment.GetEnvironmentVariable("AUTHELIA_ENABLED");
    if (!string.IsNullOrEmpty(autheliaEnabled))
        options.Enabled = autheliaEnabled.Equals("true", StringComparison.OrdinalIgnoreCase);
    
    var autheliaUserHeader = Environment.GetEnvironmentVariable("AUTHELIA_USER_HEADER");
    if (!string.IsNullOrEmpty(autheliaUserHeader))
        options.UserHeader = autheliaUserHeader;
    
    var autheliaDefaultRole = Environment.GetEnvironmentVariable("AUTHELIA_DEFAULT_ROLE");
    if (!string.IsNullOrEmpty(autheliaDefaultRole))
        options.DefaultRole = autheliaDefaultRole;
    
    var autheliaAdminGroups = Environment.GetEnvironmentVariable("AUTHELIA_ADMIN_GROUPS");
    if (!string.IsNullOrEmpty(autheliaAdminGroups))
        options.AdminGroups = autheliaAdminGroups;
});

// Configure database
var configDirectory = builder.Configuration["AppSettings:ConfigDirectory"] ?? "/Config";
var rawConnectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? $"Data Source={Path.Combine(configDirectory, "comicmaintainer.db")}";
// Normalize the connection string (pooling, shared cache, default timeout) once so
// every DbContext instance is built from the same hardened settings.
var connectionString = SqliteDbContextOptionsExtensions.NormalizeSqliteConnectionString(rawConnectionString);

// Register DbContext for Identity and scoped usage
builder.Services.AddDbContext<ComicMaintainerDbContext>((sp, options) =>
    options.UseComicMaintainerSqlite(
        connectionString,
        sp.GetService<ILoggerFactory>()));

// Register DbContextFactory for singleton services that need DbContext access
// Create a custom factory that creates independent DbContext instances
builder.Services.AddSingleton<IDbContextFactory<ComicMaintainerDbContext>>(sp =>
{
    return new ComicMaintainerDbContextFactory(connectionString, sp.GetService<ILoggerFactory>());
});

// Configure Data Protection to persist keys in Config directory
try
{
    var dataProtectionPath = Path.Combine(configDirectory, "DataProtection-Keys");
    Directory.CreateDirectory(dataProtectionPath);
    
    var dpBuilder = builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionPath))
        .SetApplicationName("ComicMaintainer");
    
    // On Linux/Docker, use unprotected keys as DPAPI is not available
    // Security Note: Keys are protected by:
    // 1. Container isolation (keys not accessible outside container)
    // 2. File system permissions (controlled via PUID/PGID)
    // 3. Volume mount security (host controls access to /Config)
    // This is acceptable for containerized deployments where the container itself
    // provides the security boundary. For additional security, mount /Config to
    // an encrypted volume on the host system.
    if (!OperatingSystem.IsWindows())
    {
        dpBuilder.UnprotectKeysWithAnyCertificate();
    }
}
catch (Exception ex)
{
    Log.Warning(ex, "Failed to configure DataProtection with persistent storage. Using temporary storage.");
}

// Configure Identity
builder.Services.AddIdentity<ApplicationUser, ApplicationRole>(options =>
{
    // Password settings
    options.Password.RequireDigit = true;
    options.Password.RequireLowercase = true;
    options.Password.RequireUppercase = true;
    options.Password.RequireNonAlphanumeric = false;
    options.Password.RequiredLength = 8;
    
    // User settings
    options.User.RequireUniqueEmail = true;
})
.AddEntityFrameworkStores<ComicMaintainerDbContext>()
.AddDefaultTokenProviders();

// Configure JWT Authentication
var jwtSettings = builder.Configuration.GetSection("JwtSettings").Get<JwtSettings>() ?? new JwtSettings();
var jwtSecret = Environment.GetEnvironmentVariable("JWT_SECRET") ?? jwtSettings.Secret;

if (string.IsNullOrEmpty(jwtSecret))
{
    throw new InvalidOperationException("JWT_SECRET must be configured in appsettings.json or environment variable");
}

// Warn if using default JWT secret
var defaultSecret = "YourSecretKeyHere-ChangeInProduction-MustBeAtLeast32CharactersLong!";
if (jwtSecret == defaultSecret)
{
    Log.Warning("⚠️ WARNING: Using default JWT secret! Please set JWT_SECRET environment variable for production.");
    Log.Warning("⚠️ Current secret is stored in appsettings.json which is not persisted in /Config directory.");
    Log.Warning("⚠️ Set JWT_SECRET environment variable to use a secure, persistent secret.");
}

// Check if Authelia is enabled
var autheliaSettings = builder.Configuration.GetSection("AutheliaSettings").Get<AutheliaSettings>() ?? new AutheliaSettings();
var autheliaEnabled = Environment.GetEnvironmentVariable("AUTHELIA_ENABLED");
if (!string.IsNullOrEmpty(autheliaEnabled))
{
    autheliaSettings.Enabled = autheliaEnabled.Equals("true", StringComparison.OrdinalIgnoreCase);
}

// Configure authentication schemes
var authBuilder = builder.Services.AddAuthentication(options =>
{
    // Set Authelia as default if enabled, otherwise use JWT
    if (autheliaSettings.Enabled)
    {
        options.DefaultAuthenticateScheme = "Authelia";
        options.DefaultChallengeScheme = "Authelia";
        Log.Information("✅ Authelia authentication enabled - forward auth headers will be trusted");
    }
    else
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    }
});

// Add Authelia authentication handler if enabled
if (autheliaSettings.Enabled)
{
    authBuilder.AddScheme<AutheliaAuthenticationOptions, AutheliaAuthenticationHandler>(
        "Authelia",
        options => { });
    
    Log.Information("Authelia authentication configured with fallback to JWT for SSE connections");
}

// Always add JWT Bearer for backward compatibility and API access
authBuilder.AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = jwtSettings.Issuer,
        ValidAudience = jwtSettings.Audience,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret))
    };
    
    // Configure events to return JSON for authentication failures and support token in query string
    options.Events = new JwtBearerEvents
    {
        OnMessageReceived = context =>
        {
            // Allow token to be passed via query string for EventSource/SSE connections
            // EventSource API doesn't support custom headers, so we need this for SSE endpoints
            if (string.IsNullOrEmpty(context.Token) && context.Request.Query.ContainsKey("access_token"))
            {
                context.Token = context.Request.Query["access_token"];
            }
            return Task.CompletedTask;
        },
        OnChallenge = context =>
        {
            // Override the default behavior to return JSON instead of redirecting
            context.HandleResponse();
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json; charset=utf-8";
            
            var result = JsonSerializer.Serialize(new { error = "Unauthorized", message = "Authentication required" });
            return context.Response.WriteAsync(result);
        },
        OnForbidden = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/json; charset=utf-8";
            
            var result = JsonSerializer.Serialize(new { error = "Forbidden", message = "Insufficient permissions" });
            return context.Response.WriteAsync(result);
        }
    };
});

// Configure authorization policies to support multiple authentication schemes
builder.Services.AddAuthorization(options =>
{
    // Default policy that accepts both Authelia and JWT authentication
    // This allows SSE connections to work with JWT tokens even when Authelia is the default
    if (autheliaSettings.Enabled)
    {
        options.DefaultPolicy = new AuthorizationPolicyBuilder()
            .AddAuthenticationSchemes("Authelia", JwtBearerDefaults.AuthenticationScheme)
            .RequireAuthenticatedUser()
            .Build();
        
        Log.Information("Authorization policy configured to accept both Authelia and JWT Bearer tokens");
    }
    else
    {
        options.DefaultPolicy = new AuthorizationPolicyBuilder()
            .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
            .RequireAuthenticatedUser()
            .Build();
    }
});

// Add services to the container
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Add response compression for better performance
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
});

// Add request timeout (ASP.NET Core 9 best practice)
builder.Services.AddRequestTimeouts(options =>
{
    options.DefaultPolicy = new Microsoft.AspNetCore.Http.Timeouts.RequestTimeoutPolicy
    {
        Timeout = TimeSpan.FromSeconds(30)
    };
    // Longer timeout for comic page operations
    options.AddPolicy("ComicOperations", TimeSpan.FromMinutes(2));
});

// Add rate limiting (ASP.NET Core 9 best practice)
builder.Services.AddRateLimiter(options =>
{
    options.GlobalLimiter = System.Threading.RateLimiting.PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        // Allow 100 requests per minute per IP
        return System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                PermitLimit = 100,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst,
                QueueLimit = 10
            });
    });
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

// Add output caching for better performance  
builder.Services.AddOutputCache(options =>
{
    // Don't cache by default - caching should be explicit
    options.AddBasePolicy(builder => builder.NoCache());
    // Cache comic pages for 1 hour since they don't change frequently
    options.AddPolicy("ComicPages", builder => builder
        .Expire(TimeSpan.FromHours(1))
        .SetVaryByQuery("filePath", "page"));
});

// Add SignalR
builder.Services.AddSignalR();
builder.Services.AddMemoryCache();

// Add health checks
builder.Services.AddHealthChecks();

// Add CORS with security-conscious configuration
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        // Get allowed origins from configuration or environment variable
        var allowedOriginsConfig = builder.Configuration.GetSection("CorsSettings:AllowedOrigins").Get<string[]>();
        var allowedOriginsEnv = Environment.GetEnvironmentVariable("CORS_ALLOWED_ORIGINS");
        
        string[] allowedOrigins;
        if (!string.IsNullOrEmpty(allowedOriginsEnv))
        {
            // Environment variable takes precedence (comma-separated)
            allowedOrigins = allowedOriginsEnv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        else if (allowedOriginsConfig != null && allowedOriginsConfig.Length > 0)
        {
            // Use configuration from appsettings.json
            allowedOrigins = allowedOriginsConfig;
        }
        else
        {
            // Default to localhost only (safe default)
            allowedOrigins = new[] { "http://localhost:5000", "https://localhost:5000" };
            Log.Warning("⚠️ No CORS origins configured. Using default localhost-only policy.");
            Log.Warning("⚠️ Set CORS_ALLOWED_ORIGINS environment variable for production.");
        }
        
        policy.WithOrigins(allowedOrigins)
              .AllowAnyMethod()
              .AllowAnyHeader()
              .AllowCredentials();
    });
});

// Register application services
builder.Services.AddSingleton<EventBroadcasterService>();
builder.Services.AddSingleton<IEventBroadcaster>(sp => sp.GetRequiredService<EventBroadcasterService>());
builder.Services.AddSingleton<IFileStoreService, FileStoreService>();
builder.Services.AddSingleton<IComicProcessorService, ComicProcessorService>();
builder.Services.AddSingleton<IFileWatcherService, FileWatcherService>();
builder.Services.AddSingleton<IProcessingHistoryService, ProcessingHistoryService>();
builder.Services.AddSingleton<ISettingsService, SettingsService>();
builder.Services.AddSingleton<IComicReaderService, ComicReaderService>();
builder.Services.AddSingleton<ISeriesLibraryService, SeriesLibraryService>();
builder.Services.AddSingleton<ISeriesMetadataCacheService, SeriesMetadataCacheService>();
builder.Services.AddSingleton<ISeriesMetadataRefreshJobService, SeriesMetadataRefreshJobService>();
builder.Services.AddHttpClient(nameof(ComicVineSeriesMetadataService));
builder.Services.AddHttpClient(nameof(MangaDexSeriesMetadataService));
builder.Services.AddHttpClient(nameof(AniListManhwaSeriesMetadataService));
builder.Services.AddSingleton<ComicVineSeriesMetadataService>();
builder.Services.AddSingleton<MangaDexSeriesMetadataService>();
builder.Services.AddSingleton<AniListManhwaSeriesMetadataService>();
builder.Services.AddSingleton<IExternalSeriesMetadataService>(sp =>
    new CompositeExternalSeriesMetadataService(
        [
            sp.GetRequiredService<ComicVineSeriesMetadataService>(),
            sp.GetRequiredService<MangaDexSeriesMetadataService>(),
            sp.GetRequiredService<AniListManhwaSeriesMetadataService>()
        ],
        sp.GetRequiredService<ILogger<CompositeExternalSeriesMetadataService>>()));

// Reader foundation services
builder.Services.AddSingleton<IReadingProgressService, ReadingProgressService>();
builder.Services.AddSingleton<IReaderPreferenceService, ReaderPreferenceService>();
builder.Services.AddSingleton<IReadingSessionService, ReadingSessionService>();

builder.Services.AddScoped<IAuthService, AuthService>();

// Add hosted service for file watcher
builder.Services.AddHostedService<FileWatcherHostedService>();

// Add hosted service for database cleanup
builder.Services.AddHostedService<DatabaseCleanupHostedService>();

var app = builder.Build();

// Print startup banner
var logger = app.Services.GetRequiredService<ILogger<Program>>();
var version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "1.0.0";
logger.LogInformation("╔══════════════════════════════════════════════════╗");
logger.LogInformation("║         Comic Maintainer - .NET Edition          ║");
logger.LogInformation("║                  Version {Version}║", version.PadRight(24));
logger.LogInformation("╚══════════════════════════════════════════════════╝");

// Initialize database and seed roles
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ComicMaintainerDbContext>();
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

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();

// Use rate limiter
app.UseRateLimiter();

// Use request timeouts
app.UseRequestTimeouts();

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
            context.Response.Headers["Content-Security-Policy"] = "upgrade-insecure-requests; frame-ancestors 'none'";
        }
    }
    else
    {
        // CSP: Prevent framing even without HTTPS (replaces X-Frame-Options)
        if (!context.Response.Headers.ContainsKey("Content-Security-Policy"))
        {
            context.Response.Headers["Content-Security-Policy"] = "frame-ancestors 'none'";
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

// Inject the current assembly version into HTML pages (replaces __APP_VERSION__).
// This MUST run before UseStaticFiles so that direct requests for *.html
// (and the root "/") never get served as raw static files containing the
// unsubstituted placeholder.
app.UseMiddleware<HtmlVersionInjectionMiddleware>();

// Serve static files from wwwroot with cache control
// Note: sw.js is served via ServiceWorkerController to prevent redirect issues
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        // Service worker files should not be served as static files
        // They are served via the ServiceWorkerController to avoid redirect issues
        if (ctx.File.Name.Equals("sw.js", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Context.Response.StatusCode = 404;
            return;
        }
        
        // Don't cache HTML files to ensure users always get the latest version.
        // (Normally these are served by HtmlVersionInjectionMiddleware above and
        // never reach the static file handler, but keep this as defense in depth.)
        if (ctx.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Context.Response.Headers["Cache-Control"] = "no-store, private";
        }
        // CSS and JS are versioned via ?v=<app-version> query strings emitted in
        // the HTML. We must not allow them to be served stale from the browser
        // cache for an hour, otherwise a new HTML referencing a new version may
        // still pull the old asset body from disk cache (without a query string).
        // "no-cache" still allows the browser to revalidate (304s), it just
        // refuses to serve the body without checking with the origin first.
        else if (ctx.Context.Request.Path.StartsWithSegments("/css") ||
                 ctx.Context.Request.Path.StartsWithSegments("/js"))
        {
            ctx.Context.Response.Headers["Cache-Control"] = "no-cache, must-revalidate";
        }
        // Cache other static assets (images, icons, fonts) for 1 hour with cache busting via query string
        else
        {
            ctx.Context.Response.Headers["Cache-Control"] = "public, max-age=3600";
        }
    }
});

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHub<ProgressHub>("/hubs/progress");

// Map health check endpoints
app.MapHealthChecks("/health");

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

// Log startup complete
var appSettingsValue = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<AppSettings>>().CurrentValue;
logger.LogInformation("Server started successfully");
logger.LogInformation("Watched Directory: {WatchedDir}", appSettingsValue.WatchedDirectory);
var watcherEnabled = appSettingsValue.WatcherEnableRename || appSettingsValue.WatcherEnableNormalize;
logger.LogInformation("Watcher Status: {Status} (Rename: {Rename}, Normalize: {Normalize})", 
    watcherEnabled ? "Enabled" : "Disabled",
    appSettingsValue.WatcherEnableRename,
    appSettingsValue.WatcherEnableNormalize);

app.Run();

// Helper functions
static long LoadLogMaxBytes(string userSettingsPath)
{
    const long defaultLogMaxBytes = 10_485_760; // 10 MB
    
    try
    {
        if (!File.Exists(userSettingsPath))
        {
            return defaultLogMaxBytes;
        }

        var json = File.ReadAllText(userSettingsPath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // New shape: { "AppSettings": { "LogMaxBytes": ... } }
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("AppSettings", out var appSettingsElement) &&
            appSettingsElement.ValueKind == JsonValueKind.Object &&
            appSettingsElement.TryGetProperty("LogMaxBytes", out var newShapeValue) &&
            newShapeValue.ValueKind == JsonValueKind.Number &&
            newShapeValue.TryGetInt32(out var newShapeBytes))
        {
            return newShapeBytes;
        }

        // Legacy flat shape: { "LogMaxBytes": ... }
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("LogMaxBytes", out var legacyValue) &&
            legacyValue.ValueKind == JsonValueKind.Number &&
            legacyValue.TryGetInt32(out var legacyBytes))
        {
            return legacyBytes;
        }
    }
    catch
    {
        // If any error occurs, use default value
    }

    return defaultLogMaxBytes;
}

/// <summary>
/// Ensures the user-settings.json file exists and is in the new "AppSettings" object-rooted shape.
/// Performs a one-time migration from the legacy flat shape (top-level keys like "FilenameFormat")
/// to the new shape ({ "AppSettings": { ... } }) so the file can be registered as a configuration
/// source under the AppSettings section with reload-on-change support.
/// </summary>
static void EnsureUserSettingsFileMigrated(string userSettingsPath)
{
    try
    {
        if (!File.Exists(userSettingsPath))
        {
            // Create an empty wrapper so the file watcher in the configuration provider
            // has a stable file to observe from process start.
            File.WriteAllText(userSettingsPath, "{\n  \"AppSettings\": {}\n}");
            return;
        }

        var json = File.ReadAllText(userSettingsPath);
        if (string.IsNullOrWhiteSpace(json))
        {
            File.WriteAllText(userSettingsPath, "{\n  \"AppSettings\": {}\n}");
            return;
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        // Already in the new shape
        if (root.TryGetProperty("AppSettings", out _))
        {
            return;
        }

        // Migrate flat shape -> object-rooted shape
        var migrated = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var prop in root.EnumerateObject())
        {
            migrated[prop.Name] = prop.Value.Clone();
        }

        var newRoot = new Dictionary<string, object?>
        {
            ["AppSettings"] = migrated
        };
        var serialized = JsonSerializer.Serialize(newRoot, new JsonSerializerOptions { WriteIndented = true });

        // Write atomically (temp file + move) so the configuration file watcher does not
        // observe a half-written file.
        var tempPath = userSettingsPath + ".migrating";
        File.WriteAllText(tempPath, serialized);
        File.Move(tempPath, userSettingsPath, overwrite: true);
    }
    catch
    {
        // If migration fails, leave the file untouched. The configuration provider will
        // simply not pick up legacy values until the user re-saves a setting.
    }
}

/// <summary>
/// Applies environment-variable overrides to <see cref="AppSettings"/> so they always win
/// over values from appsettings.json and user-settings.json. Implemented as
/// <see cref="Microsoft.Extensions.Options.IPostConfigureOptions{TOptions}"/> so it runs
/// every time the options instance is (re)built, including after a user-settings.json reload.
/// </summary>
internal sealed class AppSettingsEnvironmentPostConfigure : Microsoft.Extensions.Options.IPostConfigureOptions<AppSettings>
{
    public void PostConfigure(string? name, AppSettings options)
    {
        var watchedDir = Environment.GetEnvironmentVariable("WATCHED_DIR");
        if (!string.IsNullOrEmpty(watchedDir))
            options.WatchedDirectory = watchedDir;

        var duplicateDir = Environment.GetEnvironmentVariable("DUPLICATE_DIR");
        if (!string.IsNullOrEmpty(duplicateDir))
            options.DuplicateDirectory = duplicateDir;

        var configDirEnv = Environment.GetEnvironmentVariable("CONFIG_DIR");
        if (!string.IsNullOrEmpty(configDirEnv))
            options.ConfigDirectory = configDirEnv;

        var basePath = Environment.GetEnvironmentVariable("BASE_PATH");
        if (!string.IsNullOrEmpty(basePath))
            options.BasePath = basePath;

        var enableExternalSeriesMetadata = Environment.GetEnvironmentVariable("ENABLE_EXTERNAL_SERIES_METADATA");
        if (!string.IsNullOrEmpty(enableExternalSeriesMetadata))
            options.EnableExternalSeriesMetadata = enableExternalSeriesMetadata.Equals("true", StringComparison.OrdinalIgnoreCase);

        var comicVineApiKey = Environment.GetEnvironmentVariable("COMICVINE_API_KEY");
        if (!string.IsNullOrEmpty(comicVineApiKey))
            options.ComicVineApiKey = comicVineApiKey;

        var comicVineBaseUrl = Environment.GetEnvironmentVariable("COMICVINE_BASE_URL");
        if (!string.IsNullOrEmpty(comicVineBaseUrl))
            options.ComicVineBaseUrl = comicVineBaseUrl;

        var enableMangaDexMetadata = Environment.GetEnvironmentVariable("ENABLE_MANGADEX_METADATA");
        if (!string.IsNullOrEmpty(enableMangaDexMetadata))
            options.EnableMangaDexMetadata = enableMangaDexMetadata.Equals("true", StringComparison.OrdinalIgnoreCase);

        var mangaDexBaseUrl = Environment.GetEnvironmentVariable("MANGADEX_BASE_URL");
        if (!string.IsNullOrEmpty(mangaDexBaseUrl))
            options.MangaDexBaseUrl = mangaDexBaseUrl;

        var enableAniListManhwaMetadata = Environment.GetEnvironmentVariable("ENABLE_ANILIST_MANHWA_METADATA");
        if (!string.IsNullOrEmpty(enableAniListManhwaMetadata))
            options.EnableAniListManhwaMetadata = enableAniListManhwaMetadata.Equals("true", StringComparison.OrdinalIgnoreCase);

        var aniListBaseUrl = Environment.GetEnvironmentVariable("ANILIST_BASE_URL");
        if (!string.IsNullOrEmpty(aniListBaseUrl))
            options.AniListBaseUrl = aniListBaseUrl;
    }
}

// Make the implicit Program class public so test projects can access it
public partial class Program { }

// Helper class to create DbContext instances independently without lifetime conflicts
internal class ComicMaintainerDbContextFactory : IDbContextFactory<ComicMaintainerDbContext>
{
    private readonly string _connectionString;
    private readonly ILoggerFactory? _loggerFactory;

    public ComicMaintainerDbContextFactory(string connectionString, ILoggerFactory? loggerFactory = null)
    {
        _connectionString = connectionString;
        _loggerFactory = loggerFactory;
    }

    public ComicMaintainerDbContext CreateDbContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<ComicMaintainerDbContext>();
        optionsBuilder.UseComicMaintainerSqlite(_connectionString, _loggerFactory);
        return new ComicMaintainerDbContext(optionsBuilder.Options);
    }
}
