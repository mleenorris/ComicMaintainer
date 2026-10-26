using System.Text.RegularExpressions;

namespace ComicMaintainer.Tests.Wwwroot;

/// <summary>
/// Guards the split of the library SPA into ordered classic scripts. They
/// share one global scope (inline onclick handlers call their functions), so
/// index.html must load every one of them, in the documented order, and the
/// service worker must know about each of them. The standalone pages
/// (reader, login, setup, offline) each load their own page script from
/// js/pages/ instead of carrying inline code.
/// </summary>
public class AppScriptSplitTests
{
    private static readonly Regex ScriptTag =
        new("<script\\s+src=\"/(js/[^\"?]+\\.js)\\?v=__APP_VERSION__\"\\s*></script>");

    [Fact]
    public void IndexHtml_LoadsEveryAppScriptInOrder()
    {
        var html = WwwrootFiles.Read("index.html");

        var loaded = ScriptTag.Matches(html).Select(m => m.Groups[1].Value).ToList();

        Assert.Equal(WwwrootFiles.AppScripts, loaded);
    }

    [Fact]
    public void IndexHtml_LoadsSharedHelpersFirstAndBootstrapLast()
    {
        Assert.Equal("js/shared.js", WwwrootFiles.AppScripts[0]);
        Assert.Equal("js/app/templating.js", WwwrootFiles.AppScripts[1]);
        Assert.Equal("js/main.js", WwwrootFiles.AppScripts[^1]);

        // The scripts are classic scripts sharing the global scope. A module
        // gets its own scope (breaking inline handlers) and defer/async would
        // change when the bootstrap runs relative to DOMContentLoaded.
        var html = WwwrootFiles.Read("index.html");
        Assert.DoesNotMatch(new Regex("<script[^>]+(type=\"module\"|\\sdefer|\\sasync)[^>]*src=\"/js/"), html);
        Assert.DoesNotMatch(new Regex("<script[^>]+src=\"/js/[^>]+(type=\"module\"|\\sdefer|\\sasync)"), html);
    }

    [Fact]
    public void EveryScriptUnderJs_IsAnAppScriptOrAPageScript()
    {
        var jsRoot = Path.Combine(WwwrootFiles.WwwRoot, "js");
        var onDisk = Directory.EnumerateFiles(jsRoot, "*.js", SearchOption.AllDirectories)
            .Select(path => "js/" + Path.GetRelativePath(jsRoot, path).Replace(Path.DirectorySeparatorChar, '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        // Everything outside js/pages/ is part of the SPA and must be loaded by
        // index.html; each script in js/pages/ belongs to one standalone page.
        Assert.Equal(
            WwwrootFiles.AppScripts.Concat(WwwrootFiles.PageScripts.Values).OrderBy(path => path, StringComparer.Ordinal),
            onDisk);
        Assert.All(WwwrootFiles.AppScripts, script => Assert.False(script.StartsWith("js/pages/", StringComparison.Ordinal)));
        Assert.All(WwwrootFiles.PageScripts.Values, script => Assert.StartsWith("js/pages/", script));
    }

    public static TheoryData<string> StandalonePages => new(WwwrootFiles.PageScripts.Keys);

    [Theory]
    [MemberData(nameof(StandalonePages))]
    public void StandalonePage_LoadsItsPageScriptLastAndHasNoInlineScript(string page)
    {
        var html = WwwrootFiles.Read(page);
        var pageScript = WwwrootFiles.PageScripts[page];

        // The page script runs straight away and looks up the page's markup, so
        // it must come after all of it, at the end of <body>.
        var tag = $"<script src=\"/{pageScript}?v=__APP_VERSION__\"></script>";
        Assert.Matches(new Regex(Regex.Escape(tag) + @"\s*</body>\s*</html>\s*$"), html);
        Assert.Equal(pageScript, ScriptTag.Matches(html).Select(m => m.Groups[1].Value).LastOrDefault());

        // All of the page's code lives in js/ where it is measured and tested;
        // a new inline block would silently escape both.
        Assert.DoesNotMatch(new Regex(@"<script(?![^>]*\bsrc=)[^>]*>"), html);

        // A classic script, like the app scripts: the markup's inline on*
        // handlers call its top-level functions.
        Assert.DoesNotMatch(new Regex("<script[^>]+(type=\"module\"|\\sdefer|\\sasync)[^>]*src=\"/js/"), html);
        Assert.DoesNotMatch(new Regex("<script[^>]+src=\"/js/[^>]+(type=\"module\"|\\sdefer|\\sasync)"), html);
    }

    [Theory]
    [MemberData(nameof(StandalonePages))]
    public void StandalonePage_ScriptsDoNotDeclareATopLevelFunctionTwice(string page)
    {
        var scripts = ScriptTag.Matches(WwwrootFiles.Read(page)).Select(m => m.Groups[1].Value);

        var duplicates = DuplicateTopLevelFunctions(scripts);

        Assert.True(duplicates.Count == 0, $"{page}: functions declared more than once: {string.Join(", ", duplicates)}");
    }

    [Fact]
    public void ServiceWorker_PrecachesEveryAppScriptInLoadOrder()
    {
        var sw = WwwrootFiles.Read("sw.js");

        var list = Regex.Match(sw, @"const APP_SCRIPTS = \[(.*?)\];", RegexOptions.Singleline);
        Assert.True(list.Success, "sw.js must declare the APP_SCRIPTS precache list");

        var precached = Regex.Matches(list.Groups[1].Value, @"'/(js/[^']+\.js)'")
            .Select(m => m.Groups[1].Value)
            .ToList();
        Assert.Equal(WwwrootFiles.AppScripts, precached);

        // Precached under the same versioned URL the page requests, and added
        // during install.
        Assert.Contains("${script}?v=${APP_VERSION}", sw);
        Assert.Contains("precacheAppScripts(cache)", sw);
    }

    [Fact]
    public void ServiceWorker_PrecachesThePageScriptsOfDocumentsItServesOffline()
    {
        var sw = WwwrootFiles.Read("sw.js");

        var list = Regex.Match(sw, @"const PAGE_SCRIPTS = \[(.*?)\];", RegexOptions.Singleline);
        Assert.True(list.Success, "sw.js must declare the PAGE_SCRIPTS precache list");
        var precached = Regex.Matches(list.Groups[1].Value, @"'/(js/[^']+\.js)'")
            .Select(m => m.Groups[1].Value)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        // A document served from the cache while offline (a cached navigation
        // or the offline.html fallback) is useless without its page script.
        var documents = Regex.Match(sw, @"const DOCUMENT_CACHE_PATHS = \[(.*?)\];", RegexOptions.Singleline);
        Assert.True(documents.Success, "sw.js must declare DOCUMENT_CACHE_PATHS");
        var expected = Regex.Matches(documents.Groups[1].Value, @"'/([^'/]+\.html)'")
            .Select(m => m.Groups[1].Value)
            .Where(WwwrootFiles.PageScripts.ContainsKey)
            .Select(page => WwwrootFiles.PageScripts[page])
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
        Assert.Contains("js/pages/offline.js", expected);
        Assert.Equal(expected, precached);

        // Precached with the app scripts, under the same versioned URL.
        Assert.Contains("[...APP_SCRIPTS, ...PAGE_SCRIPTS].map((script) =>", sw);
    }

    [Fact]
    public void AppScripts_DoNotDeclareATopLevelFunctionTwice()
    {
        var duplicates = DuplicateTopLevelFunctions(WwwrootFiles.AppScripts);

        Assert.True(duplicates.Count == 0, $"Functions declared more than once: {string.Join(", ", duplicates)}");
    }

    // Across classic scripts a later declaration silently replaces an earlier
    // one, so a duplicate would make behaviour depend on load order. Top-level
    // code is indented by 0 (shared.js) or 8 spaces (the rest).
    private static List<string> DuplicateTopLevelFunctions(IEnumerable<string> scripts) =>
        scripts
            .SelectMany(script => Regex.Matches(
                    WwwrootFiles.Read(script),
                    @"^(?: {8})?(?:async\s+)?function\s+([A-Za-z_$][\w$]*)\s*\(",
                    RegexOptions.Multiline)
                .Select(m => m.Groups[1].Value))
            .GroupBy(name => name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

    [Fact]
    public void ReaderHtml_LoadsSharedHelpersBeforeItsPageScript()
    {
        var reader = WwwrootFiles.Read("reader.html");
        var script = WwwrootFiles.Read(WwwrootFiles.PageScripts["reader.html"]);

        var shared = reader.IndexOf("<script src=\"/js/shared.js?v=__APP_VERSION__\"></script>", StringComparison.Ordinal);
        var page = reader.IndexOf("<script src=\"/js/pages/reader.js?v=__APP_VERSION__\"></script>", StringComparison.Ordinal);
        Assert.True(shared >= 0, "reader.html must load /js/shared.js");
        Assert.True(page > shared, "/js/shared.js must load before reader.html's page script uses its helpers");

        // The helpers live in shared.js only; a local copy would shadow it.
        Assert.DoesNotContain("function escapeHtml(", script);
        Assert.DoesNotContain("function isAutheliaAuthenticated(", script);
        Assert.DoesNotContain("function buildCredentialedFetchOptions(", script);
        Assert.Contains("return buildCredentialedFetchOptions(extra, token);", script);
    }
}
