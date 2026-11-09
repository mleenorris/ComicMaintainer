namespace ComicMaintainer.MauiApp.Services.Platform;

/// <summary>Keeps the settings in the device's preferences.</summary>
public sealed class PreferencesSettingsService : ISettingsService
{
    private const string ServerUrlKey = "server_url";

    public string ServerUrl
    {
        get => Preferences.Default.Get(ServerUrlKey, string.Empty);
        set => Preferences.Default.Set(ServerUrlKey, value);
    }
}
