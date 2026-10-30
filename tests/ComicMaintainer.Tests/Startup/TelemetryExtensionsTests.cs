using System.Reflection;
using ComicMaintainer.WebApi.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Instrumentation.AspNetCore;
using OpenTelemetry.Instrumentation.Http;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace ComicMaintainer.Tests.Startup;

/// <summary>
/// Tests for the OpenTelemetry registrations made by <c>TelemetryExtensions.AddComicMaintainerTelemetry</c>,
/// exercised on a bare <see cref="ServiceCollection"/>. Exporters are never built here, so nothing is sent
/// anywhere.
/// </summary>
public sealed class TelemetryExtensionsTests
{
    private const string OtlpEndpoint = "http://127.0.0.1:4318";

    // TelemetryExtensions is internal to the WebApi assembly (no InternalsVisibleTo).
    private static readonly Func<IServiceCollection, IConfiguration, IServiceCollection> AddComicMaintainerTelemetry = typeof(Program).Assembly
        .GetType("ComicMaintainer.WebApi.Startup.TelemetryExtensions", throwOnError: true)!
        .GetMethod("AddComicMaintainerTelemetry", BindingFlags.Static | BindingFlags.Public)!
        .CreateDelegate<Func<IServiceCollection, IConfiguration, IServiceCollection>>();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WithoutAnOtlpEndpoint_DoesNotRegisterTracing(string? endpoint)
    {
        var services = Register(("OTEL_EXPORTER_OTLP_ENDPOINT", endpoint));

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(TracerProvider));
        using var provider = services.BuildServiceProvider();
        Assert.Null(provider.GetService<TracerProvider>());
        // Metrics are always collected in-process.
        Assert.Contains(services, d => d.ServiceType == typeof(MeterProvider));
    }

    [Fact]
    public void WithAnOtlpEndpoint_RegistersTracing()
    {
        var services = Register(("OTEL_EXPORTER_OTLP_ENDPOINT", OtlpEndpoint));

        Assert.Contains(services, d => d.ServiceType == typeof(TracerProvider));
    }

    [Theory]
    [InlineData("/health", false)]
    [InlineData("/health/ready", false)]
    [InlineData("/HEALTH", false)]
    [InlineData("/metrics", false)]
    [InlineData("/hubs/events", false)]
    [InlineData("/api/version", true)]
    [InlineData("/healthcheck", true)]
    [InlineData("/metricsx", true)]
    [InlineData("/", true)]
    public void WithAnOtlpEndpoint_TracesEveryRequestExceptProbesScrapesAndHubs(string path, bool traced)
    {
        using var provider = Register(("OTEL_EXPORTER_OTLP_ENDPOINT", OtlpEndpoint)).BuildServiceProvider();
        var filter = provider.GetRequiredService<IOptionsMonitor<AspNetCoreTraceInstrumentationOptions>>().CurrentValue.Filter;
        var context = new DefaultHttpContext();
        context.Request.Path = path;

        Assert.NotNull(filter);
        Assert.Equal(traced, filter(context));
    }

    // AutheliaHealthMonitor checks Authelia every 30 seconds when AUTHELIA_URL is set.
    [Fact]
    public void WithAnOtlpEndpoint_TracesOutgoingRequestsExceptAutheliaHealthChecks()
    {
        using var provider = Register(("OTEL_EXPORTER_OTLP_ENDPOINT", OtlpEndpoint)).BuildServiceProvider();
        var filter = provider.GetRequiredService<IOptionsMonitor<HttpClientTraceInstrumentationOptions>>().CurrentValue.FilterHttpRequestMessage;
        var healthEndpoint = new Uri("http://authelia:9091/api/health");
        using var healthCheck = AutheliaHealthMonitor.CreateHealthCheckRequest(healthEndpoint);
        using var otherRequest = new HttpRequestMessage(HttpMethod.Get, healthEndpoint);

        Assert.NotNull(filter);
        Assert.False(filter(healthCheck));
        Assert.True(filter(otherRequest));
    }

    [Theory]
    [InlineData("library-service", "library-service")]
    [InlineData(null, "ComicMaintainer")]
    [InlineData("   ", "ComicMaintainer")]
    public void DescribesTheServiceInTheTelemetryResource(string? configuredName, string expectedName)
    {
        using var provider = Register(("OTEL_SERVICE_NAME", configuredName)).BuildServiceProvider();

        var attributes = provider.GetRequiredService<MeterProvider>().GetResource().Attributes
            .ToDictionary(a => a.Key, a => a.Value);

        Assert.Equal(expectedName, attributes["service.name"]);
        Assert.Equal(typeof(Program).Assembly.GetName().Version!.ToString(), attributes["service.version"]);
    }

    private static ServiceCollection Register(params (string Key, string? Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        AddComicMaintainerTelemetry(services, configuration);
        return services;
    }
}
