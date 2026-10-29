using System.Text.RegularExpressions;

namespace ComicMaintainer.Tests.Wwwroot;

/// <summary>
/// Guard rails for the "Delete All Duplicates" affordance in the Duplicate
/// Review modal. Deleting duplicates one-by-one is unusable for large
/// libraries, so the modal must offer a bulk action that reuses the existing
/// background delete job instead of issuing one DELETE per file.
/// </summary>
public class DuplicateReviewBulkDeleteTests
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

    [Fact]
    public void IndexHtml_DuplicateReviewModalHasDeleteAllButton()
    {
        var html = Read("index.html");

        var button = Regex.Match(
            html,
            "<button[^>]*id=\"duplicateReviewDeleteAllBtn\"[^>]*>",
            RegexOptions.IgnoreCase);

        Assert.True(button.Success, "Expected a #duplicateReviewDeleteAllBtn button in the duplicate review modal.");
        Assert.Contains("onclick=\"deleteAllDuplicates()\"", button.Value);
        Assert.Contains("data-requires-write", button.Value);
    }

    [Fact]
    public void MainJs_DeleteAllDuplicatesUsesBulkDeleteJob()
    {
        var js = WwwrootFiles.ReadAppScripts();

        Assert.Contains("async function deleteAllDuplicates()", js);

        var start = js.IndexOf("async function deleteAllDuplicates()", StringComparison.Ordinal);
        var body = js.Substring(start, Math.Min(3000, js.Length - start));

        // The bulk path must go through the background job endpoint, not loop
        // over per-file DELETE requests.
        Assert.Contains("/api/jobs/delete-selected", body);
        Assert.Contains("confirm(", body);
        Assert.DoesNotContain("deleteSingleFile(", body);
    }

    [Fact]
    public void MainJs_DeleteAllDuplicatesButtonIsDisabledWhenNoDuplicates()
    {
        var js = WwwrootFiles.ReadAppScripts();

        var start = js.IndexOf("function updateDuplicateReviewNavigation()", StringComparison.Ordinal);
        Assert.True(start >= 0, "Expected updateDuplicateReviewNavigation in main.js");

        var body = js.Substring(start, Math.Min(1200, js.Length - start));
        Assert.Contains("duplicateReviewDeleteAllBtn", body);
        Assert.Matches(new Regex(@"deleteAllBtn\.disabled\s*=\s*total\s*===\s*0"), body);
    }

    [Fact]
    public void MainJs_DuplicateReviewReloadClearsStateAndIgnoresStaleResponses()
    {
        var js = WwwrootFiles.ReadAppScripts();

        var start = js.IndexOf("async function openDuplicateReviewModal(", StringComparison.Ordinal);
        Assert.True(start >= 0, "Expected openDuplicateReviewModal in main.js");

        var body = js.Substring(start, Math.Min(3000, js.Length - start));
        Assert.Contains("const loadId = ++duplicateReviewLoadId;", body);
        Assert.Contains("duplicateReviewFiles = [];", body);
        Assert.Contains("updateDuplicateReviewNavigation();", body);
        Assert.Contains("if (loadId !== duplicateReviewLoadId) return;", body);
    }
}
