using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using ComicMaintainer.Tests.Helpers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ComicMaintainer.Tests.Startup;

/// <summary>
/// The optional Prometheus endpoint (<c>TelemetryExtensions.UseComicMaintainerMetricsEndpoint</c>) on a small real
/// pipeline, so what is asserted is what a scraper gets: whether it answers, and whether the token is required.
/// </summary>
public sealed class TelemetryMetricsEndpointTests
{
    private const string TelemetryCategory = "ComicMaintainer.WebApi.Startup.TelemetryExtensions";
    private const string Token = "s3cret-scrape-token";

    // TelemetryExtensions is internal to the WebApi assembly (no InternalsVisibleTo).
    private static readonly Type Telemetry = typeof(Program).Assembly
        .GetType(TelemetryCategory, throwOnError: true)!;

    private static readonly Func<IServiceCollection, IConfiguration, IServiceCollection> AddTelemetry = Telemetry
        .GetMethod("AddComicMaintainerTelemetry", BindingFlags.Static | BindingFlags.Public)!
        .CreateDelegate<Func<IServiceCollection, IConfiguration, IServiceCollection>>();

    private static readonly Func<WebApplication, WebApplication> UseMetricsEndpoint = Telemetry
        .GetMethod("UseComicMaintainerMetricsEndpoint", BindingFlags.Static | BindingFlags.Public)!
        .CreateDelegate<Func<WebApplication, WebApplication>>();

    [Fact]
    public async Task Disabled_MetricsAreNotServed()
    {
        await using var server = await StartAsync(("Telemetry:PrometheusEnabled", "false"), ("Telemetry:MetricsToken", Token));

        var response = await server.Client.GetAsync("/metrics");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task EnabledWithoutAToken_ServesMetricsAnonymouslyAndWarnsThatItDoes()
    {
        await using var server = await StartAsync(("Telemetry:PrometheusEnabled", "true"));

        var response = await server.Client.GetAsync("/metrics");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEmpty(await response.Content.ReadAsStringAsync());
        Assert.Contains(
            server.Logs.Entries,
            e => e.Category == TelemetryCategory && e.Level == LogLevel.Warning && e.Message.Contains("exposed anonymously", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Basic s3cret-scrape-token")]
    [InlineData("Bearer")]
    [InlineData("Bearer wrong-token")]
    [InlineData("Bearer s3cret-scrape-tokenX")]
    public async Task EnabledWithAToken_RefusesAnyOtherAuthorization(string? authorization)
    {
        await using var server = await StartAsync(("Telemetry:PrometheusEnabled", "true"), ("Telemetry:MetricsToken", Token));

        var response = await SendAsync(server, authorization);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer realm=\"metrics\"", response.Headers.WwwAuthenticate.ToString());
        Assert.Empty(await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain(
            server.Logs.Entries,
            e => e.Category == TelemetryCategory && e.Message.Contains("exposed anonymously", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Bearer " + Token)]
    [InlineData("bearer " + Token)]
    [InlineData("BEARER   " + Token + "  ")]
    public async Task EnabledWithAToken_ServesMetricsToTheRightOne(string authorization)
    {
        await using var server = await StartAsync(("Telemetry:PrometheusEnabled", "true"), ("Telemetry:MetricsToken", Token));

        var response = await SendAsync(server, authorization);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEmpty(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task EnabledWithAToken_RequiresItOnlyForTheMetricsPath()
    {
        await using var server = await StartAsync(("Telemetry:PrometheusEnabled", "true"), ("Telemetry:MetricsToken", Token));

        Assert.Equal(HttpStatusCode.OK, (await server.Client.GetAsync("/api/ping")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await server.Client.GetAsync("/metricsx")).StatusCode);
    }

    private static async Task<HttpResponseMessage> SendAsync(RunningServer server, string? authorization)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/metrics");
        if (authorization is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        return await server.Client.SendAsync(request);
    }

    private static async Task<RunningServer> StartAsync(params (string Key, string? Value)[] settings)
    {
        var logs = new AuthTestHost.CapturingLoggerProvider();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Logging.AddProvider(logs);
        builder.Configuration.AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)));
        AddTelemetry(builder.Services, builder.Configuration);
        var app = builder.Build();
        UseMetricsEndpoint(app);
        app.MapGet("/api/ping", () => "pong");
        await app.StartAsync();
        return new RunningServer(app, app.GetTestClient(), logs);
    }

    private sealed record RunningServer(WebApplication App, HttpClient Client, AuthTestHost.CapturingLoggerProvider Logs) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.DisposeAsync();
        }
    }
}
