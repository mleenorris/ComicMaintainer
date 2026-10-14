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
    [InlineData("markAllFilesRead", "EVERY file in the library as read", "overwrites the read status of all files and cannot be undone")]
    [InlineData("markAllFilesUnread", "EVERY file in the library as unread", "clears the read status of all files and cannot be undone")]
    [InlineData("markSelectedFilesRead", "selected file", "overwrites the current read status and cannot be undone")]
    [InlineData("markSelectedFilesUnread", "selected file", "clears the current read status and cannot be undone")]
    public void BulkReadStatusAction_ConfirmsBeforeSendingTheBatchRequest(
        string functionName,
        string expectedScope,
        string expectedImpact)
    {
        var contents = ReadMainJs();

        var start = contents.IndexOf($"async function {functionName}(", StringComparison.Ordinal);
        Assert.True(start >= 0, $"Expected main.js to define {functionName}");

        var nextFunction = contents.IndexOf("\n        async function ", start + 1, StringComparison.Ordinal);
        var end = nextFunction >= 0 ? nextFunction : contents.Length;
        var body = contents[start..end];

        var fetchIndex = body.IndexOf("read-batch", StringComparison.Ordinal);
        Assert.True(fetchIndex >= 0, $"Expected {functionName} to call the read-batch endpoint");

        var confirmation = Regex.Match(
            body,
            @"if\s*\(\s*!\s*confirm\((?<prompt>.*?)\)\s*\)\s*\{\s*return;\s*\}",
            RegexOptions.Singleline);
        Assert.True(
            confirmation.Success,
            $"{functionName} must return when its confirmation prompt is canceled.");
        Assert.True(
            confirmation.Index < fetchIndex,
            $"{functionName} must confirm before calling the read-batch endpoint.");
        Assert.Contains(expectedScope, confirmation.Groups["prompt"].Value);
        Assert.Contains(expectedImpact, confirmation.Groups["prompt"].Value);
    }
}
