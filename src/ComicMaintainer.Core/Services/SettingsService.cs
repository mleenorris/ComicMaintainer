using System.Text.Json;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Service for persisting and managing application settings
/// Settings are stored in a JSON file in the Config directory
/// </summary>
public class SettingsService : ISettingsService
{
    private readonly ILogger<SettingsService> _logger;
    private readonly IOptionsMonitor<AppSettings> _appSettings;
    private readonly string _settingsFilePath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true
    };

    public SettingsService(
        ILogger<SettingsService> logger,
        IOptionsMonitor<AppSettings> appSettings)
    {
        _logger = logger;
        _appSettings = appSettings;
        
        var configDir = appSettings.CurrentValue.ConfigDirectory;
        
        // Ensure config directory exists
        try
        {
            Directory.CreateDirectory(configDir);
        }
        catch (UnauthorizedAccessException)
        {
            _logger.LogWarning("Unable to create config directory at {ConfigDir}, falling back to temp", configDir);
            configDir = Path.Combine(Path.GetTempPath(), "ComicMaintainer");
            Directory.CreateDirectory(configDir);
        }
        
        _settingsFilePath = Path.Combine(configDir, "user-settings.json");
        _logger.LogInformation("Settings will be persisted to: {SettingsPath}", _settingsFilePath);
    }

    public async Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        return await Task.FromResult(_appSettings.CurrentValue);
    }

    public async Task UpdateLogMaxBytesAsync(int maxBytes, CancellationToken cancellationToken = default)
    {
        if (maxBytes <= 0)
        {
            throw new ArgumentException("Max bytes must be greater than 0", nameof(maxBytes));
        }

        await UpdateSettingAsync("LogMaxBytes", maxBytes, cancellationToken);
    }

    public async Task UpdateFilenameFormatAsync(string format, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(format))
        {
            throw new ArgumentException("Format cannot be null or whitespace", nameof(format));
        }

        await UpdateSettingAsync("FilenameFormat", format, cancellationToken);
    }

    public async Task UpdateIssueNumberPaddingAsync(int padding, CancellationToken cancellationToken = default)
    {
        if (padding < 0)
        {
            throw new ArgumentException("Padding must be non-negative", nameof(padding));
        }

        await UpdateSettingAsync("IssueNumberPadding", padding, cancellationToken);
    }

    public async Task UpdateWatcherEnableRenameAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await UpdateSettingAsync("WatcherEnableRename", enabled, cancellationToken);
    }

    public async Task UpdateWatcherEnableNormalizeAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await UpdateSettingAsync("WatcherEnableNormalize", enabled, cancellationToken);
    }

    public async Task UpdateGitHubTokenAsync(string? token, CancellationToken cancellationToken = default)
    {
        await UpdateSettingAsync("GitHubToken", token, cancellationToken);
    }

    public async Task UpdateGitHubRepositoryAsync(string? repository, CancellationToken cancellationToken = default)
    {
        await UpdateSettingAsync("GitHubRepository", repository, cancellationToken);
    }

    public async Task UpdateGitHubIssueAssigneeAsync(string? assignee, CancellationToken cancellationToken = default)
    {
        await UpdateSettingAsync("GitHubIssueAssignee", assignee, cancellationToken);
    }

    public async Task UpdateDatabaseCleanupIntervalHoursAsync(int hours, CancellationToken cancellationToken = default)
    {
        if (hours < 0)
        {
            throw new ArgumentException("Cleanup interval hours must be non-negative (0 = only on startup)", nameof(hours));
        }

        await UpdateSettingAsync("DatabaseCleanupIntervalHours", hours, cancellationToken);
    }

    public async Task UpdateExternalSeriesMetadataEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await UpdateSettingAsync("EnableExternalSeriesMetadata", enabled, cancellationToken);
    }

    public async Task UpdateComicVineApiKeyAsync(string? apiKey, CancellationToken cancellationToken = default)
    {
        await UpdateSettingAsync("ComicVineApiKey", apiKey, cancellationToken);
    }

    public async Task UpdateComicVineBaseUrlAsync(string? baseUrl, CancellationToken cancellationToken = default)
    {
        await UpdateSettingAsync("ComicVineBaseUrl", baseUrl, cancellationToken);
    }

    private async Task UpdateSettingAsync(string settingName, object? value, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            // Read existing settings file or create new dictionary
            Dictionary<string, object?>? settings;
            
            if (File.Exists(_settingsFilePath))
            {
                var json = await File.ReadAllTextAsync(_settingsFilePath, cancellationToken);
                settings = JsonSerializer.Deserialize<Dictionary<string, object?>>(json);
            }
            else
            {
                settings = new Dictionary<string, object?>();
            }

            settings ??= new Dictionary<string, object?>();

            // Update the setting
            settings[settingName] = value;

            // Write back to file
            var updatedJson = JsonSerializer.Serialize(settings, _jsonOptions);
            await File.WriteAllTextAsync(_settingsFilePath, updatedJson, cancellationToken);

            // Sanitize value for logging to prevent log forging
            var sanitizedValue = value?.ToString() ?? "null";
            if (value is string strValue)
            {
                sanitizedValue = LoggingHelper.SanitizeForLog(strValue);
            }
            _logger.LogInformation("Updated setting {SettingName} to {Value}", settingName, sanitizedValue);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist setting {SettingName}", settingName);
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }
}
