using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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

    [Fact]
    public async Task UpdatedSettings_PickedUpByConfigurationReloadOnChange()
    {
        // This is the end-to-end hot-reload regression test: it wires up a real
        // ConfigurationBuilder + IOptionsMonitor against the user-settings.json file the
        // service writes to, and asserts that the new value is observed via IOptionsMonitor
        // without recreating any object — proving that Settings changes take effect without
        // restarting the service.
        var settingsFilePath = Path.Combine(_testConfigDir, "user-settings.json");

        // Seed the file with the new "AppSettings" object root so the configuration provider
        // has something to bind from process start (matches Program.cs bootstrap behaviour).
        await File.WriteAllTextAsync(settingsFilePath, "{ \"AppSettings\": {} }");

        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddJsonFile(settingsFilePath, optional: true, reloadOnChange: true)
            .Build();

        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddOptions<AppSettings>().Bind(configuration.GetSection("AppSettings"));
        var provider = services.BuildServiceProvider();
        var monitor = provider.GetRequiredService<IOptionsMonitor<AppSettings>>();

        // Act — write a new value via the service
        await _service.UpdateFilenameFormatAsync("{series} chapter {issue}");

        // Wait for the file watcher inside the configuration provider to pick up the change.
        // 5 seconds is generous; in practice the change is observed within ~100 ms.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (monitor.CurrentValue.FilenameFormat != "{series} chapter {issue}"
               && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        // Assert
        Assert.Equal("{series} chapter {issue}", monitor.CurrentValue.FilenameFormat);
    }

    [Fact]
    public async Task LegacyFlatShape_ReadCorrectlyAndRewrittenInNewShape()
    {
        // Arrange — pre-populate the file with the legacy flat shape that previous versions wrote
        var settingsFilePath = Path.Combine(_testConfigDir, "user-settings.json");
        await File.WriteAllTextAsync(settingsFilePath,
            "{\n  \"FilenameFormat\": \"old-format\",\n  \"IssueNumberPadding\": 5\n}");

        // Act — update one value through the service
        await _service.UpdateIssueNumberPaddingAsync(7);

        // Assert — file is rewritten in the new "AppSettings" object-rooted shape and the
        // previously-set FilenameFormat is preserved (legacy values are migrated forward).
        var settings = ReadAppSettingsSection(settingsFilePath);
        Assert.Equal("old-format", settings["FilenameFormat"].GetString());
        Assert.Equal(7, settings["IssueNumberPadding"].GetInt32());
    }

    [Fact]
    public async Task PersistedFile_HasAppSettingsObjectRoot()
    {
        // Act
        await _service.UpdateFilenameFormatAsync("{series} - {issue}");

        // Assert — the file MUST be rooted under "AppSettings" so the configuration provider
        // can bind it directly to the AppSettings section with reload-on-change support.
        var settingsFilePath = Path.Combine(_testConfigDir, "user-settings.json");
        var json = await File.ReadAllTextAsync(settingsFilePath);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(System.Text.Json.JsonValueKind.Object, doc.RootElement.ValueKind);
        Assert.True(doc.RootElement.TryGetProperty("AppSettings", out var appSettings));
        Assert.Equal(System.Text.Json.JsonValueKind.Object, appSettings.ValueKind);
        Assert.True(appSettings.TryGetProperty("FilenameFormat", out _));
    }
}
