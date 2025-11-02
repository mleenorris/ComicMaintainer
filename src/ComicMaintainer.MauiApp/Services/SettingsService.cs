namespace ComicMaintainer.MauiApp.Services;

public class SettingsService : ISettingsService
{
    private const string ServerUrlKey = "server_url";
    private const string DefaultServerUrl = "http://localhost:5000";

    public string ServerUrl
    {
        get => Preferences.Get(ServerUrlKey, DefaultServerUrl);
        set => Preferences.Set(ServerUrlKey, value);
    }

    public void SaveSettings()
    {
        // Settings are automatically saved via Preferences
    }

    public void LoadSettings()
    {
        // Settings are automatically loaded via Preferences
    }
}
