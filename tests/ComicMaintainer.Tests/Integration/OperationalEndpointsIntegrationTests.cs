using System.Net;
using System.Net.Http.Headers;
using ComicMaintainer.Tests.Helpers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ComicMaintainer.Tests.Integration;

/// <summary>
/// End-to-end checks for the opt-in operational surfaces: Swagger outside
/// Development, the Prometheus <c>/metrics</c> endpoint (and its optional
/// bearer token), and the admin-only backups API.
/// </summary>
[Trait("Category", "Integration")]
public class OperationalEndpointsIntegrationTests
{
    private const string MetricsToken = "test-metrics-token-123";

    private static WebApplicationFactory<Program> CreateFactory(string environment, params (string Key, string Value)[] settings)
    {
        return new TestWebApplicationFactory(builder =>
        {
            builder.UseEnvironment(environment);
            if (environment != "Development")
            {
                // appsettings.json points at container paths (/Config, ...); use the
                // same writable test locations appsettings.Development.json uses.
                const string root = "/tmp/comic_test";
                builder.UseSetting("AppSettings:WatchedDirectory", root + "/watched_dir");
                builder.UseSetting("AppSettings:DuplicateDirectory", root + "/duplicates");
                builder.UseSetting("AppSettings:ConfigDirectory", root + "/Config");
            }
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }
        });
    }

    [Fact]
    public async Task Swagger_IsDisabledInProductionByDefault()
    {
        await using var factory = CreateFactory("Production");
        var client = factory.CreateClient();

        var response = await client.GetAsync("/swagger/v1/swagger.json");

        // Without Swagger the SPA fallback answers instead of an OpenAPI document.
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("\"openapi\"", body);
    }

    [Fact]
    public async Task Swagger_CanBeEnabledOutsideDevelopment()
    {
        await using var factory = CreateFactory("Production", ("Api:EnableSwagger", "true"));
        var client = factory.CreateClient();

        var document = await client.GetAsync("/swagger/v1/swagger.json");
        Assert.Equal(HttpStatusCode.OK, document.StatusCode);
        var json = await document.Content.ReadAsStringAsync();
        Assert.Contains("/api/backups", json);

        var ui = await client.GetAsync("/swagger/index.html");
        Assert.Equal(HttpStatusCode.OK, ui.StatusCode);
        Assert.Contains("swagger", await ui.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        // The security headers still apply, and the CSP allows what the UI needs.
        var csp = ui.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("script-src 'self' 'unsafe-inline'", csp);

        // The UI's own assets are served same-origin, so 'self' covers them.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/swagger/index.js")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/swagger/swagger-ui-bundle.js")).StatusCode);
    }

    [Fact]
    public async Task Metrics_NotExposedWhenPrometheusDisabled()
    {
        await using var factory = CreateFactory("Development");
        var client = factory.CreateClient();

        var response = await client.GetAsync("/metrics");

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("# TYPE", body);
    }

    [Fact]
    public async Task Metrics_AnonymousWhenNoTokenConfigured()
    {
        await using var factory = CreateFactory("Development", ("Telemetry:PrometheusEnabled", "true"));
        var client = factory.CreateClient();

        // Generate some ASP.NET Core metrics first.
        await client.GetAsync("/health");
        var response = await client.GetAsync("/metrics");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("# TYPE", body);
    }

    [Fact]
    public async Task Metrics_RequireBearerTokenWhenConfigured()
    {
        await using var factory = CreateFactory(
            "Development",
            ("Telemetry:PrometheusEnabled", "true"),
            ("Telemetry:MetricsToken", MetricsToken));
        var client = factory.CreateClient();

        var anonymous = await client.GetAsync("/metrics");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        using var wrong = new HttpRequestMessage(HttpMethod.Get, "/metrics");
        wrong.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "nope");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(wrong)).StatusCode);

        using var right = new HttpRequestMessage(HttpMethod.Get, "/metrics");
        right.Headers.Authorization = new AuthenticationHeaderValue("Bearer", MetricsToken);
        var ok = await client.SendAsync(right);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Contains("# TYPE", await ok.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("GET", "/api/backups")]
    [InlineData("POST", "/api/backups")]
    [InlineData("GET", "/api/backups/comicmaintainer-20261020-040000.db/download")]
    [InlineData("DELETE", "/api/backups/comicmaintainer-20261020-040000.db")]
    [InlineData("POST", "/api/backups/comicmaintainer-20261020-040000.db/restore")]
    [InlineData("POST", "/api/backups/upload-restore")]
    public async Task Backups_RequireAuthentication(string method, string path)
    {
        await using var factory = CreateFactory("Development");
        var client = factory.CreateClient();

        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (path.EndsWith("upload-restore", StringComparison.Ordinal))
        {
            request.Content = new MultipartFormDataContent
            {
                { new ByteArrayContent(new byte[] { 1, 2, 3 }), "file", "backup.db" }
            };
        }
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
