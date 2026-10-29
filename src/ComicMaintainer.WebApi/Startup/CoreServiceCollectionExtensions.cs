using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.ErrorReporting;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Reader.Interfaces;
using ComicMaintainer.Core.Reader.Services;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Core.Telemetry;
using ComicMaintainer.WebApi.Logging;
using ComicMaintainer.WebApi.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace ComicMaintainer.WebApi.Startup;

/// <summary>
/// Registers settings, persistence, application services, error reporting and hosted services.
/// </summary>
internal static class CoreServiceCollectionExtensions
{
    public static IServiceCollection AddComicMaintainerSettings(this IServiceCollection services, IConfiguration configuration)
    {
        // Configure settings from appsettings.json + user-settings.json (live-reload) under "AppSettings"
        // section. Environment variable overrides are applied via IPostConfigureOptions so they always
        // win, mirroring the previous precedence: defaults → appsettings.json → user-settings.json → env.
        services.Configure<AppSettings>(configuration.GetSection("AppSettings"));
        services.AddSingleton<Microsoft.Extensions.Options.IPostConfigureOptions<AppSettings>, AppSettingsEnvironmentPostConfigure>();

        // Configure JWT settings from the section and JWT_SECRET. AddComicMaintainerAuth also resolves JWT_SECRET_FILE
        // and generated keys, then applies the resolved signing secret to these options.
        services.Configure<JwtSettings>(options => JwtSettingsBinder.Bind(configuration, options));

        // Configure Authelia settings (configuration section + AUTHELIA_* environment variables)
        services.Configure<AutheliaSettings>(options => AutheliaSettingsBinder.Bind(configuration, options));

        return services;
    }

    public static IServiceCollection AddComicMaintainerData(this IServiceCollection services, IConfiguration configuration)
    {
        // Configure database. Program publishes the config directory it resolved (CONFIG_DIR, then
        // AppSettings:ConfigDirectory, then /Config) as AppSettings:ConfigDirectory.
        var configDirectory = configuration[ConfigDirectoryResolver.ConfigurationKey] ?? ConfigDirectoryResolver.DefaultDirectory;
        var rawConnectionString = configuration.GetConnectionString("DefaultConnection")
            ?? $"Data Source={Path.Combine(configDirectory, "comicmaintainer.db")}";
        // Normalize the connection string (pooling, shared cache, default timeout) once so
        // every DbContext instance is built from the same hardened settings.
        var connectionString = SqliteDbContextOptionsExtensions.NormalizeSqliteConnectionString(rawConnectionString);

        // Register DbContext for Identity and scoped usage
        services.AddDbContext<ComicMaintainerDbContext>((sp, options) =>
            options.UseComicMaintainerSqlite(
                connectionString,
                sp.GetService<ILoggerFactory>()));

        // Register DbContextFactory for singleton services that need DbContext access
        // Create a custom factory that creates independent DbContext instances
        services.AddSingleton<IDbContextFactory<ComicMaintainerDbContext>>(sp =>
        {
            return new ComicMaintainerDbContextFactory(connectionString, sp.GetService<ILoggerFactory>());
        });

        // Online backups / staged restores of the SQLite database file.
        var databasePath = ResolveSqliteDatabasePath(connectionString);
        if (databasePath is not null)
        {
            services.AddSingleton<IDatabaseBackupService>(sp => new DatabaseBackupService(
                databasePath,
                sp.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<AppSettings>>(),
                sp.GetRequiredService<ILogger<DatabaseBackupService>>(),
                sp.GetService<ComicMaintainerMetrics>()));
        }

        // Configure Data Protection to persist keys in Config directory
        try
        {
            var dataProtectionPath = Path.Combine(configDirectory, "DataProtection-Keys");
            Directory.CreateDirectory(dataProtectionPath);
            
            var dpBuilder = services.AddDataProtection()
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

        return services;
    }

    /// <summary>
    /// The directory that holds the database, the Data Protection keys and the generated JWT signing key
    /// (<c>AppSettings:ConfigDirectory</c>, by default /Config).
    /// </summary>
    internal static string ResolveConfigDirectory(IConfiguration configuration) =>
        configuration["AppSettings:ConfigDirectory"] ?? "/Config";

    /// <summary>
    /// Absolute path of the SQLite database file named by <paramref name="connectionString"/>
    /// (a plain path or a <c>file:</c> URI), or null for in-memory/shared-memory and
    /// temporary databases (which cannot be backed up).
    /// </summary>
    internal static string? ResolveSqliteDatabasePath(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString);
        var dataSource = builder.DataSource;
        if (builder.Mode == Microsoft.Data.Sqlite.SqliteOpenMode.Memory)
        {
            return null;
        }

        // SQLite only reads a data source starting with a lower-case "file:" as a URI.
        var path = dataSource.StartsWith("file:", StringComparison.Ordinal)
            ? ResolveSqliteUriPath(dataSource)
            : dataSource;
        if (string.IsNullOrWhiteSpace(path)
            || path.Equals(":memory:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Path.GetFullPath(path);
    }

    /// <summary>
    /// The file name in a SQLite URI filename (https://sqlite.org/uri.html), decoded the way
    /// SQLite does; null when the URI opens an in-memory database or names another host.
    /// </summary>
    private static string? ResolveSqliteUriPath(string uri)
    {
        var rest = uri["file:".Length..];
        if (rest.StartsWith("//", StringComparison.Ordinal))
        {
            // SQLite refuses any authority other than none or "localhost".
            var pathStart = rest.IndexOf('/', 2);
            var authority = pathStart < 0 ? rest[2..] : rest[2..pathStart];
            if (authority.Length > 0 && authority != "localhost")
            {
                return null;
            }

            rest = pathStart < 0 ? string.Empty : rest[pathStart..];
        }

        var fragmentStart = rest.IndexOf('#');
        if (fragmentStart >= 0)
        {
            rest = rest[..fragmentStart];
        }

        var queryStart = rest.IndexOf('?');
        if (queryStart >= 0)
        {
            // Parameter names and values are case-sensitive; the last one of a name wins.
            string? mode = null;
            string? vfs = null;
            foreach (var parameter in rest[(queryStart + 1)..].Split('&'))
            {
                var separator = parameter.IndexOf('=');
                var name = DecodeSqliteUriPart(separator < 0 ? parameter : parameter[..separator]);
                var value = separator < 0 ? string.Empty : DecodeSqliteUriPart(parameter[(separator + 1)..]);
                if (name == "mode")
                {
                    mode = value;
                }
                else if (name == "vfs")
                {
                    vfs = value;
                }
            }

            if (mode == "memory" || vfs == "memdb")
            {
                return null;
            }

            rest = rest[..queryStart];
        }

        var path = DecodeSqliteUriPart(rest);

        // SQLite on Windows drops the "/" in front of a drive letter (file:///C:/data/library.db).
        // On other systems a "/" in front of an absolute path is redundant ("//data" is "/data").
        if (path.StartsWith('/') && Path.IsPathFullyQualified(path.AsSpan(1)))
        {
            path = path[1..];
        }

        return path;
    }

    // Percent-decodes like SQLite, which ignores the rest of a part after "%00".
    private static string DecodeSqliteUriPart(string part)
    {
        var decoded = Uri.UnescapeDataString(part);
        var end = decoded.IndexOf('\0');
        return end < 0 ? decoded : decoded[..end];
    }

    public static IServiceCollection AddComicMaintainerCore(this IServiceCollection services)
    {
        // Application metrics (System.Diagnostics.Metrics; exported by OpenTelemetry, see TelemetryExtensions).
        services.AddSingleton<ComicMaintainerMetrics>();

        // Register application services
        services.AddHttpContextAccessor();
        // Singleton (rather than scoped) so singleton services can depend on it; IHttpContextAccessor
        // is AsyncLocal-backed, so the resolved user is still per-request.
        services.AddSingleton<IUserContextAccessor, HttpUserContextAccessor>();
        services.AddSingleton<EventBroadcasterService>();
        services.AddSingleton<IEventBroadcaster>(sp => sp.GetRequiredService<EventBroadcasterService>());
        services.AddSingleton<IFileStoreService, FileStoreService>();
        services.AddSingleton<ISeriesNameResolver, SeriesNameResolver>();
        services.AddSingleton<IJobStateStore, JobStateStore>();
        services.AddSingleton<IComicProcessorService, ComicProcessorService>();
        services.AddSingleton<IFileWatcherService, FileWatcherService>();
        services.AddSingleton<IProcessingHistoryService, ProcessingHistoryService>();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IComicReaderService, ComicReaderService>();
        services.AddSingleton<IFileCoverCacheService, FileCoverCacheService>();
        services.AddSingleton<ISeriesLibraryService, SeriesLibraryService>();
        services.AddSingleton<IOverviewService, OverviewService>();
        services.AddSingleton<ISeriesMetadataCacheService, SeriesMetadataCacheService>();
        services.AddSingleton<ISeriesMetadataRefreshJobService, SeriesMetadataRefreshJobService>();
        services.AddSingleton<ISeriesLanguagePreferenceRetagService, SeriesLanguagePreferenceRetagService>();
        services.AddSingleton<ISeriesImageStore, SeriesImageStore>();
        services.AddSingleton<ISeriesFolderCoverWriter, SeriesFolderCoverWriter>();
        services.AddSingleton<ISeriesArchiveCoverWriter, SeriesArchiveCoverWriter>();
        services.AddSingleton<SeriesArchiveCoverWriteQueue>();
        services.AddSingleton<ISeriesArchiveCoverWriteQueue>(sp => sp.GetRequiredService<SeriesArchiveCoverWriteQueue>());
        services.AddHttpClient(SeriesImageStore.HttpClientName)
            .ConfigureHttpClient(client =>
            {
                // Most provider CDNs serve images quickly; the SeriesImageStore also
                // applies its own per-request timeout. This is the absolute upper
                // bound for very slow connections.
                client.Timeout = TimeSpan.FromSeconds(30);
                // Identify ourselves to provider CDNs (MangaDex / AniList / ComicVine).
                // Some CDNs (notably Cloudflare-fronted endpoints like
                // uploads.mangadex.org) reject requests with an empty or missing
                // User-Agent with HTTP 400, which previously caused intermittent
                // "image download failed: HTTP 400" errors for certain series.
                client.DefaultRequestHeaders.UserAgent.ParseAdd("ComicMaintainer/1.0 (+https://github.com/mleenorris/ComicMaintainer)");
            });
        services.AddHttpClient(nameof(ComicVineSeriesMetadataService));
        services.AddHttpClient(nameof(MangaDexSeriesMetadataService))
            .ConfigureHttpClient(client =>
            {
                // MangaDex (and AniList below) recommend setting a descriptive
                // User-Agent. Identifying ourselves makes it easier for the provider
                // to contact us if our traffic causes issues, and some providers
                // reject empty User-Agent values from misbehaving clients.
                client.DefaultRequestHeaders.UserAgent.ParseAdd("ComicMaintainer/1.0 (+https://github.com/mleenorris/ComicMaintainer)");
            });
        services.AddHttpClient(nameof(AniListManhwaSeriesMetadataService))
            .ConfigureHttpClient(client =>
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd("ComicMaintainer/1.0 (+https://github.com/mleenorris/ComicMaintainer)");
            });
        services.AddHttpClient(nameof(AniListMangaSeriesMetadataService))
            .ConfigureHttpClient(client =>
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd("ComicMaintainer/1.0 (+https://github.com/mleenorris/ComicMaintainer)");
            });
        services.AddHttpClient(SuwayomiService.HttpClientName)
            .ConfigureHttpClient(client =>
            {
                // Suwayomi is normally a server on the local network; keep the timeout
                // short so a wrong URL fails fast instead of hanging a request.
                client.Timeout = TimeSpan.FromSeconds(30);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("ComicMaintainer/1.0 (+https://github.com/mleenorris/ComicMaintainer)");
            });
        services.AddSingleton<ISuwayomiService, SuwayomiService>();
        services.AddSingleton<ISuwayomiDownloadImportService, SuwayomiDownloadImportService>();
        services.AddSingleton<ComicVineSeriesMetadataService>();
        services.AddSingleton<MangaDexSeriesMetadataService>();
        services.AddSingleton<AniListManhwaSeriesMetadataService>();
        services.AddSingleton<AniListMangaSeriesMetadataService>();
        services.AddSingleton<IExternalSeriesMetadataService>(sp =>
            new CompositeExternalSeriesMetadataService(
                [
                    sp.GetRequiredService<ComicVineSeriesMetadataService>(),
                    sp.GetRequiredService<MangaDexSeriesMetadataService>(),
                    sp.GetRequiredService<AniListManhwaSeriesMetadataService>(),
                    sp.GetRequiredService<AniListMangaSeriesMetadataService>()
                ],
                sp.GetRequiredService<ILogger<CompositeExternalSeriesMetadataService>>()));

        // Reader foundation services
        services.AddSingleton<IReadingProgressService, ReadingProgressService>();
        services.AddSingleton<IReaderPreferenceService, ReaderPreferenceService>();
        services.AddSingleton<IReadingSessionService, ReadingSessionService>();

        // Per-user web UI preferences (theme, pagination, library view/filter/sort)
        services.AddSingleton<IUserPreferencesService, UserPreferencesService>();

        services.AddScoped<IAuthService, AuthService>();

        // Email delivery to saved ereader devices (with optional EPUB/AZW3 conversion).
        services.AddSingleton<IEpubConversionService, EpubConversionService>();
        services.AddSingleton<IAzw3ConversionService, Azw3ConversionService>();
        services.AddSingleton<IComicEmailSender, SmtpComicEmailSender>();
        services.AddSingleton<IEreaderDeviceService, EreaderDeviceService>();
        // The queue resolves the delivery service lazily: the service enqueues work and
        // the queue executes it, so constructor injection both ways would be a cycle.
        services.AddSingleton<ComicEmailQueue>(sp => new ComicEmailQueue(
            () => sp.GetRequiredService<IComicEmailService>(),
            sp.GetRequiredService<ILogger<ComicEmailQueue>>(),
            sp.GetService<ComicMaintainerMetrics>()));
        services.AddSingleton<IComicEmailQueue>(sp => sp.GetRequiredService<ComicEmailQueue>());
        services.AddSingleton<IComicEmailService, ComicEmailService>();
        // Condensed EPUB downloads run detached from the request that asked for them so
        // a book that takes minutes can be polled for progress instead of being built
        // behind a request that a browser or proxy will time out.
        services.AddSingleton<ICondensedBookBuildTracker, CondensedBookBuildTracker>();

        return services;
    }

    public static IServiceCollection AddComicMaintainerErrorReporting(this IServiceCollection services)
    {
        // ---------------------------------------------------------------------------
        // Automated error reporting: Error/Fatal log events -> deduplicated GitHub issues.
        //
        // Everything here is inert until ErrorReportingEnabled is turned on and a
        // repository and token are configured; the sink drops every event and the
        // dispatcher idles on an empty queue. Registration is unconditional so the
        // feature can be switched on at runtime without restarting the container.
        // ---------------------------------------------------------------------------
        services.AddSingleton<IErrorReportQueue>(_ => new ErrorReportQueue());
        services.AddSingleton<IErrorReportFactory, ErrorReportFactory>();
        services.AddHttpClient(GitHubIssueClient.HttpClientName)
            .ConfigureHttpClient(client =>
            {
                client.BaseAddress = new Uri("https://api.github.com/");
                // GitHub rejects requests with no User-Agent.
                client.DefaultRequestHeaders.UserAgent.ParseAdd("ComicMaintainer/1.0 (+https://github.com/mleenorris/ComicMaintainer)");
                // Reporting is strictly best-effort background work; never let a slow
                // GitHub hold the dispatcher (and therefore the queue) for long.
                client.Timeout = TimeSpan.FromSeconds(20);
            });
        services.AddSingleton<IGitHubIssueClient, GitHubIssueClient>();
        services.AddSingleton<IErrorReportingService, ErrorReportingService>();
        // Picked up by Serilog's ReadFrom.Services(...) above and attached to the
        // logging pipeline, so every Error/Fatal from any component is captured.
        services.AddSingleton<Serilog.Core.ILogEventSink, ErrorReportingSink>();
        services.AddHostedService<ErrorReportDispatchHostedService>();

        return services;
    }

    public static IServiceCollection AddComicMaintainerHostedServices(this IServiceCollection services)
    {
        // Add hosted service for file watcher
        services.AddHostedService<FileWatcherHostedService>();

        // Add hosted service that drains the background series archive cover write queue
        services.AddHostedService<SeriesArchiveCoverWriteQueueHostedService>();

        // Add hosted service that drains the background comic email delivery queue
        services.AddHostedService<ComicEmailQueueHostedService>();

        // Add hosted service for database cleanup
        services.AddHostedService<DatabaseCleanupHostedService>();

        // Reconcile persisted batch job state, marking anything left in flight by a restart as
        // interrupted so clients polling an old job id get a definite answer.
        services.AddHostedService<JobStateReconciliationHostedService>();

        // Scheduled-jobs framework: handlers + service + hosted runner.
        services.AddSingleton<IScheduledJobHandler, MetadataAuditJobHandler>();
        services.AddSingleton<IScheduledJobHandler, FileNamingAuditJobHandler>();
        services.AddSingleton<IScheduledJobHandler, SeriesLanguageAuditJobHandler>();
        services.AddSingleton<IScheduledJobHandler, LibraryScanJobHandler>();
        services.AddSingleton<IScheduledJobHandler, MetadataBackfillJobHandler>();
        services.AddSingleton<IScheduledJobHandler, MissingIssuesCheckJobHandler>();
        services.AddSingleton<IScheduledJobHandler, SuwayomiDownloadImportJobHandler>();
        // Only registered when the database is a file (see AddComicMaintainerData).
        if (services.Any(d => d.ServiceType == typeof(IDatabaseBackupService)))
        {
            services.AddSingleton<IScheduledJobHandler, DatabaseBackupJobHandler>();
        }
        services.AddSingleton<ScheduledJobService>();
        services.AddSingleton<IScheduledJobService>(sp => sp.GetRequiredService<ScheduledJobService>());
        services.AddSingleton<ScheduledJobsHostedService>();
        services.AddHostedService(sp => sp.GetRequiredService<ScheduledJobsHostedService>());

        return services;
    }
}
