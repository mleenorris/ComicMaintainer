using System.Text.RegularExpressions;

namespace ComicMaintainer.Tests.Wwwroot;

/// <summary>
/// Guard rails for shift-click range selection in the series detail issue
/// grid: clicking one issue's checkbox and then shift-clicking another selects
/// (or clears) every issue between the two.
///
/// Like the other <c>Wwwroot</c> suites these are text-level assertions on
/// <c>main.js</c> as shipped, because it is a plain static asset with no JS
/// test runner in the repo. The pieces asserted here are the ones that are
/// easy to "simplify" back into a plain toggle without any visible failure
/// until a user tries to select a run of issues.
/// </summary>
public class SeriesIssueRangeSelectionTests
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
    public void IssueCheckbox_RoutesThroughTheRangeAwareSelectionHandler()
    {
        var contents = ReadMainJs();

        // The issue card's checkbox must go through toggleSeriesIssueSelection
        // rather than toggleFileSelection directly: that wrapper is what keeps
        // the anchor up to date and expands a shift-click into a range.
        Assert.Contains(
            "onchange=\"toggleSeriesIssueSelection('${escapeJs(issue.file_path)}', this.checked)\"",
            contents);
        Assert.Contains("function toggleSeriesIssueSelection(filepath, checked)", contents);
    }

    [Fact]
    public void IssueCheckbox_CapturesTheShiftModifierFromTheOriginatingEvent()
    {
        var contents = ReadMainJs();

        // The change event carries no modifier state, and the click a <label>
        // forwards to its checkbox is synthesised, so the modifier has to be
        // read from the mousedown/keydown on the label. Losing either binding
        // silently downgrades every shift-click to a plain toggle.
        var label = Regex.Match(contents, "<label class=\"series-issue-select\"[^>]*>");
        Assert.True(label.Success, "Expected the series issue card to render a .series-issue-select label.");
        Assert.Contains("onmousedown=\"rememberSeriesIssueRangeModifier(event)\"", label.Value);
        Assert.Contains("onkeydown=\"rememberSeriesIssueRangeModifier(event)\"", label.Value);

        // The click handler still has to stop the card underneath from
        // reacting to a click on the checkbox.
        Assert.Contains("onclick=\"event.stopPropagation()\"", label.Value);

        Assert.Contains("function rememberSeriesIssueRangeModifier(event)", contents);
        Assert.Contains("seriesIssueRangeSelectArmed = !!(event && event.shiftKey);", contents);

        // The flag must be consumed by the toggle it belongs to, otherwise the
        // next plain click would be treated as a range selection.
        Assert.Matches(
            new Regex(
                @"function toggleSeriesIssueSelection\(filepath, checked\) \{.*?seriesIssueRangeSelectArmed = false;",
                RegexOptions.Singleline),
            contents);
    }

    [Fact]
    public void RangeSelection_UsesRenderedCardOrderSoMissingIssuesAreSkipped()
    {
        var contents = ReadMainJs();

        // The grid sorts by issue number and interleaves missing-issue
        // placeholders, which carry no data-file-path. Reading the range off
        // the rendered cards is what makes "everything in between" match what
        // the user sees and keeps placeholders out of the selection.
        var range = Regex.Match(
            contents,
            @"function getSeriesIssueSelectionRange\(anchorPath, targetPath\) \{.*?\n        \}",
            RegexOptions.Singleline);
        Assert.True(range.Success, "Expected a getSeriesIssueSelectionRange() helper.");

        Assert.Contains(".series-issue-card[data-file-path]", range.Value);
        Assert.Contains("Math.min(anchorIndex, targetIndex)", range.Value);
        Assert.Contains("Math.max(anchorIndex, targetIndex) + 1", range.Value);

        // An anchor that is no longer rendered must degrade to a plain toggle
        // instead of selecting an arbitrary slice of the grid.
        Assert.Contains("if (anchorIndex === -1 || targetIndex === -1) return null;", range.Value);
    }

    [Fact]
    public void RangeSelection_SyncsCheckboxesAndTheSharedSelectionUi()
    {
        var contents = ReadMainJs();

        var apply = Regex.Match(
            contents,
            @"function applySeriesIssueRangeSelection\(paths, checked\) \{.*?\n        \}",
            RegexOptions.Singleline);
        Assert.True(apply.Success, "Expected an applySeriesIssueRangeSelection() helper.");

        // Every issue in the range has to land in selectedFiles, which is what
        // the bulk actions (process / rename / email / delete) operate on.
        Assert.Contains("selectedFiles.add(path);", apply.Value);
        Assert.Contains("selectedFiles.delete(path);", apply.Value);

        // Only the clicked checkbox is toggled by the browser, so the rest of
        // the range is updated here; without it the boxes and the highlight
        // disagree with the selection.
        Assert.Contains("updateSeriesIssueSelectionState(path, checked);", apply.Value);
        Assert.Matches(
            new Regex(
                @"function updateSeriesIssueSelectionState\(filepath, checked\) \{.*?checkbox\.checked = checked;",
                RegexOptions.Singleline),
            contents);

        // The toolbar count and the select-all tri-state are refreshed once for
        // the whole range rather than per issue.
        Assert.Contains("updateSelectInfo();", apply.Value);
        Assert.Contains("updateSelectAllCheckbox();", apply.Value);
    }

    [Fact]
    public void RangeSelection_AnchorIsClearedWhenTheSeriesDetailCloses()
    {
        var contents = ReadMainJs();

        // Leaving the series must drop the anchor so a shift-click in another
        // series cannot extend from an issue the user no longer sees.
        Assert.Matches(
            new Regex(
                @"function closeSeriesDetail\(\) \{.*?lastSelectedIssuePath = null;",
                RegexOptions.Singleline),
            contents);
    }

    [Fact]
    public void RangeSelection_StateIsResetWhenRoutingAwayOrOpeningAnotherSeries()
    {
        var contents = ReadMainJs();

        Assert.Matches(
            new Regex(
                @"function handleRouteChange\(\) \{\s*const route = parseHash\(\);.*?if \(route\.view !== 'series' \|\| currentSeriesDetailId !== route\.seriesId\) \{\s*lastSelectedIssuePath = null;\s*seriesIssueRangeSelectArmed = false;",
                RegexOptions.Singleline),
            contents);
    }
}
