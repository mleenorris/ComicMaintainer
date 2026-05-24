using System.Text.RegularExpressions;

namespace ComicMaintainer.Tests.Wwwroot;

/// <summary>
/// Guards the "user-facing actions audit" outcome: action handlers in
/// <c>main.js</c> must not regress to full-page reloads or hard SPA-to-static
/// navigations after long-running operations. New asynchronous actions should
/// rely on the standard SSE-driven progress modal + targeted in-place
/// refreshes instead.
/// </summary>
public class MainJsSeamlessUpdatesTests
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
    public void MainJs_DoesNotInvokeWindowLocationReload()
    {
        var contents = ReadMainJs();
        // window.location.reload() forces a full-page refresh. User-facing
        // actions must update the UI in place via SSE. The only allowed call
        // is the service-worker `controllerchange` handler, which is needed
        // so a new SW serves the next page load (explicitly out of scope in
        // the audit plan).
        var matches = Regex.Matches(contents, @"window\.location\.reload\s*\(");
        Assert.True(matches.Count <= 1,
            $"main.js contains {matches.Count} window.location.reload() call(s); only the service-worker controllerchange handler is allowed. User-facing actions must update the UI in place via SSE — use showMessage() + the relevant loadXxx() helper instead.");

        if (matches.Count == 1)
        {
            // Verify the single remaining call is the service-worker one.
            var idx = matches[0].Index;
            var windowStart = Math.Max(0, idx - 400);
            var windowEnd = Math.Min(contents.Length, idx + 100);
            var snippet = contents.Substring(windowStart, windowEnd - windowStart);
            Assert.Contains("controllerchange", snippet);
        }
    }

    [Fact]
    public void MainJs_DoesNotHardNavigateToScheduledJobsHtml()
    {
        var contents = ReadMainJs();
        // The Scheduled Jobs page is being migrated to an in-SPA modal.
        // Forbid new hard navigations to /jobs.html from JS handlers so we
        // don't regress.
        var matches = Regex.Matches(contents, @"window\.location\.href\s*=\s*['""]/jobs\.html");
        Assert.True(matches.Count == 0,
            "main.js must not hard-navigate to /jobs.html: open the scheduled-jobs modal in the SPA instead.");
    }

    [Fact]
    public void MainJs_DoesNotUseBlockingAlertForPostActionNotifications()
    {
        // alert() blocks the UI thread and requires a click; user-facing
        // action results should surface through the non-blocking showMessage
        // toast. We allow at most a handful of legacy alert() calls during
        // the migration but assert there's no growth.
        // (Current count after the refactor: 0.)
        var contents = ReadMainJs();
        var matches = Regex.Matches(contents, @"\balert\s*\(");
        Assert.True(matches.Count == 0,
            $"main.js contains {matches.Count} alert() call(s); replace them with showMessage(..., 'info'|'success'|'warning'|'error').");
    }
}
