using System.Text.RegularExpressions;

namespace ComicMaintainer.Tests.Wwwroot;

/// <summary>
/// Guard rails for Settings → Backups, the UI over the administrator-only
/// <c>/api/backups</c> endpoints. A restore replaces the whole database and a
/// delete cannot be undone, so the destructive actions must be confirmed, and
/// the section must stay hidden from users who cannot administer.
/// </summary>
public class BackupsUiTests
{
    private static string IndexHtml => WwwrootFiles.Read("index.html");
    private static string Scripts => WwwrootFiles.ReadAppScripts();

    private static readonly string[] BackupFunctions =
    {
        "updateBackupsSectionVisibility",
        "fetchBackupsApi",
        "loadBackups",
        "renderBackupsList",
        "createBackup",
        "downloadBackup",
        "deleteBackup",
        "restoreBackup",
        "restoreBackupFromFile",
        "cancelPendingRestore",
    };

    [Fact]
    public void SettingsPage_HasBackupsSectionAndNavLink()
    {
        var section = ExtractSection(IndexHtml, "settings-backups");
        Assert.Contains("class=\"settings-card\"", section);
        Assert.Contains(">Backups</h3>", section);
        Assert.Contains("id=\"backupsList\"", section);
        Assert.Contains("onclick=\"createBackup()\"", section);
        Assert.Contains("Create backup now", section);

        var settingsView = IndexHtml[IndexHtml.IndexOf("id=\"settingsView\"", StringComparison.Ordinal)..];
        Assert.True(settingsView.IndexOf("id=\"settings-backups\"", StringComparison.Ordinal) > 0,
            "The Backups section belongs to the Settings page");

        Assert.Matches(
            new Regex("id=\"settingsNavBackups\"[^>]*data-settings-target=\"settings-backups\"[^>]*onclick=\"scrollToSettingsSection\\('settings-backups'\\)\""),
            IndexHtml);
    }

    [Fact]
    public void RestoreFromFile_AcceptsOnlyDatabaseFiles()
    {
        var section = ExtractSection(IndexHtml, "settings-backups");
        Assert.Matches(new Regex("<input type=\"file\" id=\"restoreBackupFileInput\" accept=\"\\.db\"[^>]*onchange=\"restoreBackupFromFile\\(this\\)\""), section);

        var body = ExtractFunction(Scripts, "restoreBackupFromFile");
        Assert.Contains("\\.db$", body);
    }

    [Fact]
    public void RestorePendingBanner_SaysRestartRequiredAndCanBeCancelled()
    {
        var section = ExtractSection(IndexHtml, "settings-backups");
        Assert.Matches(new Regex("id=\"backupsRestorePending\"[^>]*hidden"), section);
        Assert.Contains("Restart required", section);
        Assert.Contains("onclick=\"cancelPendingRestore()\"", section);

        Assert.Contains("data.restore_pending", ExtractFunction(Scripts, "loadBackups"));
        var cancel = ExtractFunction(Scripts, "cancelPendingRestore");
        Assert.Contains("'/api/backups/restore-pending'", cancel);
        Assert.Contains("method: 'DELETE'", cancel);
    }

    [Fact]
    public void BackupActions_CallTheBackupsEndpoints()
    {
        Assert.Contains("fetchBackupsApi('/api/backups')", ExtractFunction(Scripts, "loadBackups"));

        var create = ExtractFunction(Scripts, "createBackup");
        Assert.Contains("fetchBackupsApi('/api/backups', { method: 'POST' })", create);

        Assert.Contains("`/api/backups/${encodeURIComponent(name)}/download`", ExtractFunction(Scripts, "downloadBackup"));

        var delete = ExtractFunction(Scripts, "deleteBackup");
        Assert.Contains("`/api/backups/${encodeURIComponent(name)}`", delete);
        Assert.Contains("method: 'DELETE'", delete);

        var restore = ExtractFunction(Scripts, "restoreBackup");
        Assert.Contains("`/api/backups/${encodeURIComponent(name)}/restore`", restore);
        Assert.Contains("method: 'POST'", restore);

        var upload = ExtractFunction(Scripts, "restoreBackupFromFile");
        Assert.Contains("'/api/backups/upload-restore'", upload);
        Assert.Contains("formData.append('file', file)", upload);
        Assert.Contains("method: 'POST'", upload);
    }

    [Fact]
    public void FileUpload_LetsTheBrowserSetTheMultipartContentType()
    {
        // getAuthHeaders() defaults Content-Type to JSON, which would break
        // the multipart boundary; the helper must not merge it back in.
        var upload = ExtractFunction(Scripts, "restoreBackupFromFile");
        Assert.Contains("delete headers['Content-Type']", upload);

        var fetchHelper = ExtractFunction(Scripts, "fetchBackupsApi");
        Assert.Contains("headers: options.headers || getAuthHeaders()", fetchHelper);
        Assert.Contains("handleAuthError(response)", fetchHelper);
    }

    [Theory]
    [InlineData("deleteBackup")]
    [InlineData("restoreBackup")]
    [InlineData("restoreBackupFromFile")]
    public void DestructiveActions_AreConfirmedBeforeTheRequest(string functionName)
    {
        var body = ExtractFunction(Scripts, functionName);
        var confirmIndex = Regex.Match(body, @"if \(!confirm(Restore)?\(").Index;
        var fetchIndex = body.IndexOf("fetchBackupsApi(", StringComparison.Ordinal);

        Assert.True(confirmIndex > 0, $"{functionName}() must ask for confirmation");
        Assert.True(fetchIndex > confirmIndex, $"{functionName}() must confirm before calling the API");
    }

    [Fact]
    public void RestoreConfirmation_WarnsAboutRestartAndSafetyBackup()
    {
        var confirmRestore = ExtractFunction(Scripts, "confirmRestore");
        Assert.Contains("confirm(", confirmRestore);
        Assert.Contains("safety backup", confirmRestore);
        Assert.Contains("restarts", confirmRestore);
    }

    [Fact]
    public void Download_UsesAnAuthenticatedBlobRequest()
    {
        var download = ExtractFunction(Scripts, "downloadBackup");
        Assert.Contains("fetchBackupsApi(", download);
        Assert.Contains("response.blob()", download);
        Assert.Contains("URL.createObjectURL(blob)", download);
        Assert.Contains("link.download = name", download);
        Assert.Contains("URL.revokeObjectURL(url)", download);
        Assert.DoesNotContain("window.location", download);
        Assert.DoesNotContain("window.open", download);

        Assert.Contains("credentials: 'include'", ExtractFunction(Scripts, "fetchBackupsApi"));
    }

    [Fact]
    public void Section_IsAdministratorOnly()
    {
        var visibility = ExtractFunction(Scripts, "updateBackupsSectionVisibility");
        Assert.Contains("!userCapabilities.canAdminister", visibility);
        Assert.Contains("'settings-backups'", visibility);
        Assert.Contains("'settingsNavBackups'", visibility);

        Assert.Contains("updateBackupsSectionVisibility();", ExtractFunction(Scripts, "applyCapabilities"));

        // A 403 (e.g. before capabilities resolved) hides the section instead of erroring.
        var forbidden = ExtractFunction(Scripts, "handleBackupsForbidden");
        Assert.Contains("error.status === 403", forbidden);
        Assert.Contains("backupsForbidden = true", forbidden);

        Assert.Contains("loadBackups();", ExtractFunction(Scripts, "loadSettings"));
    }

    [Fact]
    public void BackupsUi_BuildsDomWithHtmlTemplates()
    {
        var list = ExtractFunction(Scripts, "renderBackupsList");
        Assert.Contains("renderHtml(list, html`", list);
        Assert.Contains("onclick=\"downloadBackup('${jsArg(backup.name)}')\"", list);
        Assert.Contains("onclick=\"restoreBackup('${jsArg(backup.name)}')\"", list);
        Assert.Contains("onclick=\"deleteBackup('${jsArg(backup.name)}')\"", list);
        Assert.Contains("uiIcon('download')", list);
        Assert.Contains("uiIcon('trash')", list);

        foreach (var name in BackupFunctions)
        {
            Assert.DoesNotContain("innerHTML", ExtractFunction(Scripts, name));
        }
    }

    [Fact]
    public void BackupsUi_UsesSpriteIconsNotEmoji()
    {
        var section = ExtractSection(IndexHtml, "settings-backups");
        var code = section + string.Concat(BackupFunctions.Select(name => ExtractFunction(Scripts, name)));
        Assert.DoesNotMatch(new Regex(@"[\u2600-\u27BF]|\p{Cs}"), code);
        Assert.Contains("<use href=\"#i-hard-drive\"/>", section);
    }

    private static string ExtractSection(string html, string id)
    {
        var start = html.IndexOf($"<section class=\"settings-card\" id=\"{id}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"Expected index.html to contain section#{id}");
        var end = html.IndexOf("</section>", start, StringComparison.Ordinal);
        Assert.True(end > start, $"Expected section#{id} to be closed");
        return html[start..end];
    }

    /// <summary>
    /// Returns the source of a function declaration by brace matching from the
    /// body's opening brace (the first <c>) {</c>, so default parameter values
    /// such as <c>options = {}</c> are skipped).
    /// </summary>
    private static string ExtractFunction(string source, string name)
    {
        var match = Regex.Match(source, $@"(?:async\s+)?function {Regex.Escape(name)}\(");
        Assert.True(match.Success, $"Expected the app scripts to declare {name}()");
        var start = match.Index;

        var bodyMatch = new Regex(@"\)\s*\{").Match(source, start);
        Assert.True(bodyMatch.Success, $"Expected a body for {name}()");
        var braceStart = bodyMatch.Index + bodyMatch.Length - 1;

        var depth = 0;
        for (var i = braceStart; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source[start..(i + 1)];
                }
            }
        }

        throw new InvalidOperationException($"Unbalanced braces while extracting {name}()");
    }
}
