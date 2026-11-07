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
    private static string ReadMainJs()
    {
        // main.js is now split into several ordered scripts; scan all of them.
        return WwwrootFiles.ReadAppScripts();
    }

    [Theory]
    // The whole library is marked server-side (markWholeLibraryRead posts /api/files/read-all);
    // a selection is sent to the read-batch endpoint.
    [InlineData("markAllFilesRead", "EVERY file in the library as read", "overwrites the read status of all files and cannot be undone", "markWholeLibraryRead(true)")]
    [InlineData("markAllFilesUnread", "EVERY file in the library as unread", "clears the read status of all files and cannot be undone", "markWholeLibraryRead(false)")]
    [InlineData("markSelectedFilesRead", "selected file", "overwrites the current read status and cannot be undone", "read-batch")]
    [InlineData("markSelectedFilesUnread", "selected file", "clears the current read status and cannot be undone", "read-batch")]
    public void BulkReadStatusAction_ConfirmsBeforeSendingTheRequest(
        string functionName,
        string expectedScope,
        string expectedImpact,
        string expectedRequest)
    {
        var contents = ReadMainJs();

        var start = contents.IndexOf($"async function {functionName}(", StringComparison.Ordinal);
        Assert.True(start >= 0, $"Expected main.js to define {functionName}");

        var nextFunction = contents.IndexOf("\n        async function ", start + 1, StringComparison.Ordinal);
        var end = nextFunction >= 0 ? nextFunction : contents.Length;
        var body = contents[start..end];

        var fetchIndex = body.IndexOf(expectedRequest, StringComparison.Ordinal);
        Assert.True(fetchIndex >= 0, $"Expected {functionName} to send {expectedRequest}");

        var confirmation = Regex.Match(
            body,
            @"if\s*\(\s*!\s*confirm\((?<prompt>.*?)\)\s*\)\s*\{\s*return;\s*\}",
            RegexOptions.Singleline);
        Assert.True(
            confirmation.Success,
            $"{functionName} must return when its confirmation prompt is canceled.");
        Assert.True(
            confirmation.Index < fetchIndex,
            $"{functionName} must confirm before sending {expectedRequest}.");
        Assert.Contains(expectedScope, confirmation.Groups["prompt"].Value);
        Assert.Contains(expectedImpact, confirmation.Groups["prompt"].Value);
    }
}
