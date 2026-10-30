using System.Security.Cryptography;
using System.Text;
using ComicMaintainer.Core.Telemetry;
using ComicMaintainer.WebApi.Services;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace ComicMaintainer.WebApi.Startup;

/// <summary>
/// OpenTelemetry metrics/tracing registration and the optional Prometheus
/// scraping endpoint.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Metrics (the <c>ComicMaintainer</c> meter plus ASP.NET Core, HttpClient and
/// runtime instrumentation) are always collected in-process; collection is cheap when
/// nothing exports them.</item>
/// <item><c>Telemetry:PrometheusEnabled</c> (env <c>TELEMETRY__PROMETHEUSENABLED</c>)
/// exposes them at <c>/metrics</c>. When <c>Telemetry:MetricsToken</c> is set the
/// endpoint requires <c>Authorization: Bearer &lt;token&gt;</c>; otherwise it is
/// anonymous and should be restricted at the reverse proxy.</item>
/// <item>Setting <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> enables the OTLP exporter for both
/// metrics and traces (the standard <c>OTEL_EXPORTER_OTLP_*</c> variables apply).</item>
/// </list>
/// </remarks>
internal static class TelemetryExtensions
{
    public const string MetricsPath = "/metrics";
    public const string PrometheusEnabledKey = "Telemetry:PrometheusEnabled";
    public const string MetricsTokenKey = "Telemetry:MetricsToken";
    public const string OtlpEndpointKey = "OTEL_EXPORTER_OTLP_ENDPOINT";

    public static IServiceCollection AddComicMaintainerTelemetry(this IServiceCollection services, IConfiguration configuration)
    {
        var prometheusEnabled = configuration.GetValue<bool>(PrometheusEnabledKey);
        var otlpEnabled = !string.IsNullOrWhiteSpace(configuration[OtlpEndpointKey]);
        var serviceName = configuration["OTEL_SERVICE_NAME"];
        if (string.IsNullOrWhiteSpace(serviceName))
        {
            serviceName = "ComicMaintainer";
        }
        var serviceVersion = typeof(TelemetryExtensions).Assembly.GetName().Version?.ToString();

        var otel = services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName, serviceVersion: serviceVersion))
            .WithMetrics(metrics =>
            {
                metrics
                    .AddMeter(ComicMaintainerMetrics.MeterName)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();

                if (prometheusEnabled)
                {
                    metrics.AddPrometheusExporter();
                }

                if (otlpEnabled)
                {
                    // Endpoint, protocol and headers come from the standard OTEL_EXPORTER_OTLP_* settings.
                    metrics.AddOtlpExporter();
                }
            });

        if (otlpEnabled)
        {
            otel.WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation(options =>
                {
                    // Probes, scrapes and long-lived streams would only add noise.
                    options.Filter = context =>
                        !context.Request.Path.StartsWithSegments("/health") &&
                        !context.Request.Path.StartsWithSegments(MetricsPath) &&
                        !context.Request.Path.StartsWithSegments("/hubs");
                })
                .AddHttpClientInstrumentation(options =>
                {
                    // Authelia's health is checked every 30 seconds when AUTHELIA_URL is set.
                    options.FilterHttpRequestMessage = request => !AutheliaHealthMonitor.IsHealthCheck(request);
                })
                .AddOtlpExporter());
        }

        return services;
    }

    /// <summary>
    /// Maps the Prometheus scraping endpoint at <see cref="MetricsPath"/> when enabled.
    /// It is a branched pipeline placed before routing, authentication and the SPA
    /// fallback, so neither the JWT/Authelia schemes nor the fallback can intercept it.
    /// </summary>
    public static WebApplication UseComicMaintainerMetricsEndpoint(this WebApplication app)
    {
        if (!app.Configuration.GetValue<bool>(PrometheusEnabledKey))
        {
            return app;
        }

        var token = app.Configuration[MetricsTokenKey];
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(TelemetryExtensions).FullName!);
        if (string.IsNullOrWhiteSpace(token))
        {
            logger.LogWarning(
                "Prometheus metrics are exposed anonymously at {Path}. Set Telemetry:MetricsToken or restrict the path at your reverse proxy.",
                MetricsPath);
        }

        app.UseOpenTelemetryPrometheusScrapingEndpoint(
            meterProvider: null,
            predicate: context => context.Request.Path == MetricsPath,
            path: null,
            configureBranchedPipeline: branch =>
            {
                if (!string.IsNullOrWhiteSpace(token))
                {
                    branch.Use(async (context, next) =>
                    {
                        if (!IsAuthorized(context.Request.Headers.Authorization.ToString(), token))
                        {
                            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                            context.Response.Headers.WWWAuthenticate = "Bearer realm=\"metrics\"";
                            return;
                        }

                        await next(context);
                    });
                }
            },
            optionsName: null);

        return app;
    }

    internal static bool IsAuthorized(string? authorizationHeader, string expectedToken)
    {
        const string scheme = "Bearer ";
        if (string.IsNullOrEmpty(authorizationHeader)
            || !authorizationHeader.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var presented = Encoding.UTF8.GetBytes(authorizationHeader[scheme.Length..].Trim());
        var expected = Encoding.UTF8.GetBytes(expectedToken);
        return CryptographicOperations.FixedTimeEquals(presented, expected);
    }
}
