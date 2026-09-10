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

    public async Task UpdateWriteCoverToFirstArchiveAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await UpdateSettingAsync("WriteCoverToFirstArchive", enabled, cancellationToken);
    }

    public async Task UpdateWriteCoverToSeriesFolderAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await UpdateSettingAsync("WriteCoverToSeriesFolder", enabled, cancellationToken);
    }

    public async Task UpdateDownloadExternalSeriesImagesAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await UpdateSettingAsync("DownloadExternalSeriesImages", enabled, cancellationToken);
    }

    public async Task UpdateSeriesImageMaxBytesAsync(int maxBytes, CancellationToken cancellationToken = default)
    {
        if (maxBytes <= 0)
        {
            throw new ArgumentException("Series image max bytes must be greater than 0", nameof(maxBytes));
        }

        await UpdateSettingAsync("SeriesImageMaxBytes", maxBytes, cancellationToken);
    }

    public async Task UpdateSeriesImageMaxDownloadBytesAsync(int maxDownloadBytes, CancellationToken cancellationToken = default)
    {
        if (maxDownloadBytes <= 0)
        {
            throw new ArgumentException("Series image max download bytes must be greater than 0", nameof(maxDownloadBytes));
        }

        await UpdateSettingAsync("SeriesImageMaxDownloadBytes", maxDownloadBytes, cancellationToken);
    }

    public async Task UpdateSeriesImageMaxDimensionAsync(int maxDimension, CancellationToken cancellationToken = default)
    {
        if (maxDimension <= 0)
        {
            throw new ArgumentException("Series image max dimension must be greater than 0", nameof(maxDimension));
        }

        await UpdateSettingAsync("SeriesImageMaxDimension", maxDimension, cancellationToken);
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

    public async Task UpdateDefaultLibraryViewAsync(string view, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(view))
        {
            throw new ArgumentException("View cannot be null or whitespace", nameof(view));
        }

        var normalized = view.Trim().ToLowerInvariant();
        if (normalized != "files" && normalized != "series")
        {
            throw new ArgumentException("View must be either 'files' or 'series'", nameof(view));
        }

        await UpdateSettingAsync("DefaultLibraryView", normalized, cancellationToken);
    }

    public async Task UpdateAllowRegistrationAsync(bool allowed, CancellationToken cancellationToken = default)
    {
        await UpdateSettingAsync("AllowRegistration", allowed, cancellationToken);
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

    public async Task UpdateMangaDexEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await UpdateSettingAsync("EnableMangaDexMetadata", enabled, cancellationToken);
    }

    public async Task UpdateMangaDexBaseUrlAsync(string? baseUrl, CancellationToken cancellationToken = default)
    {
        await UpdateSettingAsync("MangaDexBaseUrl", baseUrl, cancellationToken);
    }

    public async Task UpdateAniListEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await UpdateSettingAsync("EnableAniListMetadata", enabled, cancellationToken);
    }

    public async Task UpdateAniListBaseUrlAsync(string? baseUrl, CancellationToken cancellationToken = default)
    {
        await UpdateSettingAsync("AniListBaseUrl", baseUrl, cancellationToken);
    }

    public async Task UpdateDefaultPreferredLanguageAsync(string? language, CancellationToken cancellationToken = default)
    {
        // Null/empty is allowed (clears the default); otherwise validate
        // against the same allow-list the per-series API uses so the JSON
        // file never persists a code that no one will honour at read time.
        var normalized = Models.SeriesLanguagePreference.ValidateOrThrow(language);
        await UpdateSettingAsync("DefaultPreferredLanguage", normalized, cancellationToken);
    }

    private async Task UpdateSettingAsync(string settingName, object? value, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            // Read existing user-settings.json which is rooted under "AppSettings" so the
            // configuration provider can bind it directly to AppSettings via reload-on-change.
            Dictionary<string, object?> appSettingsSection;

            if (File.Exists(_settingsFilePath))
            {
                var json = await File.ReadAllTextAsync(_settingsFilePath, cancellationToken);
                appSettingsSection = ReadAppSettingsSection(json);
            }
            else
            {
                appSettingsSection = new Dictionary<string, object?>(StringComparer.Ordinal);
            }

            // Update the setting
            appSettingsSection[settingName] = value;

            // Serialize and write atomically (temp file + move) so the configuration provider's
            // file watcher does not observe a half-written file.
            var root = new Dictionary<string, object?>
            {
                ["AppSettings"] = appSettingsSection
            };
            var updatedJson = JsonSerializer.Serialize(root, _jsonOptions);

            var tempPath = _settingsFilePath + ".tmp";
            await File.WriteAllTextAsync(tempPath, updatedJson, cancellationToken);
            File.Move(tempPath, _settingsFilePath, overwrite: true);

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

    /// <summary>
    /// Read the existing user-settings.json content into a mutable dictionary representing
    /// the AppSettings section. Supports both the new object-rooted shape ({ "AppSettings": {...} })
    /// and the legacy flat shape (top-level keys) for backward compatibility.
    /// </summary>
    private static Dictionary<string, object?> ReadAppSettingsSection(string json)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json))
        {
            return result;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return result;
            }

            // New shape: { "AppSettings": { ... } }
            if (root.TryGetProperty("AppSettings", out var appSettingsElement) &&
                appSettingsElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in appSettingsElement.EnumerateObject())
                {
                    result[prop.Name] = ConvertJsonElement(prop.Value);
                }
                return result;
            }

            // Legacy flat shape: top-level keys are the setting names
            foreach (var prop in root.EnumerateObject())
            {
                result[prop.Name] = ConvertJsonElement(prop.Value);
            }
        }
        catch (JsonException)
        {
            // Corrupt or unreadable file — start with an empty section. The next write
            // will replace the file with a valid one.
        }

        return result;
    }

    private static object? ConvertJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var l) ? (object)l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            // For arrays/objects, round-trip via raw JSON so they are re-serialized verbatim.
            _ => JsonDocument.Parse(element.GetRawText()).RootElement.Clone(),
        };
    }
}
