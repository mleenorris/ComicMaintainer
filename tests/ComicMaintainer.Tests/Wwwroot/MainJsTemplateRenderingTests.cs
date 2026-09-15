using System.Text.RegularExpressions;

namespace ComicMaintainer.Tests.Wwwroot;

/// <summary>
/// Guard rails for how <c>main.js</c> turns data into DOM.
///
/// The UI is rendered from markup strings. Assigning those strings straight to
/// <c>innerHTML</c> means every interpolation needs a hand-written escape, and
/// a forgotten one is invisible until a series title contains a quote or an
/// angle bracket. <c>main.js</c> therefore funnels all rendering through an
/// auto-escaping <c>html``</c> tagged template plus <c>renderHtml()</c>, which
/// clones a parsed <c>&lt;template&gt;</c> into the target.
///
/// These are text-level assertions on the shipped asset (it is plain
/// JavaScript served statically, with no JS test runner in the repo). They
/// exist to stop the pattern eroding one convenient <c>innerHTML</c> at a
/// time.
/// </summary>
public class MainJsTemplateRenderingTests
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
    public void MainJs_HasNoInnerHtmlAssignmentsOutsideTheTemplateHelper()
    {
        var contents = ReadMainJs();

        // Comment lines mention `innerHTML` when explaining why the helpers
        // exist, so only look at executable lines.
        var assignments = contents
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => !line.StartsWith("//") && !line.StartsWith("*") && !line.StartsWith("/*"))
            .Where(line => Regex.IsMatch(line, @"\.innerHTML\s*\+?="))
            .ToList();

        // The one permitted assignment is the helper's own parse step.
        Assert.Equal(new[] { "template.innerHTML = markup;" }, assignments);
    }

    [Fact]
    public void MainJs_ExposesTheTemplateRenderingHelpers()
    {
        var contents = ReadMainJs();

        Assert.Contains("function html(strings, ...values)", contents);
        Assert.Contains("function renderHtml(target, content)", contents);
        Assert.Contains("function clearChildren(target)", contents);
        Assert.Contains("function rawHtml(value)", contents);
        Assert.Contains("function jsArg(value)", contents);
    }

    [Fact]
    public void MainJs_RendersByCloningAParsedTemplate()
    {
        var contents = ReadMainJs();

        // Cloning the parsed <template> content (rather than re-parsing into
        // the live element) is what keeps repeat renders cheap and builds the
        // nodes off-document.
        Assert.Contains(".content.cloneNode(true)", contents);
        Assert.Contains("target.replaceChildren(htmlToFragment(content));", contents);
    }

    [Fact]
    public void MainJs_EscapeHtmlEscapesQuotes()
    {
        var contents = ReadMainJs();

        // escapeHtml is used inside quoted attributes (aria-label, title, ...).
        // If it does not escape quotes, a value containing one breaks out of
        // the attribute and the remainder is parsed as markup.
        Assert.Contains("'\"': '&quot;'", contents);
        Assert.Contains("\"'\": '&#39;'", contents);
        Assert.Matches(@"function escapeHtml\(text\) \{\s*return String\(text \?\? ''\)\.replace\(", contents);
    }

    [Fact]
    public void MainJs_DoesNotDoubleEscapeInsideHtmlTemplates()
    {
        var contents = ReadMainJs();

        // html`` escapes every interpolation itself, so an escapeHtml() call
        // inside one would double-encode (&amp;lt; instead of &lt;). Catch the
        // common shape: `${escapeHtml(...)}` appearing on a line that is part
        // of an html`` literal is hard to detect textually, so instead assert
        // the two helpers are never composed directly.
        Assert.DoesNotContain("html`${escapeHtml(", contents);
        Assert.DoesNotContain("rawHtml(escapeHtml(", contents);
    }

    [Fact]
    public void MainJs_UsesJsArgForInlineHandlerArguments()
    {
        var contents = ReadMainJs();

        // jsArg() marks its result trusted so html`` leaves the JS escaping
        // intact. Using a bare value there would HTML-escape the quotes, which
        // the parser decodes straight back into string-terminating quotes.
        Assert.Contains("function jsArg(value) {", contents);
        Assert.Contains("return rawHtml(escapeJs(String(value ?? '')));", contents);
    }
}
