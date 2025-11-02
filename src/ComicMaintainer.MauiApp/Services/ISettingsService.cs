namespace ComicMaintainer.MauiApp.Services;

public interface ISettingsService
{
    string ServerUrl { get; set; }
    void SaveSettings();
    void LoadSettings();
}
