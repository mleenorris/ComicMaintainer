using System.Text.RegularExpressions;

namespace ComicMaintainer.Tests.Wwwroot;

/// <summary>
/// Guard rails for the shared look of the web UI: theme tokens, default button
/// styling, the header and page widths, and the Scheduled Jobs page.
/// Each test pins a specific visual defect that previously shipped. Like the
/// other <c>Wwwroot</c> suites these are text-level assertions on the files as
/// shipped, because the styles are plain static assets with no build step.
/// </summary>
public class FrontendVisualConsistencyTests
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

    private static string DarkThemeBlock(string css)
    {
        var dark = Regex.Match(css, @"\[data-theme=""dark""\]\s*\{[\s\S]*?\n        \}", RegexOptions.Singleline);
        Assert.True(dark.Success, "Could not isolate the dark theme variable block.");
        return dark.Value;
    }

    private static string RuleBody(string css, string selectorPattern)
    {
        var rule = Regex.Match(css, @"(?:^|\n)\s*" + selectorPattern + @"\s*\{([^}]*)\}");
        Assert.True(rule.Success, $"Could not find a rule for {selectorPattern}.");
        return rule.Groups[1].Value;
    }

    [Theory]
    [InlineData("css", "main.css")]
    [InlineData("index.html")]
    [InlineData("app-scripts")]
    public void SpaStyles_CustomPropertiesWithoutAFallbackAreDefined(params string[] file)
    {
        var css = Read("css", "main.css");
        // "app-scripts" stands for every script index.html loads (formerly main.js).
        var source = file is ["app-scripts"] ? WwwrootFiles.ReadAppScripts() : Read(file);

        // var(--name) with no fallback resolves to nothing when --name is never
        // declared. --accent-primary was missing for a long time, which left
        // the active List/Grid/Compact button and selected issues transparent.
        var defined = Regex.Matches(css, @"(--[A-Za-z0-9-]+)\s*:")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
        var undefined = Regex.Matches(source, @"var\((--[A-Za-z0-9-]+)\)")
            .Select(m => m.Groups[1].Value)
            .Where(name => !defined.Contains(name))
            .Distinct()
            .ToList();

        Assert.True(undefined.Count == 0, $"Undefined custom properties used without a fallback: {string.Join(", ", undefined)}");
    }

    [Fact]
    public void MainCss_ThemesDriveNativeControlColours()
    {
        var css = Read("css", "main.css");

        // Without color-scheme, checkboxes, selects and scrollbars stay light
        // on the dark theme.
        Assert.Contains("color-scheme: light", RuleBody(css, @":root"));
        Assert.Contains("color-scheme: dark", DarkThemeBlock(css));
        Assert.Contains("accent-color: var(--accent-primary)", RuleBody(css, @"body"));
    }

    [Fact]
    public void MainCss_HiddenAttributeAlwaysHides()
    {
        var css = Read("css", "main.css");

        // Several components set display:flex on buttons, which beats the user
        // agent's [hidden] rule. Review Duplicates / Combine Folders and the
        // mobile Clear / Email Selected buttons showed while meant to be hidden.
        Assert.Matches(new Regex(@"(?:^|\n)\s*\[hidden\]\s*\{\s*display:\s*none\s*!important;\s*\}"), css);
    }

    [Fact]
    public void MainCss_PlainButtonsHaveAThemedDefault()
    {
        var css = Read("css", "main.css");

        // A .btn without a colour modifier used the browser's default button
        // colours: a bright light-grey slab with black text in dark mode.
        var btn = RuleBody(css, @"\.btn");
        Assert.Contains("background: var(--btn-neutral-bg)", btn);
        Assert.Contains("color: var(--text-primary)", btn);
        Assert.Contains("--btn-neutral-bg", DarkThemeBlock(css));

        // The action dropdown toggles share the default, so one without a colour
        // modifier (Metadata) no longer renders white text on light grey.
        var toggle = RuleBody(css, @"\.action-dropdown-toggle");
        Assert.Contains("background: var(--btn-neutral-bg)", toggle);
        Assert.DoesNotContain("color: white", toggle);
    }

    [Fact]
    public void MainCss_PageContainerFillsTheWidthOfTheHeader()
    {
        var css = Read("css", "main.css");

        // The body is a column flexbox, so without an explicit width the
        // container shrink-wrapped its content and pages were narrower than,
        // and misaligned with, the header.
        Assert.Matches(new Regex(@"(?:^|\n)\s*width:\s*100%;"), RuleBody(css, @"\.container"));
    }

    [Fact]
    public void MainCss_SettingsMenuScrollsWithinTheViewport()
    {
        var css = Read("css", "main.css");

        // The menu is taller than many screens; without a max height its last
        // items (Logout, About) were unreachable.
        var menu = RuleBody(css, @"\.settings-dropdown-menu");
        Assert.Contains("max-height:", menu);
        Assert.Contains("overflow-y: auto", menu);
    }

    [Fact]
    public void MainCss_MobileHeaderSearchShrinksAndAvoidsFocusZoom()
    {
        var css = Read("css", "main.css");

        // The desktop min-width pushed the Sort control off a 390px screen.
        Assert.Matches(new Regex(@"\.header-search\s*\{[^}]*flex:\s*1;[^}]*min-width:\s*0;"), css);

        // iOS Safari zooms into inputs smaller than 16px on focus.
        Assert.Matches(new Regex(@"\.header-search input\s*\{[^}]*width:\s*100%;[^}]*font-size:\s*16px;"), css);
    }

    [Fact]
    public void MainCss_TabletHeaderOverridesApplyAfterDesktopRules()
    {
        var css = Read("css", "main.css");

        Assert.Matches(new Regex(@"\.header-search\s*\{[^}]*min-width:\s*200px;[\s\S]*?@media \(max-width: 1200px\) and \(min-width: 769px\)\s*\{\s*\.header-search\s*\{\s*min-width:\s*140px;"), css);
        Assert.Matches(new Regex(@"\.settings-dropdown-menu\s*\{[^}]*right:\s*0;[\s\S]*?@media \(max-width: 1200px\) and \(min-width: 769px\)\s*\{\s*\.settings-dropdown-menu\s*\{[^}]*right:\s*auto;[^}]*left:\s*0;"), css);
    }

    [Fact]
    public void MainCss_SeriesDetailLookupBadgeFlowsWithTheTitle()
    {
        var css = Read("css", "main.css");

        // The title has no positioned ancestor, so the absolutely positioned
        // badge ended up behind the sticky header.
        Assert.Contains("position: static", RuleBody(css, @"\.series-detail-summary-body h2 \.series-lookup-badge"));
    }

    [Fact]
    public void JobsHtml_OnlyRedirectsToTheThemedAppPage()
    {
        var html = Read("jobs.html");

        // Scheduled Jobs used to be a standalone page that had to re-implement
        // the theme (dark-mode users once landed on a light page). It is a page
        // of index.html now, which themes every page, so jobs.html must only
        // forward old links there rather than render a page of its own.
        Assert.Contains("window.location.replace('/#/jobs')", html);
        Assert.DoesNotContain("<style", html);
        Assert.DoesNotContain("stylesheet", html);
        Assert.DoesNotContain("/api/", html);
    }
}
