using System.Text;
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

// Configure Serilog with two sinks: console and file
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    // Console sink - only show Information and above, clean formatting
    .WriteTo.Console(
        restrictedToMinimumLevel: LogEventLevel.Information,
        outputTemplate: "[{Timestamp:HH:mm:ss}] [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    // File sink - capture everything at Debug level and above
    .WriteTo.File(
        Path.Combine(configDir, "debug.log"),
        restrictedToMinimumLevel: LogEventLevel.Debug,
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 3,
        fileSizeLimitBytes: 10_485_760, // 10 MB
        outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}")
    // Override specific namespaces to reduce console noise
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Migrations", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Model.Validation", LogEventLevel.Error)
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);

// Use Serilog for logging
builder.Host.UseSerilog();

// Configure settings from environment variables and appsettings
builder.Services.Configure<AppSettings>(options =>
{
    builder.Configuration.GetSection("AppSettings").Bind(options);
    
    // Override with environment variables if present
    var watchedDir = Environment.GetEnvironmentVariable("WATCHED_DIR");
    if (!string.IsNullOrEmpty(watchedDir))
        options.WatchedDirectory = watchedDir;
    
    var duplicateDir = Environment.GetEnvironmentVariable("DUPLICATE_DIR");
    if (!string.IsNullOrEmpty(duplicateDir))
        options.DuplicateDirectory = duplicateDir;
    
    var configDir = Environment.GetEnvironmentVariable("CONFIG_DIR");
    if (!string.IsNullOrEmpty(configDir))
        options.ConfigDirectory = configDir;
    
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
    // Keys are protected by file system permissions (container isolation + PUID/PGID)
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

// Add CORS
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

// Register application services
builder.Services.AddSingleton<IFileStoreService, FileStoreService>();
builder.Services.AddSingleton<IComicProcessorService, ComicProcessorService>();
builder.Services.AddSingleton<IFileWatcherService, FileWatcherService>();
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
    
    // Seed default admin user if it doesn't exist
    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
    var adminEmail = Environment.GetEnvironmentVariable("ADMIN_EMAIL") ?? "admin@comicmaintainer.local";
    var adminUsername = Environment.GetEnvironmentVariable("ADMIN_USERNAME") ?? "admin";
    var adminPassword = Environment.GetEnvironmentVariable("ADMIN_PASSWORD") ?? "Admin123!";
    
    if (await userManager.FindByNameAsync(adminUsername) == null)
    {
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
logger.LogInformation("Watcher Status: {Status}", appSettingsValue.WatcherEnabled ? "Enabled" : "Disabled");

app.Run();
