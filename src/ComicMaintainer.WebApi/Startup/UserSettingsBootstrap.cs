using System.Text.Json;

namespace ComicMaintainer.WebApi.Startup;

/// <summary>
/// Bootstrap helpers for user-settings.json that run before the host is built.
/// </summary>
internal static class UserSettingsBootstrap
{
    internal static long LoadLogMaxBytes(string userSettingsPath)
    {
        const long defaultLogMaxBytes = 10_485_760; // 10 MB
        
        try
        {
            if (!File.Exists(userSettingsPath))
            {
                return defaultLogMaxBytes;
            }

            var json = File.ReadAllText(userSettingsPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // New shape: { "AppSettings": { "LogMaxBytes": ... } }
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("AppSettings", out var appSettingsElement) &&
                appSettingsElement.ValueKind == JsonValueKind.Object &&
                appSettingsElement.TryGetProperty("LogMaxBytes", out var newShapeValue) &&
                newShapeValue.ValueKind == JsonValueKind.Number &&
                newShapeValue.TryGetInt32(out var newShapeBytes))
            {
                return newShapeBytes;
            }

            // Legacy flat shape: { "LogMaxBytes": ... }
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("LogMaxBytes", out var legacyValue) &&
                legacyValue.ValueKind == JsonValueKind.Number &&
                legacyValue.TryGetInt32(out var legacyBytes))
            {
                return legacyBytes;
            }
        }
        catch
        {
            // If any error occurs, use default value
        }

        return defaultLogMaxBytes;
    }

    /// <summary>
    /// Ensures the user-settings.json file exists and is in the new "AppSettings" object-rooted shape.
    /// Performs a one-time migration from the legacy flat shape (top-level keys like "FilenameFormat")
    /// to the new shape ({ "AppSettings": { ... } }) so the file can be registered as a configuration
    /// source under the AppSettings section with reload-on-change support.
    /// </summary>
    internal static void EnsureUserSettingsFileMigrated(string userSettingsPath)
    {
        try
        {
            if (!File.Exists(userSettingsPath))
            {
                // Create an empty wrapper so the file watcher in the configuration provider
                // has a stable file to observe from process start.
                File.WriteAllText(userSettingsPath, "{\n  \"AppSettings\": {}\n}");
                return;
            }

            var json = File.ReadAllText(userSettingsPath);
            if (string.IsNullOrWhiteSpace(json))
            {
                File.WriteAllText(userSettingsPath, "{\n  \"AppSettings\": {}\n}");
                return;
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            // Already in the new shape
            if (root.TryGetProperty("AppSettings", out _))
            {
                return;
            }

            // Migrate flat shape -> object-rooted shape
            var migrated = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var prop in root.EnumerateObject())
            {
                migrated[prop.Name] = prop.Value.Clone();
            }

            var newRoot = new Dictionary<string, object?>
            {
                ["AppSettings"] = migrated
            };
            var serialized = JsonSerializer.Serialize(newRoot, new JsonSerializerOptions { WriteIndented = true });

            // Write atomically (temp file + move) so the configuration file watcher does not
            // observe a half-written file.
            var tempPath = userSettingsPath + ".migrating";
            File.WriteAllText(tempPath, serialized);
            File.Move(tempPath, userSettingsPath, overwrite: true);
        }
        catch
        {
            // If migration fails, leave the file untouched. The configuration provider will
            // simply not pick up legacy values until the user re-saves a setting.
        }
    }
}
