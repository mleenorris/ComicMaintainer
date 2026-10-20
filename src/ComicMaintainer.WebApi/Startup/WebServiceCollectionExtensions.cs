using System.IO.Compression;
using ComicMaintainer.WebApi.Authorization;
using ComicMaintainer.WebApi.HealthChecks;
using ComicMaintainer.WebApi.Infrastructure;
using ComicMaintainer.WebApi.Middleware;
using Microsoft.AspNetCore.ResponseCompression;
using Serilog;

namespace ComicMaintainer.WebApi.Startup;

/// <summary>
/// Registers MVC, Swagger, compression, request timeouts, rate limiting, output caching, SignalR, health checks, problem details and CORS.
/// </summary>
internal static class WebServiceCollectionExtensions
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
        services.AddRateLimiter(options =>
        {
            options.GlobalLimiter = System.Threading.RateLimiting.PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                // Allow generous per-minute limits for authenticated traffic to avoid
                // throttling normal UI activity (metadata refresh polling + image loads).
                var isAuthenticated = context.User?.Identity?.IsAuthenticated == true;
                var partitionKey = isAuthenticated
                    ? $"user:{context.User?.Identity?.Name ?? "authenticated"}"
                    : $"ip:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";

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
}
