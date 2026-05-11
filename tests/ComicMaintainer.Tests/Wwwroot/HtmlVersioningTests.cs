using System.Text.RegularExpressions;

namespace ComicMaintainer.Tests.Wwwroot;

/// <summary>
/// Markup-level guard rails for the cache-busting strategy. The HTML files
/// must reference CSS/JS via versioned URLs (using the __APP_VERSION__
/// placeholder that the server replaces at request time) so the browser never
/// fetches the unversioned URL — otherwise the service worker would cache it
/// indefinitely and force users to hard-refresh to pick up new versions.
/// </summary>
public class HtmlVersioningTests
{
    private static string WwwRoot
    {
        get
        {
            // Walk up from the test binary directory to the repo root, then
            // into the WebApi wwwroot folder.
            var current = AppContext.BaseDirectory;
            for (var i = 0; i < 10 && current != null; i++)
            {
                var candidate = Path.Combine(current, "src", "ComicMaintainer.WebApi", "wwwroot");
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
                current = Path.GetDirectoryName(current);
            }
            throw new DirectoryNotFoundException("Could not locate wwwroot relative to test binaries.");
        }
    }

    [Theory]
    [InlineData("index.html")]
    [InlineData("reader.html")]
    public void HtmlPage_DoesNotReferenceUnversionedMainCssOrJs(string fileName)
    {
        var path = Path.Combine(WwwRoot, fileName);
        Assert.True(File.Exists(path), $"Expected {path} to exist");

        var html = File.ReadAllText(path);

        // Any /css/...css (or /js/...js) URL must carry a ?v= query string,
        // otherwise the browser will hit the unversioned URL which is never
        // invalidated when the app is updated.
        var unversionedCss = new Regex("href\\s*=\\s*[\"']/css/[^\"'?\\s]+\\.css[\"']");
        var unversionedJs = new Regex("src\\s*=\\s*[\"']/js/[^\"'?\\s]+\\.js[\"']");

        Assert.False(
            unversionedCss.IsMatch(html),
            $"{fileName} contains an unversioned /css/*.css reference. " +
            "Use /css/foo.css?v=__APP_VERSION__ so the server can inject the current version.");
        Assert.False(
            unversionedJs.IsMatch(html),
            $"{fileName} contains an unversioned /js/*.js reference. " +
            "Use /js/foo.js?v=__APP_VERSION__ so the server can inject the current version.");
    }

    [Fact]
    public void IndexHtml_UsesAppVersionPlaceholderForMainCssAndMainJs()
    {
        var html = File.ReadAllText(Path.Combine(WwwRoot, "index.html"));

        Assert.Contains("/css/main.css?v=__APP_VERSION__", html);
        Assert.Contains("/js/main.js?v=__APP_VERSION__", html);
    }

    [Fact]
    public void ReaderHtml_UsesAppVersionPlaceholderForMainCss()
    {
        var html = File.ReadAllText(Path.Combine(WwwRoot, "reader.html"));

        Assert.Contains("/css/main.css?v=__APP_VERSION__", html);
    }
}
