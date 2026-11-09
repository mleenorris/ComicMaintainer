using System.Net;
using System.Text.Json;
using ComicMaintainer.Tests.Helpers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ComicMaintainer.Tests.Integration;

/// <summary>
/// The app hosted under a base path, from the outside: pages link their files under the prefix,
/// and requests work whether the reverse proxy forwards the prefix or strips it
/// (docs/swag-configs/comicmaintainer.subfolder.conf forwards it).
/// </summary>
[Trait("Category", "Integration")]
public class BasePathHostingTests
{
    private const string BasePath = "/comics";

    private static WebApplicationFactory<Program> CreateFactory(string? basePath) =>
        new TestWebApplicationFactory(builder =>
        {
            builder.UseEnvironment("Development");
            if (basePath is not null)
            {
                builder.UseSetting("AppSettings:BasePath", basePath);
            }
        });

    /// <summary>The prefix a request carries under each way a proxy can pass it on.</summary>
    public static TheoryData<string> ProxyPrefixes => new() { BasePath, string.Empty };

    [Theory]
    [MemberData(nameof(ProxyPrefixes))]
    public async Task LoginPage_LinksEveryFileUnderTheBasePath(string requestPrefix)
    {
        await using var factory = CreateFactory(BasePath);
        var client = factory.CreateClient();

        var response = await client.GetAsync($"{requestPrefix}/login.html");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains($"data-base-path=\"{BasePath}\"", html, StringComparison.Ordinal);
        Assert.Contains($"src=\"{BasePath}/js/shared.js?v=", html, StringComparison.Ordinal);
        Assert.Contains($"src=\"{BasePath}/js/pages/login.js?v=", html, StringComparison.Ordinal);
        Assert.DoesNotContain("__BASE_PATH__", html, StringComparison.Ordinal);
        Assert.DoesNotContain("src=\"/js/", html, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"/icons/", html, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(ProxyPrefixes))]
    public async Task ApiCall_IsAnswered(string requestPrefix)
    {
        await using var factory = CreateFactory(BasePath);
        var client = factory.CreateClient();

        var response = await client.GetAsync($"{requestPrefix}/api/version");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(string.IsNullOrEmpty(body.RootElement.GetProperty("version").GetString()));
    }

    [Theory]
    [MemberData(nameof(ProxyPrefixes))]
    public async Task StaticAsset_IsServed(string requestPrefix)
    {
        await using var factory = CreateFactory(BasePath);
        var client = factory.CreateClient();

        var response = await client.GetAsync($"{requestPrefix}/js/shared.js");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("function apiUrl(", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/comics")]
    [InlineData("/comics/")]
    [InlineData("/comics/index.html")]
    [InlineData("/comics/#/library")]
    [InlineData("/")]
    public async Task AppShell_IsServedAtEveryEntryPoint(string path)
    {
        await using var factory = CreateFactory(BasePath);
        var client = factory.CreateClient();

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains($"data-base-path=\"{BasePath}\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("__BASE_PATH__", html, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(ProxyPrefixes))]
    public async Task ServiceWorker_KnowsTheBasePath(string requestPrefix)
    {
        await using var factory = CreateFactory(BasePath);
        var client = factory.CreateClient();

        var response = await client.GetAsync($"{requestPrefix}/sw.js");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var script = await response.Content.ReadAsStringAsync();
        Assert.Contains($"INJECTED_BASE_PATH = '{BasePath}'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("__BASE_PATH__", script, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(ProxyPrefixes))]
    public async Task Manifest_PointsInsideTheBasePath(string requestPrefix)
    {
        await using var factory = CreateFactory(BasePath);
        var client = factory.CreateClient();

        var response = await client.GetAsync($"{requestPrefix}/manifest.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var manifest = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = manifest.RootElement;
        Assert.Equal($"{BasePath}/", root.GetProperty("start_url").GetString());
        Assert.Equal($"{BasePath}/", root.GetProperty("scope").GetString());
        Assert.Equal($"{BasePath}/", root.GetProperty("id").GetString());
        Assert.All(root.GetProperty("icons").EnumerateArray(), icon =>
            Assert.StartsWith($"{BasePath}/icons/", icon.GetProperty("src").GetString(), StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/")]
    public async Task WithoutABasePath_PagesLinkFromTheDomainRoot(string? configured)
    {
        await using var factory = CreateFactory(configured);
        var client = factory.CreateClient();

        var login = await (await client.GetAsync("/login.html")).Content.ReadAsStringAsync();
        var worker = await (await client.GetAsync("/sw.js")).Content.ReadAsStringAsync();
        using var manifest = JsonDocument.Parse(await (await client.GetAsync("/manifest.json")).Content.ReadAsStringAsync());

        Assert.Contains("data-base-path=\"\"", login, StringComparison.Ordinal);
        Assert.Contains("src=\"/js/shared.js?v=", login, StringComparison.Ordinal);
        Assert.DoesNotContain("__BASE_PATH__", login, StringComparison.Ordinal);
        Assert.Contains("INJECTED_BASE_PATH = ''", worker, StringComparison.Ordinal);
        Assert.Equal("/", manifest.RootElement.GetProperty("start_url").GetString());
    }

    [Fact]
    public async Task WithoutABasePath_APrefixedRequestIsNotTheApp()
    {
        await using var factory = CreateFactory(null);
        var client = factory.CreateClient();

        var response = await client.GetAsync($"{BasePath}/api/version");

        // A path outside the app's routes falls through to the SPA's index page.
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task AnUnusableBasePath_IsIgnored()
    {
        await using var factory = CreateFactory("/com ics");
        var client = factory.CreateClient();

        var login = await (await client.GetAsync("/login.html")).Content.ReadAsStringAsync();

        Assert.Contains("data-base-path=\"\"", login, StringComparison.Ordinal);
        Assert.Contains("src=\"/js/shared.js?v=", login, StringComparison.Ordinal);
    }
}
