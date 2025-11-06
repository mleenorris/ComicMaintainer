using System.Text;
using System.Text.Json;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models.Auth;
using ComicMaintainer.Core.Services;
using ComicMaintainer.WebApi.Hubs;
using ComicMaintainer.WebApi.Middleware;
using ComicMaintainer.WebApi.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
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

// Load user settings to get LogMaxBytes
var logMaxBytes = LoadLogMaxBytes(configDir);

var builder = WebApplication.CreateBuilder(args);

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

// Configure settings from environment variables and appsettings
builder.Services.Configure<AppSettings>(options =>
{
    builder.Configuration.GetSection("AppSettings").Bind(options);
    
    // Load user settings from file
    LoadUserSettings(configDir, options);
    
    // Override with environment variables if present (highest priority)
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
});

// Configure JWT settings
builder.Services.Configure<JwtSettings>(options =>
{
    builder.Configuration.GetSection("JwtSettings").Bind(options);
    
    // Override with environment variable if present
    var jwtSecret = Environment.GetEnvironmentVariable("JWT_SECRET");
    if (!string.IsNullOrEmpty(jwtSecret))
        options.Secret = jwtSecret;
});

// Configure database
var configDirectory = builder.Configuration["AppSettings:ConfigDirectory"] ?? "/Config";
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") 
    ?? $"Data Source={Path.Combine(configDirectory, "comicmaintainer.db")}";
builder.Services.AddDbContext<ComicMaintainerDbContext>(options =>
    options.UseSqlite(connectionString));

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

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
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
});

// Add services to the container
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Add SignalR
builder.Services.AddSignalR();

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
builder.Services.AddScoped<IAuthService, AuthService>();

// Add hosted service for file watcher
builder.Services.AddHostedService<FileWatcherHostedService>();

var app = builder.Build();

// Print startup banner
var logger = app.Services.GetRequiredService<ILogger<Program>>();
var version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "1.0.0";
logger.LogInformation("╔══════════════════════════════════════════════════╗");
logger.LogInformation("║         Comic Maintainer - .NET Edition          ║");
logger.LogInformation("║                  Version {Version}                  ║", version.PadRight(21));
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

// Add security headers middleware
app.Use(async (context, next) =>
{
    // Prevent MIME type sniffing
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    
    // Prevent clickjacking
    context.Response.Headers["X-Frame-Options"] = "DENY";
    
    // Enable XSS protection
    context.Response.Headers["X-XSS-Protection"] = "1; mode=block";
    
    // Control referrer information
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    
    // Restrict dangerous browser features
    context.Response.Headers["Permissions-Policy"] = "geolocation=(), microphone=(), camera=()";
    
    // Add HSTS and CSP headers when behind HTTPS proxy
    if (context.Request.Headers.ContainsKey("X-Forwarded-Proto") && 
        context.Request.Headers["X-Forwarded-Proto"] == "https")
    {
        // HSTS: Force HTTPS for 1 year
        context.Response.Headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
        
        // CSP: Upgrade insecure requests
        if (!context.Response.Headers.ContainsKey("Content-Security-Policy"))
        {
            context.Response.Headers["Content-Security-Policy"] = "upgrade-insecure-requests";
        }
    }
    
    // Prevent caching for API endpoints
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        context.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, private";
        context.Response.Headers["Pragma"] = "no-cache";
    }
    
    await next();
});

// Add path validation middleware for security
app.UseMiddleware<PathValidationMiddleware>();

// Serve static files from wwwroot (we'll copy the Python templates/static there)
app.UseStaticFiles();

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHub<ProgressHub>("/hubs/progress");

// Map default route to serve index.html
app.MapFallbackToFile("index.html");

// Log startup complete
var appSettingsValue = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AppSettings>>().Value;
logger.LogInformation("Server started successfully");
logger.LogInformation("Watched Directory: {WatchedDir}", appSettingsValue.WatchedDirectory);
var watcherEnabled = appSettingsValue.WatcherEnableRename || appSettingsValue.WatcherEnableNormalize;
logger.LogInformation("Watcher Status: {Status} (Rename: {Rename}, Normalize: {Normalize})", 
    watcherEnabled ? "Enabled" : "Disabled",
    appSettingsValue.WatcherEnableRename,
    appSettingsValue.WatcherEnableNormalize);

app.Run();

// Helper functions
static long LoadLogMaxBytes(string configDir)
{
    const long defaultLogMaxBytes = 10_485_760; // 10 MB
    
    try
    {
        var settingsFilePath = Path.Combine(configDir, "user-settings.json");
        if (!File.Exists(settingsFilePath))
        {
            return defaultLogMaxBytes;
        }

        var json = File.ReadAllText(settingsFilePath);
        var settings = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
        
        if (settings != null && settings.TryGetValue("LogMaxBytes", out var value))
        {
            if (value.ValueKind == JsonValueKind.Number)
            {
                return value.GetInt32();
            }
        }
    }
    catch
    {
        // If any error occurs, use default value
    }

    return defaultLogMaxBytes;
}

static void LoadUserSettings(string configDir, AppSettings options)
{
    try
    {
        var settingsFilePath = Path.Combine(configDir, "user-settings.json");
        if (!File.Exists(settingsFilePath))
        {
            return;
        }

        var json = File.ReadAllText(settingsFilePath);
        var settings = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
        
        if (settings == null)
        {
            return;
        }

        // Load each setting from the user settings file
        if (settings.TryGetValue("LogMaxBytes", out var logMaxBytes) && logMaxBytes.ValueKind == JsonValueKind.Number)
        {
            options.LogMaxBytes = logMaxBytes.GetInt32();
        }

        if (settings.TryGetValue("FilenameFormat", out var filenameFormat) && filenameFormat.ValueKind == JsonValueKind.String)
        {
            var format = filenameFormat.GetString();
            if (!string.IsNullOrWhiteSpace(format))
            {
                options.FilenameFormat = format;
            }
        }

        if (settings.TryGetValue("IssueNumberPadding", out var issueNumberPadding) && issueNumberPadding.ValueKind == JsonValueKind.Number)
        {
            options.IssueNumberPadding = issueNumberPadding.GetInt32();
        }

        if (settings.TryGetValue("WatcherEnableRename", out var watcherEnableRename) && 
            (watcherEnableRename.ValueKind == JsonValueKind.True || watcherEnableRename.ValueKind == JsonValueKind.False))
        {
            options.WatcherEnableRename = watcherEnableRename.GetBoolean();
        }

        if (settings.TryGetValue("WatcherEnableNormalize", out var watcherEnableNormalize) && 
            (watcherEnableNormalize.ValueKind == JsonValueKind.True || watcherEnableNormalize.ValueKind == JsonValueKind.False))
        {
            options.WatcherEnableNormalize = watcherEnableNormalize.GetBoolean();
        }
    }
    catch
    {
        // If any error occurs, just continue with default values
    }
}

// Make the implicit Program class public so test projects can access it
public partial class Program { }
