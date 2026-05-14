using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using System.Text.Json;

namespace ComicMaintainer.Tests.Services;

public class SettingsServiceTests : IDisposable
{
    private readonly string _testConfigDir;
    private readonly Mock<ILogger<SettingsService>> _loggerMock;
    private readonly Mock<IOptionsMonitor<AppSettings>> _appSettingsMock;
    private readonly AppSettings _appSettings;
    private readonly SettingsService _service;

    public SettingsServiceTests()
    {
        // Create a temporary directory for test settings
        _testConfigDir = Path.Combine(Path.GetTempPath(), $"ComicMaintainerTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testConfigDir);

        _appSettings = new AppSettings
        {
            ConfigDirectory = _testConfigDir,
            LogMaxBytes = 10485760,
            FilenameFormat = "{series} - Chapter {issue}",
            IssueNumberPadding = 4,
            WatcherEnableRename = true,
            WatcherEnableNormalize = true
        };

        _loggerMock = new Mock<ILogger<SettingsService>>();
        _appSettingsMock = new Mock<IOptionsMonitor<AppSettings>>();
        _appSettingsMock.Setup(x => x.CurrentValue).Returns(_appSettings);

        _service = new SettingsService(_loggerMock.Object, _appSettingsMock.Object);
    }

    /// <summary>
    /// Reads the AppSettings section from user-settings.json. The persisted file is rooted under
    /// "AppSettings" so it can be registered as a configuration source bound to the AppSettings
    /// section with reload-on-change support.
    /// </summary>
    private static Dictionary<string, JsonElement> ReadAppSettingsSection(string filePath)
    {
        var json = File.ReadAllText(filePath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);

        Assert.True(
            root.TryGetProperty("AppSettings", out var appSettingsElement),
            "user-settings.json should contain an 'AppSettings' object root");
        Assert.Equal(JsonValueKind.Object, appSettingsElement.ValueKind);

        var result = new Dictionary<string, JsonElement>();
        foreach (var prop in appSettingsElement.EnumerateObject())
        {
            result[prop.Name] = prop.Value.Clone();
        }
        return result;
    }

    public void Dispose()
    {
        // Clean up test directory
        if (Directory.Exists(_testConfigDir))
        {
            Directory.Delete(_testConfigDir, true);
        }
    }

    [Fact]
    public async Task UpdateLogMaxBytes_PersistsSettingToFile()
    {
        // Arrange
        const int newMaxBytes = 20971520; // 20 MB

        // Act
        await _service.UpdateLogMaxBytesAsync(newMaxBytes);

        // Assert
        var settingsFilePath = Path.Combine(_testConfigDir, "user-settings.json");
        Assert.True(File.Exists(settingsFilePath));

        var settings = ReadAppSettingsSection(settingsFilePath);

        Assert.True(settings.ContainsKey("LogMaxBytes"));
        Assert.Equal(newMaxBytes, settings["LogMaxBytes"].GetInt32());
    }

    [Fact]
    public async Task UpdateLogMaxBytes_WithInvalidValue_ThrowsException()
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => 
            _service.UpdateLogMaxBytesAsync(0));
        
        await Assert.ThrowsAsync<ArgumentException>(() => 
            _service.UpdateLogMaxBytesAsync(-100));
    }

    [Fact]
    public async Task UpdateFilenameFormat_PersistsSettingToFile()
    {
        // Arrange
        const string newFormat = "{series} v{volume} #{issue}";

        // Act
        await _service.UpdateFilenameFormatAsync(newFormat);

        // Assert
        var settingsFilePath = Path.Combine(_testConfigDir, "user-settings.json");
        Assert.True(File.Exists(settingsFilePath));

        var settings = ReadAppSettingsSection(settingsFilePath);

        Assert.True(settings.ContainsKey("FilenameFormat"));
        Assert.Equal(newFormat, settings["FilenameFormat"].GetString());
    }

    [Fact]
    public async Task UpdateFilenameFormat_WithEmptyValue_ThrowsException()
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => 
            _service.UpdateFilenameFormatAsync(""));
        
        await Assert.ThrowsAsync<ArgumentException>(() => 
            _service.UpdateFilenameFormatAsync("   "));
    }

    [Fact]
    public async Task UpdateIssueNumberPadding_PersistsSettingToFile()
    {
        // Arrange
        const int newPadding = 3;

        // Act
        await _service.UpdateIssueNumberPaddingAsync(newPadding);

        // Assert
        var settingsFilePath = Path.Combine(_testConfigDir, "user-settings.json");
        Assert.True(File.Exists(settingsFilePath));

        var settings = ReadAppSettingsSection(settingsFilePath);

        Assert.True(settings.ContainsKey("IssueNumberPadding"));
        Assert.Equal(newPadding, settings["IssueNumberPadding"].GetInt32());
    }

    [Fact]
    public async Task UpdateWatcherEnableRename_PersistsSettingToFile()
    {
        // Arrange
        const bool newValue = false;

        // Act
        await _service.UpdateWatcherEnableRenameAsync(newValue);

        // Assert
        var settingsFilePath = Path.Combine(_testConfigDir, "user-settings.json");
        Assert.True(File.Exists(settingsFilePath));

        var settings = ReadAppSettingsSection(settingsFilePath);

        Assert.True(settings.ContainsKey("WatcherEnableRename"));
        Assert.Equal(newValue, settings["WatcherEnableRename"].GetBoolean());
    }

    [Fact]
    public async Task UpdateGitHubToken_PersistsSettingToFile()
    {
        // Arrange
        const string newToken = "ghp_new_test_token";

        // Act
        await _service.UpdateGitHubTokenAsync(newToken);

        // Assert
        var settingsFilePath = Path.Combine(_testConfigDir, "user-settings.json");
        Assert.True(File.Exists(settingsFilePath));

        var settings = ReadAppSettingsSection(settingsFilePath);

        Assert.True(settings.ContainsKey("GitHubToken"));
        Assert.Equal(newToken, settings["GitHubToken"].GetString());
    }

    [Fact]
    public async Task UpdateGitHubRepository_PersistsSettingToFile()
    {
        // Arrange
        const string newRepo = "newowner/newrepo";

        // Act
        await _service.UpdateGitHubRepositoryAsync(newRepo);

        // Assert
        var settingsFilePath = Path.Combine(_testConfigDir, "user-settings.json");
        Assert.True(File.Exists(settingsFilePath));

        var settings = ReadAppSettingsSection(settingsFilePath);

        Assert.True(settings.ContainsKey("GitHubRepository"));
        Assert.Equal(newRepo, settings["GitHubRepository"].GetString());
    }

    [Fact]
    public async Task UpdateDatabaseCleanupIntervalHours_PersistsSettingToFile()
    {
        // Arrange
        const int newInterval = 24;

        // Act
        await _service.UpdateDatabaseCleanupIntervalHoursAsync(newInterval);

        // Assert
        var settingsFilePath = Path.Combine(_testConfigDir, "user-settings.json");
        Assert.True(File.Exists(settingsFilePath));

        var settings = ReadAppSettingsSection(settingsFilePath);

        Assert.True(settings.ContainsKey("DatabaseCleanupIntervalHours"));
        Assert.Equal(newInterval, settings["DatabaseCleanupIntervalHours"].GetInt32());
    }

    [Fact]
    public async Task UpdateDatabaseCleanupIntervalHours_WithZero_PersistsSettingToFile()
    {
        // Arrange - 0 means only run on startup
        const int newInterval = 0;

        // Act
        await _service.UpdateDatabaseCleanupIntervalHoursAsync(newInterval);

        // Assert
        var settingsFilePath = Path.Combine(_testConfigDir, "user-settings.json");
        Assert.True(File.Exists(settingsFilePath));

        var settings = ReadAppSettingsSection(settingsFilePath);

        Assert.True(settings.ContainsKey("DatabaseCleanupIntervalHours"));
        Assert.Equal(newInterval, settings["DatabaseCleanupIntervalHours"].GetInt32());
    }

    [Fact]
    public async Task UpdateDatabaseCleanupIntervalHours_WithNegativeValue_ThrowsException()
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => 
            _service.UpdateDatabaseCleanupIntervalHoursAsync(-1));
        
        await Assert.ThrowsAsync<ArgumentException>(() => 
            _service.UpdateDatabaseCleanupIntervalHoursAsync(-100));
    }

    [Fact]
    public async Task MultipleUpdates_PreservesAllSettings()
    {
        // Arrange & Act
        await _service.UpdateLogMaxBytesAsync(20971520);
        await _service.UpdateFilenameFormatAsync("{series} v{volume}");
        await _service.UpdateIssueNumberPaddingAsync(3);
        await _service.UpdateWatcherEnableRenameAsync(false);

        // Assert
        var settingsFilePath = Path.Combine(_testConfigDir, "user-settings.json");
        Assert.True(File.Exists(settingsFilePath));

        var settings = ReadAppSettingsSection(settingsFilePath);

        Assert.Equal(4, settings.Count);
        Assert.Equal(20971520, settings["LogMaxBytes"].GetInt32());
        Assert.Equal("{series} v{volume}", settings["FilenameFormat"].GetString());
        Assert.Equal(3, settings["IssueNumberPadding"].GetInt32());
        Assert.False(settings["WatcherEnableRename"].GetBoolean());
    }

    [Fact]
    public async Task GetSettingsAsync_ReturnsCurrentSettings()
    {
        // Act
        var result = await _service.GetSettingsAsync();

        // Assert
        Assert.NotNull(result);
        Assert.Equal(_appSettings.LogMaxBytes, result.LogMaxBytes);
        Assert.Equal(_appSettings.FilenameFormat, result.FilenameFormat);
        Assert.Equal(_appSettings.IssueNumberPadding, result.IssueNumberPadding);
    }
}
