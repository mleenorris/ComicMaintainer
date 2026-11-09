using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ComicMaintainer.MauiApp.Services;

namespace ComicMaintainer.MauiApp.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly IApiService _apiService;
    private readonly ISettingsService _settingsService;
    private readonly ISessionManager _session;
    private readonly IUserDialogs _dialogs;
    private readonly IThemeService _theme;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ServerWarning))]
    private string _serverUrl = string.Empty;

    [ObservableProperty]
    private bool _isTesting;

    [ObservableProperty]
    private string _testResult = string.Empty;

    [ObservableProperty]
    private bool _isDarkMode;

    public SettingsViewModel(
        IApiService apiService,
        ISettingsService settingsService,
        ISessionManager session,
        IUserDialogs dialogs,
        IThemeService theme)
    {
        _apiService = apiService;
        _settingsService = settingsService;
        _session = session;
        _dialogs = dialogs;
        _theme = theme;
        ServerUrl = _settingsService.ServerUrl;
        IsDarkMode = _theme.IsDarkMode;
    }

    /// <summary>Warns about an address that would send the password unencrypted; empty when there is nothing to warn about.</summary>
    public string ServerWarning => ServerAddress.DescribeRisk(ServerUrl);

    /// <summary>Who the app is signed in as, and how.</summary>
    public string AccountDescription
    {
        get
        {
            var name = _session.Account?.Username;
            var who = string.IsNullOrEmpty(name) ? "Signed in" : $"Signed in as {name}";
            return _session.CurrentKind == CredentialKind.ApiKey ? $"{who} with an API key" : who;
        }
    }

    partial void OnIsDarkModeChanged(bool value) => _theme.IsDarkMode = value;

    [RelayCommand]
    private async Task TestConnection()
    {
        IsTesting = true;
        TestResult = "Testing connection...";

        try
        {
            var result = await _apiService.TestConnectionAsync(ServerUrl);
            TestResult = (result.Success ? "✓ " : "✗ ") + result.Message;
        }
        finally
        {
            IsTesting = false;
        }
    }

    [RelayCommand]
    private async Task SaveSettings()
    {
        if (!ServerAddress.TryParse(ServerUrl, out var normalized, out var error))
        {
            TestResult = "✗ " + error;
            return;
        }

        var changed = !string.Equals(normalized, _settingsService.ServerUrl, StringComparison.Ordinal);
        if (changed && _session.CurrentKind is not null)
        {
            // A credential only works on the server that issued it, and must never be sent to another one.
            if (await _dialogs.ConfirmAsync("Change server", "Changing the server signs you out of this one.", "Change", "Cancel"))
            {
                _settingsService.ServerUrl = normalized;
                await _session.SignOutAsync();
            }

            return;
        }

        _settingsService.ServerUrl = normalized;
        ServerUrl = normalized;
        TestResult = "✓ Settings saved";
    }

    [RelayCommand]
    private Task SignOut() => _session.SignOutAsync();
}
