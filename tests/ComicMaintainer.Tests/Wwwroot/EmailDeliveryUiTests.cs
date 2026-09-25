using System.Text.RegularExpressions;

namespace ComicMaintainer.Tests.Wwwroot;

/// <summary>
/// Guard rails for the entry points that make the "email comics to an ereader"
/// feature reachable from the UI. The endpoints existed long before any of these
/// menu items did, and the feature is invisible (and effectively unusable)
/// without them, so each entry point is asserted on the files as shipped.
/// </summary>
public class EmailDeliveryUiTests
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
    public void IndexHtml_HeaderMenuOpensEreaderDevices()
    {
        var html = Read("index.html");

        // Device management used to live only inside the Settings modal, whose
        // body is disabled wholesale for non-administrators. The header menu
        // entry is what lets an ordinary user with CanModifyLibrary add a
        // device at all.
        Assert.Matches(
            new Regex(@"settings-dropdown-item[^>]*onclick=""openEmailDevicesModal\(\); closeSettingsMenu\(\);""", RegexOptions.Singleline),
            html);
    }

    [Fact]
    public void IndexHtml_HeaderMenuOpensDeliveryHistory()
    {
        var html = Read("index.html");

        Assert.Matches(
            new Regex(@"settings-dropdown-item[^>]*onclick=""openEmailDeliveryHistoryModal\(\); closeSettingsMenu\(\);""", RegexOptions.Singleline),
            html);
        Assert.Contains("id=\"emailDeliveryHistoryModal\"", html);
        Assert.Contains("id=\"emailDeliveryHistoryList\"", html);
    }

    [Fact]
    public void IndexHtml_SendModalHasEmptyStateCallToAction()
    {
        var html = Read("index.html");

        // With no saved devices the device <select> is empty and Send is
        // disabled; without this call to action there is nothing telling the
        // user what to do next.
        Assert.Contains("id=\"emailSendNoDevices\"", html);
        Assert.Contains("No devices yet", html);
        Assert.Contains("openEmailDevicesModal()", html);
    }

    [Fact]
    public void IndexHtml_SendModalHasStatusHintElement()
    {
        var html = Read("index.html");

        Assert.Contains("id=\"emailSendStatusHint\"", html);
    }

    [Fact]
    public void MainJs_SendModalChecksEmailStatusAndDevices()
    {
        var js = Read("js", "main.js");

        // Both must be resolved before the modal is usable, otherwise the only
        // feedback about an unconfigured SMTP server arrives after Send.
        Assert.Contains("fetchEmailStatus", js);
        Assert.Matches(new Regex(@"setEmailSendHint\(", RegexOptions.Singleline), js);
        Assert.Matches(new Regex(@"setEmailSendNoDevicesVisible\(true\)", RegexOptions.Singleline), js);
        Assert.Matches(new Regex(@"setEmailSendEnabled\(false\)", RegexOptions.Singleline), js);
    }

    [Fact]
    public void MainJs_LoadsDeliveryHistoryFromApi()
    {
        var js = Read("js", "main.js");

        Assert.Contains("/api/email/deliveries", js);
        Assert.Contains("function renderEmailDeliveryHistory", js);
        Assert.Contains("function closeEmailDeliveryHistoryModal", js);
    }

    [Fact]
    public void MainJs_SeriesSelectionToolbarOffersEmailSelected()
    {
        var js = Read("js", "main.js");

        // The series grid has no per-card menu (each card is a single <button>),
        // so the selection toolbar is the send entry point for that view.
        Assert.Contains("seriesSelectionEmailBtn", js);
        Assert.Matches(
            new Regex(@"id=""seriesSelectionEmailBtn""[^>]*onclick=""openEmailSendModalForSelected\(\)""", RegexOptions.Singleline),
            js);
    }

    [Fact]
    public void ReaderHtml_HasEmailCurrentComicAction()
    {
        var html = Read("reader.html");

        Assert.Contains("id=\"emailComicBtn\"", html);
        Assert.Contains("openReaderEmailPrompt()", html);
        Assert.Contains("id=\"emailPrompt\"", html);
        Assert.Contains("/api/email/send", html);
    }

    [Fact]
    public void ReaderHtml_EmailPromptClosesOnEscapeAndBlocksChromeAutoHide()
    {
        var html = Read("reader.html");

        // Escape must close the prompt rather than exit the reader, and the
        // chrome must not auto-hide while the prompt is open.
        Assert.Contains("hideReaderEmailPrompt()", html);
        Assert.Matches(
            new Regex(@"emailPrompt\.classList\.contains\('show'\)", RegexOptions.Singleline),
            html);
    }

    [Fact]
    public void ReaderHtml_HidesEmailActionForReadOnlyUsers()
    {
        var html = Read("reader.html");

        Assert.Contains("applyReaderEmailVisibility", html);
        Assert.Contains("canModifyLibrary === false", html);
    }
}
