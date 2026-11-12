using System.Text.Json;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// One save of the Settings page (<see cref="SettingsService.UpdateSettingsAsync"/>): every group
/// in one atomic write that is visible to readers when the call returns, where null keeps a value.
/// </summary>
public sealed class SettingsServiceSaveAllTests : IDisposable
{
    private const string SeededSettings = """
        {
          "AppSettings": {
            "SmtpHost": "mail.old.example",
            "SmtpPort": 25,
            "SmtpUsername": "old-user",
            "SmtpPassword": "old-password",
            "EmailFromName": "Old Name",
            "ComicVineApiKey": "old-key",
            "ComicVineBaseUrl": "https://old.comicvine.example",
            "StaleCleanupSentinelFile": ".old-sentinel"
          }
        }
        """;

    private readonly string _configDir;
    private readonly string _settingsPath;
    private readonly Mock<IConfigurationRoot> _configurationRoot = new();
    private readonly AppSettings _settings;
    private readonly SettingsService _service;
    private readonly SettingsServiceCoverageTests.CapturingLogger _logger = new();

    public SettingsServiceSaveAllTests()
    {
        _configDir = Path.Join(Path.GetTempPath(), $"SettingsServiceSaveAll_{Guid.NewGuid():N}");
        _settingsPath = Path.Join(_configDir, "user-settings.json");
        _settings = new AppSettings { ConfigDirectory = _configDir };
        _service = new SettingsService(_logger, new TestOptionsMonitor<AppSettings>(_settings), _configurationRoot.Object);
    }

    public void Dispose()
    {
        if (Directory.Exists(_configDir))
        {
            Directory.Delete(_configDir, recursive: true);
        }
    }

    private JsonElement SavedSection()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(_settingsPath));
        return document.RootElement.GetProperty("AppSettings").Clone();
    }

    private static SettingsUpdate EveryGroup() => new()
    {
        FilenameFormat = "{series} - {issue}",
        LogMaxBytes = 2 * 1048576,
        IssueNumberPadding = 4,
        DatabaseCleanup = new DatabaseCleanupUpdate(24, 20, 12, " .mounted ", 30, 1000),
        ExternalSeriesMetadata = new ExternalSeriesMetadataUpdate(true, " new-key ", " https://comicvine.example ", false, null, true, "https://anilist.example"),
        SeriesImageLimits = new SeriesImageLimitsUpdate(1048576, 2 * 1048576, 800),
        Email = new EmailUpdate(" mail.example ", 465, " user ", "secret ", true, false, " from@example.com ", " Comics ", 30),
        EmailRateLimit = new EmailRateLimitUpdate(10, 100, 5),
        Suwayomi = new SuwayomiUpdate(true, " http://suwayomi.example:4567/ ", " reader ", "pass", string.Empty, string.Empty),
        ErrorReporting = new ErrorReportingUpdate(true, "ghp_token", " octocat ", 3, 48, false),
    };

    [Fact]
    public async Task UpdateSettingsAsync_WritesEveryGroupInOneAtomicWrite()
    {
        await _service.UpdateSettingsAsync(EveryGroup());

        // One save, one configuration reload.
        _configurationRoot.Verify(r => r.Reload(), Times.Once());
        var saved = SavedSection();
        Assert.Equal("{series} - {issue}", saved.GetProperty("FilenameFormat").GetString());
        Assert.Equal(2097152, saved.GetProperty("LogMaxBytes").GetInt32());
        Assert.Equal(4, saved.GetProperty("IssueNumberPadding").GetInt32());
        Assert.Equal(24, saved.GetProperty("DatabaseCleanupIntervalHours").GetInt32());
        Assert.Equal(".mounted", saved.GetProperty("StaleCleanupSentinelFile").GetString());
        Assert.Equal("new-key", saved.GetProperty("ComicVineApiKey").GetString());
        Assert.Equal(800, saved.GetProperty("SeriesImageMaxDimension").GetInt32());
        Assert.Equal("mail.example", saved.GetProperty("SmtpHost").GetString());
        Assert.Equal("secret ", saved.GetProperty("SmtpPassword").GetString());
        Assert.Equal(10, saved.GetProperty("EmailMaxSendsPerHour").GetInt32());
        Assert.Equal("http://suwayomi.example:4567", saved.GetProperty("SuwayomiBaseUrl").GetString());
        Assert.Equal("ghp_token", saved.GetProperty("ErrorReportingGitHubToken").GetString());
    }

    [Fact]
    public async Task UpdateSettingsAsync_LogsEachSettingButNeverASecret()
    {
        await _service.UpdateSettingsAsync(EveryGroup());

        Assert.Contains(_logger.Messages, m => m.Contains("FilenameFormat", StringComparison.Ordinal));
        Assert.DoesNotContain(_logger.Messages, m => m.Contains("ghp_token", StringComparison.Ordinal));
        Assert.DoesNotContain(_logger.Messages, m => m.Contains("new-key", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UpdateSettingsAsync_ALaterInvalidGroup_SavesNothing()
    {
        var update = EveryGroup() with
        {
            ErrorReporting = new ErrorReportingUpdate(false, null, null, MaxIssuesPerDay: 101),
        };

        var ex = await Assert.ThrowsAsync<SettingsValidationException>(() => _service.UpdateSettingsAsync(update));

        Assert.Equal("Maximum issues per day must be between 0 and 100", ex.Reason);
        Assert.False(File.Exists(_settingsPath));
        _configurationRoot.Verify(r => r.Reload(), Times.Never());
    }

    [Fact]
    public async Task UpdateSettingsAsync_WithNothingSet_DoesNotWrite()
    {
        await _service.UpdateSettingsAsync(new SettingsUpdate());

        Assert.False(File.Exists(_settingsPath));
        _configurationRoot.Verify(r => r.Reload(), Times.Never());
    }

    [Fact]
    public async Task UpdateSettingsAsync_Null_IsABugNotAValidationFailure()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => _service.UpdateSettingsAsync(null!));
    }

    [Fact]
    public async Task UpdateSettingsAsync_NullFieldsKeepTheirStoredValues()
    {
        Directory.CreateDirectory(_configDir);
        File.WriteAllText(_settingsPath, SeededSettings);

        await _service.UpdateSettingsAsync(new SettingsUpdate { Email = new EmailUpdate(SmtpPort: 2525) });

        var saved = SavedSection();
        Assert.Equal(2525, saved.GetProperty("SmtpPort").GetInt32());
        Assert.Equal("mail.old.example", saved.GetProperty("SmtpHost").GetString());
        Assert.Equal("old-user", saved.GetProperty("SmtpUsername").GetString());
        Assert.Equal("old-password", saved.GetProperty("SmtpPassword").GetString());
        Assert.Equal("Old Name", saved.GetProperty("EmailFromName").GetString());
    }

    [Fact]
    public async Task UpdateSettingsAsync_EmptyTextClearsAndBlankUrlKeeps()
    {
        Directory.CreateDirectory(_configDir);
        File.WriteAllText(_settingsPath, SeededSettings);

        await _service.UpdateSettingsAsync(new SettingsUpdate
        {
            Email = new EmailUpdate(SmtpHost: "", SmtpUsername: "   ", SmtpPassword: "", EmailFromName: ""),
            DatabaseCleanup = new DatabaseCleanupUpdate(StaleCleanupSentinelFile: ""),
            ExternalSeriesMetadata = new ExternalSeriesMetadataUpdate(ComicVineApiKey: "", ComicVineBaseUrl: "   "),
        });

        var saved = SavedSection();
        Assert.Equal(JsonValueKind.Null, saved.GetProperty("SmtpHost").ValueKind);
        Assert.Equal(JsonValueKind.Null, saved.GetProperty("SmtpUsername").ValueKind);
        Assert.Equal(JsonValueKind.Null, saved.GetProperty("SmtpPassword").ValueKind);
        Assert.Equal("ComicMaintainer", saved.GetProperty("EmailFromName").GetString());
        Assert.Equal(string.Empty, saved.GetProperty("StaleCleanupSentinelFile").GetString());
        Assert.Equal(JsonValueKind.Null, saved.GetProperty("ComicVineApiKey").ValueKind);
        Assert.Equal("https://old.comicvine.example", saved.GetProperty("ComicVineBaseUrl").GetString());
    }

    [Theory]
    [InlineData(null, 2097152, null, false)]
    [InlineData(1048576, null, null, false)]
    [InlineData(4194304, null, null, true)]
    [InlineData(null, 524288, null, true)]
    [InlineData(null, null, 900, false)]
    public async Task UpdateSettingsAsync_ImageLimitsAreComparedWithTheOtherCurrentLimit(int? maxBytes, int? maxDownloadBytes, int? maxDimension, bool refused)
    {
        _settings.SeriesImageMaxBytes = 1048576;
        _settings.SeriesImageMaxDownloadBytes = 2097152;
        var update = new SettingsUpdate { SeriesImageLimits = new SeriesImageLimitsUpdate(maxBytes, maxDownloadBytes, maxDimension) };

        if (refused)
        {
            await Assert.ThrowsAsync<SettingsValidationException>(() => _service.UpdateSettingsAsync(update));
        }
        else
        {
            await _service.UpdateSettingsAsync(update);
            Assert.True(File.Exists(_settingsPath));
        }
    }

    [Theory]
    [InlineData(true, null, null, true)]            // enabling while nothing configures a URL
    [InlineData(true, "", null, true)]              // enabling and clearing the URL
    [InlineData(true, "http://a.example", null, false)]
    [InlineData(true, null, "http://stored.example", false)] // enabling with the stored URL
    [InlineData(null, "", "http://stored.example", true)]    // clearing the URL of an enabled integration
    [InlineData(false, "", null, false)]
    public async Task UpdateSettingsAsync_SuwayomiEnablingUsesTheUrlInEffectAfterTheSave(bool? enabled, string? requestUrl, string? storedUrl, bool refused)
    {
        _settings.EnableSuwayomi = enabled is null;
        _settings.SuwayomiBaseUrl = storedUrl!;
        var update = new SettingsUpdate { Suwayomi = new SuwayomiUpdate(Enabled: enabled, BaseUrl: requestUrl) };

        if (refused)
        {
            await Assert.ThrowsAsync<SettingsValidationException>(() => _service.UpdateSettingsAsync(update));
        }
        else
        {
            await _service.UpdateSettingsAsync(update);
        }
    }

    [Theory]
    [InlineData(true, null, null, true)]            // enabling with no token anywhere
    [InlineData(true, null, "stored-token", false)] // enabling with the stored token
    [InlineData(null, "", "stored-token", true)]    // clearing the token of enabled reporting
    [InlineData(null, null, null, false)]           // switch and token both left out
    public async Task UpdateSettingsAsync_ErrorReportingNeedsATokenOnlyWhenItIsAffected(bool? enabled, string? requestToken, string? storedToken, bool refused)
    {
        _settings.ErrorReportingEnabled = enabled is null;
        _settings.ErrorReportingGitHubToken = storedToken;
        var update = new SettingsUpdate { ErrorReporting = new ErrorReportingUpdate(enabled, requestToken, MaxIssuesPerDay: 1) };

        if (refused)
        {
            await Assert.ThrowsAsync<SettingsValidationException>(() => _service.UpdateSettingsAsync(update));
        }
        else
        {
            await _service.UpdateSettingsAsync(update);
        }
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(InvalidDataException))]
    public async Task UpdateSettingsAsync_AFailedReload_LogsAWarningButKeepsTheSave(Type failureType)
    {
        _configurationRoot.Setup(r => r.Reload()).Throws((Exception)Activator.CreateInstance(failureType)!);

        await _service.UpdateSettingsAsync(new SettingsUpdate { IssueNumberPadding = 5 });

        Assert.Equal(5, SavedSection().GetProperty("IssueNumberPadding").GetInt32());
        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("reloaded", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UpdateSettingsAsync_AnUnexpectedReloadFailure_IsNotHidden()
    {
        // A bug in a change listener must not be reported as a successful save.
        _configurationRoot.Setup(r => r.Reload()).Throws(new InvalidOperationException("listener bug"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.UpdateSettingsAsync(new SettingsUpdate { IssueNumberPadding = 5 }));
    }

    [Fact]
    public async Task UpdateSettingsAsync_AClientAbort_IsNotLoggedAsAFailureAndSavesNothing()
    {
        using var request = new CancellationTokenSource();
        var service = new AbortingSettingsService(_logger, new TestOptionsMonitor<AppSettings>(_settings), _configurationRoot.Object, request);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.UpdateSettingsAsync(new SettingsUpdate { IssueNumberPadding = 5 }, request.Token));

        Assert.DoesNotContain(_logger.Entries, e => e.Level >= LogLevel.Warning);
        Assert.False(File.Exists(_settingsPath));
        _configurationRoot.Verify(r => r.Reload(), Times.Never());
    }

    [Fact]
    public async Task UpdateSettingsAsync_AWriteFailure_IsLoggedWithTheSettingNames()
    {
        // A directory where the service stages its temporary file makes the write fail.
        Directory.CreateDirectory(_settingsPath + ".tmp");

        await Assert.ThrowsAnyAsync<Exception>(() => _service.UpdateSettingsAsync(new SettingsUpdate { IssueNumberPadding = 5 }));

        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("IssueNumberPadding", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UpdateSettingsAsync_IsVisibleThroughARealMonitorBeforeItReturns()
    {
        // reloadOnChange is off: only the service's own reload can make this visible.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AppSettings:ConfigDirectory"] = _configDir })
            .AddJsonFile(_settingsPath, optional: true, reloadOnChange: false)
            .Build();
        var services = new ServiceCollection();
        services.AddOptions<AppSettings>().Bind(configuration.GetSection("AppSettings"));
        var monitor = services.BuildServiceProvider().GetRequiredService<IOptionsMonitor<AppSettings>>();
        var service = new SettingsService(_logger, monitor, configuration);

        await service.UpdateSettingsAsync(EveryGroup() with { Suwayomi = new SuwayomiUpdate(true, "http://suwayomi.example") });

        Assert.True(monitor.CurrentValue.EnableSuwayomi);
        Assert.Equal("http://suwayomi.example", monitor.CurrentValue.SuwayomiBaseUrl);
        Assert.Equal("{series} - {issue}", monitor.CurrentValue.FilenameFormat);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UpdateSettingsAsync_SavesFillComicInfoFromProviders(bool enabled)
    {
        await _service.UpdateSettingsAsync(new SettingsUpdate
        {
            ExternalSeriesMetadata = new ExternalSeriesMetadataUpdate(FillComicInfoFromProviders: enabled)
        });

        Assert.Equal(enabled, SavedSection().GetProperty("FillComicInfoFromProviders").GetBoolean());
    }

    [Fact]
    public async Task UpdateSettingsAsync_LeavesFillComicInfoFromProvidersAloneWhenItIsOmitted()
    {
        await _service.UpdateSettingsAsync(new SettingsUpdate
        {
            ExternalSeriesMetadata = new ExternalSeriesMetadataUpdate(Enabled: true)
        });

        Assert.False(SavedSection().TryGetProperty("FillComicInfoFromProviders", out _));
    }

    [Fact]
    public void FillComicInfoFromProviders_IsOnByDefault()
    {
        Assert.True(new AppSettings().FillComicInfoFromProviders);
    }

    /// <summary>Stands in for a client that disconnects while the file is being rewritten.</summary>
    private sealed class AbortingSettingsService(
        ILogger<SettingsService> logger,
        IOptionsMonitor<AppSettings> options,
        IConfigurationRoot configuration,
        CancellationTokenSource request)
        : SettingsService(logger, options, configuration)
    {
        protected override void OnStagingSetting(string settingName) => request.Cancel();
    }
}
