namespace ComicMaintainer.Tests.Wwwroot;

/// <summary>
/// Shared access to the shipped SPA assets for the text-level Wwwroot suites.
///
/// The library SPA used to be a single <c>js/main.js</c>; it is now a set of
/// classic scripts that index.html loads in a fixed order and that share one
/// global scope. Guards that used to scan main.js scan the concatenation of
/// all of them (<see cref="ReadAppScripts"/>), so a rule such as "no direct
/// innerHTML assignment" applies to every file, whichever one the code lives in.
/// </summary>
internal static class WwwrootFiles
{
    /// <summary>
    /// The app scripts, relative to wwwroot, in the order index.html loads
    /// them. main.js (the bootstrap) must stay last.
    /// </summary>
    public static readonly IReadOnlyList<string> AppScripts = new[]
    {
        "js/shared.js",
        "js/app/templating.js",
        "js/app/api.js",
        "js/app/state.js",
        "js/app/events.js",
        "js/app/shell.js",
        "js/app/service-worker.js",
        "js/app/router.js",
        "js/app/library.js",
        "js/app/series.js",
        "js/app/suwayomi-search.js",
        "js/app/file-list.js",
        "js/app/file-actions.js",
        "js/app/jobs.js",
        "js/app/settings.js",
        "js/app/scheduled-jobs.js",
        "js/app/email.js",
        "js/app/menus.js",
        "js/app/series-metadata.js",
        "js/app/modals.js",
        "js/app/error-reporting.js",
        "js/main.js",
    };

    /// <summary>
    /// The standalone pages' own scripts, keyed by the page that loads them.
    /// Each page used to carry this code in an inline &lt;script&gt; block; it
    /// lives in js/pages/ so it can be measured and tested like the app
    /// scripts. A page script shares only its own page's global scope.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> PageScripts = new Dictionary<string, string>
    {
        ["login.html"] = "js/pages/login.js",
        ["offline.html"] = "js/pages/offline.js",
        ["reader.html"] = "js/pages/reader.js",
        ["setup.html"] = "js/pages/setup.js",
    };

    public static string WwwRoot
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

    /// <summary>Reads a file under wwwroot, given as a '/'-separated relative path.</summary>
    public static string Read(string relativePath)
    {
        var path = Path.Combine(new[] { WwwRoot }.Concat(relativePath.Split('/')).ToArray());
        Assert.True(File.Exists(path), $"Expected {path} to exist");
        return File.ReadAllText(path);
    }

    /// <summary>
    /// All app scripts concatenated in load order, i.e. the code the browser
    /// runs for index.html (excluding index.html's own inline script).
    /// </summary>
    public static string ReadAppScripts() =>
        string.Join("\n", AppScripts.Select(Read));

    /// <summary>
    /// A standalone page's markup followed by its page script, for guards
    /// that span both (e.g. a button in the markup and the handler it calls).
    /// </summary>
    public static string ReadPageWithScript(string page) =>
        Read(page) + "\n" + Read(PageScripts[page]);
}
