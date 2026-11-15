using System.Text.RegularExpressions;

namespace ComicMaintainer.Tests.Wwwroot;

/// <summary>
/// Guard rails for the service worker's caching strategy. <c>sw.js</c> is
/// plain JavaScript shipped as a static asset, so these are text-level
/// assertions on the shipped file: they exist to stop the offline/PWA
/// behaviour silently regressing (for example by dropping the offline page
/// from the precache list, or by letting a write request be served from a
/// cache).
/// </summary>
public class ServiceWorkerCachingTests
{
    private static string WwwRoot
    {
        get
        {
            var current = AppContext.BaseDirectory;
            for (var i = 0; i < 10 && current != null; i++)
            {
                var candidate = Path.Join(current, "src", "ComicMaintainer.WebApi", "wwwroot");
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
                current = Path.GetDirectoryName(current);
            }
            throw new DirectoryNotFoundException("Could not locate wwwroot relative to test binaries.");
        }
    }

    private static string ReadServiceWorker()
    {
        var path = Path.Join(WwwRoot, "sw.js");
        Assert.True(File.Exists(path), $"Expected {path} to exist");
        return File.ReadAllText(path);
    }

    [Fact]
    public void ServiceWorker_PrecachesOfflinePage()
    {
        var contents = ReadServiceWorker();

        Assert.Contains("'/offline.html'", contents);
        Assert.True(
            File.Exists(Path.Join(WwwRoot, "offline.html")),
            "sw.js precaches /offline.html, so wwwroot/offline.html must exist or installation will fail " +
            "(cache.addAll rejects when any entry 404s, which would disable the service worker entirely).");
    }

    [Fact]
    public void ServiceWorker_UsesSeparateBoundedImageCache()
    {
        var contents = ReadServiceWorker();

        // Covers and pages live in caches of their own so they survive version bumps,
        // and each must be bounded so a large library cannot fill up storage.
        Assert.Matches(new Regex(@"cacheName:\s*IMAGE_CACHE_NAME,\s*maxEntries:\s*MAX_IMAGE_CACHE_ENTRIES"), contents);
        Assert.Matches(new Regex(@"cacheName:\s*PAGE_CACHE_NAME,\s*maxEntries:\s*MAX_PAGE_CACHE_ENTRIES"), contents);
        Assert.Matches(new Regex(@"trimCache\(\s*cacheName\s*,\s*maxEntries\s*\)"), contents);
    }

    [Fact]
    public void ServiceWorker_PreservesImageCacheDuringActivateCleanup()
    {
        var contents = ReadServiceWorker();

        // The activate handler deletes every cache sharing the app prefix. The
        // image cache uses that same prefix, so it must be explicitly excluded
        // or every release would throw away the whole cover cache.
        Assert.Contains("cacheName !== IMAGE_CACHE_NAME", contents);
        Assert.Contains("cacheName !== PAGE_CACHE_NAME", contents);
    }

    [Fact]
    public void ServiceWorker_DoesNotInterceptApiWrites()
    {
        var contents = ReadServiceWorker();

        // Mutations must reach the network untouched; serving or synthesising a
        // response for them would make a failed write look like a success.
        Assert.Matches(
            new Regex(@"path\.startsWith\('/api/'\)\s*&&\s*request\.method\s*!==\s*'GET'"),
            contents);
    }

    [Fact]
    public void ServiceWorker_ClearsImageCacheOnRequest()
    {
        var swContents = ReadServiceWorker();
        var mainJs = WwwrootFiles.ReadAppScripts();

        // Cached covers/pages are authenticated content, so signing out must
        // purge them. Both halves of the contract are asserted so neither side
        // can be removed on its own.
        Assert.Contains("CLEAR_IMAGE_CACHE", swContents);
        Assert.Contains("caches.delete(IMAGE_CACHE_NAME)", swContents);
        Assert.Contains("CLEAR_IMAGE_CACHE", mainJs);
    }
}
