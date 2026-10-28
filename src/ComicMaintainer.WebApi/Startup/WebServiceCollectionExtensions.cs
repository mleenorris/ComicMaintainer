using System.Globalization;
using System.IO.Compression;
using ComicMaintainer.WebApi.Authentication;
using ComicMaintainer.WebApi.Authorization;
using ComicMaintainer.WebApi.HealthChecks;
using ComicMaintainer.WebApi.Infrastructure;
using ComicMaintainer.WebApi.Middleware;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.ResponseCompression;
using Serilog;

namespace ComicMaintainer.WebApi.Startup;

/// <summary>
/// Registers MVC, Swagger, compression, request timeouts, rate limiting, output caching, SignalR, health checks, problem details and CORS.
/// </summary>
public static class WebServiceCollectionExtensions
{
    public static IServiceCollection AddComicMaintainerWeb(this IServiceCollection services, IConfiguration configuration)
    {
        // Add services to the container
        services.AddControllers(options =>
        {
            // Require the appropriate policy for every state-changing endpoint on the
            // library/settings controllers (see WriteOperationAuthorizationConvention).
            options.Conventions.Add(new WriteOperationAuthorizationConvention());
        });
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen();

        // Add response compression for better performance
        services.AddResponseCompression(options =>
        {
            options.EnableForHttps = true;

            // Register both providers explicitly so Brotli is always preferred for
            // clients that advertise it (it compresses the ~450 KB main.js and
            // ~120 KB main.css noticeably better than gzip), with gzip as fallback.
            options.Providers.Add<BrotliCompressionProvider>();
            options.Providers.Add<GzipCompressionProvider>();

            // The defaults cover HTML/CSS/JS/JSON; add the other text-ish payloads the
            // web UI serves so they are compressed too.
            options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(new[]
            {
                "image/svg+xml",
                "application/manifest+json",
                "text/javascript"
            });
        });

        // Static assets are served once per release and are far more compressible than
        // dynamic API responses, so spend the extra CPU on a better ratio.
        services.Configure<BrotliCompressionProviderOptions>(options =>
        {
            options.Level = CompressionLevel.Optimal;
        });

        services.Configure<GzipCompressionProviderOptions>(options =>
        {
            options.Level = CompressionLevel.Optimal;
        });

        // Add request timeout.
        //
        // The default policy applies to *every* request, so each policy here answers a
        // timed-out request through RequestTimeoutPolicies.WriteTimedOutResponseAsync:
        // it logs the timeout at Error (the framework only logs a context-free warning,
        // which automated error reporting never sees) and returns the error/correlation
        // body the UI knows how to show instead of an empty 504.
        services.AddRequestTimeouts(options =>
        {
            options.DefaultPolicy = RequestTimeoutPolicies.Create(RequestTimeoutPolicies.DefaultTimeout);
            // For endpoints that do real work before they can answer: condensing a whole
            // series into one book, planning or queueing a bulk send.
            options.AddPolicy(
                RequestTimeoutPolicies.LongRunning,
                RequestTimeoutPolicies.Create(RequestTimeoutPolicies.LongRunningTimeout));
        });

        // Add rate limiting (ASP.NET Core 9 best practice)
        // Behind a reverse proxy every request arrives from the proxy, so anonymous requests are counted per client
        // address that a trusted proxy forwards in X-Forwarded-For, not per connection: otherwise one client could
        // exhaust the anonymous limit for every visitor, and lock them all out of signing in.
        var trustedProxies = LoadRateLimiterTrustedProxies(configuration);
        var forwardedHopLimit = LoadRateLimiterForwardedHopLimit(configuration);
        if (trustedProxies.Networks.Count > 0)
        {
            services.AddSingleton<IStartupFilter>(serviceProvider =>
                new RateLimiterTrustedProxyLoggingStartupFilter(
                    trustedProxies,
                    forwardedHopLimit,
                    serviceProvider.GetRequiredService<ILogger<RateLimiterTrustedProxyLoggingStartupFilter>>()));
        }

        services.AddRateLimiter(options =>
        {
            options.GlobalLimiter = System.Threading.RateLimiting.PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                // Allow generous per-minute limits for authenticated traffic to avoid
                // throttling normal UI activity (metadata refresh polling + image loads).
                var isAuthenticated = context.User?.Identity?.IsAuthenticated == true;
                var partitionKey = isAuthenticated
                    ? $"user:{context.User?.Identity?.Name ?? "authenticated"}"
                    : $"ip:{ForwardedClientAddress.Resolve(context, trustedProxies, forwardedHopLimit)?.ToString() ?? "unknown"}";

                return System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: partitionKey,
                    factory: _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
                    {
                        PermitLimit = isAuthenticated ? 1000 : 100,
                        Window = TimeSpan.FromMinutes(1),
                        QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst,
                        QueueLimit = isAuthenticated ? 100 : 10
                    });
            });
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        });

        // Add output caching for better performance  
        services.AddOutputCache(options =>
        {
            // Don't cache by default - caching should be explicit
            options.AddBasePolicy(builder => builder.NoCache());
            // Cache comic pages for 1 hour since they don't change frequently
            options.AddPolicy("ComicPages", builder => builder
                .Expire(TimeSpan.FromHours(1))
                .SetVaryByQuery("filePath", "page"));
        });

        // Add SignalR
        services.AddSignalR();
        services.AddMemoryCache();

        // Add health checks.
        // "/health" is a pure liveness probe (the process is up and serving requests);
        // "/health/ready" additionally verifies the database is reachable, which is what
        // container orchestrators should gate traffic on.
        services.AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>("database", tags: new[] { "ready" });

        // Turn unhandled exceptions into RFC 7807 problem documents that carry the
        // request's correlation id, instead of an empty 500 nobody can trace.
        services.AddProblemDetails(options =>
        {
            options.CustomizeProblemDetails = ctx =>
            {
                // Applies to the framework-generated problem documents too (400 model
                // validation, 404, 415, ...) so *every* problem response is traceable.
                ctx.ProblemDetails.Extensions.TryAdd("correlationId", ctx.HttpContext.GetCorrelationId());
            };
        });
        services.AddExceptionHandler<GlobalExceptionHandler>();

        // Add CORS with security-conscious configuration
        services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
            {
                // Get allowed origins from configuration or environment variable
                var allowedOriginsConfig = configuration.GetSection("CorsSettings:AllowedOrigins").Get<string[]>();
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

        return services;
    }

    /// <summary>
    /// The reverse proxies whose <c>X-Forwarded-For</c> the rate limiter uses (see <see cref="ForwardedClientAddress"/>):
    /// <c>TRUSTED_PROXIES</c> (an unset or empty variable keeps <c>ReverseProxySettings:TrustedProxies</c>), plus
    /// <c>AUTHELIA_TRUSTED_PROXIES</c> when Authelia is enabled, because the proxy that sends the Authelia headers
    /// relays every request. Refuses to start on an entry that is not an IP address or CIDR range.
    /// </summary>
    private static TrustedProxyAllowlist LoadRateLimiterTrustedProxies(IConfiguration configuration)
    {
        const string variable = "TRUSTED_PROXIES";
        var value = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(value))
        {
            value = configuration["ReverseProxySettings:TrustedProxies"];
        }

        if (!TrustedProxyAllowlist.TryParseOptional(value, variable, out var trustedProxies, out var error))
        {
            throw new InvalidOperationException(error + " See docs/REVERSE_PROXY.md.");
        }

        // AddComicMaintainerAuth has already refused to start if Authelia is enabled with an invalid list.
        var autheliaSettings = AutheliaSettingsBinder.Load(configuration);
        if (autheliaSettings.Enabled
            && TrustedProxyAllowlist.TryParse(autheliaSettings.TrustedProxies, out var autheliaProxies, out _))
        {
            trustedProxies = trustedProxies.Union(autheliaProxies);
        }

        return trustedProxies;
    }

    /// <summary>
    /// The maximum number of right-most <c>X-Forwarded-For</c> entries the rate limiter follows; defaults to one
    /// so a trusted network gateway cannot expose client-controlled entries farther to the left.
    /// </summary>
    private static int LoadRateLimiterForwardedHopLimit(IConfiguration configuration)
    {
        const string variable = "TRUSTED_PROXY_HOPS";
        var value = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(value))
        {
            value = configuration["ReverseProxySettings:ForwardedHopLimit"];
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return 1;
        }

        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var hopLimit) || hopLimit < 1)
        {
            throw new InvalidOperationException(
                $"Invalid {variable} value '{value}'. Set it to a positive integer. See docs/REVERSE_PROXY.md.");
        }

        return hopLimit;
    }

    private sealed class RateLimiterTrustedProxyLoggingStartupFilter(
        TrustedProxyAllowlist trustedProxies,
        int forwardedHopLimit,
        ILogger<RateLimiterTrustedProxyLoggingStartupFilter> logger) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            logger.LogInformation(
                "Rate limiting counts anonymous requests from {TrustedProxies} per X-Forwarded-For client address, up to {ForwardedHopLimit} hop(s)",
                string.Join(", ", trustedProxies.Networks),
                forwardedHopLimit);
            next(app);
        };
    }
}
