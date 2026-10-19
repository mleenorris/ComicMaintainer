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
        var js = Read("js", "main.js");

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
        var js = Read("js", "main.js");

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
        var js = Read("js", "main.js");

        Assert.Contains("const PAGE_TITLES = { home: 'Overview', library: 'Library', settings: 'Settings' };", js);
        Assert.Matches(@"applyViewVisibility\(route\.view\);\s*enterPage\(route\.view\);", js);
        // A series page belongs to the library so returning keeps its scroll.
        Assert.Contains("const page = view === 'series' ? 'library' : view;", js);
    }
}
