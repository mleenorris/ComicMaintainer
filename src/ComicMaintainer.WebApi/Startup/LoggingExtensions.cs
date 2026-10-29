using Serilog;
using Serilog.Events;

namespace ComicMaintainer.WebApi.Startup;

/// <summary>
/// Serilog configuration for the web host (console, app/debug/watcher/metadata/auth log files).
/// </summary>
internal static class LoggingExtensions
{
    public static WebApplicationBuilder AddComicMaintainerLogging(this WebApplicationBuilder builder, string configDir, long logMaxBytes)
    {
        // Use Serilog for logging - configure with the builder context to ensure proper integration
        //
        // Source-context predicates for routing logs to dedicated files. Anything
        // produced by these classes is routed to the matching log file AND excluded
        // from app.log so the general application log stays focused on high-signal
        // events. They still go to debug.log (which is the catch-all).
        Func<Serilog.Events.LogEvent, bool> isWatcherSource = e =>
            e.Properties.TryGetValue("SourceContext", out var ctx)
            && (ctx.ToString().Contains("FileWatcherService")
                || ctx.ToString().Contains("FileWatcherHostedService"));

        // Metadata-related sources: every external provider client, the composite
        // service, the cache, the refresh job, the provider health tracker, the
        // per-series image store, the MetadataController API surface, and (looser)
        // any other type whose SourceContext contains "Metadata".
        Func<Serilog.Events.LogEvent, bool> isMetadataSource = e =>
        {
            if (!e.Properties.TryGetValue("SourceContext", out var ctx)) return false;
            var s = ctx.ToString();
            return s.Contains("Metadata")
                || s.Contains("AniListMangaSeriesMetadataService")
                || s.Contains("AniListManhwaSeriesMetadataService")
                || s.Contains("ComicVineSeriesMetadataService")
                || s.Contains("MangaDexSeriesMetadataService")
                || s.Contains("CompositeExternalSeriesMetadataService")
                || s.Contains("ProviderHealthTracker")
                || s.Contains("SeriesImageStore")
                || s.Contains("MetadataController");
        };

        // Auth-related sources: AuthController API surface, the AuthService that
        // performs credential validation / user CRUD / token issuance, and the
        // Authelia reverse-proxy authentication handler. Routes all of these to a
        // dedicated auth.log so authentication activity can be audited without
        // sifting through the general application log.
        Func<Serilog.Events.LogEvent, bool> isAuthSource = e =>
        {
            if (!e.Properties.TryGetValue("SourceContext", out var ctx)) return false;
            var s = ctx.ToString();
            return s.Contains("AuthController")
                || s.Contains("AuthService")
                || s.Contains("AutheliaAuthenticationHandler");
        };

        builder.Host.UseSerilog((context, services, configuration) => configuration
            .ReadFrom.Configuration(context.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            // Every debug.log line carries the correlation id of the request that
            // produced it (pushed by RequestCorrelationMiddleware). Work that is not
            // part of a request — the watcher, scheduled jobs, startup — has no id, so
            // it gets a literal "-" rather than an empty bracket pair. AddPropertyIfAbsent
            // semantics mean the real id always wins when one is in scope.
            .Enrich.WithProperty("CorrelationId", "-")
            .MinimumLevel.Debug()
            // Console sink - only show Information and above, clean formatting
            .WriteTo.Console(
                restrictedToMinimumLevel: LogEventLevel.Information,
                outputTemplate: "[{Timestamp:HH:mm:ss}] [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            // Basic/Info log file - Information level and above, excluding watcher, metadata, AND auth logs
            .WriteTo.Logger(lc => lc
                .Filter.ByExcluding(e => isWatcherSource(e) || isMetadataSource(e) || isAuthSource(e))
                .WriteTo.File(
                    Path.Join(configDir, "app.log"),
                    restrictedToMinimumLevel: LogEventLevel.Information,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 7,
                    fileSizeLimitBytes: logMaxBytes,
                    outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{Level:u3}] {Message:lj}{NewLine}{Exception}"))
            // Debug log file - capture everything at Debug level and above (including watcher and metadata)
            .WriteTo.File(
                Path.Join(configDir, "debug.log"),
                restrictedToMinimumLevel: LogEventLevel.Debug,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 3,
                fileSizeLimitBytes: logMaxBytes,
                outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{Level:u3}] [{SourceContext}] [{CorrelationId}] {Message:lj}{NewLine}{Exception}")
            // Watcher-specific log file - capture all watcher-related logs
            .WriteTo.Logger(lc => lc
                .Filter.ByIncludingOnly(e => isWatcherSource(e))
                .WriteTo.File(
                    Path.Join(configDir, "watcher.log"),
                    restrictedToMinimumLevel: LogEventLevel.Debug,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 7,
                    fileSizeLimitBytes: logMaxBytes,
                    outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{Level:u3}] {Message:lj}{NewLine}{Exception}"))
            // Metadata-specific log file - capture external provider lookups, cache
            // updates, refresh jobs and image fetches. Makes debugging metadata
            // requests possible without sifting through the noisier debug log.
            .WriteTo.Logger(lc => lc
                .Filter.ByIncludingOnly(e => isMetadataSource(e))
                .WriteTo.File(
                    Path.Join(configDir, "metadata.log"),
                    restrictedToMinimumLevel: LogEventLevel.Debug,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 7,
                    fileSizeLimitBytes: logMaxBytes,
                    outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}"))
            // Auth-specific log file - capture login attempts, token issuance,
            // user CRUD, and Authelia reverse-proxy authentication. Kept separate
            // from the general app log so authentication activity can be audited.
            .WriteTo.Logger(lc => lc
                .Filter.ByIncludingOnly(e => isAuthSource(e))
                .WriteTo.File(
                    Path.Join(configDir, "auth.log"),
                    restrictedToMinimumLevel: LogEventLevel.Debug,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 7,
                    fileSizeLimitBytes: logMaxBytes,
                    outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}"))
            // Override specific namespaces to reduce console noise
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Migrations", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Model.Validation", LogEventLevel.Error));

        return builder;
    }
}
