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
    private readonly SettingsService _service;

    public AppSettingsEnvironmentOverridesTests()
    {
        _service = new SettingsService(
            NullLogger<SettingsService>.Instance,
            new TestOptionsMonitor<AppSettings>(new AppSettings { ConfigDirectory = _configDir }),
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
            Mock.Of<IComicProcessorService>(),
            Mock.Of<IFileStoreService>(),
            settingsService ?? Mock.Of<ISettingsService>(),
            Mock.Of<IHostApplicationLifetime>());

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
