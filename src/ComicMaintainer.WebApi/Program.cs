using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Utilities;
using ComicMaintainer.Core.Services;
using ComicMaintainer.WebApi.Startup;

var builder = WebApplication.CreateBuilder(args);

// One directory holds the database, DataProtection keys, logs, user-settings.json, backups and
// caches: CONFIG_DIR, then AppSettings:ConfigDirectory, then /Config (see ConfigDirectoryResolver).
var configDir = ConfigDirectoryResolver.Resolve(builder.Configuration);
string? unwritableConfigDir = null;

// Ensure the config directory exists
try
{
    Directory.CreateDirectory(configDir);
}
catch (UnauthorizedAccessException)
{
    // Fallback to temp directory if we don't have permission
    unwritableConfigDir = configDir;
    configDir = Path.Join(Path.GetTempPath(), "ComicMaintainer");
    Directory.CreateDirectory(configDir);
}

// Ensure user-settings.json exists and is in the new shape (object root under "AppSettings")
// so that the configuration provider can register it as a reload-on-change source from startup.
var userSettingsPath = Path.Join(configDir, "user-settings.json");
UserSettingsBootstrap.EnsureUserSettingsFileMigrated(userSettingsPath);

// Load user settings to get LogMaxBytes
var logMaxBytes = UserSettingsBootstrap.LoadLogMaxBytes(userSettingsPath);

// Register user-settings.json as a live (reload-on-change) configuration source so that
// SettingsService writes are picked up at runtime by IOptionsMonitor<AppSettings>.
// The file is intentionally optional (it may have been removed) and is reloaded on change.
builder.Configuration.AddJsonFile(userSettingsPath, optional: true, reloadOnChange: true);

// Publish the directory chosen above as AppSettings:ConfigDirectory, after every other source, so the
// database and DataProtection keys (AddComicMaintainerData) and AppSettings.ConfigDirectory (settings,
// backups, caches, the logs page) all use it.
builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
{
    [ConfigDirectoryResolver.ConfigurationKey] = configDir,
    [ConfigDirectoryResolver.ResolvedDirectoryConfigurationKey] = configDir,
});

// Use Serilog for logging (see Startup/LoggingExtensions.cs)
builder.AddComicMaintainerLogging(configDir, logMaxBytes);

// Service registration, in the original order (see Startup/*Extensions.cs):
// settings → database/data protection → identity/authentication/authorization →
// web stack (MVC, compression, timeouts, rate limiting, caching, health, CORS) →
// telemetry (OpenTelemetry metrics/tracing, see Startup/TelemetryExtensions.cs) →
// application services → error reporting → hosted services.
builder.Services
    .AddComicMaintainerSettings(builder.Configuration)
    .AddComicMaintainerData(builder.Configuration)
    .AddComicMaintainerAuth(builder.Configuration)
    .AddComicMaintainerWeb(builder.Configuration)
    .AddComicMaintainerTelemetry(builder.Configuration)
    .AddComicMaintainerCore()
    .AddComicMaintainerErrorReporting()
    .AddComicMaintainerHostedServices();

var app = builder.Build();

// Print startup banner
var logger = app.Services.GetRequiredService<ILogger<Program>>();
var version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "1.0.0";
logger.LogInformation("╔══════════════════════════════════════════════════╗");
logger.LogInformation("║         Comic Maintainer - .NET Edition          ║");
logger.LogInformation("║                  Version {Version}║", version.PadRight(24));
logger.LogInformation("╚══════════════════════════════════════════════════╝");

if (unwritableConfigDir is not null)
{
    logger.LogWarning(
        "Cannot create the config directory {RequestedConfigDirectory}; using {ConfigDirectory} instead, which may not survive a restart. Set CONFIG_DIR to a writable directory.",
        LoggingHelper.SanitizeForLog(unwritableConfigDir),
        LoggingHelper.SanitizeForLog(configDir));
}

// Report where the JWT signing secret came from (see Startup/JwtSecretResolver.cs). It was resolved while the
// services were registered, before this logger existed.
app.Services.GetRequiredService<JwtSigningSecret>().LogSource(logger);

// Initialize database and seed roles
await app.InitializeComicMaintainerDatabaseAsync(logger);

// Configure the HTTP request pipeline (see Startup/WebApplicationExtensions.cs)
app.UseComicMaintainerPipeline();

// Log startup complete
var appSettingsValue = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<AppSettings>>().CurrentValue;
logger.LogInformation("Server started successfully");
logger.LogInformation("Watched Directory: {WatchedDir}", appSettingsValue.WatchedDirectory);
var watcherEnabled = appSettingsValue.WatcherEnableRename || appSettingsValue.WatcherEnableNormalize;
logger.LogInformation("Watcher Status: {Status} (Rename: {Rename}, Normalize: {Normalize})", 
    watcherEnabled ? "Enabled" : "Disabled",
    appSettingsValue.WatcherEnableRename,
    appSettingsValue.WatcherEnableNormalize);

// The settings API range-checks the padding, but user-settings.json and the environment don't.
// Filenames clamp it, so name the value that is actually used.
var effectiveIssueNumberPadding = ComicFileProcessor.ClampIssueNumberPadding(appSettingsValue.IssueNumberPadding);
if (effectiveIssueNumberPadding != appSettingsValue.IssueNumberPadding)
{
    logger.LogWarning(
        "IssueNumberPadding {Padding} is outside the supported range 0-{MaxPadding}; using {EffectivePadding} instead",
        appSettingsValue.IssueNumberPadding,
        AppSettings.MaxIssueNumberPadding,
        effectiveIssueNumberPadding);
    appSettingsValue.IssueNumberPadding = effectiveIssueNumberPadding;
}

app.Run();

// Make the implicit Program class public so test projects can access it
public partial class Program { }
