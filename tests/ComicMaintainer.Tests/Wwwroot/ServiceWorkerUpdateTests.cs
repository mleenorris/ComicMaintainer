using System.Text.RegularExpressions;

namespace ComicMaintainer.Tests.Wwwroot;

/// <summary>
/// Guards the "new version available" banner. Its Reload button used to sit on
/// "Reloading…" for minutes: the service worker proxied the page's SSE stream,
/// a proxied stream never finishes, and the browser will not let a waiting
/// worker take over while the current one still has a response in flight — so
/// SKIP_WAITING never led to 'controllerchange' and the page never reloaded.
/// Text-level assertions on the shipped files, like the other Wwwroot suites.
/// </summary>
public class ServiceWorkerUpdateTests
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

    private static string ReadAsset(params string[] relativePath)
    {
        var path = Path.Combine(new[] { WwwRoot }.Concat(relativePath).ToArray());
        Assert.True(File.Exists(path), $"Expected {path} to exist");
        return File.ReadAllText(path);
    }

    /// <summary>
    /// Returns the source of a top-level function in main.js, from its
    /// declaration up to the next top-level declaration or doc comment.
    /// </summary>
    private static string ExtractMainJsFunction(string contents, string name)
    {
        var start = contents.IndexOf($"function {name}(", StringComparison.Ordinal);
        Assert.True(start >= 0, $"main.js must define {name}()");
        var next = Regex.Match(contents[(start + 1)..], @"\n {8}(?:function |/\*\*|// )");
        return next.Success ? contents.Substring(start, next.Index + 1) : contents[start..];
    }

    [Fact]
    public void ServiceWorker_LeavesEventStreamsToTheBrowser()
    {
        var contents = ReadAsset("sw.js");

        // Returning without respondWith() hands the request back to the
        // browser, so the stream is never in flight on the worker.
        var bypass = Regex.Match(
            contents,
            @"if\s*\(\s*request\.headers\.get\('Accept'\)\?\.includes\('text/event-stream'\)\s*\)\s*\{\s*return;\s*\}");
        Assert.True(bypass.Success,
            "sw.js must not proxy text/event-stream requests: an open stream keeps the worker busy and stops a waiting update from ever taking over.");

        var apiProxy = contents.IndexOf("// Network-first strategy for API calls", StringComparison.Ordinal);
        Assert.True(apiProxy >= 0, "Expected sw.js to proxy GET /api/ requests network-first");
        Assert.True(bypass.Index < apiProxy,
            "The event-stream bypass must run before the /api/ network-first branch, which would otherwise proxy /api/events/stream.");
    }

    [Fact]
    public void ServiceWorker_WaitsForTheUserBeforeTakingOver()
    {
        var contents = ReadAsset("sw.js");

        var install = contents.IndexOf("self.addEventListener('install'", StringComparison.Ordinal);
        var activate = contents.IndexOf("self.addEventListener('activate'", StringComparison.Ordinal);
        Assert.True(install >= 0 && activate > install, "Expected sw.js to register install and then activate handlers");

        // skipWaiting() during install swaps the worker under an open page and
        // reloads it via 'controllerchange' without the user asking.
        var installHandler = contents[install..activate];
        Assert.DoesNotContain("skipWaiting", installHandler);

        // The banner's Reload button is the opt-in, so the worker must still
        // honour it.
        Assert.Matches(
            new Regex(@"event\.data\.type\s*===\s*'SKIP_WAITING'\s*\)\s*\{\s*self\.skipWaiting\(\);"),
            contents);
    }

    [Fact]
    public void MainJs_UpdateBannerReloadAlwaysEndsInAReload()
    {
        var contents = ReadAsset("js", "main.js");

        Assert.Contains("applyServiceWorkerUpdate(worker);", ExtractMainJsFunction(contents, "promptForServiceWorkerUpdate"));

        var apply = ExtractMainJsFunction(contents, "applyServiceWorkerUpdate");

        // Older workers proxy the SSE stream; this tab's connection must be
        // closed or it keeps that worker busy so the new one cannot take over.
        var closeStream = apply.IndexOf("cleanupEventSource();", StringComparison.Ordinal);
        var skipWaiting = apply.IndexOf("postMessage({ type: 'SKIP_WAITING' })", StringComparison.Ordinal);
        Assert.True(closeStream >= 0, "applyServiceWorkerUpdate must close the SSE connection");
        Assert.True(skipWaiting > closeStream, "The SSE connection must be closed before asking the new worker to take over");

        // A worker that is already in control (or was replaced) has nothing left
        // to wait for.
        Assert.Matches(new Regex(@"worker\.state === 'activated' \|\| worker\.state === 'redundant'"), apply);

        // Another tab can still hold the old worker busy, so the button must
        // fall back to reloading rather than wait indefinitely.
        Assert.Matches(
            new Regex(@"setTimeout\(\(\)\s*=>\s*\{\s*reloadForServiceWorkerUpdate\([^)]*\);\s*\},\s*SW_UPDATE_RELOAD_TIMEOUT_MS\)"),
            apply);
    }

    [Fact]
    public void MainJs_ServiceWorkerReloadsShareOneGuard()
    {
        var contents = ReadAsset("js", "main.js");

        // 'controllerchange' and the banner's fallback both reload; routing them
        // through one guarded helper stops a double reload.
        Assert.Matches(
            new Regex(@"addEventListener\('controllerchange',\s*\(\)\s*=>\s*\{\s*reloadForServiceWorkerUpdate\("),
            contents);

        var reload = ExtractMainJsFunction(contents, "reloadForServiceWorkerUpdate");
        Assert.Matches(new Regex(@"if \(serviceWorkerReloadStarted\)\s*(?:return;|\{\s*return;\s*\})\s*serviceWorkerReloadStarted = true;"), reload);
        Assert.Contains("window.location.reload();", reload);
    }
}
