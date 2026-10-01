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
    public void IndexHtml_SendModalOffersCondenseOptions()
    {
        var html = Read("index.html");

        Assert.Contains("id=\"emailCondenseModeSelect\"", html);
        Assert.Contains("value=\"count\"", html);
        Assert.Contains("value=\"all\"", html);
        Assert.Contains("id=\"emailCondenseCountInput\"", html);
        // The format picker drives the condensed format, so it must tell the
        // plan read-out when it changes.
        Assert.Contains("onchange=\"onEmailSendFormatChange()\"", html);
        // Where the estimated sizes and the "too large to email" warning go.
        Assert.Contains("id=\"emailCondensePlan\"", html);
        // Where a running/failed/finished book build reports its status.
        Assert.Contains("id=\"emailCondenseBuilds\"", html);
    }

    [Fact]
    public void MainJs_CondensePlanBlocksSendingOversizedBooks()
    {
        var js = Read("js", "main.js");

        // The user must learn before sending that a mass condense cannot be
        // emailed, and be offered the download instead.
        Assert.Contains("/api/email/condense-plan", js);
        Assert.Contains("/api/email/condense-builds", js);
        Assert.Matches(
            new Regex(@"setEmailSendEnabled\(emailSendAvailable && plan\.can_email === true\)", RegexOptions.Singleline),
            js);
        Assert.Contains("function downloadCondensedBook", js);
    }

    [Fact]
    public void MainJs_TracksCondensedEpubBuildProgress()
    {
        var js = Read("js", "main.js");

        // A big condensed book takes minutes to build. The UI must start a
        // tracked build, poll it, and surface both progress and the failure
        // reason rather than leaving the user with a silent spinner.
        Assert.Contains("function pollEmailCondenseBuilds", js);
        Assert.Contains("function renderEmailCondenseBuilds", js);
        Assert.Contains("function cancelCondensedBookBuild", js);
        Assert.Contains("/api/email/condense-builds/${encodeURIComponent(buildId)}/download", js);
        Assert.Matches(
            new Regex(@"build\.status === 'failed'", RegexOptions.Singleline),
            js);
    }

    [Fact]
    public void MainJs_ReconcilesAndSerializesCondensedBuildPolling()
    {
        var js = Read("js", "main.js");

        Assert.Contains("function reconcileEmailCondenseBuilds", js);
        Assert.Matches(
            new Regex(@"refreshEmailCondenseBuilds\(\).*?reconcileEmailCondenseBuilds\(", RegexOptions.Singleline),
            js);
        Assert.Matches(
            new Regex(@"pollEmailCondenseBuilds\(\).*?reconcileEmailCondenseBuilds\(", RegexOptions.Singleline),
            js);
        Assert.Contains("emailCondenseBuildPollInFlight", js);
        Assert.Contains("setTimeout(", js);
        Assert.Contains("progress.current_issue", js);
    }

    [Fact]
    public void MainJs_CondensedSendsKeepEpubAndAzw3Selectable()
    {
        var js = Read("js", "main.js");

        // A condensed book is generated, so only the original archives are
        // unavailable while condensing: the picker stays enabled and the user
        // keeps the choice between EPUB and AZW3.
        Assert.Contains("function applyEmailCondenseFormatRestriction(condensing)", js);
        Assert.Matches(
            new Regex(@"option\.disabled = condensing && !generated;\s*option\.hidden = condensing && !generated;", RegexOptions.Singleline),
            js);
        Assert.Matches(
            new Regex(@"if \(!isCondensableEmailFormat\(format\.value\)\) format\.value = 'epub';", RegexOptions.Singleline),
            js);
        // The user's own format is restored once condensing is turned off.
        Assert.Matches(
            new Regex(@"format\.value = emailFormatBeforeCondense;", RegexOptions.Singleline),
            js);
        Assert.Contains("condenseMode,", js);
        Assert.Contains("issuesPerBook", js);
    }

    [Fact]
    public void MainJs_CondenseRequestsCarryTheChosenFormat()
    {
        var js = Read("js", "main.js");

        // The plan, the send and any downloaded build must all use the format
        // the user picked, and a built AZW3 must not be saved as an .epub.
        Assert.Contains("deliveryFormat: getEmailCondenseFormat()", js);
        Assert.Matches(
            new Regex(@"function isCondensableEmailFormat\(value\)\s*\{[^}]*'azw3'", RegexOptions.Singleline),
            js);
        Assert.Contains("function getEmailCondenseFormat()", js);
        Assert.Contains("build?.delivery_format === 'azw3' ? 'azw3' : 'epub'", js);
    }

    [Fact]
    public void FormatSelectors_OfferAzw3()
    {
        var html = Read("index.html");
        var reader = Read("reader.html");

        // The device default, the per-send format and the subscription format
        // must all offer AZW3, otherwise the backend format is unreachable.
        Assert.Equal(3, Regex.Matches(html, "<option value=\"azw3\">").Count);
        Assert.Contains("<option value=\"azw3\">", reader);
    }

    [Fact]
    public void MainJs_KeepsAzw3AsADeviceDefault()
    {
        var js = Read("js", "main.js");

        // The device format used to be coerced to epub/original, which silently
        // dropped an AZW3 default on both load and save.
        Assert.Matches(
            new Regex(@"function normalizeEmailDeviceFormat\(value\)\s*\{[^}]*'azw3'", RegexOptions.Singleline),
            js);
        Assert.Contains("normalizeEmailDeviceFormat(device.deliveryFormat)", js);
        Assert.Contains("normalizeEmailDeviceFormat(document.getElementById('emailDeviceFormat').value)", js);
        Assert.Contains("if (normalized === 'azw3') return 'AZW3 (Kindle)';", js);
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
    public void MainJs_SeriesEmailWaitsForPendingPathLoads()
    {
        var js = Read("js", "main.js");

        Assert.Contains("emailBtn.disabled = Array.from(selectedSeries).some", js);
        Assert.Matches(
            new Regex(@"async function openEmailSendModalForSelected\(\).*await Promise\.all\(pendingLoads\).*const files = Array\.from\(selectedFiles\)", RegexOptions.Singleline),
            js);
    }

    [Fact]
    public void MainJs_SavingEmailSettingsInvalidatesStatusCache()
    {
        var js = Read("js", "main.js");

        Assert.Matches(
            new Regex(@"if \(!emailSettingsResponse\.ok\).*emailStatusCache = null;", RegexOptions.Singleline),
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
    public void ReaderHtml_EmailFormatSelectorHasLabel()
    {
        var html = Read("reader.html");

        Assert.Contains("<label for=\"emailPromptFormat\">Format:</label>", html);
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

    [Fact]
    public void MainJs_CondensedDownloadDoesNotRevokeTheObjectUrlBeforeTheSaveStarts()
    {
        var js = Read("js", "main.js");

        // Clicking the link only hands the browser the object URL; revoking it
        // in the same tick cancels the save outright in some browsers, which is
        // exactly how a condensed book "downloads successfully" and never
        // arrives.
        Assert.DoesNotMatch(
            new Regex(@"link\.click\(\);\s*link\.remove\(\);\s*URL\.revokeObjectURL", RegexOptions.Singleline),
            js);
        Assert.Matches(
            new Regex(@"setTimeout\(\(\) => URL\.revokeObjectURL\(url\)", RegexOptions.Singleline),
            js);
    }

    [Fact]
    public void MainJs_CondensedBookFailuresAreReportedForAutomaticIssueFiling()
    {
        var js = Read("js", "main.js");

        // These catch blocks used to end at a banner, so a failed condensed
        // build or download was visible only to the person looking at it.
        Assert.Contains("function reportCondenseFailure", js);
        Assert.Contains("reportCondenseFailure('download', error)", js);
        Assert.Contains("reportCondenseFailure('start build', error)", js);

        // A 4xx is the server answering, not failing; filing an issue for every
        // "series exceeds the issue limit" would bury the real failures.
        Assert.Matches(
            new Regex(@"status >= 400 && status < 500\) return;", RegexOptions.Singleline),
            js);
    }
}
