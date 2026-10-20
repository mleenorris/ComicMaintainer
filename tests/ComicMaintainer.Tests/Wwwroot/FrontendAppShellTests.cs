using System.Text.RegularExpressions;

namespace ComicMaintainer.Tests.Wwwroot;

/// <summary>
/// Guard rails for the app shell introduced by the UI overhaul: the desktop
/// sidebar, the Settings page route and the inline SVG icon sprite. Like the
/// other <c>Wwwroot</c> suites these are text-level assertions on the shipped
/// static files.
/// </summary>
public class FrontendAppShellTests
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

    private static string Read(params string[] relativeParts)
    {
        var path = Path.Join(new[] { WwwRoot }.Concat(relativeParts).ToArray());
        Assert.True(File.Exists(path), $"Expected {path} to exist");
        return File.ReadAllText(path);
    }

    private static HashSet<string> SpriteSymbols(string indexHtml) =>
        Regex.Matches(indexHtml, @"<symbol id=""(i-[a-z0-9-]+)""")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void IndexHtml_EveryIconReferenceHasASpriteSymbol()
    {
        // A <use> pointing at a missing symbol renders nothing, silently
        // leaving an empty gap where the icon should be.
        var html = Read("index.html");
        var symbols = SpriteSymbols(html);
        Assert.NotEmpty(symbols);

        var missing = Regex.Matches(html, @"<use href=""#(i-[a-z0-9-]+)""")
            .Select(m => m.Groups[1].Value)
            .Where(id => !symbols.Contains(id))
            .Distinct()
            .ToList();

        Assert.True(missing.Count == 0, "Icons referenced without a sprite symbol: " + string.Join(", ", missing));
    }

    [Fact]
    public void MainJs_EveryUiIconNameHasASpriteSymbol()
    {
        var symbols = SpriteSymbols(Read("index.html"));
        var js = WwwrootFiles.ReadAppScripts();

        var missing = Regex.Matches(js, @"uiIcon\('([a-z0-9-]+)'\)")
            .Select(m => "i-" + m.Groups[1].Value)
            .Where(id => !symbols.Contains(id))
            .Distinct()
            .ToList();

        Assert.True(missing.Count == 0, "uiIcon() names without a sprite symbol: " + string.Join(", ", missing));
    }

    [Fact]
    public void IndexHtml_HasLabelledSidebarNavigation()
    {
        var html = Read("index.html");

        Assert.Matches(@"<aside class=""app-sidebar"" id=""appSidebar""", html);
        Assert.Matches(@"<nav class=""sidebar-nav"" aria-label=""[^""]+""", html);
        foreach (var id in new[] { "sidebarOverviewBtn", "sidebarLibraryBtn", "sidebarSettingsBtn" })
        {
            Assert.Contains($"id=\"{id}\"", html);
        }
    }

    [Fact]
    public void Sidebar_PreservesWatcherAnnouncementsAndInstallAction()
    {
        var html = Read("index.html");
        var js = WwwrootFiles.ReadAppScripts();

        Assert.Matches(
            @"id=""watcherStatusSidebar""[^>]*role=""status""[^>]*aria-live=""polite""",
            html);
        Assert.Contains("id=\"installAppSidebarButton\"", html);
        Assert.Contains("'installAppButton', 'installAppSidebarButton'", js);
        Assert.Contains("setInstallAppButtonsVisible(true)", js);
        Assert.Contains("setInstallAppButtonsVisible(false)", js);
    }

    [Fact]
    public void Settings_RemainsUnavailableUntilLoadSucceedsAndCanBeRetried()
    {
        var html = Read("index.html");
        var js = WwwrootFiles.ReadAppScripts();

        Assert.Contains("id=\"settingsLoadStatus\"", html);
        Assert.Contains("id=\"settingsRetryButton\" onclick=\"loadSettings()\"", html);
        Assert.Contains("el.disabled = !settingsLoaded || (!isUserSetting && !userCapabilities.canAdminister);", js);
        Assert.Contains("button.disabled = !settingsLoaded || !userCapabilities.canAdminister;", js);
        Assert.Contains("if (!settingsLoaded || !userCapabilities.canAdminister) return;", js);
        Assert.Contains("setSettingsLoadStatus('error'", js);
        Assert.Contains("settingsLoaded = true;", js);
    }

    [Fact]
    public void Settings_SectionObserverUsesViewportHeightAndRebuildsAfterResize()
    {
        var js = WwwrootFiles.ReadAppScripts();

        Assert.Contains("onViewportResize(updateSettingsNavActive);", js);
        Assert.Contains("const rootHeight = document.documentElement.clientHeight;", js);
        Assert.Contains("settingsSectionObserverHeight === rootHeight", js);
        Assert.Contains("`${-Math.round(rootHeight * 0.15)}px 0px ${-Math.round(rootHeight * 0.70)}px 0px`", js);
    }

    [Fact]
    public void Settings_SaveRequestsIncludeAuthenticationHeaders()
    {
        var js = WwwrootFiles.ReadAppScripts();

        foreach (var path in new[]
        {
            "/api/settings/filename-format",
            "/api/settings/log-max-bytes",
            "/api/settings/issue-number-padding",
            "/api/settings/database-cleanup-interval-hours",
            "/api/settings/external-series-metadata"
        })
        {
            var requestStart = js.IndexOf($"fetch(apiUrl('{path}')", StringComparison.Ordinal);
            Assert.True(requestStart >= 0, $"Expected a save request for {path}");

            var requestEnd = js.IndexOf("});", requestStart, StringComparison.Ordinal);
            Assert.True(requestEnd > requestStart, $"Could not find the end of the save request for {path}");
            Assert.Contains("...getAuthHeaders()", js[requestStart..requestEnd]);
        }
    }

    [Fact]
    public void MainCss_SidebarGridIsScopedToTheAppShell()
    {
        // jobs.html and reader.html load main.css too; an unscoped body grid
        // squeezed their content into the empty sidebar column.
        var css = Read("css", "main.css");
        Assert.Contains("body.app-shell {", css);
        Assert.DoesNotMatch(@"\n\s*body\s*\{\s*display:\s*grid", css);
        Assert.Contains("<body class=\"app-shell\">", Read("index.html"));
    }

    [Fact]
    public void Settings_IsARoutedPageRatherThanAModal()
    {
        var html = Read("index.html");
        var js = WwwrootFiles.ReadAppScripts();

        Assert.DoesNotContain("id=\"settingsModal\"", html);
        Assert.DoesNotContain("settingsModal", js);

        // The page lives inside the <main> landmark so it is reachable by
        // the skip link and screen-reader landmark navigation.
        var main = Regex.Match(html, @"<main\b[\s\S]*?</main>");
        Assert.True(main.Success, "index.html has no <main> element.");
        Assert.Contains("id=\"settingsView\"", main.Value);

        Assert.Contains("if (raw === '/settings') return { view: 'settings' };", js);
        Assert.Matches(@"function openSettings\(\)\s*\{\s*navigate\('#/settings'\);", js);
    }

    [Fact]
    public void Router_TreatsOverviewLibraryAndSettingsAsSeparatePages()
    {
        var js = WwwrootFiles.ReadAppScripts();

        Assert.Contains("const PAGE_TITLES = { home: 'Overview', library: 'Library', settings: 'Settings' };", js);
        Assert.Matches(@"applyViewVisibility\(route\.view\);\s*enterPage\(route\.view\);", js);
        // A series page belongs to the library so returning keeps its scroll.
        Assert.Contains("const page = view === 'series' ? 'library' : view;", js);
    }
}
