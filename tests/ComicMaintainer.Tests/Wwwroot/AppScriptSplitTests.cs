using System.Text.RegularExpressions;

namespace ComicMaintainer.Tests.Wwwroot;

/// <summary>
/// Guards the split of the library SPA into ordered classic scripts. They
/// share one global scope (inline onclick handlers call their functions), so
/// index.html must load every one of them, in the documented order, and the
/// service worker must know about each of them.
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
    public void EveryScriptUnderJs_IsLoadedByIndexHtml()
    {
        var jsRoot = Path.Combine(WwwrootFiles.WwwRoot, "js");
        var onDisk = Directory.EnumerateFiles(jsRoot, "*.js", SearchOption.AllDirectories)
            .Select(path => "js/" + Path.GetRelativePath(jsRoot, path).Replace(Path.DirectorySeparatorChar, '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(WwwrootFiles.AppScripts.OrderBy(path => path, StringComparer.Ordinal), onDisk);
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
    public void AppScripts_DoNotDeclareATopLevelFunctionTwice()
    {
        // Across classic scripts a later declaration silently replaces an
        // earlier one, so a duplicate would make behaviour depend on load order.
        // Top-level code is indented by 0 (shared.js) or 8 spaces (the rest).
        var duplicates = WwwrootFiles.AppScripts
            .SelectMany(script => Regex.Matches(
                    WwwrootFiles.Read(script),
                    @"^(?: {8})?(?:async\s+)?function\s+([A-Za-z_$][\w$]*)\s*\(",
                    RegexOptions.Multiline)
                .Select(m => m.Groups[1].Value))
            .GroupBy(name => name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        Assert.True(duplicates.Count == 0, $"Functions declared more than once: {string.Join(", ", duplicates)}");
    }

    [Fact]
    public void ReaderHtml_LoadsSharedHelpersBeforeItsInlineScript()
    {
        var reader = WwwrootFiles.Read("reader.html");

        var shared = reader.IndexOf("<script src=\"/js/shared.js?v=__APP_VERSION__\"></script>", StringComparison.Ordinal);
        var inline = reader.IndexOf("<script>", StringComparison.Ordinal);
        Assert.True(shared >= 0, "reader.html must load /js/shared.js");
        Assert.True(inline > shared, "/js/shared.js must load before reader.html's inline script uses its helpers");

        // The helpers live in shared.js only; a local copy would shadow it.
        Assert.DoesNotContain("function escapeHtml(", reader);
        Assert.DoesNotContain("function isAutheliaAuthenticated(", reader);
        Assert.DoesNotContain("function buildCredentialedFetchOptions(", reader);
        Assert.Contains("return buildCredentialedFetchOptions(extra, token);", reader);
    }
}
