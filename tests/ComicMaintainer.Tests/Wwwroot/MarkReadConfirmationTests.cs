using System.Text.RegularExpressions;

namespace ComicMaintainer.Tests.Wwwroot;

/// <summary>
/// Bulk read-status changes (Mark All/Selected Read/Unread) rewrite the read
/// state of many files at once and cannot be undone, so each entry point must
/// ask for confirmation before it issues the batch request. These are
/// text-level assertions on the shipped <c>main.js</c> (there is no JS test
/// runner in the repo).
/// </summary>
public class MarkReadConfirmationTests
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

    [Theory]
    [InlineData("markAllFilesRead")]
    [InlineData("markAllFilesUnread")]
    [InlineData("markSelectedFilesRead")]
    [InlineData("markSelectedFilesUnread")]
    public void BulkReadStatusAction_ConfirmsBeforeSendingTheBatchRequest(string functionName)
    {
        var contents = ReadMainJs();

        var start = contents.IndexOf($"async function {functionName}(", StringComparison.Ordinal);
        Assert.True(start >= 0, $"Expected main.js to define {functionName}");

        var fetchIndex = contents.IndexOf("read-batch", start, StringComparison.Ordinal);
        Assert.True(fetchIndex > start, $"Expected {functionName} to call the read-batch endpoint");

        var body = contents[start..fetchIndex];

        Assert.True(
            Regex.IsMatch(body, @"if\s*\(\s*!\s*confirm\("),
            $"{functionName} must prompt for confirmation (if (!confirm(...)) return;) before changing read status, " +
            "so an accidental click does not set or clear read state in bulk.");
    }
}
