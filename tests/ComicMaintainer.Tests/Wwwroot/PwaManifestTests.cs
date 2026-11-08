using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ComicMaintainer.Tests.Wwwroot;

/// <summary>
/// The PWA manifest must be a real same-origin file. The app's CSP sets
/// <c>manifest-src 'self'</c>, which does not match a <c>blob:</c> URL, and a
/// manifest's root-relative <c>start_url</c>, <c>scope</c> and icon paths only
/// resolve against an http(s) manifest URL. An inline blob manifest therefore
/// left browsers with no usable manifest, so the app could not be installed.
/// </summary>
public class PwaManifestTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly Regex ManifestLink = new(
        @"<link\b[^>]*\brel=""manifest""[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly WebApplicationFactory<Program> _factory;

    public PwaManifestTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    private static string ManifestHref(string html)
    {
        var links = ManifestLink.Matches(html);
        Assert.Single(links);
        var href = Regex.Match(links[0].Value, @"\bhref=""([^""]*)""");
        Assert.True(href.Success, "The manifest <link> must carry an href: " + links[0].Value);
        return href.Groups[1].Value;
    }

    [Fact]
    public void IndexHtml_LinksTheSameOriginManifestFile_NotABlob()
    {
        var html = WwwrootFiles.Read("index.html");

        Assert.Equal("/manifest.json", ManifestHref(html));
        Assert.DoesNotContain("createObjectURL", html, StringComparison.Ordinal);
        Assert.DoesNotContain("application/manifest+json", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ManifestJson_IsInstallableAndItsIconsShip()
    {
        using var manifest = JsonDocument.Parse(WwwrootFiles.Read("manifest.json"));
        var root = manifest.RootElement;

        Assert.Equal("Comic Maintainer", root.GetProperty("name").GetString());
        Assert.Equal("standalone", root.GetProperty("display").GetString());
        Assert.Equal("any", root.GetProperty("orientation").GetString());
        foreach (var member in new[] { "id", "start_url", "scope" })
        {
            Assert.Equal("/", root.GetProperty(member).GetString());
        }

        var icons = root.GetProperty("icons").EnumerateArray().ToList();
        Assert.Contains(icons, icon => icon.GetProperty("sizes").GetString() == "192x192");
        Assert.Contains(icons, icon => icon.GetProperty("sizes").GetString() == "512x512");
        foreach (var icon in icons)
        {
            var src = icon.GetProperty("src").GetString()!;
            Assert.StartsWith("/", src, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Join(WwwrootFiles.WwwRoot, src.TrimStart('/'))), $"Manifest icon {src} is not shipped");
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ServedIndexHtml_ManifestLinkResolvesToAManifestTheCspAllows()
    {
        var client = _factory.CreateClient();

        var page = await client.GetAsync("/");
        page.EnsureSuccessStatusCode();
        var href = ManifestHref(await page.Content.ReadAsStringAsync());
        var csp = page.Headers.GetValues("Content-Security-Policy").First();
        Assert.Contains("manifest-src 'self'", csp, StringComparison.Ordinal);

        var manifestUri = new Uri(client.BaseAddress!, href);
        Assert.Equal(client.BaseAddress!.GetLeftPart(UriPartial.Authority), manifestUri.GetLeftPart(UriPartial.Authority));
        var manifest = await client.GetAsync(manifestUri);

        Assert.Equal(HttpStatusCode.OK, manifest.StatusCode);
        // Browsers parse the manifest as JSON whatever its media type; the
        // static file handler serves it as application/json.
        Assert.Contains(manifest.Content.Headers.ContentType?.MediaType, new[] { "application/json", "application/manifest+json" });
        using var json = JsonDocument.Parse(await manifest.Content.ReadAsStringAsync());
        Assert.Equal("/", json.RootElement.GetProperty("start_url").GetString());
    }
}
