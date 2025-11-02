using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ComicMaintainer.MauiApp.Services;

namespace ComicMaintainer.MauiApp.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IApiService _apiService;
    private readonly ISettingsService _settingsService;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private string _serverUrl = string.Empty;

    public MainViewModel(IApiService apiService, ISettingsService settingsService)
    {
        _apiService = apiService;
        _settingsService = settingsService;
        ServerUrl = _settingsService.ServerUrl;
        
        Task.Run(CheckConnection);
    }

    [RelayCommand]
    private async Task CheckConnection()
    {
        StatusMessage = "Checking connection...";
        IsConnected = await _apiService.TestConnectionAsync(ServerUrl);
        StatusMessage = IsConnected ? "Connected to server" : "Unable to connect to server";
    }

    [RelayCommand]
    private async Task NavigateToFiles()
    {
        if (!IsConnected)
        {
            await Application.Current!.MainPage!.DisplayAlert("Error", "Not connected to server", "OK");
            return;
        }

        await Shell.Current.GoToAsync("//FilesPage");
    }

    [RelayCommand]
    private async Task NavigateToSettings()
    {
        await Shell.Current.GoToAsync("//SettingsPage");
    }
}
