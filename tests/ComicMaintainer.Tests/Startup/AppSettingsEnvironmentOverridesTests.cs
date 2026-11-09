using System.Text.Json;
using System.Text.RegularExpressions;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using ComicMaintainer.WebApi.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ComicMaintainer.Tests.Startup;

/// <summary>
/// The table of environment overrides, and what it drives besides applying them: which settings
/// <c>GET /api/settings</c> reports as pinned and which saves are refused. These tests change
/// process environment variables, so they run in the non-parallel environment collection.
/// </summary>
[Collection(AppSettingsEnvironmentPostConfigureTests.EnvironmentCollection.Name)]
public sealed class AppSettingsEnvironmentOverridesTests : IDisposable
{
    private readonly AppSettingsEnvironmentPostConfigureTests.EnvironmentScope _environment =
        new(AppSettingsEnvironmentOverrides.All.Select(o => o.Variable).ToArray());

    private readonly string _configDir = Path.Join(Path.GetTempPath(), $"SettingsEnvLocks_{Guid.NewGuid():N}");
    private readonly AppSettings _settings;
    private readonly SettingsService _service;

    public AppSettingsEnvironmentOverridesTests()
    {
        _settings = new AppSettings { ConfigDirectory = _configDir };
        _service = new SettingsService(
            NullLogger<SettingsService>.Instance,
            new TestOptionsMonitor<AppSettings>(_settings),
            new ConfigurationBuilder().Build());
    }

    public void Dispose()
    {
        _environment.Dispose();
        if (Directory.Exists(_configDir))
        {
            Directory.Delete(_configDir, recursive: true);
        }
    }

    private string SettingsFile => Path.Join(_configDir, "user-settings.json");

    private JsonElement SavedSection()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(SettingsFile));
        return document.RootElement.GetProperty("AppSettings").Clone();
    }

    private static SettingsController CreateController(ISettingsService? settingsService = null) =>
        new(
            new TestOptionsMonitor<AppSettings>(new AppSettings()),
            NullLogger<SettingsController>.Instance,
            Mock.Of<IDbContextFactory<ComicMaintainerDbContext>>(),
            Mock.Of<IBatchJobRegistry>(),
            Mock.Of<IFileStoreService>(),
            settingsService ?? Mock.Of<ISettingsService>(),
            Mock.Of<IHostApplicationLifetime>(),
            Mock.Of<ISeriesLanguagePreferenceRetagService>(),
            Mock.Of<IEventBroadcaster>(),
            Mock.Of<IProcessingHistoryService>());

    private static string WwwrootFile(string name)
    {
        var current = AppContext.BaseDirectory;
        for (var i = 0; i < 10 && current != null; i++)
        {
            var candidate = Path.Join(current, "src", "ComicMaintainer.WebApi", "wwwroot", name);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = Path.GetDirectoryName(current);
        }

        throw new FileNotFoundException(name);
    }

    private static string[] EnvLockFields(JsonElement json) =>
        json.GetProperty("env_locked").EnumerateArray().Select(e => e.GetProperty("field").GetString()!).ToArray();

    [Fact]
    public void VariablesAndApiFields_AreUnique()
    {
        var variables = AppSettingsEnvironmentOverrides.All.Select(o => o.Variable).ToArray();
        var fields = AppSettingsEnvironmentOverrides.All.Where(o => o.ApiField is not null).Select(o => o.ApiField).ToArray();

        Assert.Equal(variables.Length, variables.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(fields.Length, fields.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void EveryApiField_IsAFieldOfGetAllSettings()
    {
        var json = JsonSerializer.SerializeToElement(((OkObjectResult)CreateController().GetAllSettings().Result!).Value);

        Assert.All(
            AppSettingsEnvironmentOverrides.All.Where(o => o.ApiField is not null),
            o => Assert.True(json.TryGetProperty(o.ApiField!, out _), $"{o.ApiField} is not in GET /api/settings"));
    }

    [Fact]
    public void TheSettingsPageMapsExactlyTheApiFieldsToInputsThatExist()
    {
        var script = File.ReadAllText(WwwrootFile(Path.Join("js", "app", "settings.js")));
        var html = File.ReadAllText(WwwrootFile("index.html"));
        var table = Regex.Match(script, @"SETTINGS_ENV_LOCK_INPUTS = Object\.freeze\(\{(.*?)\}\);", RegexOptions.Singleline).Groups[1].Value;
        var entries = Regex.Matches(table, @"(\w+): \[([^\]]*)\]")
            .ToDictionary(m => m.Groups[1].Value, m => Regex.Matches(m.Groups[2].Value, @"'(\w+)'").Select(id => id.Groups[1].Value).ToArray());

        var apiFields = AppSettingsEnvironmentOverrides.All.Where(o => o.ApiField is not null).Select(o => o.ApiField!).ToHashSet();

        Assert.Equal(apiFields.OrderBy(f => f), entries.Keys.OrderBy(f => f));
        Assert.All(entries.Values.SelectMany(ids => ids), id =>
            Assert.Contains($"id=\"{id}\"", html, StringComparison.Ordinal));
    }

    [Fact]
    public void UiFields_CoverEveryOverrideExceptTheDirectoriesRateLimitsAndTimeout()
    {
        var withoutUiField = AppSettingsEnvironmentOverrides.All.Where(o => o.ApiField is null).Select(o => o.Variable).ToArray();

        Assert.Equal(
            new[]
            {
                "ANILIST_REQUESTS_PER_MINUTE", "BASE_PATH", "COMICVINE_REQUESTS_PER_HOUR", "DUPLICATE_DIR",
                "MANGADEX_REQUESTS_PER_SECOND", "METADATA_PROVIDER_TIMEOUT_SECONDS", "SERIES_IMAGE_CACHE_DIR", "WATCHED_DIR",
            },
            withoutUiField.OrderBy(v => v, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("SMTP_PORT", "2525", true)]
    [InlineData("SMTP_PORT", "not-a-number", false)]
    [InlineData("SMTP_PORT", "70000", false)]
    [InlineData("SMTP_PORT", "0", false)]
    [InlineData("SMTP_HOST", "", false)]
    [InlineData("SMTP_USE_SSL", "no", true)]
    [InlineData("EMAIL_MAX_SENDS_PER_HOUR", "0", true)]
    public void Active_ReportsAVariableOnlyWhenItForcesAValue(string variable, string value, bool active)
    {
        _environment.Set(variable, value);

        Assert.Equal(active, AppSettingsEnvironmentOverrides.Active().Any(o => o.Variable == variable));
    }

    [Fact]
    public void GetAllSettings_ListsThePinnedFieldsAndTheirVariables()
    {
        _environment.Set("SMTP_HOST", "mail.example");
        _environment.Set("SMTP_PASSWORD", "secret");
        _environment.Set("SMTP_PORT", "not-a-number");
        _environment.Set("WATCHED_DIR", "/library");

        var json = JsonSerializer.SerializeToElement(((OkObjectResult)CreateController().GetAllSettings().Result!).Value);

        var entries = json.GetProperty("env_locked").EnumerateArray()
            .ToDictionary(e => e.GetProperty("field").GetString()!, e => e.GetProperty("variable").GetString()!);
        Assert.Equal(
            new Dictionary<string, string> { ["smtp_host"] = "SMTP_HOST", ["smtp_password_set"] = "SMTP_PASSWORD" },
            entries);
    }

    [Fact]
    public void GetAllSettings_WithNothingPinned_ReturnsAnEmptyList()
    {
        var json = JsonSerializer.SerializeToElement(((OkObjectResult)CreateController().GetAllSettings().Result!).Value);

        Assert.Empty(EnvLockFields(json));
    }

    [Fact]
    public async Task Save_ChangingAPinnedSetting_IsRefusedAndWritesNothing()
    {
        _environment.Set("SMTP_HOST", "pinned.example");
        _environment.Set("SMTP_USE_SSL", "true");

        var ex = await Assert.ThrowsAsync<SettingsLockedException>(() => _service.UpdateSettingsAsync(new SettingsUpdate
        {
            Email = new EmailUpdate(SmtpHost: "other.example", SmtpUseSsl: false, SmtpPort: 25),
        }));

        Assert.Equal(new[] { "SMTP_HOST", "SMTP_USE_SSL" }, ex.Overrides.Select(o => o.Variable).OrderBy(v => v, StringComparer.Ordinal));
        Assert.Contains("SMTP_HOST", ex.Message, StringComparison.Ordinal);
        Assert.Contains("SMTP_USE_SSL", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(SettingsFile));
    }

    [Fact]
    public async Task Save_TheSameValueAsThePinnedOne_IsNotWrittenAndNotRefused()
    {
        // Sending a whole group back must work, but the environment's value must not be copied
        // into the file, where it would outlive the variable.
        _environment.Set("SMTP_HOST", "pinned.example");
        _environment.Set("SMTP_PORT", "2525");
        _environment.Set("SUWAYOMI_BASE_URL", "http://suwayomi.example:4567/");
        _settings.SuwayomiBaseUrl = "http://suwayomi.example:4567/";

        await _service.UpdateSettingsAsync(new SettingsUpdate
        {
            Email = new EmailUpdate(SmtpHost: " pinned.example ", SmtpPort: 2525, SmtpUsername: "user"),
            Suwayomi = new SuwayomiUpdate(Enabled: true, BaseUrl: "http://suwayomi.example:4567"),
        });

        var saved = SavedSection();
        Assert.Equal("user", saved.GetProperty("SmtpUsername").GetString());
        Assert.True(saved.GetProperty("EnableSuwayomi").GetBoolean());
        Assert.False(saved.TryGetProperty("SmtpHost", out _));
        Assert.False(saved.TryGetProperty("SmtpPort", out _));
        Assert.False(saved.TryGetProperty("SuwayomiBaseUrl", out _));
    }

    [Fact]
    public async Task Save_OnlyPinnedValuesThatAreUnchanged_DoesNotWriteAtAll()
    {
        _environment.Set("SMTP_PORT", "2525");

        await _service.UpdateEmailRateLimitSettingsAsync(1, 2, 3);
        File.Delete(SettingsFile);

        await _service.UpdateSettingsAsync(new SettingsUpdate { Email = new EmailUpdate(SmtpPort: 2525) });

        Assert.False(File.Exists(SettingsFile));
    }

    [Theory]
    [InlineData("the-real-secret")] // an equal value must not be distinguishable from a wrong guess
    [InlineData("a-wrong-guess")]
    [InlineData("")]                // clearing it
    public async Task Save_AnyWriteToAPinnedSecret_IsRefused(string password)
    {
        _environment.Set("SMTP_PASSWORD", "the-real-secret");

        var ex = await Assert.ThrowsAsync<SettingsLockedException>(() => _service.UpdateSettingsAsync(new SettingsUpdate
        {
            Email = new EmailUpdate(SmtpPassword: password),
        }));

        Assert.Equal("SMTP_PASSWORD", Assert.Single(ex.Overrides).Variable);
    }

    [Fact]
    public async Task Save_APinnedSecretLeftOut_IsAllowed()
    {
        _environment.Set("SMTP_PASSWORD", "the-real-secret");

        await _service.UpdateSettingsAsync(new SettingsUpdate { Email = new EmailUpdate(SmtpPort: 25) });

        Assert.False(SavedSection().TryGetProperty("SmtpPassword", out _));
    }

    [Fact]
    public async Task Save_AVariableWithAnUnusableValue_DoesNotPinTheSetting()
    {
        _environment.Set("SMTP_PORT", "not-a-number");

        await _service.UpdateSettingsAsync(new SettingsUpdate { Email = new EmailUpdate(SmtpPort: 465) });

        Assert.Equal(465, SavedSection().GetProperty("SmtpPort").GetInt32());
    }

    [Fact]
    public async Task Save_APinnedValueTheFormCouldNotEnterLeftOut_DoesNotBlockTheRestOfTheGroup()
    {
        // The environment accepts sizes the Settings page does not (EMAIL_MAX_ATTACHMENT_MB=500).
        _environment.Set("EMAIL_MAX_ATTACHMENT_MB", "500");

        await _service.UpdateSettingsAsync(new SettingsUpdate { Email = new EmailUpdate(SmtpPort: 25) });

        Assert.Equal(25, SavedSection().GetProperty("SmtpPort").GetInt32());
    }

    [Fact]
    public async Task TheOpdsCatalogIsOffUntilSwitchedOnAndSaves()
    {
        Assert.False(new AppSettings().OpdsEnabled);

        await _service.UpdateSettingsAsync(new SettingsUpdate { Opds = new OpdsUpdate(Enabled: true) });

        Assert.True(SavedSection().GetProperty(nameof(AppSettings.OpdsEnabled)).GetBoolean());
    }

    [Fact]
    public async Task TheOpdsSwitch_WhenPinned_IsRefused()
    {
        _environment.Set("OPDS_ENABLED", "true");

        var ex = await Assert.ThrowsAsync<SettingsLockedException>(() => _service.UpdateSettingsAsync(new SettingsUpdate
        {
            Opds = new OpdsUpdate(Enabled: false),
        }));

        Assert.Equal("OPDS_ENABLED", Assert.Single(ex.Overrides).Variable);
        Assert.Contains("opds_enabled", EnvLockFields(JsonSerializer.SerializeToElement(((OkObjectResult)CreateController().GetAllSettings().Result!).Value)));
    }

    [Fact]
    public async Task TheOpdsGroup_LeftEmpty_ChangesNothing()
    {
        await _service.UpdateSettingsAsync(new SettingsUpdate { Opds = new OpdsUpdate() });

        Assert.False(File.Exists(SettingsFile));
    }

    [Fact]
    public async Task SingleSetting_APinnedChange_IsRefused()
    {
        _environment.Set("DOWNLOAD_EXTERNAL_SERIES_IMAGES", "false");

        await Assert.ThrowsAsync<SettingsLockedException>(() => _service.UpdateDownloadExternalSeriesImagesAsync(true));
    }

    [Fact]
    public async Task GroupEndpointSave_APinnedChange_IsRefused()
    {
        _environment.Set("ENABLE_MANGADEX_METADATA", "true");

        var ex = await Assert.ThrowsAsync<SettingsLockedException>(() => _service.UpdateExternalSeriesMetadataSettingsAsync(
            true, null, null, enableMangaDex: false, null, true, null));

        Assert.Equal("ENABLE_MANGADEX_METADATA", Assert.Single(ex.Overrides).Variable);
    }

    [Theory]
    [InlineData("email", "SMTP_HOST", "pinned.example", "SmtpHost")]
    [InlineData("email", "SMTP_PORT", "2525", "SmtpPort")]
    [InlineData("email", "SMTP_USERNAME", "pinned-user", "SmtpUsername")]
    [InlineData("email", "SMTP_USE_SSL", "true", "SmtpUseSsl")]
    [InlineData("email", "SMTP_ALLOW_INSECURE", "true", "SmtpAllowInsecure")]
    [InlineData("email", "EMAIL_FROM_ADDRESS", "pinned@example.com", "EmailFromAddress")]
    [InlineData("email", "EMAIL_FROM_NAME", "Pinned", "EmailFromName")]
    [InlineData("email", "EMAIL_MAX_ATTACHMENT_MB", "500", "EmailMaxAttachmentMegabytes")]
    [InlineData("email-rate-limit", "EMAIL_MAX_SENDS_PER_HOUR", "5", "EmailMaxSendsPerHour")]
    [InlineData("email-rate-limit", "EMAIL_MAX_SENDS_PER_DAY", "5", "EmailMaxSendsPerDay")]
    [InlineData("email-rate-limit", "EMAIL_MIN_SECONDS_BETWEEN_SENDS", "5", "EmailMinSecondsBetweenSends")]
    [InlineData("suwayomi", "ENABLE_SUWAYOMI", "true", "EnableSuwayomi")]
    [InlineData("suwayomi", "SUWAYOMI_BASE_URL", "http://suwayomi.example:4567", "SuwayomiBaseUrl")]
    [InlineData("suwayomi", "SUWAYOMI_USERNAME", "pinned-user", "SuwayomiUsername")]
    [InlineData("error-reporting", "ERROR_REPORTING_ENABLED", "true", "ErrorReportingEnabled")]
    [InlineData("error-reporting", "ERROR_REPORTING_ASSIGNEE", "octocat", "ErrorReportingAssignee")]
    [InlineData("error-reporting", "ERROR_REPORTING_MAX_ISSUES_PER_DAY", "3", "ErrorReportingMaxIssuesPerDay")]
    [InlineData("error-reporting", "ERROR_REPORTING_DEDUPE_WINDOW_HOURS", "3", "ErrorReportingDedupeWindowHours")]
    [InlineData("error-reporting", "ERROR_REPORTING_COMMENT_ON_RECURRENCE", "false", "ErrorReportingCommentOnRecurrence")]
    [InlineData("external-series-metadata", "ENABLE_EXTERNAL_SERIES_METADATA", "true", "EnableExternalSeriesMetadata")]
    [InlineData("external-series-metadata", "ENABLE_MANGADEX_METADATA", "true", "EnableMangaDexMetadata")]
    [InlineData("external-series-metadata", "ENABLE_ANILIST_METADATA", "true", "EnableAniListMetadata")]
    public async Task GroupEndpoint_AFieldLeftOut_IsNotAWriteToThePinnedSetting(string group, string variable, string value, string settingName)
    {
        _environment.Set(variable, value);

        var result = await SendEmptyRequestTo(group);

        Assert.IsType<OkObjectResult>(result);
        var saved = SavedSection();
        Assert.False(saved.TryGetProperty(settingName, out _), $"{settingName} was written");
        Assert.NotEmpty(saved.EnumerateObject());
    }

    [Fact]
    public async Task GroupEndpoint_AFieldLeftOut_IsReadAsBlankOffOrItsDefaultWhenNothingPinsIt()
    {
        foreach (var group in new[] { "email", "email-rate-limit", "suwayomi", "error-reporting", "external-series-metadata" })
        {
            Assert.IsType<OkObjectResult>(await SendEmptyRequestTo(group));
        }

        var saved = SavedSection();
        Assert.Equal(JsonValueKind.Null, saved.GetProperty("SmtpHost").ValueKind);
        Assert.Equal(587, saved.GetProperty("SmtpPort").GetInt32());
        Assert.False(saved.GetProperty("SmtpUseSsl").GetBoolean());
        Assert.Equal("ComicMaintainer", saved.GetProperty("EmailFromName").GetString());
        Assert.Equal(25, saved.GetProperty("EmailMaxAttachmentMegabytes").GetInt32());
        Assert.Equal(0, saved.GetProperty("EmailMaxSendsPerHour").GetInt32());
        Assert.False(saved.GetProperty("EnableSuwayomi").GetBoolean());
        Assert.Equal(JsonValueKind.Null, saved.GetProperty("SuwayomiBaseUrl").ValueKind);
        Assert.False(saved.GetProperty("ErrorReportingEnabled").GetBoolean());
        Assert.Equal(10, saved.GetProperty("ErrorReportingMaxIssuesPerDay").GetInt32());
        Assert.Equal(24, saved.GetProperty("ErrorReportingDedupeWindowHours").GetInt32());
        Assert.True(saved.GetProperty("ErrorReportingCommentOnRecurrence").GetBoolean());
        Assert.False(saved.GetProperty("EnableMangaDexMetadata").GetBoolean());
    }

    [Fact]
    public async Task GroupEndpoint_APinnedFieldThatIsSentDifferently_IsStillRefused()
    {
        _environment.Set("SMTP_PORT", "2525");
        var controller = CreateController(_service);

        var result = await controller.UpdateEmailSettings(new SettingsController.EmailSettingsRequest { SmtpPort = 587 });

        Assert.IsType<ConflictObjectResult>(result);
        Assert.False(File.Exists(SettingsFile));
    }

    private Task<ActionResult> SendEmptyRequestTo(string group)
    {
        var controller = CreateController(_service);
        return group switch
        {
            "email" => controller.UpdateEmailSettings(new SettingsController.EmailSettingsRequest()),
            "email-rate-limit" => controller.UpdateEmailRateLimitSettings(new SettingsController.EmailRateLimitSettingsRequest()),
            "suwayomi" => controller.UpdateSuwayomiSettings(new SettingsController.SuwayomiSettingsRequest()),
            "error-reporting" => controller.UpdateErrorReportingSettings(new SettingsController.ErrorReportingSettingsRequest()),
            "external-series-metadata" => controller.UpdateExternalSeriesMetadata(new SettingsController.ExternalSeriesMetadataSettingsRequest()),
            _ => throw new ArgumentOutOfRangeException(nameof(group), group, null),
        };
    }

    [Fact]
    public async Task Save_AWholeEmailGroupBackWithPinnedValuesASaveWouldReject_IsAccepted()
    {
        _environment.Set("EMAIL_MAX_ATTACHMENT_MB", "500");
        _environment.Set("EMAIL_FROM_ADDRESS", "not-an-address");

        await _service.UpdateSettingsAsync(new SettingsUpdate
        {
            Email = new EmailUpdate(SmtpPort: 25, EmailFromAddress: "not-an-address", EmailMaxAttachmentMb: 500),
        });

        var saved = SavedSection();
        Assert.Equal(25, saved.GetProperty("SmtpPort").GetInt32());
        Assert.False(saved.TryGetProperty("EmailMaxAttachmentMegabytes", out _));
        Assert.False(saved.TryGetProperty("EmailFromAddress", out _));
    }

    [Fact]
    public async Task Save_AWholeErrorReportingGroupBackWithPinnedValuesASaveWouldReject_IsAccepted()
    {
        _environment.Set("ERROR_REPORTING_ASSIGNEE", "not a login!");
        _environment.Set("ERROR_REPORTING_MAX_ISSUES_PER_DAY", "900");
        _environment.Set("ERROR_REPORTING_DEDUPE_WINDOW_HOURS", "9000");

        await _service.UpdateSettingsAsync(new SettingsUpdate
        {
            ErrorReporting = new ErrorReportingUpdate(Assignee: "not a login!", MaxIssuesPerDay: 900, DedupeWindowHours: 9000, CommentOnRecurrence: false),
        });

        var saved = SavedSection();
        Assert.False(saved.GetProperty("ErrorReportingCommentOnRecurrence").GetBoolean());
        Assert.False(saved.TryGetProperty("ErrorReportingAssignee", out _));
        Assert.False(saved.TryGetProperty("ErrorReportingMaxIssuesPerDay", out _));
        Assert.False(saved.TryGetProperty("ErrorReportingDedupeWindowHours", out _));
    }

    [Fact]
    public async Task Save_AWholeSuwayomiGroupBackWithPinnedValuesASaveWouldReject_IsAccepted()
    {
        _environment.Set("SUWAYOMI_BASE_URL", "ftp://pinned.example");
        _environment.Set("SUWAYOMI_DOWNLOADS_DIR", "/no/such/folder");
        _environment.Set("SUWAYOMI_IMPORT_DIR", "../outside");

        await _service.UpdateSettingsAsync(new SettingsUpdate
        {
            Suwayomi = new SuwayomiUpdate(BaseUrl: "ftp://pinned.example/", Username: "reader", DownloadsDirectory: "/no/such/folder", ImportDirectory: "../outside"),
        });

        var saved = SavedSection();
        Assert.Equal("reader", saved.GetProperty("SuwayomiUsername").GetString());
        Assert.False(saved.TryGetProperty("SuwayomiBaseUrl", out _));
        Assert.False(saved.TryGetProperty("SuwayomiDownloadsDirectory", out _));
        Assert.False(saved.TryGetProperty("SuwayomiImportDirectory", out _));
    }

    [Fact]
    public async Task Save_APinnedSentinelFileASaveWouldReject_IsAccepted()
    {
        var sentinel = new string('s', DatabaseCleanupSettings.MaxSentinelFileLength + 1);
        _environment.Set("STALE_CLEANUP_SENTINEL_FILE", sentinel);

        await _service.UpdateSettingsAsync(new SettingsUpdate
        {
            DatabaseCleanup = new DatabaseCleanupUpdate(IntervalHours: 2, StaleCleanupSentinelFile: sentinel),
        });

        var saved = SavedSection();
        Assert.Equal(2, saved.GetProperty("DatabaseCleanupIntervalHours").GetInt32());
        Assert.False(saved.TryGetProperty("StaleCleanupSentinelFile", out _));
    }

    [Fact]
    public async Task SeriesImageLimits_APinnedPairInTheWrongOrder_IsAcceptedWhenSentBack()
    {
        _environment.Set("SERIES_IMAGE_MAX_BYTES", (30 * 1048576).ToString());
        _environment.Set("SERIES_IMAGE_MAX_DOWNLOAD_BYTES", (20 * 1048576).ToString());

        var result = await CreateController(_service).UpdateSeriesImageLimits(
            new SettingsController.SeriesImageLimitsRequest { MaxMB = 30, MaxDownloadMB = 20, MaxDimension = 900 });

        Assert.IsType<OkObjectResult>(result);
        var saved = SavedSection();
        Assert.Equal(900, saved.GetProperty("SeriesImageMaxDimension").GetInt32());
        Assert.False(saved.TryGetProperty("SeriesImageMaxBytes", out _));
        Assert.False(saved.TryGetProperty("SeriesImageMaxDownloadBytes", out _));
    }

    [Fact]
    public async Task SeriesImageLimits_TheWrongOrderWithNothingPinned_IsRefused()
    {
        var result = await CreateController(_service).UpdateSeriesImageLimits(
            new SettingsController.SeriesImageLimitsRequest { MaxMB = 30, MaxDownloadMB = 20, MaxDimension = 900 });

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.False(File.Exists(SettingsFile));
    }

    [Fact]
    public async Task Save_AnOutOfRangeValueOtherThanThePinnedOne_IsStillValidated()
    {
        _environment.Set("EMAIL_MAX_ATTACHMENT_MB", "500");

        await Assert.ThrowsAsync<SettingsValidationException>(() => _service.UpdateSettingsAsync(new SettingsUpdate
        {
            Email = new EmailUpdate(EmailMaxAttachmentMb: 900),
        }));

        Assert.False(File.Exists(SettingsFile));
    }

    [Fact]
    public async Task ErrorReporting_EnablingWithAPinnedTokenLeftOut_IsAllowed()
    {
        _environment.Set("ERROR_REPORTING_GITHUB_TOKEN", "ghp_pinned");

        await _service.UpdateSettingsAsync(new SettingsUpdate
        {
            ErrorReporting = new ErrorReportingUpdate(Enabled: true, MaxIssuesPerDay: 2),
        });

        var saved = SavedSection();
        Assert.True(saved.GetProperty("ErrorReportingEnabled").GetBoolean());
        Assert.False(saved.TryGetProperty("ErrorReportingGitHubToken", out _));
    }

    [Fact]
    public async Task Controller_APinnedChange_Answers409WithTheVariablesAndAnError()
    {
        _environment.Set("SMTP_HOST", "pinned.example");
        var controller = CreateController(_service);

        var result = await controller.UpdateEmailSettings(new SettingsController.EmailSettingsRequest { SmtpHost = "other.example", SmtpPort = 25 });

        var json = JsonSerializer.SerializeToElement(Assert.IsType<ConflictObjectResult>(result).Value);
        Assert.Equal(409, json.GetProperty("status").GetInt32());
        Assert.Contains("SMTP_HOST", json.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Contains("SMTP_HOST", json.GetProperty("detail").GetString(), StringComparison.Ordinal);
        var entry = Assert.Single(json.GetProperty("env_locked").EnumerateArray());
        Assert.Equal("smtp_host", entry.GetProperty("field").GetString());
        Assert.Equal("SMTP_HOST", entry.GetProperty("variable").GetString());
    }
}
