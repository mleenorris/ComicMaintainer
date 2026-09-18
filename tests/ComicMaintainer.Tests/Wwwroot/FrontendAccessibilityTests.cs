using System.Text.RegularExpressions;

namespace ComicMaintainer.Tests.Wwwroot;

/// <summary>
/// Guard rails for the accessibility contracts the shipped static assets rely
/// on. Like the other <c>Wwwroot</c> suites these are text-level assertions on
/// the files as shipped: the markup and scripts are plain static assets with no
/// build step, so a regression here is invisible until someone tries to use the
/// site with a screen reader or without a mouse.
/// </summary>
public class FrontendAccessibilityTests
{
    private static string WwwRoot
    {
        get
        {
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

    private static string Read(params string[] relativeParts)
    {
        var path = Path.Combine(new[] { WwwRoot }.Concat(relativeParts).ToArray());
        Assert.True(File.Exists(path), $"Expected {path} to exist");
        return File.ReadAllText(path);
    }

    [Fact]
    public void IndexHtml_MessageContainerIsALiveRegion()
    {
        var html = Read("index.html");

        // showMessage() appends every notification the library page produces to
        // #messageContainer, including all error reporting. Without a live
        // region none of it is announced, so a screen reader user gets no
        // feedback at all when an action succeeds or fails.
        var container = Regex.Match(html, @"<div id=""messageContainer""[^>]*>");
        Assert.True(container.Success, "index.html must still contain the #messageContainer element.");
        Assert.Contains("aria-live=\"polite\"", container.Value);
        Assert.Contains("role=\"status\"", container.Value);
    }

    [Fact]
    public void MainJs_MarksErrorMessagesAsAlerts()
    {
        var contents = Read("js", "main.js");

        // #messageContainer is polite, which is right for progress chatter but
        // lets a failure queue behind it. Errors must opt into role="alert" so
        // they interrupt instead.
        Assert.Matches(
            new Regex(@"if\s*\(\s*type\s*===\s*'error'\s*\)\s*\{\s*messageEl\.setAttribute\(\s*'role'\s*,\s*'alert'\s*\)"),
            contents);
    }

    [Theory]
    [InlineData("login.html")]
    [InlineData("setup.html")]
    public void StandalonePage_RestoresKeyboardFocusIndicator(string fileName)
    {
        var html = Read(fileName);

        // These pages clear the default focus outline for mouse users. They do
        // not load css/main.css, which is where the global :focus-visible
        // replacement lives, so each must carry its own or keyboard users lose
        // the focus ring on the sign-in form entirely.
        Assert.Contains("outline: none", html);
        Assert.Matches(
            new Regex(@"input:focus-visible[^{]*\{[^}]*outline:\s*2px\s+solid", RegexOptions.Singleline),
            html);
    }

    [Theory]
    [InlineData("login.html")]
    [InlineData("setup.html")]
    public void StandalonePage_ThemeToggleHasAccessibleName(string fileName)
    {
        var html = Read(fileName);

        // The button's only content is an emoji, and `title` is not a reliable
        // accessible name, so it needs an explicit aria-label.
        var toggle = Regex.Match(html, @"<button class=""theme-toggle""[^>]*>");
        Assert.True(toggle.Success, $"{fileName} must still contain the theme toggle button.");
        Assert.Contains("aria-label=", toggle.Value);
    }

    [Theory]
    [InlineData("login.html")]
    [InlineData("setup.html")]
    public void StandalonePage_AnnouncesStatusMessages(string fileName)
    {
        var html = Read(fileName);

        // A rejected sign-in writes to #errorMessage and nothing else, so
        // without role="alert" a screen reader user is given no indication the
        // attempt failed.
        var error = Regex.Match(html, @"<div id=""errorMessage""[^>]*>");
        Assert.True(error.Success, $"{fileName} must still contain #errorMessage.");
        Assert.Contains("role=\"alert\"", error.Value);

        var success = Regex.Match(html, @"<div id=""successMessage""[^>]*>");
        Assert.True(success.Success, $"{fileName} must still contain #successMessage.");
        Assert.Contains("aria-live=\"polite\"", success.Value);
    }

    [Theory]
    [InlineData("login.html")]
    [InlineData("setup.html")]
    public void StandalonePage_RevealsStatusElementBeforeWritingText(string fileName)
    {
        var html = Read(fileName);

        // The elements are display:none until '.show' is added, which keeps
        // them out of the accessibility tree. The class must be added before
        // the text is written, otherwise the content changes while the region
        // is still hidden and may never be announced.
        Assert.Matches(
            new Regex(@"errorElement\.classList\.add\('show'\);\s*errorElement\.textContent\s*="),
            html);
        Assert.Matches(
            new Regex(@"successElement\.classList\.add\('show'\);\s*successElement\.textContent\s*="),
            html);
    }

    [Theory]
    [InlineData("index.html")]
    [InlineData("login.html")]
    [InlineData("setup.html")]
    [InlineData("jobs.html")]
    [InlineData("offline.html")]
    public void Page_ExposesAMainLandmark(string fileName)
    {
        var html = Read(fileName);

        Assert.Matches(new Regex(@"<main[\s>]"), html);
    }

    [Fact]
    public void MainCss_ReducedMotionResetDoesNotFreezeLoadingSpinners()
    {
        var css = Read("css", "main.css");

        // The reduced-motion block zeroes every animation. Spinners are the
        // only indication that a request is in flight — several replace the
        // button label while it runs — so they must be exempted or the user is
        // left with no feedback at all.
        var reducedMotion = Regex.Match(
            css,
            @"@media\s*\(prefers-reduced-motion:\s*reduce\)\s*\{(?:[^{}]|\{[^{}]*\})*\}",
            RegexOptions.Singleline);
        Assert.True(reducedMotion.Success, "css/main.css must still contain a prefers-reduced-motion block.");
        Assert.Contains(".spinner", reducedMotion.Value);
        Assert.Contains("animation-iteration-count: infinite !important", reducedMotion.Value);
    }
}
