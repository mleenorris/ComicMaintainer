using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ComicMaintainer.MauiApp.Services;

namespace ComicMaintainer.MauiApp.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly IApiService _apiService;
    private readonly ISettingsService _settingsService;

    [ObservableProperty]
    private string _serverUrl = string.Empty;

    [ObservableProperty]
    private bool _isTesting;

    [ObservableProperty]
    private string _testResult = string.Empty;

    [ObservableProperty]
    private bool _isDarkMode;

    public SettingsViewModel(IApiService apiService, ISettingsService settingsService)
    {
        _apiService = apiService;
        _settingsService = settingsService;
        ServerUrl = _settingsService.ServerUrl;
        IsDarkMode = Application.Current?.UserAppTheme == AppTheme.Dark;
    }

    partial void OnIsDarkModeChanged(bool value)
    {
        if (Application.Current != null)
        {
            Application.Current.UserAppTheme = value ? AppTheme.Dark : AppTheme.Light;
        }
    }

    [RelayCommand]
    private async Task TestConnection()
    {
        if (string.IsNullOrWhiteSpace(ServerUrl))
        {
            TestResult = "Please enter a server URL";
            return;
        }

        IsTesting = true;
        TestResult = "Testing connection...";

        try
        {
            var isConnected = await _apiService.TestConnectionAsync(ServerUrl);
            TestResult = isConnected ? "✓ Connection successful!" : "✗ Connection failed";
        }
        catch (Exception ex)
        {
            TestResult = $"✗ Error: {ex.Message}";
        }
        finally
        {
            IsTesting = false;
        }
    }

    [RelayCommand]
    private void SaveSettings()
    {
        _settingsService.ServerUrl = ServerUrl;
        _settingsService.SaveSettings();
        TestResult = "✓ Settings saved";
    }
}
