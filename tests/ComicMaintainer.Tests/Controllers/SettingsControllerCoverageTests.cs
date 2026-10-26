using System.Linq.Expressions;
using System.Security.Claims;
using System.Text.Json;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.ErrorReporting;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Tests.Helpers;
using ComicMaintainer.WebApi.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;

namespace ComicMaintainer.Tests.Controllers;

public class SettingsControllerCoverageTests
{
    private const int BytesPerMb = 1048576;

    private readonly AppSettings _settings = new()
    {
        FilenameFormat = "{series} #{issue}",
        IssueNumberPadding = 3,
        LogMaxBytes = 5 * BytesPerMb,
        DatabaseCleanupIntervalHours = 6,
        DefaultLibraryView = "series",
        DefaultPreferredLanguage = "en",
        SeriesImageMaxBytes = 3 * BytesPerMb + BytesPerMb / 2,
        SeriesImageMaxDownloadBytes = 8 * BytesPerMb,
        SeriesImageMaxDimension = 1600,
    };

    private readonly Mock<ILogger<SettingsController>> _logger = new();
    private readonly Mock<IDbContextFactory<ComicMaintainerDbContext>> _dbFactory = new(MockBehavior.Strict);
    private readonly Mock<IComicProcessorService> _processor = new();
    private readonly Mock<IFileStoreService> _fileStore = new();
    private readonly Mock<ISettingsService> _settingsService = new();
    private readonly Mock<IHostApplicationLifetime> _lifetime = new();
    private readonly Mock<ISeriesLanguagePreferenceRetagService> _retag = new();
    private readonly Mock<IEventBroadcaster> _broadcaster = new();

    private SettingsController CreateController(
        bool withRetag = false,
        bool withBroadcaster = false,
        IDbContextFactory<ComicMaintainerDbContext>? dbFactory = null) =>
        new(
            new TestOptionsMonitor<AppSettings>(_settings),
            _logger.Object,
            dbFactory ?? _dbFactory.Object,
            _processor.Object,
            _fileStore.Object,
            _settingsService.Object,
            _lifetime.Object,
            withRetag ? _retag.Object : null,
            withBroadcaster ? _broadcaster.Object : null);

    private static JsonElement OkJson(ActionResult<object> result) =>
        JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result.Result).Value);

    private static JsonElement OkJson(ActionResult result) =>
        JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value);

    private static string BadRequestError(ActionResult result) =>
        JsonSerializer.SerializeToElement(Assert.IsType<BadRequestObjectResult>(result).Value)
            .GetProperty("error").GetString()!;

    private static JsonElement ServerErrorJson(ActionResult result)
    {
        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status500InternalServerError, objectResult.StatusCode);
        return JsonSerializer.SerializeToElement(objectResult.Value);
    }

    private void VerifyLogged(LogLevel level, string fragment, Times times, Exception? exception = null) =>
        _logger.Verify(
            l => l.Log(
                level,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains(fragment)),
                It.Is<Exception?>(e => exception == null || ReferenceEquals(e, exception)),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            times);

    // ---------------------------------------------------------------- GET /api/settings

    [Fact]
    public void GetAllSettings_ReportsWhichSecretsAreStoredWithoutReturningThem()
    {
        _settings.SmtpPassword = "smtp-pw-do-not-echo";
        _settings.SuwayomiPassword = "suwayomi-pw-do-not-echo";
        _settings.ErrorReportingGitHubToken = "ghp_token_do_not_echo";
        _settings.ComicVineApiKey = "cv-key-do-not-echo";

        var result = CreateController().GetAllSettings();

        var json = OkJson(result);
        Assert.True(json.GetProperty("smtp_password_set").GetBoolean());
        Assert.True(json.GetProperty("suwayomi_password_set").GetBoolean());
        Assert.True(json.GetProperty("error_reporting_github_token_set").GetBoolean());
        Assert.True(json.GetProperty("comicvine_api_key_set").GetBoolean());
        // Any signed-in user (read-only ones included) may read these settings.
        Assert.False(json.TryGetProperty("comicvine_api_key", out _));
        var raw = json.GetRawText();
        Assert.DoesNotContain("smtp-pw-do-not-echo", raw);
        Assert.DoesNotContain("suwayomi-pw-do-not-echo", raw);
        Assert.DoesNotContain("ghp_token_do_not_echo", raw);
        Assert.DoesNotContain("cv-key-do-not-echo", raw);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void GetAllSettings_ReportsMissingSecretsAsNotSet(string? secret)
    {
        _settings.SmtpPassword = secret;
        _settings.SuwayomiPassword = secret;
        _settings.ErrorReportingGitHubToken = secret;
        _settings.ComicVineApiKey = secret;

        var json = OkJson(CreateController().GetAllSettings());

        Assert.False(json.GetProperty("smtp_password_set").GetBoolean());
        Assert.False(json.GetProperty("suwayomi_password_set").GetBoolean());
        Assert.False(json.GetProperty("error_reporting_github_token_set").GetBoolean());
        Assert.False(json.GetProperty("comicvine_api_key_set").GetBoolean());
    }

    [Fact]
    public void GetAllSettings_ReturnsFixedErrorReportingDestinationAndConfiguredValues()
    {
        var json = OkJson(CreateController().GetAllSettings());

        Assert.Equal(ErrorReportingDestination.Owner, json.GetProperty("error_reporting_github_owner").GetString());
        Assert.Equal(ErrorReportingDestination.Repo, json.GetProperty("error_reporting_github_repo").GetString());
        Assert.Equal("series", json.GetProperty("default_library_view").GetString());
        Assert.Equal("en", json.GetProperty("default_preferred_language").GetString());
        Assert.Equal(1600, json.GetProperty("series_image_max_dimension").GetInt32());
    }

    // ---------------------------------------------------------------- simple GETs

    [Fact]
    public void GetSeriesImageLimits_ConvertsStoredBytesToMegabytes()
    {
        var json = OkJson(CreateController().GetSeriesImageLimits());

        Assert.Equal(3.5, json.GetProperty("maxMB").GetDouble());
        Assert.Equal(8.0, json.GetProperty("maxDownloadMB").GetDouble());
        Assert.Equal(1600, json.GetProperty("maxDimension").GetInt32());
    }

    [Fact]
    public void GetDefaultLibraryView_ReturnsConfiguredView()
    {
        var json = OkJson(CreateController().GetDefaultLibraryView());

        Assert.Equal("series", json.GetProperty("view").GetString());
    }

    [Theory]
    [InlineData("ja")]
    [InlineData(null)]
    public void GetDefaultPreferredLanguage_ReturnsConfiguredLanguage(string? language)
    {
        _settings.DefaultPreferredLanguage = language;

        var json = OkJson(CreateController().GetDefaultPreferredLanguage());

        Assert.Equal(language, json.GetProperty("language").GetString());
    }

    // ---------------------------------------------------------------- boolean toggles

    public static TheoryData<string> Toggles => new()
    {
        "watcher-enable-rename",
        "watcher-enable-normalize",
        "write-cover-to-first-archive",
        "write-cover-to-series-folder",
        "download-external-series-images",
        "allow-registration",
    };

    private static string ToggleError(string toggle) => toggle switch
    {
        "allow-registration" => "Failed to update self-service registration setting",
        _ => "Failed to update " + toggle.Replace('-', ' '),
    };

    private void SetToggle(string toggle, bool value)
    {
        switch (toggle)
        {
            case "watcher-enable-rename": _settings.WatcherEnableRename = value; break;
            case "watcher-enable-normalize": _settings.WatcherEnableNormalize = value; break;
            case "write-cover-to-first-archive": _settings.WriteCoverToFirstArchive = value; break;
            case "write-cover-to-series-folder": _settings.WriteCoverToSeriesFolder = value; break;
            case "download-external-series-images": _settings.DownloadExternalSeriesImages = value; break;
            case "allow-registration": _settings.AllowRegistration = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(toggle), toggle, null);
        }
    }

    private static ActionResult<object> GetToggle(SettingsController c, string toggle) => toggle switch
    {
        "watcher-enable-rename" => c.GetWatcherEnableRename(),
        "watcher-enable-normalize" => c.GetWatcherEnableNormalize(),
        "write-cover-to-first-archive" => c.GetWriteCoverToFirstArchive(),
        "write-cover-to-series-folder" => c.GetWriteCoverToSeriesFolder(),
        "download-external-series-images" => c.GetDownloadExternalSeriesImages(),
        "allow-registration" => c.GetAllowRegistration(),
        _ => throw new ArgumentOutOfRangeException(nameof(toggle), toggle, null),
    };

    private static Task<ActionResult> PutToggle(SettingsController c, string toggle, bool enabled, bool legacyPost) =>
        (toggle, legacyPost) switch
        {
            ("watcher-enable-rename", false) => c.UpdateWatcherEnableRename(new() { Enabled = enabled }),
            ("watcher-enable-rename", true) => c.SetWatcherEnableRename(new() { Enabled = enabled }),
            ("watcher-enable-normalize", false) => c.UpdateWatcherEnableNormalize(new() { Enabled = enabled }),
            ("watcher-enable-normalize", true) => c.SetWatcherEnableNormalize(new() { Enabled = enabled }),
            ("write-cover-to-first-archive", false) => c.UpdateWriteCoverToFirstArchive(new() { Enabled = enabled }),
            ("write-cover-to-first-archive", true) => c.SetWriteCoverToFirstArchive(new() { Enabled = enabled }),
            ("write-cover-to-series-folder", false) => c.UpdateWriteCoverToSeriesFolder(new() { Enabled = enabled }),
            ("write-cover-to-series-folder", true) => c.SetWriteCoverToSeriesFolder(new() { Enabled = enabled }),
            ("download-external-series-images", false) => c.UpdateDownloadExternalSeriesImages(new() { Enabled = enabled }),
            ("download-external-series-images", true) => c.SetDownloadExternalSeriesImages(new() { Enabled = enabled }),
            ("allow-registration", false) => c.UpdateAllowRegistration(new() { Enabled = enabled }),
            ("allow-registration", true) => c.SetAllowRegistration(new() { Enabled = enabled }),
            _ => throw new ArgumentOutOfRangeException(nameof(toggle), toggle, null),
        };

    private static Expression<Func<ISettingsService, Task>> ToggleCall(string toggle, bool enabled) => toggle switch
    {
        "watcher-enable-rename" => s => s.UpdateWatcherEnableRenameAsync(enabled, It.IsAny<CancellationToken>()),
        "watcher-enable-normalize" => s => s.UpdateWatcherEnableNormalizeAsync(enabled, It.IsAny<CancellationToken>()),
        "write-cover-to-first-archive" => s => s.UpdateWriteCoverToFirstArchiveAsync(enabled, It.IsAny<CancellationToken>()),
        "write-cover-to-series-folder" => s => s.UpdateWriteCoverToSeriesFolderAsync(enabled, It.IsAny<CancellationToken>()),
        "download-external-series-images" => s => s.UpdateDownloadExternalSeriesImagesAsync(enabled, It.IsAny<CancellationToken>()),
        "allow-registration" => s => s.UpdateAllowRegistrationAsync(enabled, It.IsAny<CancellationToken>()),
        _ => throw new ArgumentOutOfRangeException(nameof(toggle), toggle, null),
    };

    [Theory]
    [MemberData(nameof(Toggles))]
    public void Toggle_Get_ReflectsCurrentSetting(string toggle)
    {
        var controller = CreateController();

        SetToggle(toggle, true);
        Assert.True(OkJson(GetToggle(controller, toggle)).GetProperty("enabled").GetBoolean());

        SetToggle(toggle, false);
        Assert.False(OkJson(GetToggle(controller, toggle)).GetProperty("enabled").GetBoolean());
    }

    [Theory]
    [MemberData(nameof(Toggles))]
    public async Task Toggle_PutAndLegacyPost_PersistRequestedValue(string toggle)
    {
        var controller = CreateController();

        var put = await PutToggle(controller, toggle, enabled: true, legacyPost: false);
        var post = await PutToggle(controller, toggle, enabled: false, legacyPost: true);

        Assert.Contains("updated successfully", OkJson(put).GetProperty("message").GetString());
        Assert.Contains("updated successfully", OkJson(post).GetProperty("message").GetString());
        _settingsService.Verify(ToggleCall(toggle, true), Times.Once());
        _settingsService.Verify(ToggleCall(toggle, false), Times.Once());
        _settingsService.VerifyNoOtherCalls();
    }

    [Theory]
    [MemberData(nameof(Toggles))]
    public async Task Toggle_ServiceFailure_Returns500WithoutLeakingExceptionDetails(string toggle)
    {
        var expectedError = ToggleError(toggle);
        var failure = new InvalidOperationException("disk full: /config/settings.json");
        _settingsService.Setup(ToggleCall(toggle, true)).ThrowsAsync(failure);

        var result = await PutToggle(CreateController(), toggle, enabled: true, legacyPost: false);

        var json = ServerErrorJson(result);
        Assert.Equal(expectedError, json.GetProperty("error").GetString());
        Assert.DoesNotContain("disk full", json.GetRawText());
        VerifyLogged(LogLevel.Error, expectedError, Times.Once(), failure);
    }

    [Fact]
    public async Task UpdateAllowRegistration_NullBody_ReturnsBadRequest()
    {
        var result = await CreateController().UpdateAllowRegistration(null!);

        Assert.Equal("Enabled is required", BadRequestError(result));
        _settingsService.VerifyNoOtherCalls();
    }

    // ---------------------------------------------------------------- scalar settings

    [Fact]
    public async Task UpdateFilenameFormat_ServiceFailure_Returns500()
    {
        var failure = new IOException("settings file locked");
        _settingsService.Setup(s => s.UpdateFilenameFormatAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);

        var result = await CreateController().UpdateFilenameFormat(new() { Format = "{series}" });

        Assert.Equal("Failed to update filename format", ServerErrorJson(result).GetProperty("error").GetString());
        VerifyLogged(LogLevel.Error, "Failed to update filename format", Times.Once(), failure);
    }

    [Fact]
    public async Task SetIssueNumberPadding_LegacyPost_PersistsPadding()
    {
        var result = await CreateController().SetIssueNumberPadding(new() { Padding = 5 });

        Assert.Equal("Issue number padding updated successfully", OkJson(result).GetProperty("message").GetString());
        _settingsService.Verify(s => s.UpdateIssueNumberPaddingAsync(5, It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task UpdateIssueNumberPadding_ServiceFailure_Returns500()
    {
        var failure = new InvalidOperationException("boom");
        _settingsService.Setup(s => s.UpdateIssueNumberPaddingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);

        var result = await CreateController().UpdateIssueNumberPadding(new() { Padding = 2 });

        Assert.Equal("Failed to update issue number padding", ServerErrorJson(result).GetProperty("error").GetString());
        VerifyLogged(LogLevel.Error, "Failed to update issue number padding", Times.Once(), failure);
    }

    [Theory]
    [InlineData(1.5, 1572864)]
    [InlineData(2047, 2146435072)]
    public async Task SetLogMaxBytes_LegacyPost_ConvertsMegabytesToBytes(double maxMb, int expectedBytes)
    {
        var result = await CreateController().SetLogMaxBytes(new() { MaxMB = maxMb });

        Assert.IsType<OkObjectResult>(result);
        _settingsService.Verify(s => s.UpdateLogMaxBytesAsync(expectedBytes, It.IsAny<CancellationToken>()), Times.Once());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2047.5)]
    public async Task UpdateLogMaxBytes_OutOfRange_ReturnsBadRequest(double maxMb)
    {
        var result = await CreateController().UpdateLogMaxBytes(new() { MaxMB = maxMb });

        Assert.Equal("MaxMB must be between 1 and 2047", BadRequestError(result));
        _settingsService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UpdateLogMaxBytes_ServiceFailure_Returns500()
    {
        var failure = new InvalidOperationException("boom");
        _settingsService.Setup(s => s.UpdateLogMaxBytesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);

        var result = await CreateController().UpdateLogMaxBytes(new() { MaxMB = 10 });

        Assert.Equal("Failed to update log max bytes", ServerErrorJson(result).GetProperty("error").GetString());
        VerifyLogged(LogLevel.Error, "Failed to update log max bytes", Times.Once(), failure);
    }

    [Fact]
    public async Task SetSeriesImageLimits_LegacyPost_PersistsAllThreeLimits()
    {
        var result = await CreateController().SetSeriesImageLimits(new() { MaxMB = 0.5, MaxDownloadMB = 2, MaxDimension = 900 });

        Assert.Equal("Series image limits updated successfully", OkJson(result).GetProperty("message").GetString());
        _settingsService.Verify(s => s.UpdateSeriesImageMaxBytesAsync(BytesPerMb / 2, It.IsAny<CancellationToken>()), Times.Once());
        _settingsService.Verify(s => s.UpdateSeriesImageMaxDownloadBytesAsync(2 * BytesPerMb, It.IsAny<CancellationToken>()), Times.Once());
        _settingsService.Verify(s => s.UpdateSeriesImageMaxDimensionAsync(900, It.IsAny<CancellationToken>()), Times.Once());
    }

    [Theory]
    [InlineData(1, 0, 100)]
    [InlineData(1, 2, 0)]
    [InlineData(1, 2, -5)]
    public async Task UpdateSeriesImageLimits_NonPositiveLimit_ReturnsBadRequest(double maxMb, double maxDownloadMb, int maxDimension)
    {
        var result = await CreateController().UpdateSeriesImageLimits(
            new() { MaxMB = maxMb, MaxDownloadMB = maxDownloadMb, MaxDimension = maxDimension });

        Assert.Equal("Series image limits must all be greater than 0", BadRequestError(result));
        _settingsService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UpdateSeriesImageLimits_ServiceFailure_Returns500AndStopsUpdating()
    {
        var failure = new InvalidOperationException("boom");
        _settingsService.Setup(s => s.UpdateSeriesImageMaxDownloadBytesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);

        var result = await CreateController().UpdateSeriesImageLimits(new() { MaxMB = 1, MaxDownloadMB = 1, MaxDimension = 800 });

        Assert.Equal("Failed to update series image limits", ServerErrorJson(result).GetProperty("error").GetString());
        _settingsService.Verify(s => s.UpdateSeriesImageMaxDimensionAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never());
        VerifyLogged(LogLevel.Error, "Failed to update series image limits", Times.Once(), failure);
    }

    [Fact]
    public async Task SetDatabaseCleanupIntervalHours_LegacyPost_PersistsHours()
    {
        var result = await CreateController().SetDatabaseCleanupIntervalHours(new() { Hours = 48 });

        Assert.Equal("Database cleanup interval updated successfully", OkJson(result).GetProperty("message").GetString());
        _settingsService.Verify(s => s.UpdateDatabaseCleanupIntervalHoursAsync(48, It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task UpdateDatabaseCleanupIntervalHours_ServiceFailure_Returns500()
    {
        var failure = new InvalidOperationException("boom");
        _settingsService.Setup(s => s.UpdateDatabaseCleanupIntervalHoursAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);

        var result = await CreateController().UpdateDatabaseCleanupIntervalHours(new() { Hours = 1 });

        Assert.Equal("Failed to update database cleanup interval", ServerErrorJson(result).GetProperty("error").GetString());
        VerifyLogged(LogLevel.Error, "Failed to update database cleanup interval", Times.Once(), failure);
    }

    // ---------------------------------------------------------------- default library view

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task UpdateDefaultLibraryView_MissingView_ReturnsBadRequest(string? view)
    {
        var result = await CreateController().UpdateDefaultLibraryView(new() { View = view });

        Assert.Equal("View is required", BadRequestError(result));
        _settingsService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UpdateDefaultLibraryView_NullBody_ReturnsBadRequest()
    {
        var result = await CreateController().UpdateDefaultLibraryView(null!);

        Assert.Equal("View is required", BadRequestError(result));
    }

    [Theory]
    [InlineData("grid")]
    [InlineData("file")]
    public async Task UpdateDefaultLibraryView_UnknownView_ReturnsBadRequest(string view)
    {
        var result = await CreateController().UpdateDefaultLibraryView(new() { View = view });

        Assert.Equal("View must be either 'files' or 'series'", BadRequestError(result));
        _settingsService.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("  Series ", "series")]
    [InlineData("FILES", "files")]
    public async Task UpdateDefaultLibraryView_NormalizesView(string view, string expected)
    {
        var result = await CreateController().UpdateDefaultLibraryView(new() { View = view });

        Assert.Equal("Default library view updated successfully", OkJson(result).GetProperty("message").GetString());
        _settingsService.Verify(s => s.UpdateDefaultLibraryViewAsync(expected, It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task SetDefaultLibraryView_LegacyPost_PersistsView()
    {
        var result = await CreateController().SetDefaultLibraryView(new() { View = "files" });

        Assert.IsType<OkObjectResult>(result);
        _settingsService.Verify(s => s.UpdateDefaultLibraryViewAsync("files", It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task UpdateDefaultLibraryView_ServiceFailure_Returns500()
    {
        var failure = new InvalidOperationException("boom");
        _settingsService.Setup(s => s.UpdateDefaultLibraryViewAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);

        var result = await CreateController().UpdateDefaultLibraryView(new() { View = "series" });

        Assert.Equal("Failed to update default library view", ServerErrorJson(result).GetProperty("error").GetString());
        VerifyLogged(LogLevel.Error, "Failed to update default library view", Times.Once(), failure);
    }

    // ---------------------------------------------------------------- default preferred language

    [Fact]
    public async Task UpdateDefaultPreferredLanguage_EffectiveChange_QueuesRetagAndLogsCount()
    {
        _retag.Setup(r => r.QueueRetagForGlobalDefaultAsync(It.IsAny<CancellationToken>())).ReturnsAsync(3);

        var result = await CreateController(withRetag: true).UpdateDefaultPreferredLanguage(new() { Language = "ja" });

        Assert.Equal("Default preferred language updated successfully", OkJson(result).GetProperty("message").GetString());
        _settingsService.Verify(s => s.UpdateDefaultPreferredLanguageAsync("ja", It.IsAny<CancellationToken>()), Times.Once());
        _retag.Verify(r => r.QueueRetagForGlobalDefaultAsync(It.IsAny<CancellationToken>()), Times.Once());
        VerifyLogged(LogLevel.Information, "Flagged 3 file(s)", Times.Once());
    }

    [Fact]
    public async Task UpdateDefaultPreferredLanguage_EffectiveChangeWithNothingToRetag_DoesNotLogFlaggedFiles()
    {
        _retag.Setup(r => r.QueueRetagForGlobalDefaultAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);

        var result = await CreateController(withRetag: true).UpdateDefaultPreferredLanguage(new() { Language = "ko" });

        Assert.IsType<OkObjectResult>(result);
        _retag.Verify(r => r.QueueRetagForGlobalDefaultAsync(It.IsAny<CancellationToken>()), Times.Once());
        VerifyLogged(LogLevel.Information, "Flagged", Times.Never());
    }

    [Fact]
    public async Task UpdateDefaultPreferredLanguage_NullBodyClearsLanguageAndQueuesRetag()
    {
        var result = await CreateController(withRetag: true).UpdateDefaultPreferredLanguage(null!);

        Assert.IsType<OkObjectResult>(result);
        _settingsService.Verify(s => s.UpdateDefaultPreferredLanguageAsync(null, It.IsAny<CancellationToken>()), Times.Once());
        _retag.Verify(r => r.QueueRetagForGlobalDefaultAsync(It.IsAny<CancellationToken>()), Times.Once());
    }

    [Theory]
    [InlineData("ja", "JA-Latn")]
    [InlineData("ja", "ja")]
    [InlineData(null, "")]
    [InlineData(null, "xx")]
    public async Task UpdateDefaultPreferredLanguage_NoEffectiveChange_DoesNotQueueRetag(string? current, string requested)
    {
        _settings.DefaultPreferredLanguage = current;

        var result = await CreateController(withRetag: true).UpdateDefaultPreferredLanguage(new() { Language = requested });

        Assert.IsType<OkObjectResult>(result);
        _settingsService.Verify(s => s.UpdateDefaultPreferredLanguageAsync(requested, It.IsAny<CancellationToken>()), Times.Once());
        _retag.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UpdateDefaultPreferredLanguage_RetagFailure_IsLoggedButStillSucceeds()
    {
        var failure = new InvalidOperationException("retag store unavailable");
        _retag.Setup(r => r.QueueRetagForGlobalDefaultAsync(It.IsAny<CancellationToken>())).ThrowsAsync(failure);

        var result = await CreateController(withRetag: true).UpdateDefaultPreferredLanguage(new() { Language = "zh" });

        Assert.Equal("Default preferred language updated successfully", OkJson(result).GetProperty("message").GetString());
        VerifyLogged(LogLevel.Warning, "Failed to flag files for metadata backfill", Times.Once(), failure);
    }

    [Fact]
    public async Task UpdateDefaultPreferredLanguage_WithoutRetagService_StillUpdatesSetting()
    {
        var result = await CreateController(withRetag: false).UpdateDefaultPreferredLanguage(new() { Language = "ja" });

        Assert.IsType<OkObjectResult>(result);
        _settingsService.Verify(s => s.UpdateDefaultPreferredLanguageAsync("ja", It.IsAny<CancellationToken>()), Times.Once());
        _retag.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UpdateDefaultPreferredLanguage_InvalidLanguage_ReturnsBadRequestWithServiceMessage()
    {
        _settingsService.Setup(s => s.UpdateDefaultPreferredLanguageAsync("fr", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("Unsupported language 'fr'"));

        var result = await CreateController(withRetag: true).UpdateDefaultPreferredLanguage(new() { Language = "fr" });

        Assert.Equal("Unsupported language 'fr'", BadRequestError(result));
        _retag.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UpdateDefaultPreferredLanguage_ServiceFailure_Returns500()
    {
        var failure = new InvalidOperationException("boom");
        _settingsService.Setup(s => s.UpdateDefaultPreferredLanguageAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);

        var result = await CreateController(withRetag: true).UpdateDefaultPreferredLanguage(new() { Language = "ja" });

        Assert.Equal("Failed to update default preferred language", ServerErrorJson(result).GetProperty("error").GetString());
        VerifyLogged(LogLevel.Error, "Failed to update default preferred language", Times.Once(), failure);
        _retag.VerifyNoOtherCalls();
    }

    // ---------------------------------------------------------------- external series metadata

    [Fact]
    public async Task UpdateExternalSeriesMetadata_TrimsValuesAndDoesNotEchoApiKey()
    {
        var request = new SettingsController.ExternalSeriesMetadataSettingsRequest
        {
            Enabled = true,
            ComicVineApiKey = "  cv-key-do-not-echo  ",
            ComicVineBaseUrl = " https://comicvine.example/api ",
            EnableMangaDex = true,
            MangaDexBaseUrl = " https://mangadex.example ",
            EnableAniList = false,
            AniListBaseUrl = " https://anilist.example/graphql ",
        };

        var result = await CreateController().UpdateExternalSeriesMetadata(request);

        var json = OkJson(result);
        Assert.Equal("External series metadata settings updated successfully", json.GetProperty("message").GetString());
        Assert.DoesNotContain("cv-key-do-not-echo", json.GetRawText());
        _settingsService.Verify(s => s.UpdateExternalSeriesMetadataEnabledAsync(true, It.IsAny<CancellationToken>()), Times.Once());
        _settingsService.Verify(s => s.UpdateComicVineApiKeyAsync("cv-key-do-not-echo", It.IsAny<CancellationToken>()), Times.Once());
        _settingsService.Verify(s => s.UpdateComicVineBaseUrlAsync("https://comicvine.example/api", It.IsAny<CancellationToken>()), Times.Once());
        _settingsService.Verify(s => s.UpdateMangaDexEnabledAsync(true, It.IsAny<CancellationToken>()), Times.Once());
        _settingsService.Verify(s => s.UpdateMangaDexBaseUrlAsync("https://mangadex.example", It.IsAny<CancellationToken>()), Times.Once());
        _settingsService.Verify(s => s.UpdateAniListEnabledAsync(false, It.IsAny<CancellationToken>()), Times.Once());
        _settingsService.Verify(s => s.UpdateAniListBaseUrlAsync("https://anilist.example/graphql", It.IsAny<CancellationToken>()), Times.Once());
        _settingsService.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SetExternalSeriesMetadata_BlankValues_ClearApiKeyAndKeepBaseUrls(string blank)
    {
        var request = new SettingsController.ExternalSeriesMetadataSettingsRequest
        {
            Enabled = false,
            ComicVineApiKey = blank,
            ComicVineBaseUrl = blank,
            MangaDexBaseUrl = blank,
            AniListBaseUrl = blank,
        };

        var result = await CreateController().SetExternalSeriesMetadata(request);

        Assert.IsType<OkObjectResult>(result);
        _settingsService.Verify(s => s.UpdateExternalSeriesMetadataEnabledAsync(false, It.IsAny<CancellationToken>()), Times.Once());
        _settingsService.Verify(s => s.UpdateComicVineApiKeyAsync(null, It.IsAny<CancellationToken>()), Times.Once());
        _settingsService.Verify(s => s.UpdateMangaDexEnabledAsync(false, It.IsAny<CancellationToken>()), Times.Once());
        _settingsService.Verify(s => s.UpdateAniListEnabledAsync(false, It.IsAny<CancellationToken>()), Times.Once());
        _settingsService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UpdateExternalSeriesMetadata_OmittedApiKey_KeepsTheStoredKey()
    {
        // GET /api/settings never returns the key, so the settings form leaves
        // it out unless the user types a new one; that must not wipe it.
        var request = new SettingsController.ExternalSeriesMetadataSettingsRequest
        {
            Enabled = true,
            ComicVineApiKey = null,
            EnableMangaDex = true,
            EnableAniList = true,
        };

        var result = await CreateController().UpdateExternalSeriesMetadata(request);

        Assert.IsType<OkObjectResult>(result);
        _settingsService.Verify(s => s.UpdateComicVineApiKeyAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never());
        _settingsService.Verify(s => s.UpdateExternalSeriesMetadataEnabledAsync(true, It.IsAny<CancellationToken>()), Times.Once());
        _settingsService.Verify(s => s.UpdateMangaDexEnabledAsync(true, It.IsAny<CancellationToken>()), Times.Once());
        _settingsService.Verify(s => s.UpdateAniListEnabledAsync(true, It.IsAny<CancellationToken>()), Times.Once());
        _settingsService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UpdateExternalSeriesMetadata_ServiceFailure_Returns500WithoutLeakingDetails()
    {
        var failure = new InvalidOperationException("cv-key-do-not-echo rejected");
        _settingsService.Setup(s => s.UpdateComicVineApiKeyAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);

        var result = await CreateController().UpdateExternalSeriesMetadata(new() { ComicVineApiKey = "cv-key-do-not-echo" });

        var json = ServerErrorJson(result);
        Assert.Equal("Failed to update external series metadata settings", json.GetProperty("error").GetString());
        Assert.DoesNotContain("cv-key-do-not-echo", json.GetRawText());
        VerifyLogged(LogLevel.Error, "Failed to update external series metadata settings", Times.Once(), failure);
    }

    // ---------------------------------------------------------------- cleanup-database

    [Fact]
    public async Task CleanupDatabase_RemovedEntries_BroadcastsFileListUpdate()
    {
        _fileStore.Setup(f => f.CleanupStaleEntriesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(4);

        var result = await CreateController(withBroadcaster: true).CleanupDatabase();

        var json = OkJson(result);
        Assert.True(json.GetProperty("success").GetBoolean());
        Assert.Equal(4, json.GetProperty("removedCount").GetInt32());
        Assert.Equal("Database cleanup completed. Removed 4 stale entries.", json.GetProperty("message").GetString());
        _broadcaster.Verify(b => b.BroadcastFileListUpdateAsync(), Times.Once());
    }

    [Fact]
    public async Task CleanupDatabase_NothingRemoved_DoesNotBroadcast()
    {
        _fileStore.Setup(f => f.CleanupStaleEntriesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);

        var result = await CreateController(withBroadcaster: true).CleanupDatabase();

        Assert.Equal(0, OkJson(result).GetProperty("removedCount").GetInt32());
        _broadcaster.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CleanupDatabase_BroadcastFailure_IsLoggedButStillSucceeds()
    {
        var failure = new InvalidOperationException("hub down");
        _fileStore.Setup(f => f.CleanupStaleEntriesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(2);
        _broadcaster.Setup(b => b.BroadcastFileListUpdateAsync()).ThrowsAsync(failure);

        var result = await CreateController(withBroadcaster: true).CleanupDatabase();

        Assert.True(OkJson(result).GetProperty("success").GetBoolean());
        VerifyLogged(LogLevel.Warning, "Failed to broadcast file list update after manual database cleanup", Times.Once(), failure);
    }

    [Fact]
    public async Task CleanupDatabase_StoreFailure_Returns500WithReason()
    {
        var failure = new InvalidOperationException("database is locked");
        _fileStore.Setup(f => f.CleanupStaleEntriesAsync(It.IsAny<CancellationToken>())).ThrowsAsync(failure);

        var result = await CreateController(withBroadcaster: true).CleanupDatabase();

        var json = ServerErrorJson(result);
        Assert.False(json.GetProperty("success").GetBoolean());
        Assert.Equal("Error during database cleanup: database is locked", json.GetProperty("error").GetString());
        VerifyLogged(LogLevel.Error, "Error during manual database cleanup", Times.Once(), failure);
        _broadcaster.VerifyNoOtherCalls();
    }

    // ---------------------------------------------------------------- reset

    [Fact]
    public async Task ResetDatabase_ClearsFilesAndHistoryBeforeReinitializingStore()
    {
        await using var db = await SqliteDb.CreateAsync();
        await using (var seed = await db.Factory.CreateDbContextAsync())
        {
            seed.ComicFiles.AddRange(
                new ComicFileEntity { FilePath = "/comics/a.cbz", FileName = "a.cbz", Directory = "/comics" },
                new ComicFileEntity { FilePath = "/comics/b.cbz", FileName = "b.cbz", Directory = "/comics" });
            seed.ProcessingHistory.Add(new ProcessingHistoryEntity
            {
                EntryId = Guid.NewGuid(),
                FilePath = "/comics/a.cbz",
                Action = "process",
                Success = true,
            });
            await seed.SaveChangesAsync();
        }

        var running = new ProcessingJob { JobId = Guid.NewGuid(), Status = JobStatus.Running };
        var queued = new ProcessingJob { JobId = Guid.NewGuid(), Status = JobStatus.Queued };
        var completed = new ProcessingJob { JobId = Guid.NewGuid(), Status = JobStatus.Completed };
        _processor.Setup(p => p.GetAllJobs()).Returns(new[] { running, queued, completed });

        (int Files, int History)? countsWhenStoreReinitialized = null;
        _fileStore.Setup(f => f.InitializeFromDatabaseAsync(It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await using var check = await db.Factory.CreateDbContextAsync();
                countsWhenStoreReinitialized = (await check.ComicFiles.CountAsync(), await check.ProcessingHistory.CountAsync());
            });

        var result = await CreateController(withBroadcaster: true, dbFactory: db.Factory).ResetDatabase();

        var json = OkJson(result);
        Assert.True(json.GetProperty("success").GetBoolean());
        Assert.Equal("Database reset completed successfully", json.GetProperty("message").GetString());
        Assert.Equal((0, 0), countsWhenStoreReinitialized);
        _broadcaster.Verify(b => b.BroadcastFileListUpdateAsync(), Times.Once());
        VerifyLogged(LogLevel.Information, running.JobId.ToString(), Times.Once());
        VerifyLogged(LogLevel.Information, queued.JobId.ToString(), Times.Once());
        VerifyLogged(LogLevel.Information, completed.JobId.ToString(), Times.Never());
    }

    [Fact]
    public async Task ResetDatabase_BroadcastFailure_IsLoggedButStillSucceeds()
    {
        await using var db = await SqliteDb.CreateAsync();
        var failure = new InvalidOperationException("hub down");
        _broadcaster.Setup(b => b.BroadcastFileListUpdateAsync()).ThrowsAsync(failure);

        var result = await CreateController(withBroadcaster: true, dbFactory: db.Factory).ResetDatabase();

        Assert.True(OkJson(result).GetProperty("success").GetBoolean());
        _fileStore.Verify(f => f.InitializeFromDatabaseAsync(It.IsAny<CancellationToken>()), Times.Once());
        VerifyLogged(LogLevel.Warning, "Failed to broadcast file list update after database reset", Times.Once(), failure);
    }

    [Fact]
    public async Task ResetDatabase_WithoutBroadcaster_Succeeds()
    {
        await using var db = await SqliteDb.CreateAsync();

        var result = await CreateController(withBroadcaster: false, dbFactory: db.Factory).ResetDatabase();

        Assert.True(OkJson(result).GetProperty("success").GetBoolean());
        _fileStore.Verify(f => f.InitializeFromDatabaseAsync(It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task ResetDatabase_DatabaseUnavailable_Returns500AndLeavesStoreAlone()
    {
        var failure = new InvalidOperationException("database file is read-only");

        var result = await CreateController(withBroadcaster: true, dbFactory: new ThrowingDbContextFactory(failure)).ResetDatabase();

        var json = ServerErrorJson(result);
        Assert.False(json.GetProperty("success").GetBoolean());
        Assert.Equal("Error resetting database: database file is read-only", json.GetProperty("error").GetString());
        _fileStore.Verify(f => f.InitializeFromDatabaseAsync(It.IsAny<CancellationToken>()), Times.Never());
        _broadcaster.VerifyNoOtherCalls();
        VerifyLogged(LogLevel.Error, "Error resetting database", Times.Once(), failure);
    }

    [Fact]
    public async Task ResetDatabase_StoreReinitializationFailure_Returns500WithoutBroadcast()
    {
        await using var db = await SqliteDb.CreateAsync();
        var failure = new InvalidOperationException("file store busy");
        _fileStore.Setup(f => f.InitializeFromDatabaseAsync(It.IsAny<CancellationToken>())).ThrowsAsync(failure);

        var result = await CreateController(withBroadcaster: true, dbFactory: db.Factory).ResetDatabase();

        var json = ServerErrorJson(result);
        Assert.False(json.GetProperty("success").GetBoolean());
        Assert.Equal("Error resetting database: file store busy", json.GetProperty("error").GetString());
        _broadcaster.VerifyNoOtherCalls();
        VerifyLogged(LogLevel.Error, "Error resetting database", Times.Once(), failure);
    }

    [Fact]
    public async Task ResetDatabase_DisposesTheContextItCreates()
    {
        await using var db = await SqliteDb.CreateAsync();
        var factory = new CountingDbContextFactory(db.Options);

        var result = await CreateController(dbFactory: factory).ResetDatabase();

        Assert.True(OkJson(result).GetProperty("success").GetBoolean());
        Assert.Equal(1, factory.CreatedCount);
        Assert.Equal(1, factory.DisposedCount);
    }

    // ---------------------------------------------------------------- restart

    private static ControllerContext ContextFor(ClaimsPrincipal user) =>
        new() { HttpContext = new DefaultHttpContext { User = user } };

    [Fact]
    public async Task RestartApplication_LogsRequesterAndStopsApplicationInBackground()
    {
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _lifetime.Setup(l => l.StopApplication()).Callback(() => stopped.TrySetResult());
        var controller = CreateController();
        controller.ControllerContext = ContextFor(new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, "42"), new Claim(ClaimTypes.Name, "root-admin") }, "test")));

        var result = controller.RestartApplication();

        Assert.True(OkJson(result).GetProperty("success").GetBoolean());
        VerifyLogged(LogLevel.Warning, "requested by user 42 (root-admin)", Times.Once());
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(30));
        _lifetime.Verify(l => l.StopApplication(), Times.Once());
    }

    [Fact]
    public async Task RestartApplication_UserWithoutIdClaims_StillStopsApplication()
    {
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _lifetime.Setup(l => l.StopApplication()).Callback(() => stopped.TrySetResult());
        var controller = CreateController();
        controller.ControllerContext = ContextFor(new ClaimsPrincipal(new ClaimsIdentity()));

        var result = controller.RestartApplication();

        Assert.True(OkJson(result).GetProperty("success").GetBoolean());
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void RestartApplication_FailureWhileInitiating_Returns500AndDoesNotStop()
    {
        var failure = new InvalidOperationException("log sink offline");
        _logger.Setup(l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Throws(failure);
        var controller = CreateController();
        controller.ControllerContext = ContextFor(new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, "42") }, "test")));

        var result = controller.RestartApplication();

        var json = ServerErrorJson(result);
        Assert.False(json.GetProperty("success").GetBoolean());
        Assert.Equal("Error initiating restart: log sink offline", json.GetProperty("error").GetString());
        _lifetime.Verify(l => l.StopApplication(), Times.Never());
        VerifyLogged(LogLevel.Error, "Error initiating application restart", Times.Once(), failure);
    }

    // ---------------------------------------------------------------- secret-bearing settings groups

    private const string Secret = "secret-value-do-not-echo";

    public static TheoryData<string> SecretGroups => new() { "email", "suwayomi", "error-reporting" };

    private static Task<ActionResult> PutSecretGroup(SettingsController c, string group, bool nullBody) => group switch
    {
        "email" => c.UpdateEmailSettings(nullBody ? null! : new SettingsController.EmailSettingsRequest { SmtpPassword = Secret }),
        "suwayomi" => c.UpdateSuwayomiSettings(nullBody ? null! : new SettingsController.SuwayomiSettingsRequest { Password = Secret }),
        "error-reporting" => c.UpdateErrorReportingSettings(nullBody ? null! : new SettingsController.ErrorReportingSettingsRequest { GitHubToken = Secret }),
        _ => throw new ArgumentOutOfRangeException(nameof(group), group, null),
    };

    private void SetupSecretGroupFailure(string group, Exception failure)
    {
        switch (group)
        {
            case "email":
                _settingsService.Setup(s => s.UpdateEmailSettingsAsync(
                        It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool>(),
                        It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                    .ThrowsAsync(failure);
                break;
            case "suwayomi":
                _settingsService.Setup(s => s.UpdateSuwayomiSettingsAsync(
                        It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                    .ThrowsAsync(failure);
                break;
            case "error-reporting":
                _settingsService.Setup(s => s.UpdateErrorReportingSettingsAsync(
                        It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(),
                        It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                    .ThrowsAsync(failure);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(group), group, null);
        }
    }

    [Theory]
    [MemberData(nameof(SecretGroups))]
    public async Task SecretGroup_NullBody_ReturnsBadRequest(string group)
    {
        var result = await PutSecretGroup(CreateController(), group, nullBody: true);

        Assert.Equal("Request body is required", BadRequestError(result));
        _settingsService.VerifyNoOtherCalls();
    }

    [Theory]
    [MemberData(nameof(SecretGroups))]
    public async Task SecretGroup_Success_DoesNotEchoSecret(string group)
    {
        var result = await PutSecretGroup(CreateController(), group, nullBody: false);

        var json = OkJson(result);
        Assert.EndsWith("settings updated successfully", json.GetProperty("message").GetString());
        Assert.DoesNotContain(Secret, json.GetRawText());
    }

    [Theory]
    [MemberData(nameof(SecretGroups))]
    public async Task SecretGroup_ValidationFailure_ReturnsBadRequestWithServiceMessage(string group)
    {
        SetupSecretGroupFailure(group, new ArgumentException("Value is out of range"));

        var result = await PutSecretGroup(CreateController(), group, nullBody: false);

        Assert.Equal("Value is out of range", BadRequestError(result));
    }

    [Theory]
    [InlineData("email", "Failed to update email settings")]
    [InlineData("suwayomi", "Failed to update Suwayomi settings")]
    [InlineData("error-reporting", "Failed to update error reporting settings")]
    public async Task SecretGroup_UnexpectedFailure_Returns500WithoutLeakingSecret(string group, string expectedError)
    {
        var failure = new InvalidOperationException("could not persist " + Secret);
        SetupSecretGroupFailure(group, failure);

        var result = await PutSecretGroup(CreateController(), group, nullBody: false);

        var json = ServerErrorJson(result);
        Assert.Equal(expectedError, json.GetProperty("error").GetString());
        Assert.DoesNotContain(Secret, json.GetRawText());
        VerifyLogged(LogLevel.Error, expectedError, Times.Once(), failure);
    }

    [Fact]
    public async Task UpdateEmailSettings_PassesEveryFieldToSettingsService()
    {
        var request = new SettingsController.EmailSettingsRequest
        {
            SmtpHost = "smtp.example.com",
            SmtpPort = 465,
            SmtpUsername = "mailer",
            SmtpPassword = Secret,
            SmtpUseSsl = true,
            SmtpAllowInsecure = true,
            EmailFromAddress = "comics@example.com",
            EmailFromName = "Comic Library",
            EmailMaxAttachmentMb = 40,
        };

        await CreateController().UpdateEmailSettings(request);

        _settingsService.Verify(s => s.UpdateEmailSettingsAsync(
            "smtp.example.com", 465, "mailer", Secret, true, true, "comics@example.com", "Comic Library", 40,
            It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task UpdateEmailSettings_EmptyRequest_UsesDefaultsAndKeepsStoredPassword()
    {
        await CreateController().UpdateEmailSettings(new SettingsController.EmailSettingsRequest());

        _settingsService.Verify(s => s.UpdateEmailSettingsAsync(
            null, 587, null, null, false, false, null, null, 25, It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task UpdateSuwayomiSettings_PassesEveryFieldToSettingsService()
    {
        var request = new SettingsController.SuwayomiSettingsRequest
        {
            Enabled = true,
            BaseUrl = "http://suwayomi.local:4567",
            Username = "reader",
            Password = Secret,
        };

        await CreateController().UpdateSuwayomiSettings(request);

        _settingsService.Verify(s => s.UpdateSuwayomiSettingsAsync(
            true, "http://suwayomi.local:4567", "reader", Secret, It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task UpdateSuwayomiSettings_EmptyRequest_KeepsStoredPassword()
    {
        await CreateController().UpdateSuwayomiSettings(new SettingsController.SuwayomiSettingsRequest());

        _settingsService.Verify(s => s.UpdateSuwayomiSettingsAsync(
            false, null, null, null, It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task UpdateErrorReportingSettings_PassesEveryFieldToSettingsService()
    {
        var request = new SettingsController.ErrorReportingSettingsRequest
        {
            Enabled = true,
            GitHubToken = Secret,
            Assignee = "triager",
            MaxIssuesPerDay = 3,
            DedupeWindowHours = 72,
            CommentOnRecurrence = false,
        };

        await CreateController().UpdateErrorReportingSettings(request);

        _settingsService.Verify(s => s.UpdateErrorReportingSettingsAsync(
            true, Secret, "triager", 3, 72, false, It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task UpdateErrorReportingSettings_EmptyRequest_UsesDefaultsAndKeepsStoredToken()
    {
        await CreateController().UpdateErrorReportingSettings(new SettingsController.ErrorReportingSettingsRequest());

        _settingsService.Verify(s => s.UpdateErrorReportingSettingsAsync(
            false, null, null, 10, 24, true, It.IsAny<CancellationToken>()), Times.Once());
    }

    private sealed class ThrowingDbContextFactory : IDbContextFactory<ComicMaintainerDbContext>
    {
        private readonly Exception _failure;

        public ThrowingDbContextFactory(Exception failure) => _failure = failure;

        public ComicMaintainerDbContext CreateDbContext() => throw _failure;

        public Task<ComicMaintainerDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromException<ComicMaintainerDbContext>(_failure);
    }

    private sealed class CountingDbContextFactory : IDbContextFactory<ComicMaintainerDbContext>
    {
        private readonly DbContextOptions<ComicMaintainerDbContext> _options;
        private int _createdCount;
        private int _disposedCount;

        public CountingDbContextFactory(DbContextOptions<ComicMaintainerDbContext> options) => _options = options;

        public int CreatedCount => Volatile.Read(ref _createdCount);

        public int DisposedCount => Volatile.Read(ref _disposedCount);

        public ComicMaintainerDbContext CreateDbContext()
        {
            Interlocked.Increment(ref _createdCount);
            return new CountingDbContext(_options, () => Interlocked.Increment(ref _disposedCount));
        }

        private sealed class CountingDbContext : ComicMaintainerDbContext
        {
            private readonly Action _onDisposed;

            public CountingDbContext(DbContextOptions<ComicMaintainerDbContext> options, Action onDisposed)
                : base(options) => _onDisposed = onDisposed;

            public override async ValueTask DisposeAsync()
            {
                await base.DisposeAsync();
                _onDisposed();
            }
        }
    }

    private sealed class SqliteDb : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ServiceProvider _provider;

        private SqliteDb(SqliteConnection connection, ServiceProvider provider)
        {
            _connection = connection;
            _provider = provider;
        }

        public IDbContextFactory<ComicMaintainerDbContext> Factory =>
            _provider.GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>();

        public DbContextOptions<ComicMaintainerDbContext> Options =>
            new DbContextOptionsBuilder<ComicMaintainerDbContext>().UseSqlite(_connection).Options;

        public static async Task<SqliteDb> CreateAsync()
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();
            var services = new ServiceCollection();
            services.AddDbContextFactory<ComicMaintainerDbContext>(o => o.UseSqlite(connection));
            var db = new SqliteDb(connection, services.BuildServiceProvider());
            await using var context = await db.Factory.CreateDbContextAsync();
            await context.Database.EnsureCreatedAsync();
            return db;
        }

        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
