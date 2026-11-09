using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ComicMaintainer.MauiApp.Services;

namespace ComicMaintainer.MauiApp.ViewModels;

/// <summary>
/// The sign-in page, and the one place a session's end becomes visible: whatever ended it, the user lands here and
/// reads why.
/// </summary>
public partial class LoginViewModel : ObservableObject
{
    private readonly ISessionManager _session;
    private readonly ISettingsService _settings;
    private readonly INavigator _navigator;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ServerWarning))]
    private string _serverUrl = string.Empty;

    [ObservableProperty]
    private string _username = string.Empty;

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private string _apiKey = string.Empty;

    [ObservableProperty]
    private bool _useApiKey;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    public LoginViewModel(ISessionManager session, ISettingsService settings, INavigator navigator)
    {
        _session = session;
        _settings = settings;
        _navigator = navigator;
        _session.SessionEnded += OnSessionEnded;
    }

    /// <summary>Warns about an address that would send the password unencrypted; empty when there is nothing to warn about.</summary>
    public string ServerWarning => ServerAddress.DescribeRisk(ServerUrl);

    /// <summary>Called whenever the page appears: goes straight to the library if an earlier sign-in is still good.</summary>
    public async Task InitializeAsync()
    {
        ServerUrl = _settings.ServerUrl;

        IsBusy = true;
        try
        {
            if (await _session.RestoreAsync() != SessionRestoreResult.SignedOut)
            {
                await _navigator.ShowLibraryAsync();
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SignIn()
    {
        StatusMessage = string.Empty;

        if (UseApiKey ? string.IsNullOrWhiteSpace(ApiKey) : string.IsNullOrWhiteSpace(Username) || string.IsNullOrEmpty(Password))
        {
            StatusMessage = UseApiKey ? "Enter your API key." : "Enter your username and password.";
            return;
        }

        IsBusy = true;
        try
        {
            if (UseApiKey)
            {
                await _session.SignInWithApiKeyAsync(ServerUrl, ApiKey);
            }
            else
            {
                await _session.SignInAsync(ServerUrl, Username.Trim(), Password);
            }

            Password = string.Empty;
            ApiKey = string.Empty;
            await _navigator.ShowLibraryAsync();
        }
        catch (ApiException exception)
        {
            StatusMessage = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async void OnSessionEnded(object? sender, SessionEndedEventArgs e)
    {
        StatusMessage = e.Message ?? string.Empty;
        await _navigator.ShowSignInAsync();
    }
}
