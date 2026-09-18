using System.Text.RegularExpressions;

namespace ComicMaintainer.Tests.Wwwroot;

/// <summary>
/// Guard rails for the front end's resilience and resource behaviour. These are
/// text-level assertions on <c>main.js</c> as shipped, in the same style as the
/// other <c>Wwwroot</c> suites: the file is a plain static asset with no build
/// step, so these contracts have no other place to be enforced.
/// </summary>
public class MainJsResilienceTests
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

    private static string ReadMainJs()
    {
        var path = Path.Combine(WwwRoot, "js", "main.js");
        Assert.True(File.Exists(path), $"Expected {path} to exist");
        return File.ReadAllText(path);
    }

    [Fact]
    public void MainJs_ReportsOfflineStateToTheUser()
    {
        var contents = ReadMainJs();

        // A fetch with no network rejects with a bare "Failed to fetch", which
        // the UI surfaces as a generic failure that reads like a server fault.
        // The banner states the real cause and is removed when connectivity
        // returns, so both listeners must stay wired up.
        Assert.Matches(new Regex(@"addEventListener\('offline',"), contents);
        Assert.Matches(new Regex(@"addEventListener\('online',"), contents);
        Assert.Contains("appOfflineBanner", contents);
    }

    [Fact]
    public void MainJs_ReportsUnhandledErrorsToTheUser()
    {
        var contents = ReadMainJs();

        // Without these, an uncaught rejection leaves spinners spinning and
        // stale data on screen with nothing to explain why.
        Assert.Matches(new Regex(@"addEventListener\('unhandledrejection',"), contents);
        Assert.Contains("reportUnexpectedError", contents);
    }

    [Fact]
    public void MainJs_RateLimitsUnexpectedErrorReports()
    {
        var contents = ReadMainJs();

        // A failure inside a render or loop path can fire continuously; without
        // a rate limit the resulting banners would bury the rest of the UI.
        Assert.Contains("UNEXPECTED_ERROR_REPORT_INTERVAL_MS", contents);
        Assert.Matches(
            new Regex(@"now\s*-\s*lastUnexpectedErrorReport\s*<\s*UNEXPECTED_ERROR_REPORT_INTERVAL_MS"),
            contents);
    }

    [Fact]
    public void MainJs_DebouncesViewportResizeWork()
    {
        var contents = ReadMainJs();

        // Resize fires continuously while a window is dragged. The handlers
        // evaluate media queries and write to the DOM, so they share one
        // debounced listener; registering a raw one re-introduces the per-event
        // style/layout work this replaced.
        Assert.Contains("onViewportResize", contents);

        var rawListeners = Regex.Matches(contents, @"addEventListener\(\s*'resize'");
        Assert.True(
            rawListeners.Count == 1,
            $"main.js registers {rawListeners.Count} raw 'resize' listeners; expected exactly one (the shared debounced dispatcher). " +
            "Register additional resize work with onViewportResize() instead.");
    }

    [Fact]
    public void MainJs_SkipsServiceWorkerUpdatePollingWhileHidden()
    {
        var contents = ReadMainJs();

        // The poll runs for the lifetime of the page. A background tab has
        // nobody to show an update banner to, so it must not keep issuing a
        // request a minute; returning to the tab checks immediately instead.
        Assert.Matches(
            new Regex(@"const checkForUpdate = \(\) => \{\s*if \(document\.hidden\) return;"),
            contents);
        Assert.Matches(
            new Regex(@"addEventListener\('visibilitychange',[\s\S]{0,60}?if \(!document\.hidden\) checkForUpdate\(\);"),
            contents);
    }
}
