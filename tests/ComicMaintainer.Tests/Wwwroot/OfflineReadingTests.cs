using System.Text.RegularExpressions;

namespace ComicMaintainer.Tests.Wwwroot;

/// <summary>
/// Guard rails for offline comic downloads. The feature is split across the
/// shipped static files (the service worker stores and serves the download,
/// the reader page drives it, the offline page lists it), so these text-level
/// assertions exist to stop one half being removed without the other.
/// </summary>
public class OfflineReadingTests
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

    private static string ReadAsset(params string[] segments)
    {
        var path = Path.Join(new[] { WwwRoot }.Concat(segments).ToArray());
        Assert.True(File.Exists(path), $"Expected {path} to exist");
        return File.ReadAllText(path);
    }

    [Fact]
    public void ServiceWorker_UsesDedicatedCacheForDownloadedComics()
    {
        var contents = ReadAsset("sw.js");

        // Downloads must not share the bounded image cache, or a large library
        // would silently evict pages the user deliberately saved.
        Assert.Contains("OFFLINE_CACHE_NAME", contents);
        Assert.DoesNotContain("trimCache(OFFLINE_CACHE_NAME", contents);
    }

    [Fact]
    public void ServiceWorker_PreservesOfflineCacheDuringActivateCleanup()
    {
        var contents = ReadAsset("sw.js");

        // The activate handler deletes every cache sharing the app prefix, so
        // the offline cache must be excluded or an app update would throw away
        // the user's downloads.
        Assert.Contains("cacheName !== OFFLINE_CACHE_NAME", contents);
    }

    [Fact]
    public void ServiceWorker_HandlesDownloadListAndRemoveMessages()
    {
        var contents = ReadAsset("sw.js");

        Assert.Contains("'DOWNLOAD_COMIC'", contents);
        Assert.Contains("'REMOVE_OFFLINE_COMIC'", contents);
        Assert.Contains("'LIST_OFFLINE_COMICS'", contents);
    }

    [Fact]
    public void ServiceWorker_KeepsDownloadsOnLogout()
    {
        var contents = ReadAsset("sw.js");

        // Downloads are an explicit user action, and a sign-out can happen
        // just because a token expired (possibly while offline), so logging
        // out must not silently discard saved comics. Only the opportunistic
        // image cache is purged; downloads go away via REMOVE_OFFLINE_COMIC.
        Assert.DoesNotContain("caches.delete(OFFLINE_CACHE_NAME)", contents);
        var clearHandler = contents[contents.IndexOf("CLEAR_IMAGE_CACHE", StringComparison.Ordinal)..];
        Assert.Contains("caches.delete(IMAGE_CACHE_NAME)", clearHandler);
    }

    [Fact]
    public void ServiceWorker_ServesDownloadedComicsBeforeTheNetwork()
    {
        var contents = ReadAsset("sw.js");

        // A saved comic must read identically offline, so the fetch handler
        // has to consult the offline cache before attempting the network.
        Assert.Contains("offlineKeyForRequest(request, url)", contents);
        Assert.Contains("matchOfflineComic(request, url)", contents);
        // Downloads cover the info payload too: without it the reader cannot
        // even determine the page count while offline.
        Assert.Contains("'/api/comicreader/info'", contents);
    }

    [Fact]
    public void ServiceWorker_CachesReaderDocumentForOfflineNavigation()
    {
        var contents = ReadAsset("sw.js");

        // Navigations are network-first; a successful response is stored so
        // /reader.html?file=... can still be opened with no connection.
        Assert.Contains("DOCUMENT_CACHE_PATHS", contents);
        Assert.Contains("'/reader.html'", contents);
        Assert.Matches(new Regex(@"cacheDocumentResponse\("), contents);
        Assert.Matches(new Regex(@"matchCachedDocument\("), contents);
    }

    [Fact]
    public void Reader_ExposesOfflineDownloadControl()
    {
        var contents = WwwrootFiles.ReadPageWithScript("reader.html");

        Assert.Contains("id=\"offlineBtn\"", contents);
        Assert.Contains("toggleOfflineDownload()", contents);
        Assert.Contains("type: 'DOWNLOAD_COMIC'", contents);
        Assert.Contains("type: 'REMOVE_OFFLINE_COMIC'", contents);
        // Progress feedback comes from the worker via postMessage.
        Assert.Contains("OFFLINE_DOWNLOAD_PROGRESS", contents);
        Assert.Contains("OFFLINE_DOWNLOAD_COMPLETE", contents);
        Assert.Contains("OFFLINE_DOWNLOAD_FAILED", contents);
    }

    [Fact]
    public void Reader_QueuesReadingProgressWhileOffline()
    {
        var contents = WwwrootFiles.ReadPageWithScript("reader.html");

        // Progress POSTs fail outright while offline, so they are parked in
        // localStorage instead of being lost.
        Assert.Contains("PENDING_PROGRESS_KEY", contents);
        Assert.Contains("queuePendingProgress(comicFilePath, pageNum)", contents);
        // A queued page is newer than the server's copy, so it must win when
        // the comic is reopened before the queue has been replayed.
        Assert.Contains("getPendingProgress(comicFilePath)", contents);
    }

    [Fact]
    public void Reader_ReplaysQueuedProgressWhenBackOnline()
    {
        var contents = WwwrootFiles.ReadPageWithScript("reader.html");

        Assert.Contains("flushPendingProgress", contents);
        // Reconnecting must trigger the replay; without this the queue would
        // only drain on the next page turn.
        Assert.Matches(new Regex(@"addEventListener\('online'"), contents);
    }

    [Fact]
    public void OfflinePage_ListsDownloadedComicsWithoutNetworkAccess()
    {
        var page = ReadAsset("offline.html");
        var script = ReadAsset("js", "pages", "offline.js");

        Assert.Contains("LIST_OFFLINE_COMICS", script);
        Assert.Contains("/reader.html?file=", script);
        // The offline page is shown precisely when the network is gone, so it
        // must not fetch anything or load external CSS. Its only script is its
        // own page script, which the service worker precaches with it.
        Assert.DoesNotContain("fetch(", page + script);
        Assert.DoesNotMatch(new Regex(@"<link[^>]+rel=""stylesheet"""), page);
        var scripts = Regex.Matches(page, @"<script\b[^>]*\bsrc=""([^""]+)""")
            .Select(m => m.Groups[1].Value)
            .ToList();
        Assert.Equal(new[] { "/js/pages/offline.js?v=__APP_VERSION__" }, scripts);
        Assert.Matches(new Regex(@"const PAGE_SCRIPTS = \[[^\]]*'/js/pages/offline\.js'"), ReadAsset("sw.js"));
    }
}
