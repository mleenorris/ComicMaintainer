using System.Text.RegularExpressions;

namespace ComicMaintainer.Tests.Wwwroot;

/// <summary>
/// Guard rails for the library-page usability and observability affordances:
/// the keyboard shortcuts, the "?" help overlay, the search clear button, the
/// System Diagnostics panel and the correlation reference attached to error
/// toasts. Like the other <c>Wwwroot</c> suites these are text-level assertions
/// on the files as shipped, because the markup and scripts are plain static
/// assets with no build step to catch a regression.
/// </summary>
public class FrontendUsabilityTests
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

    private static string Read(params string[] relativeParts)
    {
        var path = Path.Combine(new[] { WwwRoot }.Concat(relativeParts).ToArray());
        Assert.True(File.Exists(path), $"Expected {path} to exist");
        return File.ReadAllText(path);
    }

    [Fact]
    public void IndexHtml_HeaderSearchHasAccessibleClearButton()
    {
        var html = Read("index.html");

        var button = Regex.Match(html, "<button[^>]*id=\"headerSearchClear\"[^>]*>", RegexOptions.IgnoreCase);
        Assert.True(button.Success, "Expected a #headerSearchClear button inside the header search.");

        // The button's only visible content is a "×" glyph, so the accessible
        // name has to come from aria-label or screen reader users hear nothing.
        Assert.Contains("aria-label=\"Clear search\"", button.Value);

        // It must start hidden: an always-visible clear button on an empty box
        // is a dead control that just eats space in the header.
        Assert.Contains("hidden", button.Value);
        Assert.Contains("onclick=\"clearHeaderSearch()\"", button.Value);
    }

    [Fact]
    public void MainCss_HiddenSettingsMenuItemsAreActuallyHidden()
    {
        var css = Read("css", "main.css");

        // .settings-dropdown-item sets display:flex, which beats the user agent
        // [hidden] { display: none } rule. Without an explicit override the
        // admin-only Diagnostics entry would stay visible for everyone.
        Assert.Matches(
            new Regex(@"\.settings-dropdown-item\[hidden\]\s*\{[^}]*display:\s*none", RegexOptions.Singleline),
            css);
    }

    [Fact]
    public void IndexHtml_KeyboardShortcutsModalIsADialog()
    {
        var html = Read("index.html");

        var modal = Regex.Match(html, "<div[^>]*id=\"keyboardShortcutsModal\"[^>]*>", RegexOptions.IgnoreCase);
        Assert.True(modal.Success, "Expected a #keyboardShortcutsModal element.");
        Assert.Contains("role=\"dialog\"", modal.Value);
        Assert.Contains("aria-modal=\"true\"", modal.Value);
        Assert.Contains("aria-labelledby=\"keyboardShortcutsModalTitle\"", modal.Value);

        // The overlay is the only place the bindings are documented, so it has
        // to actually list them.
        var body = html[modal.Index..];
        foreach (var key in new[] { "/", "?", "r", "f", "g" })
        {
            Assert.Contains($"<kbd>{key}</kbd>", body);
        }
    }

    [Fact]
    public void IndexHtml_DiagnosticsModalIsADialogWithALiveRegion()
    {
        var html = Read("index.html");

        var modal = Regex.Match(html, "<div[^>]*id=\"diagnosticsModal\"[^>]*>", RegexOptions.IgnoreCase);
        Assert.True(modal.Success, "Expected a #diagnosticsModal element.");
        Assert.Contains("role=\"dialog\"", modal.Value);
        Assert.Contains("aria-modal=\"true\"", modal.Value);

        // Content is swapped in asynchronously after the fetch resolves; without
        // a live region a screen reader user never learns it arrived.
        Assert.Matches(
            new Regex("id=\"diagnosticsContent\"[^>]*aria-live=\"polite\"", RegexOptions.IgnoreCase),
            html);

        Assert.Contains("id=\"copyDiagnosticsBtn\"", html);
    }

    [Fact]
    public void IndexHtml_DiagnosticsMenuItemIsIdentifiableForCapabilityGating()
    {
        var html = Read("index.html");
        var js = Read("js", "main.js");

        Assert.Contains("id=\"diagnosticsMenuItem\"", html);

        // /api/diagnostics is admin-only; a non-admin clicking the entry would
        // only ever get a 403, so applyCapabilities has to hide it.
        Assert.Matches(
            new Regex(@"diagnosticsMenuItem[\s\S]{0,400}?canAdminister", RegexOptions.Singleline),
            js);
    }

    [Fact]
    public void MainJs_ErrorMessagesCarryTheApiFailureReference()
    {
        var js = Read("js", "main.js");

        // An error a user has to report is only actionable if it can be tied
        // back to a server log line, so errors go through the helper that
        // appends the correlation id of the call that just failed.
        Assert.Contains("type === 'error' ? withApiFailureReference(message) : message", js);
        Assert.Contains("function withApiFailureReference(", js);
        Assert.Contains("function recordApiFailure(", js);

        // Expired references would be worse than none: they would point the
        // reader at an unrelated request.
        Assert.Contains("API_FAILURE_REFERENCE_WINDOW_MS", js);

        // 401/403 are routine (expired token, non-admin probing an admin route)
        // and must not overwrite the reference for a real failure.
        Assert.Matches(
            new Regex(@"function recordApiFailure\([\s\S]{0,600}?401[\s\S]{0,80}?403", RegexOptions.Singleline),
            js);
    }

    [Fact]
    public void MainJs_LibraryShortcutsStayOutOfTheWayOfTypingAndDialogs()
    {
        var js = Read("js", "main.js");

        Assert.Contains("function handleLibraryShortcut(", js);
        Assert.Contains("document.addEventListener('keydown', handleLibraryShortcut)", js);

        var handler = Regex.Match(
            js,
            @"function handleLibraryShortcut\(event\)\s*\{[\s\S]*?\n        \}",
            RegexOptions.Singleline);
        Assert.True(handler.Success, "Could not isolate handleLibraryShortcut for inspection.");

        // A bare letter must be typed, not interpreted, while a field has focus.
        Assert.Contains("isTypingTarget(event.target)", handler.Value);

        // An open dialog owns the keyboard, including Escape (handleModalKeydown).
        Assert.Contains("getTopmostVisibleModal()", handler.Value);

        // Browser/OS chords such as Ctrl+F must keep working.
        Assert.Contains("event.ctrlKey || event.metaKey || event.altKey", handler.Value);
    }

    [Fact]
    public void MainJs_LibraryOnlyShortcutsAreInertWhenTheirControlIsHidden()
    {
        var js = Read("js", "main.js");

        // The header search and filter are display:none on the Overview route,
        // so "/", "r" and "f" would otherwise swallow the keystroke and focus
        // or click nothing at all.
        Assert.Contains("function isShortcutControlAvailable(", js);

        var handler = Regex.Match(
            js,
            @"function handleLibraryShortcut\(event\)\s*\{[\s\S]*?\n        \}",
            RegexOptions.Singleline);
        Assert.True(handler.Success, "Could not isolate handleLibraryShortcut for inspection.");

        Assert.Equal(3, Regex.Matches(handler.Value, @"isShortcutControlAvailable\(").Count);
    }

    [Fact]
    public void MainJs_ClearingTheSearchAlsoRefreshesTheList()
    {
        var js = Read("js", "main.js");

        Assert.Contains("function clearHeaderSearch(", js);
        Assert.Contains("function syncHeaderSearchClear(", js);

        var clear = Regex.Match(
            js,
            @"function clearHeaderSearch\(\)\s*\{[\s\S]*?\n        \}",
            RegexOptions.Singleline);
        Assert.True(clear.Success, "Could not isolate clearHeaderSearch for inspection.");

        // Emptying the box without re-filtering would leave the user staring at
        // the results of a query that is no longer on screen.
        Assert.Contains("filterFiles", clear.Value);

        // Typing has to keep the button's visibility in sync with the value.
        Assert.Matches(
            new Regex(@"function debouncedFilterFiles\(\)\s*\{[\s\S]*?syncHeaderSearchClear\(\)", RegexOptions.Singleline),
            js);
    }

    [Fact]
    public void MainCss_DiagnosticsStatusPillIsThemeAware()
    {
        var css = Read("css", "main.css");

        // Hard-coded dark greens/reds fall below AA contrast on the dark
        // surface, so the pill text colours come from theme variables.
        Assert.Contains("--status-ok-text", css);
        Assert.Contains("--status-warn-text", css);
        Assert.Contains("--status-bad-text", css);
        Assert.Contains("color: var(--status-ok-text)", css);
        Assert.Contains("color: var(--status-warn-text)", css);
        Assert.Contains("color: var(--status-bad-text)", css);

        // Both themes must define them or the pill falls back to inherited text.
        var dark = Regex.Match(css, @"\[data-theme=""dark""\]\s*\{[\s\S]*?\n        \}", RegexOptions.Singleline);
        Assert.True(dark.Success, "Could not isolate the dark theme variable block.");
        Assert.Contains("--status-ok-text", dark.Value);
    }
}
