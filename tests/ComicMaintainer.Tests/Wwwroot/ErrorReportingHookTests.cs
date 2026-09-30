using System.Text.RegularExpressions;

namespace ComicMaintainer.Tests.Wwwroot;

/// <summary>
/// The browser-side capture hooks are duplicated on purpose: <c>reader.html</c>
/// is standalone and never loads <c>js/main.js</c>, so a hook added only to
/// <c>main.js</c> leaves the reader — where most user time is spent — silently
/// unreported. These assertions exist to keep the two copies in step, in the
/// same text-level style as the other <c>Wwwroot</c> suites.
/// </summary>
public class ErrorReportingHookTests
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

    private static string Read(string relativePath)
    {
        var path = Path.Combine(WwwRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"Expected {path} to exist");
        return File.ReadAllText(path);
    }

    public static TheoryData<string> HookedFiles => new() { "js/main.js", "reader.html" };

    [Theory]
    [MemberData(nameof(HookedFiles))]
    public void RegistersBothGlobalErrorHooks(string file)
    {
        var contents = Read(file);

        // 'error' alone misses async failures, which is where most modern
        // frontend faults live.
        Assert.Matches(
            new Regex(@"addEventListener\(\s*['""]error['""]", RegexOptions.None, TimeSpan.FromSeconds(2)),
            contents);
        Assert.Matches(
            new Regex(@"addEventListener\(\s*['""]unhandledrejection['""]", RegexOptions.None, TimeSpan.FromSeconds(2)),
            contents);
    }

    [Theory]
    [MemberData(nameof(HookedFiles))]
    public void PostsToTheClientReportEndpoint(string file)
    {
        var contents = Read(file);

        Assert.Contains("/api/errorreports/client", contents, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(HookedFiles))]
    public void CapsReportsPerPageLoad(string file)
    {
        var hook = ExtractHookRegion(Read(file));

        // A fault inside a render loop or a scroll handler fires continuously.
        // Without a cap the hook turns one defect into a denial of service
        // against the instance's own API.
        Assert.Matches(
            new Regex(@"MAX_CLIENT_ERROR_REPORTS", RegexOptions.None, TimeSpan.FromSeconds(2)),
            hook);
        Assert.Matches(
            new Regex(@"clientErrorReportCount\s*>=\s*MAX_CLIENT_ERROR_REPORTS",
                RegexOptions.None, TimeSpan.FromSeconds(2)),
            hook);
    }

    [Theory]
    [MemberData(nameof(HookedFiles))]
    public void SuppressesDuplicateReportsFromTheSameFault(string file)
    {
        var hook = ExtractHookRegion(Read(file));

        // The same signature recurring within a page load is one defect, not
        // many; deduping here keeps the per-load budget meaningful.
        Assert.Matches(
            new Regex(@"REPORTED_ERROR_SIGNATURES\.has\(", RegexOptions.None, TimeSpan.FromSeconds(2)),
            hook);
        Assert.Matches(
            new Regex(@"REPORTED_ERROR_SIGNATURES\.add\(", RegexOptions.None, TimeSpan.FromSeconds(2)),
            hook);
    }

    [Theory]
    [MemberData(nameof(HookedFiles))]
    public void ReportingFailuresAreSwallowed(string file)
    {
        var hook = ExtractHookRegion(Read(file));

        // If reporting an error could itself throw, an unreachable or
        // unauthenticated instance would surface a second error on every
        // failure, which the hook would then try to report — recursing back
        // into itself. Both the rejected fetch and the synchronous body are
        // therefore guarded.
        Assert.Matches(
            new Regex(@"\}\)\)?\.catch\(", RegexOptions.None, TimeSpan.FromSeconds(2)),
            hook);
        Assert.Matches(
            new Regex(@"\}\s*catch\s*\(", RegexOptions.None, TimeSpan.FromSeconds(2)),
            hook);
    }

    [Fact]
    public void ReaderUsesItsOwnFetchOptionsHelper()
    {
        var contents = Read("reader.html");

        // reader.html has no access to main.js's auth helpers; using anything
        // else here would post unauthenticated and be rejected.
        var hook = ExtractHookRegion(contents);
        Assert.Contains("buildFetchOptions", hook, StringComparison.Ordinal);
    }

    [Fact]
    public void MainJsUsesTheSharedApiUrlHelper()
    {
        var contents = Read("js/main.js");

        // main.js is served from a page that may be hosted under a path
        // prefix, so a bare relative URL would break those deployments.
        var hook = ExtractHookRegion(contents);
        Assert.Contains("apiUrl(", hook, StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns the text around the client-report call so the assertions above
    /// describe the hook itself rather than matching an unrelated helper
    /// elsewhere in a large file.
    /// </summary>
    private static string ExtractHookRegion(string contents)
    {
        var index = contents.IndexOf("/api/errorreports/client", StringComparison.Ordinal);
        Assert.True(index >= 0, "Expected the file to post to /api/errorreports/client.");

        var start = Math.Max(0, index - 1500);
        var end = Math.Min(contents.Length, index + 1500);
        return contents[start..end];
    }
}
